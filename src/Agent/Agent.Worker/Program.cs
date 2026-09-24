using System.Diagnostics;
using Agent.Common;

namespace Agent.Worker;

internal static class Program
{
    // 版本号：改功能时记得+1 —— 主控端会显示它，用来判断现场那台被控端是不是旧包。
    // 1.0.1 = 支持"画面范围切换（仅副屏/全部屏幕）"+ 远端带出来的新窗口自动搬到外屏
    public const string Version = "1.0.1";

    /// <summary>单实例互斥体（保持引用，避免被 GC 提前释放）</summary>
    private static Mutex? _workerMutex;

    [STAThread]
    private static int Main(string[] args)
    {
        DisplayHelper.EnablePerMonitorDpiAwareness();

        var cfg = AgentConfig.Load();
        int sessionId = ArgInt(args, "--session") ?? SessionLauncherHelper.CurrentSessionId();
        int parentPid = ArgInt(args, "--parent") ?? 0;

        var log = new Logger("worker", minLevel: cfg.MinLogLevel, echoConsole: false);
        log.Info("========== Agent Worker 启动 ==========");

        // 单实例保护（同一会话只允许一个 Worker）。
        // 为什么需要：协调器的"看护"与"掉线重试"可能几乎同时拉起 Worker，
        // 两个 Worker 抢同一个命名管道与共享帧环时，新连接会把旧连接顶掉 →
        // 旧 Worker 退出 → 协调器再拉 → 无限循环、画面 0 帧（实测缺陷）。
        // 这里让"后来的那个"直接退出，先来的那个继续跑。
        bool createdNew;
        using var single = new Mutex(true, "Global\\RemoteControlWorker-" + sessionId, out createdNew);
        if (!createdNew)
        {
            log.Warn($"已有 Worker 在会话 {sessionId} 运行，本次启动退出（单实例保护）");
            return 0;
        }
        _workerMutex = single;   // 持有到进程退出
        log.Info($"版本 {Version} / pid={Environment.ProcessId} / 会话={sessionId} / 用户={Environment.UserName} / " +
                 $"管理员={SessionLauncherHelper.IsElevated} / 父进程={parentPid}");
        log.Info($"显示拓扑：{string.Join(" | ", DisplayHelper.GetMonitors())}");
        log.Info($"窗口站={DesktopHelper.GetWindowStationName()} 当前桌面={DesktopHelper.GetThreadDesktopName()} " +
                 $"输入桌面={DesktopHelper.GetInputDesktopName()} 在输入桌面上={DesktopHelper.IsOnInputDesktop()}");

        if (args.Contains("--capture-probe"))
        {
            RunCaptureProbe(cfg, log);
            return 0;
        }

        ApplicationConfiguration.Initialize();
        using var ctx = new WorkerContext(cfg, log, sessionId, parentPid);
        ctx.Start();
        try
        {
            Application.Run(ctx);
        }
        catch (Exception ex)
        {
            log.Error("消息循环异常", ex);
        }
        log.Info("Agent Worker 已退出");
        return 0;
    }

    /// <summary>
    /// 采集后端探测：在目标会话里逐个实测可用性并写日志（RDP 会话里 GDI/WGC 行为差异很大）。
    /// 用法： Agent.Worker.exe --capture-probe
    /// </summary>
    private static void RunCaptureProbe(AgentConfig cfg, Logger log)
    {
        log.Info("===== 采集后端探测开始 =====");
        log.Info($"会话={SessionLauncherHelper.CurrentSessionId()} 用户={Environment.UserName} 管理员={SessionLauncherHelper.IsElevated}");
        foreach (var d in DisplayHelper.GetMonitors()) log.Info($"显示器：{d}");

        var target = DisplayHelper.PickCaptureTarget();
        if (target == null) { log.Error("没有显示器"); return; }
        log.Info($"采集目标：{target}");

        foreach (var backend in new[] { "wgc", "gdi", "gdi-noblt" })
        {
            FrameSource? src = null;
            try
            {
                src = FrameSourceFactory.Create(backend, target, log);
                log.Info($"[{backend}] 创建成功：{src.Backend} {src.Width}x{src.Height}");
                using var ms = new MemoryStream();
                int okFrames = 0;
                for (int i = 0; i < 5; i++)
                {
                    ms.SetLength(0);
                    if (src.Capture(ms, out var err))
                    {
                        okFrames++;
                        if (okFrames == 1) log.Info($"[{backend}] 首帧 {ms.Length} 字节");
                    }
                    else
                    {
                        log.Warn($"[{backend}] 采集失败：{err}");
                    }
                    Thread.Sleep(60);
                }
                log.Info($"[{backend}] 结果：{(okFrames > 0 ? "可用" : "不可用")}（5 次中成功 {okFrames} 次）");
            }
            catch (Exception ex)
            {
                log.Error($"[{backend}] 异常：{ex.GetType().Name}: {ex.Message}");
                log.Error($"   堆栈：{ex.StackTrace}");
            }
            finally
            {
                try { src?.Dispose(); } catch { }
            }
        }
        log.Info("===== 采集后端探测结束 =====");
    }

    private static int? ArgInt(string[] args, string name)
    {
        var a = args.FirstOrDefault(x => x.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase));
        if (a == null) return null;
        return int.TryParse(a[(name.Length + 1)..], out var v) ? v : null;
    }
}

/// <summary>0x11 的 JSON 兼容形态（纯 UTF-8 路径仍然支持）</summary>
internal sealed class OpenSoftwareRequest
{
    public string ExePath { get; set; } = "";
    public string? Args { get; set; }
}

/// <summary>Worker 主上下文：IPC + 采集 + 编码 + 注入 + 剪贴板</summary>
public sealed class WorkerContext : ApplicationContext
{
    private readonly AgentConfig _cfg;
    private readonly Logger _log;
    private readonly int _sessionId;
    private readonly int _parentPid;

    private readonly IpcClient _ipc;
    private readonly InputInjector _injector;
    private readonly WindowManager _windows;
    private ClipboardWatcher? _clipboard;

    private FrameSource? _source;
    private FfmpegEncoder? _encoder;
    private Thread? _captureThread;
    private volatile bool _stop;
    private volatile bool _needStreamReset;
    private long _framesCaptured, _framesFed;
    private double _captureFps;
    private readonly Stopwatch _uptime = Stopwatch.StartNew();
    private System.Threading.Timer? _statusTimer;
    /// <summary>上一次"是否在输入桌面上"（只为状态变化时打一次日志，避免每秒刷屏）</summary>
    private bool? _lastOnInputDesktop;
    private DisplayInfo? _target;
    /// <summary>光标仲裁（只在采集目标是虚拟外屏时创建），见 CursorArbiter</summary>
    private CursorArbiter? _arbiter;
    /// <summary>鼠标输入路由：真实光标 / 后台定向注入 自适应，见 MouseRouter</summary>
    private MouseRouter? _router;
    /// <summary>当前画面档位：只副屏 / 全部屏幕 / 只主屏</summary>
    private volatile CaptureMode _captureMode = CaptureMode.VirtualOnly;
    /// <summary>是不是"一块画布装多块屏"（只有 AllScreens 模式才需要拼接采集）</summary>
    private bool _captureAll => _captureMode == CaptureMode.AllScreens;
    /// <summary>虚拟外屏（窗口摆放、光标仲裁一律以它为准，与"采集哪块"解耦）</summary>
    private DisplayInfo? _virtualScreen;
    /// <summary>采集目标要切换（由采集线程执行，见 CaptureLoop 顶部）</summary>
    private volatile bool _needCaptureSwitch;
    /// <summary>最近一次收到远端键鼠输入的时刻</summary>
    private long _lastRemoteInputTick;
    /// <summary>远端按下 Win 键的时刻（用来识别 Win+R）</summary>
    private long _winKeyDownTick;
    /// <summary>在这个时刻之前，若出现"运行"对话框就把它挪到外屏（只有 Win+R 会开启这个窗口期）</summary>
    private long _expectRunDialogUntil;
    /// <summary>
    /// 远端此刻**按着没抬**的键。为什么必须记：按键会被"本机用户在忙/窗口拿不到前台"这些规则丢弃，
    /// 一旦丢掉的是**抬起**事件，修饰键（Ctrl/Shift/Alt/Win）就会永远停在按下状态 ——
    /// 现场表现就是"在资源管理器里点一个文件，结果全被选中"（像一直按着 Ctrl）。
    /// 规则：按下被丢掉 → 不记；抬起**一律放行**；远端停手/退出 → 把还按着的键补一次抬起。
    /// </summary>
    private readonly HashSet<ushort> _remoteKeysDown = new();
    private long _lastRemoteKeyTick;
    /// <summary>编码器连续重启失败次数（到阈值就把硬件编码器降级成 libx264）</summary>
    private int _encoderRestartFails;
    /// <summary>码率/帧率自适应（拥塞就降档），见 BitrateGovernor</summary>
    private BitrateGovernor? _governor;
    /// <summary>因帧缓冲写不进去而丢弃的块数（拥塞信号）</summary>
    private long _ringDrops;
    /// <summary>协调器回报的"中继出口拥塞"（IPC 每秒更新一次）</summary>
    private volatile bool _outboundCongested;
    /// <summary>上一次"因为本机用户在操作所以丢掉了远端按键"的提示时间</summary>
    private long _lastKeyBlockedNotifyTick;
    private readonly Dictionary<string, int> _launched = new(StringComparer.OrdinalIgnoreCase);

    public WorkerContext(AgentConfig cfg, Logger log, int sessionId, int parentPid)
    {
        _cfg = cfg;
        _log = log;
        _sessionId = sessionId;
        _parentPid = parentPid;
        _ipc = new IpcClient(log);
        _injector = new InputInjector(log);
        _windows = new WindowManager(log);
    }

    public void Start()
    {
        _ipc.MessageReceived += OnMessage;
        if (!_ipc.Connect(TimeSpan.FromSeconds(45)))
        {
            _log.Error("无法连接 Coordinator，Worker 退出");
            ExitThread();
            return;
        }

        // 采集目标 + 编码器
        // 虚拟副屏默认模式可能是 800x600，先显式切到配置分辨率（必须在挑采集目标之前做）
        var virt = DisplayHelper.PickCaptureTarget();
        if (virt == null)
        {
            _log.Error("没有可用显示器");
            ExitThread();
            return;
        }
        DisplayHelper.TrySetDisplayMode(virt.DeviceName, _cfg.VirtualDisplayWidth,
            _cfg.VirtualDisplayHeight, _cfg.VirtualDisplayRefreshRate, msg => _log.Info(msg));

        _captureMode = _cfg.CaptureAllScreens ? CaptureMode.AllScreens : CaptureMode.VirtualOnly;
        _target = BuildCaptureTarget();
        if (_target == null)
        {
            _log.Error("没有可用显示器");
            ExitThread();
            return;
        }
        _log.Info($"实际采集目标：{_target}（{(_captureAll ? "全部屏幕" : "仅虚拟外屏")}）");

        _injector.SetCaptureTarget(_target);
        // 窗口摆放始终以虚拟外屏为准：远端打开的应用/对话框要出现在那块屏上，而不是画布中心
        _windows.SetCaptureTarget(_virtualScreen ?? _target);

        // 光标仲裁：只有"远端那块屏是虚拟屏"时才成立 —— 虚拟屏没有物理输出，
        // 远端光标本来就不会被坐着的人看见，仲裁只需处理"本机用户一动鼠标就把
        // 同一个系统光标拽回物理屏"这半个问题。若外屏没开（远端看的是物理主屏），
        // 仲裁会反过来打断远端，所以这时不创建。
        if (_cfg.CursorArbitrationEnabled && _virtualScreen is { IsVirtual: true })
        {
            try
            {
                _arbiter = new CursorArbiter(_virtualScreen, _log);
                _windows.Arbiter = _arbiter;
            }
            catch (Exception ex)
            {
                _log.Warn($"光标仲裁初始化失败（其余功能不受影响）：{ex.GetType().Name}: {ex.Message}");
            }
        }
        else if (!_target.IsVirtual)
        {
            _log.Warn("采集目标不是虚拟屏：远端只能看着物理主屏操作，本机用户会看见远端光标，" +
                      "而且两边抢同一个光标。要符合产品本意请打开 EnableVirtualDisplay。");
        }

        // 鼠标输入路由：本机用户空闲 → 远端用真实光标（100% 原生）；本机用户在忙 →
        // 远端自动改走后台定向注入（事件直接投给窗口，不碰他的光标）。见 MouseRouter。
        _router = new MouseRouter(_log, _injector, _arbiter)
        {
            ForegroundSetter = hwnd =>
            {
                _windows.RobustSetForeground(hwnd);
                return NativeApi.GetForegroundWindow() == hwnd;
            },
        };
        _router.SetCaptureTarget(_target);
        _log.Info($"鼠标输入路由已就绪：采集屏=({_target.Left},{_target.Top}) " +
                  $"{(_target.IsVirtual ? "虚拟外屏" : "⚠ 物理屏（不是虚拟外屏）")}，" +
                  $"远端画面坐标 (x,y) → 桌面坐标 ({_target.Left}+x, {_target.Top}+y)");

        // 开机先探一次光标：同一台机器上如果还有别的远控软件/旧被控端在管光标，
        // 真实光标路径的点击会落到主屏（现场最难查的那类问题）—— 这里当场判定并改走后台定向注入。
        if (!_router.ProbeCursor("启动自检", force: true))
        {
            _log.Warn("启动自检：系统光标不听从注入（很可能本机还有别的远控软件或旧被控端在运行）。" +
                      "远端鼠标已切换到后台定向注入，点击不会落到主屏；请检查被控端是不是装了两份。");
        }
        else
        {
            _log.Info("启动自检：系统光标可正常注入（远端鼠标走真实光标路径）");
        }

        // 启动时就判一次"机器上还有没有别的远控在跑"：有的话立刻退出光标争夺，
        // 别等到看护线程下一轮（10 秒）——这段时间里两边会互相抢，用户看到的就是"点不动"
        var rivalsAtBoot = InputRivals.Detect();
        if (rivalsAtBoot.Length > 0 && _arbiter is { Armed: true })
        {
            _arbiter.Disarm($"启动时检测到同类远控软件（{string.Join("/", rivalsAtBoot)}）：" +
                            "退出光标争夺，与它共用同一套系统光标", byRival: true);
            _router.RefreshBlockerHint();
        }

        _source = CreateSource(_target, _cfg.CaptureBackend);
        _governor = new BitrateGovernor(_log, _cfg.BitrateKbps, _cfg.FrameRate);
        _encoder = new FfmpegEncoder(ResolveFfmpeg(), _cfg.Encoder, _source.Width, _source.Height,
            _governor.FrameRate, _governor.BitrateKbps, _log);
        _encoder.Chunk += OnEncodedChunk;
        if (!_encoder.Start())
        {
            _log.Error($"编码器启动失败：{_encoder.LastError}");
            ExitThread();
            return;
        }
        _log.Info($"码率自适应起点：{_governor.Describe()}");

        // 剪贴板（写入必须回到 STA 主线程，见 SetClipboardOnUiThread）
        _clipboard = new ClipboardWatcher(_log);
        _clipboard.TextCopied += text =>
            _ipc.Send(new ProtocolMessage(MessageType.ClipboardText, PayloadCodec.EncodeText(text)));
        _clipboard.FilesCopied += files =>
        {
            foreach (var f in files)
                _ipc.Send(new ProtocolMessage(MessageType.ClipboardFile, JsonCodec.Encode(f)));
        };

        // 采集线程
        _captureThread = new Thread(CaptureLoop) { IsBackground = true, Name = "capture" };
        _captureThread.Start();

        // 上报就绪
        var ready = new WorkerReadyInfo
        {
            SessionId = _sessionId,
            Width = _source.Width,
            Height = _source.Height,
            Monitor = _target.DeviceName,
            Backend = _source.Backend,
            Version = Program.Version,
        };
        _ipc.Send(new ProtocolMessage(MessageType.WorkerReady, JsonCodec.Encode(ready)));
        SendMonitorInfo();

        // 同类远控软件看护：机器上还有别的远控在跑时主动**退出光标争夺**（见 CursorArbiter.Disarm）。
        // 两个远控各装一个全局鼠标钩子会互相抢那只唯一的系统光标 —— 现场就是"对方在副屏点不动、
        // 我们也点不动"，两边都用不了（实测：开着 UU远程 时 UU远程 控制不了副屏，关掉我们就可以）。
        _ = Task.Run(RivalWatchLoop);

        // 状态上报（1s）
        _statusTimer = new System.Threading.Timer(_ => SendStatus(), null, 1000, 1000);

        // 搬运/补抬起这些周期性工作**另起一条线程**做。
        // 为什么必须挪走：Worker 的主线程同时是 WinForms 消息循环线程，也是**低级键盘钩子的宿主线程**
        // （CursorArbiter 的 WH_KEYBOARD_LL 在哪个线程装的，回调就在哪个线程跑）。
        // 主线程一旦被枚举窗口这类活儿占住，钩子就会超时 —— 表现是"被控端一连接，整机键盘就发滞、丢键"。
        _ = Task.Run(HousekeepingLoop);

        // 父进程看护
        if (_parentPid > 0) WatchParent();
    }

    /// <summary>
    /// 采集目标：
    ///  · 默认 = 虚拟外屏（1080p 一块，省带宽）；
    ///  · "全部屏幕" = 以**整个虚拟桌面**为画布（主屏 + 副屏一起），画面坐标系从画布左上角起算，
    ///    主控端按 MonitorInfo.Screens 里每块屏的偏移把它画出来（物理屏那块是只读的，点了不打扰机器前面的人）。
    /// 注意：窗口摆放 / 光标仲裁**永远以虚拟外屏为准**（_virtualScreen），与采集范围无关。
    /// </summary>
    /// <summary>
    /// 创建采集源：画布模式用"每块屏各采 + 拼接"（见 CompositeFrameSource 的注释：
    /// 一次性 BitBlt 抓整个虚拟桌面在跨显卡的机器上会抓回全黑，现场踩到）。
    /// </summary>
    private FrameSource CreateSource(DisplayInfo target, string backend)
    {
        if (!_captureAll) return FrameSourceFactory.Create(backend, target, _log);
        var screens = DisplayHelper.GetMonitors();
        try
        {
            return new CompositeFrameSource(target, screens, backend, _log);
        }
        catch (Exception ex)
        {
            _log.Warn($"画布拼接采集创建失败（{ex.Message}），退回单次 BitBlt 抓整个桌面");
            return FrameSourceFactory.Create("gdi", target, _log);
        }
    }

    private DisplayInfo? BuildCaptureTarget()
    {
        var monitors = DisplayHelper.GetMonitors();
        if (monitors.Count == 0) return null;
        _virtualScreen = DisplayHelper.PickCaptureTarget();
        if (_captureMode == CaptureMode.PrimaryOnly)
        {
            var primary = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
            return primary;
        }
        if (_captureMode != CaptureMode.AllScreens) return _virtualScreen;

        var (l, t, w, h) = DisplayHelper.GetVirtualDesktopBounds();
        if (w <= 0 || h <= 0) return _virtualScreen;
        return new DisplayInfo("ALL-SCREENS", l, t, w, h,
            _virtualScreen?.IsPrimary ?? false, "全部屏幕（虚拟桌面画布）");
    }

    private string ResolveFfmpeg()
    {
        var path = FfmpegLocator.Resolve(_cfg.FfmpegPath);
        if (path == null)
        {
            // 找不到 ffmpeg 时**不要抛异常**：抛了会让 Worker 在启动阶段静默退出，
            // 表面看是"Worker 起来就死、画面 0 帧"，很难定位（实测踩过）。
            // 这里报清楚原因，让后续"编码器启动失败"的分支正常收尾。
            _log.Error($"未找到 ffmpeg.exe：把它放到 {AppContext.BaseDirectory}third_party\\ffmpeg\\ffmpeg.exe，" +
                       "或在 config\\agent.json 里配置 FfmpegPath");
            return "ffmpeg.exe";
        }
        _log.Info($"ffmpeg：{path}");
        return path;
    }

    // ---------------------------------------------------------------- 采集循环

    private void CaptureLoop()
    {
        var sw = Stopwatch.StartNew();
        long nextDue = 0;
        long lastFpsSample = 0;
        long framesAtLastSample = 0;
        int consecutiveFailures = 0;

        while (!_stop)
        {
            try
            {
                if (_needCaptureSwitch)
                {
                    _needCaptureSwitch = false;
                    SwitchCaptureTarget();
                    continue;
                }
                // 画布模式下显示拓扑变了（改分辨率/接拔屏）：画布尺寸跟着变，必须重建采集与编码
                if (_captureAll && _target != null && _source != null)
                {
                    var (cl, ct, cw, ch) = DisplayHelper.GetVirtualDesktopBounds();
                    if (cw > 0 && ch > 0 &&
                        (cw != _target.Width || ch != _target.Height || cl != _target.Left || ct != _target.Top))
                    {
                        _log.Info($"显示拓扑变化：( {_target.Left},{_target.Top} {_target.Width}x{_target.Height} ) → " +
                                  $"( {cl},{ct} {cw}x{ch} )，重建画布采集");
                        _needCaptureSwitch = true;
                        continue;
                    }
                }
                if (_needStreamReset)
                {
                    _needStreamReset = false;
                    RestartEncoder("请求重建码流");
                }
                if (_encoder is not { IsAlive: true })
                {
                    RestartEncoder("编码器已退出");
                    Thread.Sleep(300);
                    continue;
                }

                // 采集尺寸会随显示模式变化（打开游戏/播放器改分辨率、外屏被重建、
                // 本机用户改显示设置），而 ffmpeg 的 -video_size 是启动时定死的：
                // 一旦对不上，喂进去的帧会被切碎、ffmpeg 立刻退出，而重建又用同一个
                // 错尺寸 → 无限重启、零帧，用户看到的就是"打开某个应用之后黑屏卡死"。
                if (_source != null && (_encoder.Width != _source.Width || _encoder.Height != _source.Height))
                {
                    RebuildEncoderForSize(_source.Width, _source.Height);
                    continue;
                }

                // 帧间隔跟着自适应档位走（降帧率时才能真正降低负载）
                int frameMs = Math.Max(1, 1000 / Math.Max(1, _governor?.FrameRate ?? _cfg.FrameRate));
                long now = sw.ElapsedMilliseconds;
                if (now < nextDue)
                {
                    Thread.Sleep((int)Math.Min(30, Math.Max(1, nextDue - now)));
                    continue;
                }
                nextDue = now + frameMs;

                var stdin = _encoder.Stdin;
                if (stdin == null)
                {
                    Thread.Sleep(50);
                    continue;
                }

                if (!_source!.Capture(stdin, out var err))
                {
                    if (!string.IsNullOrEmpty(err))
                    {
                        if (++consecutiveFailures > 60)
                        {
                            _log.Error($"连续采集失败 {consecutiveFailures} 次，尝试重建采集器：{err}");
                            TryRecreateSource();
                            consecutiveFailures = 0;
                        }
                    }
                    continue;
                }
                consecutiveFailures = 0;
                Interlocked.Increment(ref _framesCaptured);
                Interlocked.Increment(ref _framesFed);

                // 每秒统计一次采集帧率，并让自适应看一次拥塞信号
                if (now - lastFpsSample >= 1000)
                {
                    long total = Interlocked.Read(ref _framesCaptured);
                    _captureFps = (total - framesAtLastSample) * 1000.0 / Math.Max(1, now - lastFpsSample);
                    framesAtLastSample = total;
                    lastFpsSample = now;
                    if (_governor != null && _governor.Report(Interlocked.Read(ref _ringDrops), _outboundCongested))
                        ApplyGovernorChange();
                }
            }
            catch (Exception ex)
            {
                _log.Warn($"采集循环异常：{ex.GetType().Name}: {ex.Message}");
                _needStreamReset = true;
                Thread.Sleep(300);
            }
        }
        _log.Info("采集线程结束");
    }

    /// <summary>
    /// 切换采集范围（只副屏 ⇄ 全部屏幕）。**必须在采集线程上执行**：
    /// _source/_encoder 都归采集线程所有，换的时候不能与采集动作并发。
    /// </summary>
    private void SwitchCaptureTarget()
    {
        try
        {
            var t = BuildCaptureTarget();
            if (t == null) return;
            _target = t;
            _injector.SetCaptureTarget(t);
            _windows.SetCaptureTarget(_virtualScreen ?? t);
            _router?.SetCaptureTarget(t);

            _source?.Dispose();
            _source = CreateSource(t, _cfg.CaptureBackend);
            RebuildEncoderForSize(_source.Width, _source.Height);   // 内部会发 StreamReset，让主控端重建解码器
            SendMonitorInfo();                                      // 新尺寸 + 每块屏的位置
            _log.Info($"画面范围已切换：{(_captureAll ? "全部屏幕（主屏 + 副屏画布）" : "仅虚拟外屏")} " +
                      $"{_source.Width}x{_source.Height} 后端={_source.Backend}");
        }
        catch (Exception ex)
        {
            _log.Error("切换画面范围失败", ex);
        }
    }

    private void TryRecreateSource()
    {
        try
        {
            _source?.Dispose();
            _source = FrameSourceFactory.Create("gdi", _target!, _log);
            _log.Info($"采集器已重建：{_source.Backend} {_source.Width}x{_source.Height}");
        }
        catch (Exception ex)
        {
            _log.Error("重建采集器失败", ex);
        }
    }

    /// <summary>
    /// 自适应换档：按新的码率/帧率重建编码器。
    /// 刻意**不发 StreamReset** —— 分辨率没变、编码器带内会重复 SPS/PPS，主控端不需要
    /// 重置解码器（重置一次就要重新等关键帧，那正是我们要避免的）。
    /// </summary>
    private void ApplyGovernorChange()
    {
        if (_source == null || _governor == null) return;
        try
        {
            var old = _encoder;
            old?.Stop();
            old?.Dispose();
            var enc = new FfmpegEncoder(ResolveFfmpeg(), _cfg.Encoder, _source.Width, _source.Height,
                _governor.FrameRate, _governor.BitrateKbps, _log);
            enc.Chunk += OnEncodedChunk;
            _encoder = enc;
            if (!enc.Start())
                _log.Error($"按自适应档位重建编码器失败：{enc.LastError}");
            else
                _log.Info($"编码器已按自适应档位重建：{_governor.Describe()}");
        }
        catch (Exception ex)
        {
            _log.Error("自适应换档异常", ex);
        }
    }

    /// <summary>
    /// 采集尺寸变了 → 换一个同尺寸的编码器。
    /// ffmpeg 的 -video_size 是命令行参数，运行期改不了，只能整个换掉；
    /// 不换的话就会陷入"喂进去的帧被切碎 → ffmpeg 立刻退出 → 用错尺寸重建"的死循环。
    /// </summary>
    private void RebuildEncoderForSize(int width, int height)
    {
        int oldW = _encoder?.Width ?? 0, oldH = _encoder?.Height ?? 0;
        if (width <= 0 || height <= 0)
        {
            _log.Warn($"采集尺寸非法（{width}x{height}），跳过编码器重建");
            return;
        }
        _log.Info($"采集尺寸变化：{oldW}x{oldH} → {width}x{height}，按新尺寸重建编码器");
        try
        {
            var old = _encoder;
            old?.Stop();
            old?.Dispose();
            var enc = new FfmpegEncoder(ResolveFfmpeg(), _cfg.Encoder, width, height,
                _governor?.FrameRate ?? _cfg.FrameRate, _governor?.BitrateKbps ?? _cfg.BitrateKbps, _log);
            enc.Chunk += OnEncodedChunk;
            _encoder = enc;
            if (!enc.Start())
            {
                _log.Error($"编码器按 {width}x{height} 重建失败：{enc.LastError}");
                Thread.Sleep(500);
            }
            // 通知主控端也重建解码器，避免半帧污染
            _ipc.Send(new ProtocolMessage(MessageType.StreamReset, PayloadCodec.Empty()));
        }
        catch (Exception ex)
        {
            _log.Error("重建编码器异常", ex);
            Thread.Sleep(500);
        }
    }

    private void RestartEncoder(string reason)
    {
        try
        {
            _log.Info($"重建编码流：{reason}");
            _encoder?.Stop();
            Thread.Sleep(150);
            var enc = _encoder;
            if (enc != null && !enc.Start())
            {
                _log.Error($"编码器重启失败：{enc.LastError}");
                // 硬件编码器被"打开的应用"抢走 GPU 会话后，重启多少次都是同一个错。
                // 到阈值就降级到软件编码，否则画面永远回不来（就是用户说的"黑屏卡死"）。
                if (++_encoderRestartFails >= 3)
                {
                    FfmpegEncoder.ForceSoftware(
                        $"编码器 {enc.EncoderName} 连续 {_encoderRestartFails} 次重启失败：{enc.LastError}", _log);
                    _encoderRestartFails = 0;
                }
                Thread.Sleep(1000);
                return;
            }
            _encoderRestartFails = 0;
            // 通知主控端也重建解码器，避免半帧污染
            _ipc.Send(new ProtocolMessage(MessageType.StreamReset, PayloadCodec.Empty()));
        }
        catch (Exception ex)
        {
            _log.Error("重建编码流异常", ex);
        }
    }

    /// <summary>本机用户刚动过键鼠多久之内，远端按键先不放行（够短，不会影响正常远端打字）</summary>
    private const long RemoteKeyBlockWindowMs = 800;

    /// <summary>远端按键因为本机用户正在操作而被丢弃 → 限流告知主控端（10s 最多一条）</summary>
    private void NotifyRemoteKeysBlocked()
    {
        long now = Environment.TickCount64;
        long last = Interlocked.Read(ref _lastKeyBlockedNotifyTick);
        if (now - last < 10_000) return;
        if (Interlocked.CompareExchange(ref _lastKeyBlockedNotifyTick, now, last) != last) return;
        _log.Info("本机用户正在操作，本次远端按键已丢弃（避免打进本机用户的前台窗口）");
        _ipc.Send(new ProtocolMessage(MessageType.Error,
            PayloadCodec.EncodeText("被控端有人正在本机键鼠上操作，为避免把字打进他的窗口，远端按键已暂时忽略；对方停手后自动恢复")));
    }

    /// <summary>编码器输出了一个码流块 → 写入共享内存</summary>
    private void OnEncodedChunk(byte[] buffer, int length)
    {
        try
        {
            // 帧环写不进去 = 链路承载不住当前码率。**刻意不重建码流**：
            // 重建会让主控端重置解码器、重新等关键帧，在慢链路上等于永远出不来画面
            // （实测：被控端一直 23fps 在发，主控端却一帧都解不出来）。这里只丢这一块并计数，
            // 交给 BitrateGovernor 降码率；GOP 已经收到 1 秒，丢块后最多 1 秒就能靠下一个关键帧自愈。
            // 超时也从 800ms 收到 250ms：宁可多丢一块，也别把编码器这条链子卡住那么久。
            if (!_ipc.Ring.Write(new ReadOnlySpan<byte>(buffer, 0, length), timeoutMs: 250))
            {
                long n = Interlocked.Increment(ref _ringDrops);
                if (n <= 3 || n % 100 == 0)
                    _log.Warn($"帧缓冲写不进去，丢弃本块（累计 {n} 块）：链路带宽不足，正在降码率");
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"写共享内存失败：{ex.Message}");
            _needStreamReset = true;
        }
    }

    // ---------------------------------------------------------------- 消息处理

    private void OnMessage(ProtocolMessage msg)
    {
        switch (msg.Type)
        {
            case MessageType.MoveAppWindowHint:
            {
                // 主控端在软件列表里点了"搬到副屏"：把这个进程的窗口统统挪到外屏
                int pid = PayloadCodec.DecodeCloseSoftware(msg.Payload);   // 同样是一个 4B pid
                _log.Info($"主控端请求把程序 pid={pid} 的窗口搬到外屏");
                _windows.MoveAppToVirtualScreen(pid);
                break;
            }
            case MessageType.MoveWindowHint:
            {
                // 主控端在物理屏画面上点了某个窗口：把它搬到虚拟外屏（人工搬运，不自动）
                var (mx, my) = PayloadCodec.DecodePoint(msg.Payload);
                _windows.MoveWindowAt(_injector.CaptureOrigin.Left + mx, _injector.CaptureOrigin.Top + my);
                break;
            }
            case MessageType.CaptureModeHint:
            {
                // 主控端切换"画面显示哪些屏"：只副屏（省带宽）/ 全部屏幕（主屏 + 副屏）
                if (msg.Payload.Length > 0)
                {
                    var mode = (CaptureMode)msg.Payload[0];
                    if (mode != _captureMode)
                    {
                        _captureMode = mode;
                        _needCaptureSwitch = true;      // 真正切换交给采集线程
                        _log.Info($"主控端切换画面范围：{mode}");
                    }
                }
                break;
            }
            case MessageType.MouseMove:
            {
                Interlocked.Exchange(ref _lastRemoteInputTick, Environment.TickCount64);
                var (x, y) = PayloadCodec.DecodeMouseMove(msg.Payload);
                _router?.Move(x, y);
                break;
            }
            case MessageType.MouseButton:
            {
                Interlocked.Exchange(ref _lastRemoteInputTick, Environment.TickCount64);
                var (x, y, btn, up) = PayloadCodec.DecodeMouseButton(msg.Payload);
                _router?.Button(x, y, btn, up);
                break;
            }
            case MessageType.MouseWheel:
            {
                Interlocked.Exchange(ref _lastRemoteInputTick, Environment.TickCount64);
                var (x, y, d) = PayloadCodec.DecodeMouseWheel(msg.Payload);
                _router?.Wheel(x, y, d);
                break;
            }
            case MessageType.KeyEvent:
            {
                Interlocked.Exchange(ref _lastRemoteInputTick, Environment.TickCount64);
                // 只有 Win+R 会开启"把运行框挪到外屏"的短窗口期 —— 别的窗口一律不动（产品要求）
                if (msg.Payload.Length >= 3)
                {
                    var (vkRun, upRun, _) = PayloadCodec.DecodeKey(msg.Payload);
                    long nowTick = Environment.TickCount64;
                    if (!upRun && (vkRun == 0x5B || vkRun == 0x5C))
                        Interlocked.Exchange(ref _winKeyDownTick, nowTick);
                    else if (!upRun && (vkRun == 0x52 /*R*/) &&
                             nowTick - Interlocked.Read(ref _winKeyDownTick) < 3000)
                    {
                        // 按 Win+R = 开一段 25 秒的"会话期"：先给当前窗口拍快照，
                        // 之后新出现的窗口（运行框本身 + 它启动出来的程序）一律去外屏；
                        // 快照里原有的窗口一个都不动（"除非我让它过去，否则不准动"）。
                        _windows.SnapshotBaseline();
                        Interlocked.Exchange(ref _expectRunDialogUntil, nowTick + 60000);
                        _log.Info("远端按下 Win+R：接下来 60 秒内新出现的窗口（含它启动的程序）都会摆到外屏");
                    }
                }
                var (vk, up, flags) = PayloadCodec.DecodeKey(msg.Payload);
                // 键盘焦点在一个会话里同样是"单例"：远端敲的键会进**当前前台窗口**。
                // 本机用户此刻正在打字时，前台窗口是他的，远端按键就会打进他的文档
                // （丢字/误操作，比"远端暂时打不了字"严重得多）。所以本机用户刚动过键鼠时
                // 宁可丢掉远端按键，并把原因明确告诉主控端。
                Interlocked.Exchange(ref _lastRemoteKeyTick, Environment.TickCount64);

                // **抬起事件一律放行**：丢掉一个抬起 = 那个键在被控端永远按着（Ctrl 卡住会导致"点一个全选"）。
                if (up)
                {
                    lock (_remoteKeysDown) _remoteKeysDown.Remove(vk);
                    _injector.Key(vk, up, flags);
                    break;
                }

                if (_arbiter != null && _arbiter.LocalActiveWithin(RemoteKeyBlockWindowMs))
                {
                    NotifyRemoteKeysBlocked();
                    break;
                }
                // 后台注入模式下窗口不会被点击激活，所以要显式把"远端点过的那个窗口"带前台，
                // 否则这一串按键会打进本机用户的前台窗口（本机用户在忙时上面已经拦掉了）。
                if (_router != null && !_router.EnsureClaimedForeground())
                {
                    NotifyRemoteKeysBlocked();
                    break;
                }
                lock (_remoteKeysDown) _remoteKeysDown.Add(vk);
                _injector.Key(vk, up, flags);
                break;
            }
            case MessageType.OpenSoftware:
            {
                var payloadText = PayloadCodec.DecodeText(msg.Payload);
                string exe = payloadText, args = "";
                // 兼容扩展：负载以 '{' 开头时按 JSON {exePath,args} 解析（策划协议之外的兼容扩展）
                if (payloadText.StartsWith('{'))
                {
                    try
                    {
                        var o = JsonCodec.Decode<OpenSoftwareRequest>(msg.Payload);
                        if (o != null)
                        {
                            exe = o.ExePath;
                            args = o.Args ?? "";
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.Warn($"解析打开指令失败，按纯路径处理：{ex.Message}");
                    }
                }
                _log.Info($"收到打开软件指令：{exe} {args}");
                int pid = _windows.Open(exe, args);
                if (pid > 0) _launched[exe] = pid;
                SendSoftwareState(exe, pid, pid > 0);
                break;
            }
            case MessageType.CloseSoftware:
            {
                int pid = PayloadCodec.DecodeCloseSoftware(msg.Payload);
                _log.Info($"收到关闭软件指令：pid={pid}");
                var path = _launched.FirstOrDefault(kv => kv.Value == pid).Key ?? "";
                bool ok = _windows.Close(pid);
                if (!string.IsNullOrEmpty(path)) _launched.Remove(path);
                SendSoftwareState(path, pid, !ok);
                break;
            }
            case MessageType.ClipboardText:
            {
                var text = PayloadCodec.DecodeText(msg.Payload);
                SetClipboardOnUiThread(text);
                break;
            }
            case MessageType.StreamReset:
                _log.Info("主控端请求重建码流（新 View 接入）");
                _needStreamReset = true;
                break;
            case MessageType.InputModeHint:
            {
                // 主控端选的鼠标模式：真实光标 / 后台注入 / 自动
                if (msg.Payload.Length > 0 && _router != null)
                {
                    var mode = (RemoteInputMode)msg.Payload[0];
                    if (_router.Mode != mode)
                    {
                        _router.Mode = mode;
                        _log.Info($"鼠标模式已切换为：{mode}");
                    }
                }
                break;
            }
            case MessageType.IpcBitrateHint:
            {
                // 协调器看到的中继出口拥塞（Worker 自己看不到那条腿）
                var hint = JsonCodec.Decode<BitrateHintInfo>(msg.Payload);
                _outboundCongested = hint?.Congested ?? false;
                break;
            }
            case MessageType.RequestSoftwareList:
            {
                // 上报自己启动过的进程状态
                foreach (var (path, pid) in _launched.ToArray())
                {
                    bool running = IsRunning(pid);
                    if (!running) _launched.Remove(path);
                    SendSoftwareState(path, pid, running);
                }
                break;
            }
            case MessageType.Stop:
                _log.Info("收到停止指令，Worker 退出");
                _stop = true;
                ExitThread();
                break;
            default:
                _log.Debug($"忽略消息 0x{(byte)msg.Type:X2}");
                break;
        }
    }

    /// <summary>
    /// 远端刚按过 Win+R：把随之出现的「运行」对话框挪到虚拟外屏。
    /// 为什么只做这一条：客户明确要求"只有 Win+R 这个要自动切过去，其它都不能自动动"。
    /// 判定很硬：只在 Win+R 之后的 6 秒窗口期内、且窗口类是 #32770（标准对话框）、
    /// 标题是「运行 / Run」时才搬；如果它已经在外屏上就不动。
    /// </summary>
    private void MoveRunDialogToVirtualIfExpected()
    {
        long until = Interlocked.Read(ref _expectRunDialogUntil);
        if (until == 0 || Environment.TickCount64 > until) return;
        var virt = _virtualScreen;
        if (virt is not { IsVirtual: true }) return;

        var hwnd = NativeApi.GetForegroundWindow();
        if (!LooksLikeRunDialog(hwnd)) return;
        if (!NativeApi.GetWindowRect(hwnd, out var r)) return;
        bool onVirtual = r.Left >= virt.Left && r.Left < virt.Right && r.Top >= virt.Top && r.Top < virt.Bottom;
        if (onVirtual) return;   // 已经在外屏上：什么都不做（**不要**关会话期，见下）

        _log.Info($"Win+R 的运行框出现在物理屏 ({r.Left},{r.Top}) → 挪到外屏");
        _windows.MoveWindowTo(hwnd, virt);
        // 【这里以前会把会话期清零 —— 那是 bug】运行框在 Win+R 后 1 秒内就被搬走，
        // 会话期一清零，用户接着敲命令启动的 cmd/PowerShell 就落在窗口期之外、
        // 再也没人把它搬到外屏（现场反馈："Win+R 能出来，但输入 cmd 就跑到主屏"）。
        // 会话期只按时间过期（60 秒），不因为搬了运行框而提前结束。
    }

    /// <summary>是不是"运行"对话框：标准对话框类 #32770 + 标题含 运行/Run</summary>
    private static bool LooksLikeRunDialog(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        try
        {
            string cls = NativeApi.GetClassName(hwnd) ?? "";
            if (!cls.Contains("32770")) return false;
            string title = NativeApi.GetWindowText(hwnd) ?? "";
            return title.Contains("运行") || title.Contains("Run", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>
    /// 本机用户把外屏上的窗口叫起来了（典型动作：点任务栏上那个按钮）→ 把它搬回主屏。
    /// 判据：前台窗口落在虚拟外屏上 + 本机用户最近 2 秒内动过键鼠（= 是他叫的，不是远端注入的）。
    /// </summary>
    private IntPtr _lastReturnedToPrimary = IntPtr.Zero;

    private void MoveForegroundBackToPrimaryIfLocal()
    {
        if (_virtualScreen is not { IsVirtual: true } virt) return;
        if (_arbiter == null || !_arbiter.LocalActiveWithin(2000)) return;

        var fg = NativeApi.GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == _lastReturnedToPrimary) return;
        if (!NativeApi.GetWindowRect(fg, out var r)) return;
        bool onVirtual = r.Left >= virt.Left && r.Left < virt.Right && r.Top >= virt.Top && r.Top < virt.Bottom;
        if (!onVirtual) return;

        var primary = DisplayHelper.GetMonitors().FirstOrDefault(m => m.IsPrimary);
        if (primary == null) return;
        try
        {
            if (NativeApi.IsZoomed(fg)) NativeApi.ShowWindow(fg, NativeApi.SW_RESTORE);
            // 只挪位置、不改大小、不改 Z 序：搬到主屏左上角并留 24px 边距
            int x = primary.Left + 24, y = primary.Top + 24;
            NativeApi.SetWindowPos(fg, IntPtr.Zero, x, y, 0, 0,
                NativeApi.SWP_NOSIZE | NativeApi.SWP_NOZORDER | NativeApi.SWP_NOACTIVATE);
            _lastReturnedToPrimary = fg;
            _log.Info($"本机用户把外屏上的窗口叫起来了 → 已搬回主屏 ({x},{y})");
        }
        catch (Exception ex) { _log.Debug($"搬回主屏失败：{ex.Message}"); }
    }

    /// <summary>
    /// 周期性杂务（跑在**独立线程**上，别占主线程，见 Start 里的说明）：
    ///   · 把"远端带出来的新窗口"摆到外屏；
    ///   · 把"还按着没抬"的键补一次抬起（防 Ctrl/Win 卡住）；
    ///   · 本机用户把窗口叫起来时搬回主屏。
    /// </summary>
    private void HousekeepingLoop()
    {
        while (!_stop)
        {
            try
            {
                long now = Environment.TickCount64;

                // 补抬起：远端停手 3 秒后
                if (now - Interlocked.Read(ref _lastRemoteKeyTick) > 3000)
                {
                    List<ushort> stuck;
                    lock (_remoteKeysDown) stuck = _remoteKeysDown.ToList();
                    if (stuck.Count > 0)
                    {
                        foreach (var kvk in stuck)
                        {
                            _injector.Key(kvk, true, 0);
                            _log.Warn($"远端停手，补发按键抬起 vk=0x{kvk:X2}（避免修饰键卡在被控端）");
                        }
                        lock (_remoteKeysDown) _remoteKeysDown.Clear();
                    }
                }

                // Win+R 会话期：运行框 + 它启动出来的程序都摆到外屏
                MoveRunDialogToVirtualIfExpected();
                if (now < Interlocked.Read(ref _expectRunDialogUntil))
                {
                    int n = _windows.SweepNewWindowsFromPhysical(remoteActive: true, localActive: false);
                    if (n > 0) _log.Info($"Win+R 期间把 {n} 个新窗口摆到了外屏");
                }

                // 本机用户点任务栏把外屏窗口叫起来 → 搬回主屏
                MoveForegroundBackToPrimaryIfLocal();
            }
            catch (Exception ex) { _log.Debug($"周期性杂务异常：{ex.Message}"); }
            for (int i = 0; i < 10 && !_stop; i++) Thread.Sleep(100);   // 1 秒一轮，停止时能立刻退出
        }
    }

    /// <summary>
    /// 同类远控软件看护：发现别的远控在跑就停用光标仲裁（退出对系统光标的争夺），
    /// 它们退出后再装回来。为什么值得专门盯着：这是"两边都点不动"那类现象的唯一根因，
    /// 而且它随机器上装了什么软件而变化，不能只在启动时判一次。
    /// </summary>
    private void RivalWatchLoop()
    {
        while (!_stop)
        {
            try
            {
                var rivals = InputRivals.Detect();
                if (_arbiter != null)
                {
                    if (rivals.Length > 0 && _arbiter.Armed)
                    {
                        _arbiter.Disarm($"检测到同类远控软件在运行（{string.Join("/", rivals)}）：" +
                                        "两家各装一个全局鼠标钩子会互相把对方的光标与点击抢走", byRival: true);
                        _log.Warn($"检测到 {string.Join("/", rivals)} 正在运行：已停用光标仲裁，" +
                                  "远端与它**共用同一套系统光标**（关掉那个软件即可恢复「本机用户独占光标」）。");
                        _router?.RefreshBlockerHint();
                    }
                    else if (rivals.Length == 0 && _arbiter.DisarmedByRival)
                    {
                        if (_arbiter.TryArm())
                            _log.Info("同类远控软件已退出：光标仲裁重新启用（本机用户重新独占自己的光标）");
                    }
                }
            }
            catch (Exception ex) { _log.Debug($"同类软件看护异常：{ex.Message}"); }
            for (int i = 0; i < 100 && !_stop; i++) Thread.Sleep(100);   // 10 秒一轮，停止时能立刻退出
        }
    }

    /// <summary>
    /// 把主控端下发的剪贴板文本写入本会话剪贴板。
    /// Clipboard（OLE）要求 STA 线程，而本方法由 IPC 读取线程（MTA）调用，
    /// 因此必须 marshal 回主线程（消息循环线程就是 [STAThread]）。
    /// </summary>
    private void SetClipboardOnUiThread(string text)
    {
        var marshaller = _clipboard?.Invoker;
        if (marshaller == null)
        {
            _log.Warn("剪贴板监听未就绪，丢弃下发的剪贴板文本");
            return;
        }
        try
        {
            if (marshaller.InvokeRequired)
                marshaller.BeginInvoke(new Action(() => _clipboard.SetTextFromRemote(text)));
            else
                _clipboard.SetTextFromRemote(text);
        }
        catch (Exception ex)
        {
            _log.Error($"写入剪贴板失败：{ex.Message}");
        }
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch { return false; }
    }

    private void SendSoftwareState(string exePath, int pid, bool running)
    {
        var info = new SoftwareStateInfo { ExePath = exePath, Pid = pid, IsRunning = running };
        _ipc.Send(new ProtocolMessage(MessageType.SoftwareState, JsonCodec.Encode(info)));
    }

    private void SendMonitorInfo()
    {
        var monitors = DisplayHelper.GetMonitors();
        var t = _target ?? monitors.FirstOrDefault();
        var info = new MonitorInfo
        {
            Left = t?.Left ?? 0,
            Top = t?.Top ?? 0,
            Width = _source?.Width ?? t?.Width ?? 0,
            Height = _source?.Height ?? t?.Height ?? 0,
            Dpi = 96,
            Count = monitors.Count,
            AllScreens = _captureAll,
        };
        // 每块屏在画面里的位置：主控端据此把物理屏画成"只读区域"（看得到、点不到）
        foreach (var m in monitors)
        {
            info.Screens.Add(new ScreenRect
            {
                Left = m.Left - info.Left,
                Top = m.Top - info.Top,
                Width = m.Width,
                Height = m.Height,
                IsVirtual = m.IsVirtual,
                IsPrimary = m.IsPrimary,
                Device = m.DeviceName,
            });
        }
        _ipc.Send(new ProtocolMessage(MessageType.MonitorInfo, JsonCodec.Encode(info)));
    }

    private void SendStatus()
    {
        if (_stop) return;
        try
        {
            // 安全桌面（UAC 提权弹窗 / 锁屏 / Ctrl+Alt+Del）：这期间采集抓不到、键鼠也进不去，
            // 主控端看到的是一片黑。每秒把状态上报一次，主控端就能说出"为什么黑"，而不是让人干猜。
            bool onInput = DesktopHelper.IsOnInputDesktop();
            if (_lastOnInputDesktop != onInput)
            {
                if (onInput)
                {
                    _log.Info("已回到本会话桌面，画面与操作恢复");
                }
                else
                {
                    _log.Warn($"不在输入桌面上（本进程桌面={DesktopHelper.GetThreadDesktopName()}，" +
                              $"输入桌面={DesktopHelper.GetInputDesktopName()}）：通常是 UAC 提权窗口/锁屏/Ctrl+Alt+Del，" +
                              "这期间画面取不到、键鼠也进不去");
                }
                _lastOnInputDesktop = onInput;
            }

            var status = new WorkerStatusInfo
            {
                CaptureFps = Math.Round(_captureFps, 1),
                EncodeFps = Math.Round(_captureFps, 1),
                Backend = _source?.Backend ?? "",
                FramesSent = Interlocked.Read(ref _framesCaptured),
                BytesSent = _encoder?.ByteCount ?? 0,
                SecureDesktop = !onInput,
                // 自适应与拥塞的可观测字段：主控端据此显示"为什么画面不动"
                BitrateKbps = _governor?.BitrateKbps ?? _cfg.BitrateKbps,
                FrameRate = _governor?.FrameRate ?? _cfg.FrameRate,
                AdaptiveLevel = _governor?.Level ?? 0,
                RingDropped = Interlocked.Read(ref _ringDrops),
                // 输入侧的可观测字段：主控端据此显示"远端鼠标点在哪块屏、走的哪条路"，
                // 现场"看着副屏点、结果点到主屏"这类问题一眼可辨（见 MouseRouter.CursorContested）
                InputBaseX = _injector.CaptureOrigin.Left,
                InputBaseY = _injector.CaptureOrigin.Top,
                // 注意：这里报的是"被控机上有没有虚拟外屏"，不是"正在采集的那块是不是虚拟屏"——
                // 画布模式下采集目标是整个桌面，但虚拟外屏明明是有的（报 false 会误导成"没装外屏"）
                CaptureIsVirtual = _virtualScreen is { IsVirtual: true },
                AllScreens = _captureAll,
                InputPath = _router?.CurrentPath == InputPath.Background ? "background" : "real",
                CursorContested = _router?.CursorContested ?? false,
                // 有同类软件在跑（哪怕此刻点击是好的）也要上报：主控端据此显示"与它共用光标"
                CursorBlockerHint = _router?.BlockerHint ?? "",
            };
            _ipc.Send(new ProtocolMessage(MessageType.WorkerStatus, JsonCodec.Encode(status)));
        }
        catch { }
    }

    // ---------------------------------------------------------------- 父进程看护

    private void WatchParent()
    {
        _ = Task.Run(() =>
        {
            try
            {
                var parent = Process.GetProcessById(_parentPid);
                parent.WaitForExit();
                _log.Warn("父进程（Coordinator）已退出，Worker 跟随退出");
            }
            catch (Exception ex)
            {
                _log.Warn($"父进程看护异常：{ex.Message}");
            }
            _stop = true;
            try { ExitThread(); } catch { }
        });
    }

    protected override void Dispose(bool disposing)
    {
        _stop = true;
        try { _statusTimer?.Dispose(); } catch { }
        try { _arbiter?.Dispose(); } catch { }
        try { _clipboard?.Dispose(); } catch { }
        try { _encoder?.Dispose(); } catch { }
        try { _source?.Dispose(); } catch { }
        try { _ipc.Dispose(); } catch { }
        _log.Info($"Worker 收尾完成（运行 {_uptime.Elapsed.TotalSeconds:F0}s，采集 {_framesCaptured} 帧）");
        base.Dispose(disposing);
    }
}

/// <summary>Worker 侧需要的少量会话/令牌查询（避免引用 Coordinator 程序集）</summary>
internal static class SessionLauncherHelper
{
    public static int CurrentSessionId()
    {
        NativeApi.ProcessIdToSessionId(Environment.ProcessId, out int sid);
        return sid;
    }

    public static bool IsElevated
    {
        get
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(id)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
    }
}
