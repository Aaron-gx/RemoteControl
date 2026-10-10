using System.Diagnostics;
using Agent.Common;

namespace Agent.Worker;

// 2026-10 进程合并：本文件原先的 Main（单实例互斥 / --capture-probe 探测 / --session 参数）
// 已随"Worker 独立进程"形态一起删除 —— WorkerContext 现在由 Agent.Coordinator 进程里的
// WorkerHost 以 STA 线程承载，与 Coordinator 用进程内 WorkerBridge 通信。
// 采集后端探测请用独立的 src\Tests\CaptureProbe 项目。

/// <summary>0x11 的 JSON 兼容形态（纯 UTF-8 路径仍然支持）</summary>
internal sealed class OpenSoftwareRequest
{
    public string ExePath { get; set; } = "";
    public string? Args { get; set; }
}

/// <summary>Worker 主上下文：采集 + 编码 + 注入 + 剪贴板（在 Coordinator 进程内的 STA 线程上运行）</summary>
public sealed class WorkerContext : ApplicationContext
{
    /// <summary>
    /// 版本号：改功能时记得+1 —— 主控端会显示它，用来判断现场那台被控端是不是旧包。
    /// 1.0.1 = 支持"画面范围切换（仅副屏/全部屏幕）"+ 远端带出来的新窗口自动搬到外屏
    /// 1.1.0 = 进程合并（Worker 并入 Coordinator，IPC 换进程内通道）
    /// 1.1.1 = "此电脑"等常驻进程代开窗口的主屏闪烁修复（钩子常驻 + 武装期 60ms 快扫）
    /// 1.1.2 = 此电脑 /separate 独立窗口（治"到不了副屏"）+ "最近输入者"判据（治"主屏开软件跳副屏"）
    /// 1.1.3 = CREATE 事件预定位（窗口显示前就挪到外屏，主屏零动作）；
    /// 1.1.4 = 修 CREATE 常量错误（0x8001 是 DESTROY，真正的 CREATE 是 0x8000 —— 预定位从未生效过）；
    ///         状态上报加仲裁器诊断（CursorArbiterArmed/LocalInputIdleMs）
    ///         抢窗判据改为"本机 12 秒内动过即拒"（v1.1.2 的输入先后比较被远端悬停鼠标持续刷新打穿）
    /// </summary>
    public const string Version = "1.5.20";

    private readonly AgentConfig _cfg;
    private readonly Logger _log;
    private readonly int _sessionId;
    /// <summary>引擎 STA 线程号：停止指令来自桥的分发线程，得靠它把 WM_QUIT 投回消息循环所在线程</summary>
    private readonly uint _staThreadId;

    private readonly WorkerBridge _bridge;
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
    /// <summary>主控端是否在线（协调器每秒通过 IpcBitrateHint 告知——不能用"远端没动鼠标"代替）</summary>
    private volatile bool _viewerOnline;
    /// <summary>主控端从有到无的时刻（TickCount64；0=当前在线或从未收到过状态）——"无人观看暂停"用</summary>
    private long _viewerOfflineSince;
    /// <summary>是否收到过协调器的 BitrateHint（没收到过就绝不敢暂停——老协调器配对时保持旧行为）</summary>
    private volatile bool _everGotViewerHint;
    /// <summary>最近收到的 BitrateHint 序号（金丝雀回显：协调器对照本地序号判断单向断流）</summary>
    private long _lastHintSeq;
    private long _lastReclaimTick;
    private long _lastVddRestartTick;
    private long _nextTopoCheckMs;
    private long _topoFailBackoffMs = 30_000;
    /// <summary>虚拟外屏缺位（驱动故障/被拔）：置位期间**暂停出帧**，绝不把物理屏当外屏拍（2026-10-09 隐私事故）</summary>
    private bool _virtualMissing;
    private long _nextVirtualMissingReportMs;
    /// <summary>最近一次见过的虚拟外屏设备名：缺位看护按它找回设备（_virtualScreen 会被回退成物理屏，名字会丢）</summary>
    private string _virtualDeviceName = "";
    /// <summary>最近一次健康显示布局的快照（config\display-layout.json）</summary>
    private static string DisplayLayoutPath =>
        Path.Combine(AppContext.BaseDirectory, "config", "display-layout.json");
    /// <summary>上一次"因为本机用户在操作所以丢掉了远端按键"的提示时间</summary>
    private long _lastKeyBlockedNotifyTick;
    private long _lastGateSourceLogTick;
    private readonly Dictionary<string, int> _launched = new(StringComparer.OrdinalIgnoreCase);

    public WorkerContext(AgentConfig cfg, Logger log, int sessionId, WorkerBridge bridge)
    {
        _cfg = cfg;
        _log = log;
        _sessionId = sessionId;
        _bridge = bridge;
        _staThreadId = NativeApi.GetCurrentThreadId();   // 构造发生在引擎 STA 线程上（见 WorkerHost）
        _injector = new InputInjector(log);
        _windows = new WindowManager(log);
    }

    public void Start()
    {
        _bridge.MessageFromCoordinator += OnMessage;
        _log.Info($"========== 采集引擎启动（版本 {Version}，会话 {_sessionId}，用户 {Environment.UserName}）==========");
        _log.Info($"显示拓扑：{string.Join(" | ", DisplayHelper.GetMonitors())}");
        _log.Info($"窗口站={DesktopHelper.GetWindowStationName()} 当前桌面={DesktopHelper.GetThreadDesktopName()} " +
                  $"输入桌面={DesktopHelper.GetInputDesktopName()} 在输入桌面上={DesktopHelper.IsOnInputDesktop()}");

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
        // 外屏与物理屏重叠（物理屏换分辨率/驱动重建后的常见残留）必须先归位：
        // 重叠时外屏窗口的左边缘会同时画在物理屏上，本机用户看得见（"窗口一半在主屏一半在副屏"）。
        // EnsureVirtualAdjacent 内部已核实挪动真的生效（核实不了会记日志说明原因）。
        DisplayHelper.EnsureVirtualAdjacent(virt, msg => _log.Info(msg));
        // 布局医生：与最近一次健康快照对照，说清差异（2026-10-08，对标 UU 的 display_layout 体系）
        DisplayLayoutSnapshot.Doctor(DisplayLayoutPath, _log);

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
        // 反向抓取要用"虚拟外屏在哪"（与采集目标解耦：画布模式下采集的是整个桌面，但外屏矩形另有其人）
        _windows.VirtualScreenProvider = () => _virtualScreen;

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
            _bridge.SendToCoordinator(new ProtocolMessage(MessageType.ClipboardText, PayloadCodec.EncodeText(text)));
        _clipboard.FilesCopied += files =>
        {
            foreach (var f in files)
                _bridge.SendToCoordinator(new ProtocolMessage(MessageType.ClipboardFile, JsonCodec.Encode(f)));
        };

        // 采集线程
        _captureThread = new Thread(CaptureLoop) { IsBackground = true, Name = "capture" };
        _captureThread.Start();

        // 专用输入线程（2026-10-09：注入类 Win32 调用从消息泵挪到这里，泵永不因输入卡死）
        StartInputThread();

        // 上报就绪
        var ready = new WorkerReadyInfo
        {
            SessionId = _sessionId,
            Width = _source.Width,
            Height = _source.Height,
            Monitor = _target.DeviceName,
            Backend = _source.Backend,
            Version = Version,
        };
        _bridge.SendToCoordinator(new ProtocolMessage(MessageType.WorkerReady, JsonCodec.Encode(ready)));
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
        // 【2026-10-09 隐私事故】"仅副屏"模式下虚拟外屏缺位（VDD 故障/被系统移除）时，
        // PickCaptureTarget 会退回物理主屏 —— 绝不能把物理屏当成外屏拍下去：
        // 现场后果是主控端直接看到本机用户屏幕（那次看到的是人家正在打的游戏），
        // 而且远端开的窗口全部落到物理屏上。置 _virtualMissing：采集循环暂停出帧并
        // 周期性向主控端报错，拓扑巡检照常自愈（pnputil 拉回 VDD），副屏回来即自动恢复。
        bool wasMissing = _virtualMissing;
        _virtualMissing = _cfg.EnableVirtualDisplay && _captureMode == CaptureMode.VirtualOnly
                          && _virtualScreen is not { IsVirtual: true };
        // 记住虚拟屏设备名：缺位期间 _virtualScreen 会被回退成物理屏，看护要靠名字找回设备
        if (_virtualScreen is { IsVirtual: true })
            _virtualDeviceName = _virtualScreen.DeviceName;
        if (_virtualMissing && !wasMissing)
            _log.Error("虚拟外屏缺位（VDD 故障或被移除）：暂停出帧保护本机用户屏幕，自愈进行中；" +
                       "主控端将看到暂停提示而非物理屏画面");
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
        long nextIdleLogMs = 0;
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
                // 仅副屏模式也要盯拓扑（2026-10-07）：本机用户改物理屏分辨率/接拔屏后，
                // 虚拟外屏可能留在旧坐标上、与物理屏交叠 → 副屏窗口左半边画到物理屏上
                // （"窗口一半在主屏一半在副屏"）。每 2 秒核一次外屏矩形与重叠关系。
                if (!_captureAll && sw.ElapsedMilliseconds >= _nextTopoCheckMs)
                {
                    long topoNow = sw.ElapsedMilliseconds;
                    _nextTopoCheckMs = topoNow + 2000;
                    var virt = _virtualScreen;
                    if (virt is { IsVirtual: true })
                    {
                        var cur = DisplayHelper.GetMonitors()
                            .FirstOrDefault(m => m.DeviceName == virt.DeviceName);
                        if (cur != null && !DisplayHelper.OverlapsAnyPhysical(cur))
                        {
                            // 布局健康：随手存一份快照（有变化才写盘，内部自节流）
                            DisplayLayoutSnapshot.SaveIfChanged(DisplayLayoutPath);
                        }
                        if (cur == null)
                        {
                            _log.Warn($"虚拟外屏 {virt.DeviceName} 消失，重新挑采集目标");
                            // 自愈（2026-10-08）：VDD 显示器自发消失时重启驱动设备节点把它拉回来，
                            // 10 分钟节流防抖。物理屏若也被 GPU 重置带走了，这救不了——那需要重启机器。
                            if (_cfg.EnableVirtualDisplay &&
                                topoNow - _lastVddRestartTick > 600_000)
                            {
                                _lastVddRestartTick = topoNow;
                                DisplayHelper.TryRestartVirtualDisplayDriver(_cfg.VirtualDisplayDriverId,
                                    msg => _log.Info(msg));
                            }
                            _needCaptureSwitch = true;   // BuildCaptureTarget 会重新 PickCaptureTarget
                        }
                        else if (cur.Left != virt.Left || cur.Top != virt.Top ||
                                 cur.Width != virt.Width || cur.Height != virt.Height)
                        {
                            _log.Info($"虚拟外屏位置/尺寸变化：({virt.Left},{virt.Top} {virt.Width}x{virt.Height}) → " +
                                      $"({cur.Left},{cur.Top} {cur.Width}x{cur.Height})，重建采集");
                            _virtualScreen = cur;
                            _windows.SetCaptureTarget(cur);
                            _topoFailBackoffMs = 30_000;   // 拓扑真的动了：重置归位退避
                            _needCaptureSwitch = true;
                        }
                        else if (DisplayHelper.OverlapsAnyPhysical(cur))
                        {
                            if (DisplayHelper.EnsureVirtualAdjacent(cur, msg => _log.Info(msg)))
                            {
                                var moved = DisplayHelper.GetMonitors()
                                    .FirstOrDefault(m => m.DeviceName == cur.DeviceName);
                                if (moved != null)
                                {
                                    _windows.ShiftWindowsWithVirtualScreen(cur, moved);
                                    _virtualScreen = moved;
                                    _windows.SetCaptureTarget(moved);
                                }
                                _topoFailBackoffMs = 30_000;
                                _needCaptureSwitch = true;
                            }
                            else
                            {
                                // 挪不动（驱动/同机远控软件锁死位置）：指数退避 30s→30min，
                                // 别每 2 秒白试一次（每次核实要 Sleep 600ms，会卡采集）
                                _nextTopoCheckMs = topoNow + _topoFailBackoffMs;
                                _topoFailBackoffMs = Math.Min(_topoFailBackoffMs * 2, 1_800_000);
                            }
                        }
                    }
                    else if (_virtualMissing)
                    {
                        // 【2026-10-09 修复】外屏缺位看护：_virtualScreen 已被回退成物理屏，
                        // 上面那段（含 pnputil 自愈）全被跳过 —— VSAQ 现场虚拟屏就此一去不回。
                        // 按记住的设备名继续找：回来了就重建采集；还缺着就照旧 pnputil 拉驱动（节流）。
                        var cur = DisplayHelper.GetMonitors()
                            .FirstOrDefault(m => m.DeviceName == _virtualDeviceName);
                        if (cur != null)
                        {
                            _log.Info($"虚拟外屏 {_virtualDeviceName} 已恢复，重建采集");
                            _needCaptureSwitch = true;
                        }
                        else if (topoNow - _lastVddRestartTick > 600_000)
                        {
                            _lastVddRestartTick = topoNow;
                            DisplayHelper.TryRestartVirtualDisplayDriver(_cfg.VirtualDisplayDriverId,
                                msg => _log.Info(msg));
                        }
                    }
                }
                if (_needStreamReset)
                {
                    _needStreamReset = false;
                    RestartEncoder("请求重建码流");
                }
                // 【2026-10-09 隐私事故】虚拟外屏缺位：暂停出帧（绝不把物理屏当外屏拍），
                // 每 10s 向主控端报一次错。拓扑巡检在上方照常自愈，副屏回来自动恢复。
                if (_virtualMissing)
                {
                    if (sw.ElapsedMilliseconds >= _nextVirtualMissingReportMs)
                    {
                        _nextVirtualMissingReportMs = sw.ElapsedMilliseconds + 10_000;
                        _log.Warn("虚拟外屏缺位：暂停出帧（保护本机用户屏幕），自愈进行中");
                        _bridge.SendToCoordinator(new ProtocolMessage(MessageType.Error,
                            PayloadCodec.EncodeText("虚拟外屏丢失，画面暂停；正在自动恢复，请稍候重试")));
                    }
                    Thread.Sleep(500);
                    continue;
                }
                // 没人看就别烧机器：主控端全部断开 ≥10 秒后暂停采集与编码。
                // 被控端在客户机上 7x24 挂着，无人观看还全程跑 WGC+ffmpeg，
                // 弱机器就"换一台电脑特别卡、断开后开软件都慢"（2026-10-09 现场反馈）。
                // 暂停期间 ffmpeg 空转等输入（近零开销）、拓扑自愈照常跑；
                // 一有主控端接入（BitrateHint / 码流重建）立即恢复。
                var offlineSince = Interlocked.Read(ref _viewerOfflineSince);
                if (_everGotViewerHint && !_viewerOnline && offlineSince > 0 &&
                    Environment.TickCount64 - offlineSince > 10_000)
                {
                    if (sw.ElapsedMilliseconds >= nextIdleLogMs)
                    {
                        nextIdleLogMs = sw.ElapsedMilliseconds + 300_000;
                        _log.Info("无主控端在线 ≥10 秒，暂停采集编码（有连接立即恢复）");
                    }
                    Thread.Sleep(500);
                    continue;
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
            _bridge.SendToCoordinator(new ProtocolMessage(MessageType.StreamReset, PayloadCodec.Empty()));
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
            _bridge.SendToCoordinator(new ProtocolMessage(MessageType.StreamReset, PayloadCodec.Empty()));
        }
        catch (Exception ex)
        {
            _log.Error("重建编码流异常", ex);
        }
    }

    /// <summary>本机用户刚动过键鼠多久之内，远端按键先不放行（够短，不会影响正常远端打字）</summary>
    private const long RemoteKeyBlockWindowMs = 300;   // 收紧：800ms 假阳性面太大（2026-10-08 现场：无真人的机器持续被拦）

    /// <summary>远端按键因为本机用户正在操作而被丢弃 → 限流告知主控端（10s 最多一条）</summary>
    private void NotifyRemoteKeysBlocked()
    {
        long now = Environment.TickCount64;
        long last = Interlocked.Read(ref _lastKeyBlockedNotifyTick);
        if (now - last < 10_000) return;
        if (Interlocked.CompareExchange(ref _lastKeyBlockedNotifyTick, now, last) != last) return;
        _log.Info("本机用户正在操作，本次远端按键已丢弃（避免打进本机用户的前台窗口）");
        _bridge.SendToCoordinator(new ProtocolMessage(MessageType.Error,
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
            if (!_bridge.Frames.Write(new ReadOnlySpan<byte>(buffer, 0, length), timeoutMs: 250))
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
                QueueCoalescedMove(x, y);
                break;
            }
            case MessageType.MouseButton:
            {
                Interlocked.Exchange(ref _lastRemoteInputTick, Environment.TickCount64);
                var (x, y, btn, up) = PayloadCodec.DecodeMouseButton(msg.Payload);
                RunOnInputThread("MouseButton", () => _router?.Button(x, y, btn, up));
                break;
            }
            case MessageType.MouseWheel:
            {
                Interlocked.Exchange(ref _lastRemoteInputTick, Environment.TickCount64);
                var (x, y, d) = PayloadCodec.DecodeMouseWheel(msg.Payload);
                RunOnInputThread("MouseWheel", () => _router?.Wheel(x, y, d));
                break;
            }
            case MessageType.KeyEvent:
            {
                Interlocked.Exchange(ref _lastRemoteInputTick, Environment.TickCount64);
                var payload = msg.Payload;
                RunOnInputThread("KeyEvent", () => HandleKeyEvent(payload));
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
                // 【2026-10-09 隐私事故】虚拟外屏缺位时拒绝启动：此时"外屏坐标"就是物理屏坐标，
                // 窗口会直接出现在本机用户眼前（现场：远端点开微信，微信弹在人家游戏旁边）。
                // 拒绝并把原因说给主控端，而不是把窗口开到物理屏上。
                if (_cfg.EnableVirtualDisplay && _virtualScreen is not { IsVirtual: true })
                {
                    _log.Warn($"打开软件被拒绝（{exe}）：虚拟外屏缺位，窗口会落到物理屏上");
                    SendSoftwareState(exe, -1, false);
                    _bridge.SendToCoordinator(new ProtocolMessage(MessageType.Error,
                        PayloadCodec.EncodeText("虚拟外屏未就绪，暂不能打开软件；正在自动恢复，请稍候重试")));
                    break;
                }
                // 「此电脑」（主控端软件列表置顶项）：在资源管理器里打开，窗口摆到外屏
                int pid = string.Equals(exe, WindowManager.ThisPcMarker, StringComparison.OrdinalIgnoreCase)
                    ? _windows.OpenThisPC()
                    : _windows.Open(exe, args);
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
                // 有主控端要接入：立即解除"无人观看暂停"（不等下一个 BitrateHint）
                _viewerOnline = true;
                Interlocked.Exchange(ref _viewerOfflineSince, 0);
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
                _viewerOnline = hint?.ViewerOnline ?? false;
                _everGotViewerHint = true;
                Interlocked.Exchange(ref _lastHintSeq, hint?.Seq ?? 0);   // 金丝雀回显（单向断流检测）
                if (_viewerOnline) Interlocked.Exchange(ref _viewerOfflineSince, 0);
                else if (Interlocked.Read(ref _viewerOfflineSince) == 0)
                    Interlocked.Exchange(ref _viewerOfflineSince, Environment.TickCount64);
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
                _log.Info("收到停止指令，采集引擎退出");
                _stop = true;
                // 注意：本回调跑在**桥的分发线程**上，而消息循环在本引擎的 STA 线程上 ——
                // 在别的线程上调 ExitThread() 退不了那条循环（原独立进程时代这里就是主线程，没有这问题）。
                // 正确做法：往引擎线程投递 WM_QUIT，让 Application.Run 自然返回、走 Dispose 收尾。
                NativeApi.PostThreadMessage(_staThreadId, NativeApi.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
                break;
            default:
                _log.Debug($"忽略消息 0x{(byte)msg.Type:X2}");
                break;
        }
    }

    /// <summary>
    /// 看门狗硬重启入口（2026-10-09）：给引擎 STA 线程投 WM_QUIT。
    /// 与 Stop 指令同一条路，但可从协调器/桥看门狗直接调用——分发线程卡死时
    /// 消息进不来，必须有人从外面踢门。线程若卡在钩子回调里收不到消息，
    /// WM_QUIT 会排队等它回来，栈转储已先落在日志里。
    /// </summary>
    public void RequestHardExit()
    {
        _log.Warn("收到硬退出请求（泵看门狗/状态断流监视）：投 WM_QUIT 给引擎线程");
        _stop = true;
        NativeApi.PostThreadMessage(_staThreadId, NativeApi.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
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

                // 副屏孤儿窗口回收：主控端离线时，把所有"整块落在副屏上"的可见窗口搬回主屏。
                // 为什么需要这个（除了 SHOW 事件上的反向抓取）：Telegram 等从托盘恢复**不触发 SHOW**
                // （窗口一直是"可见"的，只是不在前台），SHOW 路径抓不到 → 客户反馈"tg 第一次打开不回主屏"。
                // 周期检查每秒跑一次，条件更严（要求主控端离线 ≥10 秒），避免跟远端操作打架。
                // 孤儿回收：主控端离线时把副屏窗口搬回主屏（客户反馈"断开后此电脑留在副屏"）。
                // 【2026-10-07 重新启用】改为 10 秒一轮 + 只在主控端离线时跑（性能可控）。
                if (!_viewerOnline && now - _lastReclaimTick > 10_000)
                {
                    _lastReclaimTick = now;
                    _windows.ReclaimOrphanWindowsFromVirtual();
                }

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

                // Win+R 会话期：运行框 + 它启动出来的程序都摆到外屏。
                // 两道闸门缺一不可（少一道就会出现"本机用户一开微信就被搬到副屏"）：
                //   ① 远端**刚刚**在操作（3 秒内）—— 命令敲下去后程序一般 1~3 秒就起来了；
                //   ② 本机用户此刻没在动键鼠 —— 他动手时一个窗口都不碰（他在自己开程序）。
                MoveRunDialogToVirtualIfExpected();
                // 判据用**远端键盘**（而不是"远端动过鼠标"）：新程序只可能由 Win+R / 敲命令带出来，
                // 而鼠标是随时在动的 —— 用鼠标当判据会导致"本机用户一开应用就被搬走"（现场反馈）。
                bool remoteTyping = now - Interlocked.Read(ref _lastRemoteKeyTick) < 5000;
                bool localBusy = _arbiter?.LocalActiveWithin(2000) ?? false;
                if (now < Interlocked.Read(ref _expectRunDialogUntil) && remoteTyping && !localBusy)
                {
                    int n = _windows.SweepNewWindowsFromPhysical(remoteActive: true, localActive: false);
                    if (n > 0) _log.Info($"Win+R 期间把 {n} 个新窗口摆到了外屏");
                }

                // 本机用户点任务栏把外屏窗口叫起来 → 搬回主屏（默认关闭，见 AgentConfig.ReturnWindowOnLocalClick：
                // 开着会把远端正在用的窗口抢回主屏，现场反馈过，所以做成开关且默认关）
                if (_cfg.ReturnWindowOnLocalClick) MoveForegroundBackToPrimaryIfLocal();
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

    // ---------------------------------------------------------------- 专用输入线程（2026-10-09 二次卡死事故）

    /// <summary>输入动作队列（泵线程只入队，绝不在这里跑 Win32 注入）</summary>
    private System.Threading.Channels.Channel<(string What, Action Work)>? _inputQueue;
    private Thread? _inputThread;
    /// <summary>当前输入动作开始时刻（0=空闲）：卡死检测依据</summary>
    private long _inputBusySinceTick;
    private string _inputBusyWhat = "";
    private long _inputThreadReplacedCount;
    private long _lastInputThreadReplaceTick;

    /// <summary>
    /// 【2026-10-09 二次卡死事故】SendInput/键鼠路由/前台切换这类 Win32 调用会被同机安全软件、
    /// 反作弊（ACE 等）或钩子链**无限期卡死**——现场：用户操作过一轮之后，主控端→被控端的
    /// 打开软件/剪贴板/切画面全部失灵 40+ 分钟（消息泵被注入调用堵死，画面照常）。
    /// 所以注入类消息一律转交**专用输入线程**串行执行：
    ///   · 泵线程只入队（永不阻塞）→ 控制通道（打开软件/状态/剪贴板）永不被输入拖死；
    ///   · 输入线程卡死 &gt; 5s：记日志、丢弃积压、换新线程（旧线程泄漏在内核调用里，可接受）；
    ///   · 30s 内最多换一次线程（防卡死风暴无限泄漏）。
    /// </summary>
    private void RunOnInputThread(string what, Action work)
    {
        var q = _inputQueue;
        if (q == null) return;
        q.Writer.TryWrite((what, work));
    }

    /// <summary>
    /// 【2026-10-10 拖拽修复】鼠标移动**合并**：主控端以 60~125Hz 发移动事件，串行注入跟不上时
    /// 队列越积越旧——表现为"整个画面慢一拍"、拖拽时抬起都发生了光标还没走到位（拖拽落点全错）。
    /// 远程桌面的标准做法：移动事件永远只注入**最新**坐标，中间的过期点直接丢（视觉无差别，
    /// 光标轨迹本来就画不过来）；按下/抬起/滚轮/键盘仍走队列严格保序。
    /// 实现：最新坐标存槽位；队列里最多只有一个"移动槽"，处理时取槽内最新值。
    /// </summary>
    private readonly object _moveGate = new();
    private int _latestMoveX, _latestMoveY;
    private bool _moveSlotQueued;

    private void QueueCoalescedMove(int x, int y)
    {
        lock (_moveGate)
        {
            _latestMoveX = x;
            _latestMoveY = y;
            if (_moveSlotQueued) return;     // 队列里已有移动槽：更新最新坐标即可，过期点自然作废
            _moveSlotQueued = true;
        }
        var q = _inputQueue;
        if (q == null) { lock (_moveGate) _moveSlotQueued = false; return; }
        q.Writer.TryWrite(("MouseMove", () =>
        {
            int mx, my;
            lock (_moveGate)
            {
                mx = _latestMoveX;
                my = _latestMoveY;
                _moveSlotQueued = false;
            }
            _router?.Move(mx, my);
        }));
    }

    private void StartInputThread()
    {
        _inputQueue = System.Threading.Channels.Channel.CreateUnbounded<(string, Action)>(
            new System.Threading.Channels.UnboundedChannelOptions { SingleReader = true });
        _inputThread = new Thread(InputLoop) { IsBackground = true, Name = "input-exec" };
        _inputThread.Start();
    }

    private void InputLoop()
    {
        var q = _inputQueue;
        if (q == null) return;
        while (!_stop)
        {
            (string What, Action Work) item;
            try
            {
                if (!q.Reader.TryRead(out item))
                {
                    var wait = q.Reader.WaitToReadAsync().AsTask();
                    if (!wait.Wait(1000)) continue;
                    if (!q.Reader.TryRead(out item)) continue;
                }
            }
            catch { return; }

            Interlocked.Exchange(ref _inputBusySinceTick, Environment.TickCount64);
            _inputBusyWhat = item.What;
            long start = Environment.TickCount64;
            try { item.Work(); }
            catch (Exception ex) { _log.Warn($"输入处理异常（{item.What}）：{ex.Message}"); }
            finally { Interlocked.Exchange(ref _inputBusySinceTick, 0); }
            long cost = Environment.TickCount64 - start;
            if (cost > 1000)
                _log.Warn($"⚠ 输入动作 {item.What} 耗时 {cost / 1000.0:F1}s（疑似被安全软件/钩子拖住）");
        }
    }

    /// <summary>输入线程卡死自愈：由状态线程每秒核一次（5s 卡死 → 换线程；30s 节流）</summary>
    private void CheckInputThreadHealth()
    {
        long busy = Interlocked.Read(ref _inputBusySinceTick);
        if (busy == 0) return;
        long stuckMs = Environment.TickCount64 - busy;
        if (stuckMs < 5000) return;
        long now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastInputThreadReplaceTick) < 30_000) return;
        Interlocked.Exchange(ref _lastInputThreadReplaceTick, now);
        Interlocked.Increment(ref _inputThreadReplacedCount);
        _log.Error($"⚠⚠ 输入线程卡死 {stuckMs / 1000.0:F0}s（正在执行 {_inputBusyWhat}，累计换线程 " +
                   $"{_inputThreadReplacedCount} 次）——丢弃积压输入并更换输入线程。常见元凶：反作弊/安全软件" +
                   "拦截 SendInput、钩子链无响应。控制通道不受影响。");
        // 旧队列连同积压一起废弃（过期输入没有重放价值），起全新线程
        try { _inputQueue?.Writer.TryComplete(); } catch { }
        StartInputThread();
    }

    /// <summary>
    /// 键盘事件处理（从 OnMessage 的 KeyEvent case 原样搬来：注入类逻辑统一在专用输入线程上跑）。
    /// </summary>
    private void HandleKeyEvent(byte[] payload)
    {
        // 只有 Win+R 会开启"把运行框挪到外屏"的短窗口期 —— 别的窗口一律不动（产品要求）
        if (payload.Length >= 3)
        {
            var (vkRun, upRun, _) = PayloadCodec.DecodeKey(payload);
            long nowTick = Environment.TickCount64;
            if (!upRun && (vkRun == 0x5B || vkRun == 0x5C))
                Interlocked.Exchange(ref _winKeyDownTick, nowTick);
            else if (!upRun && (vkRun == 0x52 /*R*/) &&
                     nowTick - Interlocked.Read(ref _winKeyDownTick) < 3000)
            {
                // 按 Win+R = 开一段"会话期"：先给当前窗口拍快照，
                // 之后新出现的窗口（运行框本身 + 它启动出来的程序）一律去外屏；
                // 快照里原有的窗口一个都不动（"除非我让它过去，否则不准动"）。
                _windows.SnapshotBaseline();
                Interlocked.Exchange(ref _expectRunDialogUntil, nowTick + 60000);
                _log.Info("远端按下 Win+R：接下来 60 秒内新出现的窗口（含它启动的程序）都会摆到外屏");
            }
        }
        var (vk, up, flags) = PayloadCodec.DecodeKey(payload);
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
            return;
        }

        if (_arbiter != null && _arbiter.LocalActiveWithin(RemoteKeyBlockWindowMs))
        {
            long nowB = Environment.TickCount64;
            if (nowB - _lastGateSourceLogTick > 1000)
            {
                _lastGateSourceLogTick = nowB;
                _log.Warn($"远端按键被本机活跃闸门拦下：来源={_arbiter.LastPhysicalSource}");
            }
            NotifyRemoteKeysBlocked();
            return;
        }
        // 后台注入模式下窗口不会被点击激活，所以要显式把"远端点过的那个窗口"带前台，
        // 否则这一串按键会打进本机用户的前台窗口（本机用户在忙时上面已经拦掉了）。
        if (_router != null && !_router.EnsureClaimedForeground())
        {
            NotifyRemoteKeysBlocked();
            return;
        }
        // 前台窗口输入法英文化（3 秒节流）：真实光标模式下点进已有窗口不会经过
        // ForceActivateWindow，中文模式的组字框会吃掉后续按键（2026-10-08 现场）
        _windows.EnsureImeEnglishForForeground();
        lock (_remoteKeysDown) _remoteKeysDown.Add(vk);
        _injector.Key(vk, up, flags);
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
        _bridge.SendToCoordinator(new ProtocolMessage(MessageType.SoftwareState, JsonCodec.Encode(info)));
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
        // 每块屏在画面里的位置：主控端据此把物理屏画成"只读区域"（看得到、点不到）。
        // 【键盘兼容 2026-10-07】仅副屏模式下**不发送物理屏条目**：旧版主控端检查
        // _readOnlyRegions.Count > 0 就拦键盘，只要 Screens 列表里有物理屏（IsVirtual=false）
        // 就会拦截。不发物理屏条目 → _readOnlyRegions 为空 → 旧版键盘也能打字。
        // 坐标映射用的是上面的顶层 Left/Top/Width/Height，不依赖 Screens 列表。
        foreach (var m in monitors)
        {
            if (!_captureAll && !m.IsVirtual && _captureMode == CaptureMode.VirtualOnly)
                continue;   // 仅副屏模式：跳过物理屏，旧版主控端键盘就不拦了
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
        _bridge.SendToCoordinator(new ProtocolMessage(MessageType.MonitorInfo, JsonCodec.Encode(info)));
    }

    private void SendStatus()
    {
        if (_stop) return;
        try
        {
            CheckInputThreadHealth();   // 输入线程卡死自愈（每秒核一次，借状态线程的节拍）
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
                HintSeq = Interlocked.Read(ref _lastHintSeq),   // 金丝雀：协调器据此发现单向断流
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
                // 抢窗闸门的诊断：仲裁器在不在岗 + 本机物理输入空闲多久（-1=无仲裁器 → 闸门失效）
                CursorArbiterArmed = _arbiter is { Armed: true },
                LocalInputIdleMs = _arbiter == null ? -1 : Environment.TickCount64 - _arbiter.LastPhysicalInputTick,
            };
            _bridge.SendToCoordinator(new ProtocolMessage(MessageType.WorkerStatus, JsonCodec.Encode(status)));
        }
        catch { }
    }

    protected override void Dispose(bool disposing)
    {
        _stop = true;
        // 退订桥事件：桥的生命周期跟 Coordinator（比引擎长），引擎重启后是新的 WorkerContext，
        // 不退订的话旧的（已 Dispose 的）实例还会收到消息、跟新实例抢输入。
        _bridge.MessageFromCoordinator -= OnMessage;
        try { _statusTimer?.Dispose(); } catch { }
        try { _arbiter?.Dispose(); } catch { }
        try { _clipboard?.Dispose(); } catch { }
        try { _encoder?.Dispose(); } catch { }
        try { _source?.Dispose(); } catch { }
        _log.Info($"采集引擎收尾完成（运行 {_uptime.Elapsed.TotalSeconds:F0}s，采集 {_framesCaptured} 帧）");
        base.Dispose(disposing);
    }
}
