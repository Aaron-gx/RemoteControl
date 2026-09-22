using System.Diagnostics;
using Agent.Common;

namespace Agent.Worker;

internal static class Program
{
    public const string Version = "1.0.0";

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
    /// <summary>编码器连续重启失败次数（到阈值就把硬件编码器降级成 libx264）</summary>
    private int _encoderRestartFails;
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
        _target = DisplayHelper.PickCaptureTarget();
        if (_target == null)
        {
            _log.Error("没有可用显示器");
            ExitThread();
            return;
        }
        // 虚拟副屏默认模式可能是 800x600，先显式切到配置分辨率
        DisplayHelper.TrySetDisplayMode(_target.DeviceName, _cfg.VirtualDisplayWidth,
            _cfg.VirtualDisplayHeight, _cfg.VirtualDisplayRefreshRate, msg => _log.Info(msg));
        var refreshed = DisplayHelper.PickCaptureTarget();
        if (refreshed != null) _target = refreshed;
        _log.Info($"实际采集目标：{_target}");

        _injector.SetCaptureTarget(_target);
        _windows.SetCaptureTarget(_target);

        // 光标仲裁：只有"远端那块屏是虚拟屏"时才成立 —— 虚拟屏没有物理输出，
        // 远端光标本来就不会被坐着的人看见，仲裁只需处理"本机用户一动鼠标就把
        // 同一个系统光标拽回物理屏"这半个问题。若外屏没开（远端看的是物理主屏），
        // 仲裁会反过来打断远端，所以这时不创建。
        if (_cfg.CursorArbitrationEnabled && _target.IsVirtual)
        {
            try
            {
                _arbiter = new CursorArbiter(_target, _log);
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
        _log.Info("鼠标输入路由已就绪：本机用户空闲时远端用真实光标，本机用户一动手自动切后台定向注入");

        _source = FrameSourceFactory.Create(_cfg.CaptureBackend, _target, _log);
        _encoder = new FfmpegEncoder(ResolveFfmpeg(), _cfg.Encoder, _source.Width, _source.Height,
            _cfg.FrameRate, _cfg.BitrateKbps, _log);
        _encoder.Chunk += OnEncodedChunk;
        if (!_encoder.Start())
        {
            _log.Error($"编码器启动失败：{_encoder.LastError}");
            ExitThread();
            return;
        }

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

        // 状态上报（1s）
        _statusTimer = new System.Threading.Timer(_ => SendStatus(), null, 1000, 1000);

        // 父进程看护
        if (_parentPid > 0) WatchParent();
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
        int frameMs = Math.Max(1, 1000 / Math.Max(1, _cfg.FrameRate));
        var sw = Stopwatch.StartNew();
        long nextDue = 0;
        long lastFpsSample = 0;
        long framesAtLastSample = 0;
        int consecutiveFailures = 0;

        while (!_stop)
        {
            try
            {
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

                // 每秒统计一次采集帧率
                if (now - lastFpsSample >= 1000)
                {
                    long total = Interlocked.Read(ref _framesCaptured);
                    _captureFps = (total - framesAtLastSample) * 1000.0 / Math.Max(1, now - lastFpsSample);
                    framesAtLastSample = total;
                    lastFpsSample = now;
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
                _cfg.FrameRate, _cfg.BitrateKbps, _log);
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
            if (!_ipc.Ring.Write(new ReadOnlySpan<byte>(buffer, 0, length), timeoutMs: 800))
            {
                _log.Warn($"帧缓冲已满，丢弃 {length} 字节并要求重建码流");
                _needStreamReset = true;
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
            case MessageType.MouseMove:
            {
                var (x, y) = PayloadCodec.DecodeMouseMove(msg.Payload);
                _router?.Move(x, y);
                break;
            }
            case MessageType.MouseButton:
            {
                var (x, y, btn, up) = PayloadCodec.DecodeMouseButton(msg.Payload);
                _router?.Button(x, y, btn, up);
                break;
            }
            case MessageType.MouseWheel:
            {
                var (x, y, d) = PayloadCodec.DecodeMouseWheel(msg.Payload);
                _router?.Wheel(x, y, d);
                break;
            }
            case MessageType.KeyEvent:
            {
                var (vk, up, flags) = PayloadCodec.DecodeKey(msg.Payload);
                // 键盘焦点在一个会话里同样是"单例"：远端敲的键会进**当前前台窗口**。
                // 本机用户此刻正在打字时，前台窗口是他的，远端按键就会打进他的文档
                // （丢字/误操作，比"远端暂时打不了字"严重得多）。所以本机用户刚动过键鼠时
                // 宁可丢掉远端按键，并把原因明确告诉主控端。
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
        };
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
