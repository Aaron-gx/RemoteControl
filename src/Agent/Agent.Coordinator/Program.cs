using System.Diagnostics;
using System.Text;
using Agent.Common;

// 兼容旧多会话逃生门：本文件仍引用已废弃的独立会话配置字段(见 AppConfig.cs [Obsolete])，
// 抑制 CS0618 警告，保留 AllowLegacyRdpSession 逃生能力。产品定稿为共享模式。
#pragma warning disable CS0618

namespace Agent.Coordinator;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        DisplayHelper.EnablePerMonitorDpiAwareness();

        using var mutex = new Mutex(true, @"Global\RemoteControlAgentCoordinator", out bool isNew);
        if (!isNew)
        {
            // 已有实例在跑：静默退出（可通过日志确认）
            var l = new Logger("coordinator", minLevel: LogLevel.Info);
            l.Warn("Agent Coordinator 已在运行，本次启动退出");
            return 2;
        }

        ApplicationConfiguration.Initialize();

        CoordinatorApp? app = null;
        try
        {
            app = new CoordinatorApp(args);
            app.Start();
            Application.Run();   // 托盘消息循环
            return 0;
        }
        catch (Exception ex)
        {
            try
            {
                Logger.Current?.Error("未处理异常导致退出", ex);
                MessageBox.Show($"Agent Coordinator 启动失败：\n{ex.Message}", "远程控制 Agent",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { }
            return 1;
        }
        finally
        {
            try { app?.Dispose(); } catch { }
            try { mutex.ReleaseMutex(); } catch { }
        }
    }
}

/// <summary>
/// Coordinator 主逻辑（Session 0）：
/// 托盘 → 虚拟副屏 → RDP 会话 → 启动 Worker → 中继 WebSocket ↔ IPC 双向桥接
/// </summary>
public sealed class CoordinatorApp : IDisposable
{
    private readonly AgentConfig _cfg;
    private readonly Logger _log;
    private readonly IpcServer _ipc;
    private readonly RelayClient _relay;
    private readonly RdpSessionManager _rdp;
    private readonly VirtualDisplayManager _vdd;
    private readonly SoftwareEnumerator _sw;
    private readonly FileUploader _uploader;
    private readonly DirectLinkServer _direct;
    private readonly LicenseGate _license;
    private TrayIcon? _tray;
    private readonly CancellationTokenSource _cts = new();
    private readonly string[] _args;

    private int _workerSessionId = -1;
    private int _workerPid = -1;
    /// <summary>经中继发出的视频块数（直连生效后应停止增长；上报给主控端用于显示与验收）</summary>
    private long _relayVideoChunks;
    /// <summary>上一份状态里"被挤掉的视频块数"，用来判断这一秒有没有真丢</summary>
    private long _lastVideoDropped;
    /// <summary>当前配对的主控端数量（中继推送；>1 时视频统一走中继）</summary>
    private int _viewerCount;
    /// <summary>上次拉起 Worker 的时间：防止"看护 + 掉线重试"同时拉起两个 Worker 打架（会 0 帧）</summary>
    private DateTime _lastWorkerLaunchUtc = DateTime.MinValue;
    /// <summary>操作系统描述（缓存，避免每秒都查注册表）</summary>
    private string? _osVersion;
    private MonitorInfo? _monitor;
    private WorkerReadyInfo? _workerReady;
    private DateTime _lastViewerActivity = DateTime.MinValue;
    /// <summary>最近一次确认会话可用的时间（看护宽限期用）</summary>
    private DateTime _lastSessionOkUtc = DateTime.MinValue;
    private DateTime _lastInputWarn = DateTime.MinValue;
    private readonly object _workerLock = new();
    /// <summary>单实例互斥体（持有到进程退出；两个被控端同时跑会各装一个全局鼠标钩子互相打架）</summary>
    private Mutex? _singleInstance;

    public CoordinatorApp(string[] args)
    {
        _args = args;
        _cfg = AgentConfig.Load();
        if (args.Contains("--no-rdp")) _cfg.UseRdpSession = false;
        if (args.Contains("--no-vdd")) _cfg.EnableVirtualDisplay = false;

        _log = new Logger("coordinator", minLevel: _cfg.MinLogLevel, echoConsole: false);
        _log.Info("========== Agent Coordinator 启动 ==========");

        // 单实例保护：同一台机器只允许一个被控端在跑。
        // 为什么必须有：两个实例会各装一个全局鼠标钩子（光标仲裁）、各自注入输入 ——
        // 实测症状就是"在副屏点击，主屏的鼠标也跟着点、按钮点不到、拖不动"，
        // 而且互相抢窗口与共享帧环，极难排查（升级时装到了另一个目录就会有这种局面）。
        // 用 Global 前缀覆盖所有会话；WaitOne(0) 拿不到说明已有活着的实例。
        _singleInstance = new Mutex(false, @"Global\RemoteControlCoordinator");
        bool firstInstance;
        try { firstInstance = _singleInstance.WaitOne(0, false); }
        catch (AbandonedMutexException) { firstInstance = true; }   // 上一个实例异常退出，我们接管
        if (!firstInstance)
        {
            _log.Warn("已有被控端在运行，本次启动退出（单实例保护）。请先从托盘退出旧实例，再启动这个。");
            Console.Error.WriteLine("已有被控端在运行，本次启动退出（单实例保护）。");
            Environment.Exit(0);
        }
        _log.Info($"版本 1.0.0 / AgentId={_cfg.AgentId} / 进程会话={SessionLauncher.CurrentSessionId} / 管理员={SessionLauncher.IsElevated}");
        _log.Info($"配置：UseRdpSession={_cfg.UseRdpSession} EnableVirtualDisplay={_cfg.EnableVirtualDisplay} " +
                  $"Server={_cfg.ServerUrl} WorkerUser={_cfg.WorkerUser} FPS={_cfg.FrameRate} 编码器={_cfg.Encoder}");
        if (!string.IsNullOrEmpty(_cfg.ModeCorrection))
            _log.Warn(_cfg.ModeCorrection);
        if (_cfg.UseRdpSession)
            _log.Warn($"正在运行**旧的多会话方案**（AllowLegacyRdpSession=true）：会新建/使用本地用户 {_cfg.WorkerUser} " +
                      "和 RDP 回环会话，该账户与本机管理员的桌面、微信、文件都不共享。产品本意是共享模式。");

        KillStrayWorkers();   // 先清理上一轮残留的 Worker（否则新旧两个会抢管道与共享帧环 → 0 帧）
        _ipc = new IpcServer(_log, _cfg.FrameBufferSizeBytes);
        _ipc.RingCreated += usedLabel =>
            _log.Info(usedLabel
                ? "跨会话 IPC 安全描述符已带低完整性标签（普通用户可访问）"
                : "警告：跨会话 IPC 未带低完整性标签（缺 SeSecurityPrivilege），RDP 会话内的 Worker 可能被拒绝");
        _relay = new RelayClient(_cfg, _log);
        _rdp = new RdpSessionManager(_cfg, _log);
        _vdd = new VirtualDisplayManager(_cfg, _log);
        _sw = new SoftwareEnumerator(_log, _cfg.ReportSoftwareIcons);
        _uploader = new FileUploader(_cfg, _log);
        _direct = new DirectLinkServer(_cfg, _log);
        _license = new LicenseGate(Path.Combine(AppContext.BaseDirectory, "config", "license.cache.json"), _log);
        // 直连握手先过授权闸门：授权不可用时不允许任何人通过 P2P 接进来
        _direct.LicenseCheck = () => _license.IsUsable && _cfg.DirectLinkEnabled;
    }


    /// <summary>
    /// 清理"上一次运行残留"的 Agent.Worker（同一个安装目录里的）。
    /// 为什么需要：重启协调器时，旧 Worker 可能还活着并抢先连上命名管道，
    /// 协调器随后又拉起一个新的 → 两个 Worker 抢同一个共享帧环，表现为画面 0 帧（实测缺陷）。
    /// 只杀自己安装目录里的 Worker，绝不动别人的进程。
    /// </summary>
    /// <summary>操作系统一句话描述（形如 Windows 11 Pro 24H2 build 26100）</summary>
    private static string DescribeOs()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var product = k?.GetValue("ProductName")?.ToString() ?? "Windows";
            var display = k?.GetValue("DisplayVersion")?.ToString() ?? "";
            var build = Environment.OSVersion.Version.Build;
            return $"{product} {display} build {build}".Replace("  ", " ").Trim();
        }
        catch { return Environment.OSVersion.VersionString; }
    }

    private void KillStrayWorkers()
    {
        try
        {
            var mine = Path.GetFullPath(AppContext.BaseDirectory);
            foreach (var p in Process.GetProcessesByName("Agent.Worker"))
            {
                try
                {
                    var path = p.MainModule?.FileName;
                    if (string.IsNullOrEmpty(path) ||
                        !Path.GetFullPath(path).StartsWith(mine, StringComparison.OrdinalIgnoreCase)) continue;
                    _log.Warn($"清理残留 Worker：pid={p.Id}（{path}）");
                    p.Kill(entireProcessTree: true);
                }
                catch { }
                finally { p.Dispose(); }
            }
        }
        catch (Exception ex) { _log.Debug($"清理残留 Worker 失败：{ex.Message}"); }
    }

    public void Start()
    {
        // 设置包含 SACL（完整性标签）的安全描述符需要该特权，否则 CreateFileMapping/CreateEvent 会失败
        SessionLauncher.EnablePrivilege("SeSecurityPrivilege", _log);

        _tray = new TrayIcon(_log, $"远程控制 Agent\n{_cfg.AgentId}", new TrayHooks
        {
            DirectStatusText = DirectStatusText,
            DirectEnabled = () => _cfg.DirectLinkEnabled,
            SetDirectEnabled = SetDirectEnabled,
            DirectAddressText = () => string.IsNullOrEmpty(_cfg.DirectLinkPublicAddress)
                ? (_direct.PublicAddress ?? "") : _cfg.DirectLinkPublicAddress,
            SetDirectPublicAddress = SetDirectPublicAddress,
        });
        _tray.ExitRequested += () => Shutdown();

        EnsureBootAutostart();

        _ipc.WorkerConnected += OnWorkerConnected;
        _ipc.WorkerDisconnected += OnWorkerDisconnected;
        _ipc.WorkerMessage += OnWorkerMessage;
        _ipc.FrameReceived += OnFrame;
        _ipc.Start();

        _relay.Connected += OnRelayConnected;
        _relay.Disconnected += OnRelayDisconnected;
        _relay.MessageReceived += OnViewerMessage;
        // 校验不通过时静默停服：托盘/界面/上报都不出现"授权/激活码"字样
        // （用户不需要知道机制；排查靠本机日志与服务器端状态）
        _relay.LicenseRejected += reason =>
        {
            _log.Error($"服务器校验未通过，暂停服务：{reason}");
            _license.LockFromServer(reason);      // 触发 LockDown（停直连/停 Worker/拒新握手）
        };
        _relay.LicenseVerified += code => _license.UpdateFromServer(code);
        _relay.ViewerCountChanged += n =>
        {
            if (n == _viewerCount) return;
            _viewerCount = n;
            _log.Info($"当前主控端数量：{n}" + (n > 1
                ? "（多于一个时视频统一走中继，保证每个主控端都有画面；P2P 直连暂时挂起）"
                : ""));
            // 多于一个主控端时把直连也停掉：否则直连那一路会继续"只认直连"而看不到中继画面
            if (n > 1) _direct.StopServing("当前有多个主控端，视频改走中继");
        };
        _license.LockedOut += LockDown;
        _license.Tick();
        StartLicenseWatch();
        _ = Task.Run(() => WatchSoftwareStatesAsync(_cts.Token));   // 软件运行状态实时监测（3 秒一轮）
        _relay.InboundTraffic += OnViewerTraffic;
        _relay.Start();

        if (_direct.Start())
        {
            // 候选地址定期上报（UPnP 映射是异步完成的，所以要周期推送）
            _ = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    try
                    {
                        if (HasViewer)
                        {
                            if (!string.IsNullOrWhiteSpace(_cfg.DirectLinkPublicAddress))
                                _direct.SetManualPublic(_cfg.DirectLinkPublicAddress);
                            _relay.Queue(new ProtocolMessage(MessageType.DirectCandidates, _direct.CandidatesJson()));
                        }
                        await Task.Delay(TimeSpan.FromSeconds(15), _cts.Token);
                    }
                    catch (OperationCanceledException) { return; }
                    catch { }
                }
            });
        }

        _ = Task.Run(BootstrapAsync);
        StartDiagnostics();
        if (_cfg.UseRdpSession) StartSessionWatchdog();
        else StartWorkerWatchdog();
    }

    /// <summary>
    /// 共享模式的 Worker 看护：Worker 死掉（崩溃/被结束/被安全软件拦/被前台应用带崩）后必须**一直**
    /// 尝试拉起 —— 否则主控端会停在"连上了、但黑屏且点不动"的状态，直到有人去被控端重启程序（实测踩到）。
    /// 另外 Worker 缺失超过 30 秒就报给主控端，别让人对着黑屏猜。
    /// </summary>
    private void StartWorkerWatchdog()
    {
        _ = Task.Run(async () =>
        {
            var missingSince = DateTime.MinValue;
            while (!_cts.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(10), _cts.Token); } catch { return; }
                if (_cts.IsCancellationRequested) return;
                try
                {
                    if (_ipc.IsWorkerConnected)
                    {
                        missingSince = DateTime.MinValue;
                        continue;
                    }
                    if (missingSince == DateTime.MinValue) missingSince = DateTime.UtcNow;
                    if (!_license.IsUsable)
                    {
                        _log.Debug($"服务已暂停，暂不启动 Worker：{_license.LockReason}");
                        continue;
                    }
                    if ((DateTime.UtcNow - missingSince).TotalSeconds >= 30)
                    {
                        // 起不来就告诉主控端"为什么黑"；每 30 秒重申一次，不刷屏
                        ReportError("被控端采集进程未运行（正在自动重启），画面与操作暂时不可用");
                        missingSince = DateTime.UtcNow;
                    }
                    _workerSessionId = SessionLauncher.CurrentSessionId;   // 共享模式永远在本机当前会话
                    _log.Info("Worker 未连接，自动拉起");
                    LaunchWorker();
                }
                catch (Exception ex) { _log.Warn($"Worker 看护异常：{ex.Message}"); }
            }
        });
    }

    /// <summary>
    /// RDP 会话看护（每 20 秒）：mstsc 崩溃/被关掉、会话掉到"已断开"时自动重连，
    /// 并保证 Worker 在会话里运行（断开的会话没有显示表面，采集必然失败）。
    /// </summary>
    private void StartSessionWatchdog()
    {
        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(20), _cts.Token); } catch { return; }
                if (_cts.IsCancellationRequested) return;
                try
                {
                    // mstsc 的"安全警告/证书"对话框会把登录卡住，看护周期内顺手确认一次
                    _rdp.DismissBlockingDialogs();

                    // 宽限期只在"会话确实还在"时生效：会话消失（注销/掉线）必须尽快重建，
                    // 否则要白等 3 分钟；而"正在建立中"的判断由 EnsureSessionAlive 内部的
                    // 观察窗口 + 建会话锁负责，这里不需要再挡。
                    bool sessionPresent = _rdp.WorkerSessionId > 0 && _rdp.IsSessionActive(_rdp.WorkerSessionId);
                    if (sessionPresent && DateTime.UtcNow - _lastSessionOkUtc < TimeSpan.FromSeconds(30)) continue;

                    if (!_rdp.EnsureSessionAlive())
                    {
                        // 建会话流程进行中不要报错打扰主控端（内部有 150s 观察窗口）
                        if (!_rdp.IsEstablishing) ReportError("RDP 会话不可用，正在重试");
                        continue;
                    }
                    _lastSessionOkUtc = DateTime.UtcNow;
                    // 会话可能是看护自己重建的，且**会话号会变**（2 → 3 …），
                    // 不刷新就会拿旧会话号去启动 Worker，永远失败 → 黑屏。
                    if (_rdp.WorkerSessionId > 0 && _rdp.WorkerSessionId != _workerSessionId)
                        _workerSessionId = _rdp.WorkerSessionId;
                    // 授权停机期间不要拉起 Worker（会话本身可以留着，续期后直接复用）
                    if (!_license.IsUsable)
                    {
                        _log.Debug($"服务已暂停，暂不启动 Worker：{_license.LockReason}");
                        continue;
                    }
                    if (!_ipc.IsWorkerConnected) LaunchWorker();
                }
                catch (Exception ex) { _log.Warn($"会话看护异常：{ex.Message}"); }
            }
        });
    }

    /// <summary>
    /// 授权闸门关闭时的**硬停机**：停直连（含已在连的主控端）、停 Worker、拒绝新的直连握手。
    /// 这是防"屏蔽中继走 P2P 白嫖"的关键一步 —— 只靠服务器判定的话，P2P 流量不过服务器就管不到。
    /// 中继连接保持重试，续期后会自动恢复。
    /// </summary>
    private void LockDown(string reason)
    {
        try
        {
            _log.Error($"【服务暂停】{reason}");
            _direct.StopServing("服务暂停");       // 清掉已接入的直连主控端并停止监听
            KillWorker();                            // 没有授权就不采集、不编码
            // 不弹托盘气泡、不向主控端报错：界面上不暴露原因
            // 不弹托盘气泡、不向主控端报错：界面上不暴露原因
            // 不弹托盘气泡、不向主控端报错：界面上不暴露原因
        }
        catch (Exception ex) { _log.Warn($"停机处理异常：{ex.Message}"); }
    }

    private void KillWorker()
    {
        try
        {
            if (_workerPid > 0)
            {
                using var p = Process.GetProcessById(_workerPid);
                p.Kill(entireProcessTree: true);
                _log.Warn($"已结束 Worker（pid={_workerPid}）");
            }
        }
        catch (ArgumentException) { }
        catch (Exception ex) { _log.Warn($"结束 Worker 失败：{ex.Message}"); }
        _workerPid = -1;
    }

    /// <summary>
    /// 授权看护（每 60 秒）：
    ///  1. 本地判到期/时钟回拨；
    ///  2. **问一次服务器有没有被撤销** —— 本地看不出"厂商把这条码删了"，
    ///     而撤销必须让机器停下来（否则客户拿着作废的码继续用，还能走 P2P 直连）；
    ///  3. 授权不可用时定期重连中继，这样在服务器上装好新激活码后，
    ///     被控端**不用人工重启**就能自动恢复。
    /// </summary>
    private void StartLicenseWatch()
    {
        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(60), _cts.Token); } catch { return; }
                if (_cts.IsCancellationRequested) return;
                try
                {
                    _license.Tick();

                    // 就算本地看着"还在有效期内"，也要确认一下是不是被厂商撤销了。
                    // 探测失败（断网/超时/接口异常）当作"不知道"，绝不停机。
                    var probe = await RelayLicenseProbe.QueryAsync(_cfg.ServerUrl, TimeSpan.FromSeconds(8), _cts.Token);
                    if (probe.Known)
                    {
                        // 探到了就说明这一轮联系上服务器了 —— 刷新离线宽限的计时
                        _license.NoteServerContact();
                        if (probe.State == "revoked")
                        {
                            _log.Error($"服务器判定该激活码已被撤销：{probe.Reason}");
                            _license.LockFromServer(string.IsNullOrWhiteSpace(probe.Reason)
                                ? "该激活码已被厂商撤销"
                                : probe.Reason);
                        }
                    }

                    if (_license.IsUsable) continue;

                    // 停机中：每轮都试着连一次中继，拿到新激活码就能自动活过来
                    _log.Info("重试连接服务器以恢复服务…");
                    _relay.Restart();
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { _log.Debug($"授权看护异常：{ex.Message}"); }
            }
        });
    }

    /// <summary>每 5 秒打一行链路诊断（帧转发速率 / 发送积压 / 环形缓冲水位）</summary>
    private void StartDiagnostics()
    {
        _ = Task.Run(async () =>
        {
            long lastFrames = 0, lastQueued = 0, lastDropped = 0, lastVideoDropped = 0;
            while (!_cts.IsCancellationRequested)
            {
                try { await Task.Delay(5000, _cts.Token); } catch { return; }
                try
                {
                    long f = _ipc.FramesForwarded;
                    long q = _relay.QueuedCount, d = _relay.DroppedCount;
                    long vd = _relay.VideoDropped;
                    double fill = _ipc.Ring?.FillRatio ?? 0;
                    _log.Info($"[诊断] 转发帧 {f - lastFrames}/5s  发送积压={_relay.PendingSends}（视频 {_relay.VideoPending}）" +
                              $"本周期入队={q - lastQueued} 控制丢弃={d - lastDropped} 视频挤掉={vd - lastVideoDropped}  " +
                              $"环形水位={fill * 100:F1}%  " +
                              $"主控端={(HasViewer ? "在线" : "无")}  " +
                              $"直连={(_direct.HasViewer ? $"已建立({_direct.ViewerEndpoint}, {_direct.FramesSent}帧/{_direct.BytesSent / 1024}KB)" : (_direct.UpnpMapped ? "待接入(UPnP已映射)" : "未建立"))}");
                    lastFrames = f; lastQueued = q; lastDropped = d; lastVideoDropped = vd;
                    // 托盘悬停也能看到服务器与直连状态（右键菜单里还有更详细的）。
                    // 连不上时把**原因**写出来：口令不对和网络不通在用户那里表现完全一样，
                    // 不写清楚就只能靠猜（现场踩过：装完一直不在线）。
                    _tray?.SetTooltip(_relay.IsConnected
                        ? $"服务器√ 直连(可选)：{DirectStatusText()}"
                        : $"服务器× {_relay.LastFailure}（会自动重试）\n直连(可选)：{DirectStatusText()}");
                }
                catch (Exception ex) { _log.Debug($"诊断异常：{ex.Message}"); }
            }
        });
    }

    // ---------------------------------------------------------------- 启动流程

    private async Task BootstrapAsync()
    {
        try
        {
            // 连接口令为空是"装完一直不在线"最常见的原因（安装时没填、或中继启用了鉴权而口令不对）——
            // 这种事必须在启动日志里说清楚，否则现场只能看到"被控端在运行但列表里没有它"
            if (string.IsNullOrWhiteSpace(_cfg.AgentToken))
                _log.Warn("本机没有配置连接口令（agent.json 的 AgentToken 为空）：中继服务器若启用了鉴权，" +
                          "本机会一直显示「未连接服务器」、主控端列表里也看不到本机。\n" +
                          "  改法：重新运行安装程序并在「连接设置」里填入口令，或改 agent.json 的 AgentToken 与服务端 RC_TOKEN 一致后重启被控端。");

            // 1. RDP 服务/防火墙
            if (_cfg.UseRdpSession)
            {
                _log.Info("[1/4] 检查远程桌面服务…");
                _rdp.EnsureRdpEnabled();
                bool wrapInstalled = _rdp.IsRdpWrapInstalled();
                bool wrapWorking = _rdp.IsRdpWrapWorking();
                var (iniOk, iniDetail) = _rdp.GetRdpWrapIniSupport();
                _log.Info($"RDPWrap：已安装={wrapInstalled} 已生效={wrapWorking} ini={iniDetail}");
                if (!wrapInstalled)
                {
                    _tray?.SetTooltip("远程控制 Agent（RDPWrap 未安装）");
                    ReportError("RDPWrap 未安装：无法创建并发会话，请先运行 scripts\\install-agent.ps1");
                }
                else if (!wrapWorking)
                {
                    ReportError($"RDPWrap 未生效（127.0.0.2:3389 未监听）：{iniDetail}。可能需要更新 rdpwrap.ini");
                }
            }

            // 2. 虚拟副屏
            _log.Info("[2/4] 检查虚拟副屏…");
            if (_vdd.Ensure(out var vddMsg))
                _log.Info($"虚拟副屏：{vddMsg}");
            else
                _log.Warn($"虚拟副屏不可用：{vddMsg}");

            // 3. Worker 会话
            _log.Info("[3/4] 准备 Worker 会话…");
            if (_cfg.UseRdpSession)
            {
                if (_rdp.EnsureWorkerSession(TimeSpan.FromSeconds(_cfg.RdpConnectTimeoutSec),
                        out int sid, out string err))
                {
                    _workerSessionId = sid;
                    _lastSessionOkUtc = DateTime.UtcNow;
                }
                else
                {
                    _log.Error($"RDP 会话不可用：{err}");
                    ReportError($"RDP 会话不可用：{err}");
                }
            }
            else
            {
                _workerSessionId = SessionLauncher.CurrentSessionId;
                // 共享模式（默认）：远程操作的就是本机已登录用户这个桌面 + 虚拟外屏，
                // 软件/文件/微信等跟本机用户是同一份（同一个账户、同一个 profile）。
                _log.Info($"共享模式：Worker 运行在本机当前会话 {_workerSessionId}（远程 = 本机已登录桌面 + 虚拟外屏）");
            }

            // 4. 启动 Worker
            _log.Info("[4/4] 启动 Worker…");
            LaunchWorker();
        }
        catch (Exception ex)
        {
            _log.Error("启动流程异常", ex);
            ReportError($"Agent 启动异常：{ex.Message}");
        }
        await Task.CompletedTask;
    }

    private void LaunchWorker()
    {
        if (!_license.IsUsable)
        {
            _log.Warn($"服务已暂停（{_license.LockReason}）：暂不启动 Worker");
            return;
        }
        string exe = Path.Combine(AppContext.BaseDirectory, _cfg.WorkerExe);
        if (!File.Exists(exe))
        {
            _log.Error($"Worker 不存在：{exe}");
            ReportError($"Worker 不存在：{exe}");
            return;
        }
        lock (_workerLock)
        {
            if (_ipc.IsWorkerConnected)
            {
                _log.Info("Worker 已连接，跳过启动");
                return;
            }
            // 刚拉起过就不要重复拉：schtasks 启动到 Worker 连上管道有 1~3 秒延迟，
            // 这期间另一个调用方（看护 / 掉线重试）会以为没起来 → 拉出第二个 Worker
            // → 两个 Worker 抢同一个共享帧环，表现为画面 0 帧（实测缺陷）。
            if (DateTime.UtcNow - _lastWorkerLaunchUtc < TimeSpan.FromSeconds(20))
            {
                _log.Info($"刚拉起过 Worker（{(_lastWorkerLaunchUtc):HH:mm:ss}），跳过重复启动");
                return;
            }
            // 会话由看护重建时 id 只在 RdpSessionManager 里（而且会话号会变），这里回填
            if (_rdp.WorkerSessionId > 0 && _rdp.WorkerSessionId != _workerSessionId)
                _workerSessionId = _rdp.WorkerSessionId;
            if (_workerSessionId < 0)
            {
                _log.Warn("尚未确定 Worker 会话，等待后续重试");
                return;
            }

            string args = $"--session={_workerSessionId} --parent={Environment.ProcessId}";
            if (_workerSessionId == SessionLauncher.CurrentSessionId)
            {
                // 同会话：直接启动
                var psi = new ProcessStartInfo(exe, args)
                {
                    WorkingDirectory = AppContext.BaseDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                var p = Process.Start(psi);
                _workerPid = p?.Id ?? -1;
                _lastWorkerLaunchUtc = DateTime.UtcNow;
                _log.Info($"已在当前会话启动 Worker，pid={_workerPid}");
                return;
            }

            int pid = SessionLauncher.Launch(_workerSessionId, exe, args, _log, out string method);
            if (pid > 0)
            {
                _workerPid = pid;
                _lastWorkerLaunchUtc = DateTime.UtcNow;
                _log.Info($"Worker 已启动：session={_workerSessionId} pid={pid} 方式={method}");
            }
            else
            {
                _log.Error($"在会话 {_workerSessionId} 启动 Worker 失败（所有方式都失败）");
                ReportError($"启动 Worker 失败（会话 {_workerSessionId}）");
            }
        }
    }

    // ---------------------------------------------------------------- 中继事件

    /// <summary>
    /// 中继是透传的，Agent 无法从"自己连上服务器"推断主控端在场。
    /// 判定方式：最近 6 秒内收到过主控端的任何入站消息（主控端每 0.5~1 秒发心跳；心跳也会触发本事件）。
    /// </summary>
    private bool HasViewer =>
        DateTime.UtcNow - _lastViewerActivity < TimeSpan.FromSeconds(6);

    private void OnViewerTraffic()
    {
        bool firstInAWhile = !HasViewer;
        _lastViewerActivity = DateTime.UtcNow;
        if (firstInAWhile) OnViewerAttached();
    }

    /// <summary>
    /// 确保"开机自启"任务存在，不存在就以 **开机触发 + SYSTEM + 最高权限** 注册一个。
    ///
    /// 为什么在被控端里兜这一层：早期安装脚本注册的是"登录时启动、并且绑定安装时那个用户"，
    /// 被控机器只要停在登录界面（或重启后没人登录）就永远起不来 —— 实测踩到过：
    /// 机器重启后 4 小时都没上线，服务器上"没有在线被控端"。另外用拷贝文件部署的机器压根没有任务。
    /// 跑过一次的机器之后就能自己回来。
    /// 已存在则不动（避免覆盖运维自己改过的配置）；没有管理员权限时只记一行日志。
    /// </summary>
    private void EnsureBootAutostart()
    {
        // 配置里关掉自启的机器（例如厂商自己的调试机）不碰任务计划
        if (!_cfg.AutoStartOnBoot)
        {
            _log.Debug("配置里 AutoStartOnBoot=false，跳过开机自启检查");
            return;
        }
        const string TaskName = "RemoteControlAgent";
        try
        {
            var exe = Path.Combine(AppContext.BaseDirectory, "Agent.Coordinator.exe");
            if (!File.Exists(exe)) return;

            var query = new ProcessStartInfo("schtasks.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "/query", "/tn", TaskName }) query.ArgumentList.Add(a);
            using (var q = Process.Start(query))
            {
                if (q == null) return;
                q.StandardOutput.ReadToEnd();
                q.StandardError.ReadToEnd();
                q.WaitForExit(5000);
                if (q.ExitCode == 0) return;    // 已有任务，不动
            }

            var create = new ProcessStartInfo("schtasks.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "/create", "/tn", TaskName, "/tr", exe, "/sc", "onstart", "/ru", "SYSTEM", "/rl", "highest", "/f" })
                create.ArgumentList.Add(a);
            using var c = Process.Start(create);
            if (c == null) return;
            var output = c.StandardOutput.ReadToEnd() + c.StandardError.ReadToEnd();
            c.WaitForExit(8000);
            if (c.ExitCode == 0) _log.Info($"已注册开机自启任务（{TaskName}：开机自动启动、SYSTEM 最高权限）");
            else _log.Warn($"注册开机自启失败（无管理员权限时属正常）：{output.Trim()}");
        }
        catch (Exception ex)
        {
            _log.Debug($"检查开机自启任务异常：{ex.Message}");
        }
    }

    private void OnRelayConnected()    {
        _license.NoteServerContact();      // 连上中继 = 联系上服务器，离线宽限重新计时
        _log.Info("中继连接已建立，等待主控端接入");
        // 托盘提示里带上连接状态：连不上服务器时用户一眼能看出来（不用翻日志）
        _tray?.SetTooltip($"远程控制 Agent（{_cfg.AgentId}）\n已连接服务器");
    }

    private void OnRelayDisconnected(string reason)
    {
        _log.Info($"中继连接断开：{reason}");
        // 断开原因写进托盘：口令不对时用户重装填一次口令就能解决，网络问题只能等 ——
        // 这两件事在界面上本来完全一样，都是"不在线"
        var why = _relay.AuthRejected ? "口令不一致或未填（见日志）" : "网络/服务器不可用";
        _tray?.SetTooltip($"远程控制 Agent（{_cfg.AgentId}）\n未连接服务器（{why}，会自动重试）\n直连(可选)：{DirectStatusText()}");
    }

    /// <summary>主控端刚接入：推送显示器信息 + 软件列表，并让 Worker 重建码流（保证从关键帧开始）</summary>
    private void OnViewerAttached()
    {
        _log.Info("检测到主控端接入");
        PushMonitorInfo();
        PushSoftwareListAsync();
        _ipc.Send(new ProtocolMessage(MessageType.StreamReset, PayloadCodec.Empty()));
        _relay.Queue(new ProtocolMessage(MessageType.DirectCandidates, _direct.CandidatesJson()));
    }

    private DateTime _lastListPush = DateTime.MinValue;

    /// <summary>枚举并上报软件列表（2 秒内重复请求会被合并，避免重复枚举）</summary>
    private void PushSoftwareListAsync()
    {
        if (DateTime.UtcNow - _lastListPush < TimeSpan.FromSeconds(2)) return;
        _lastListPush = DateTime.UtcNow;
        _ = Task.Run(() => SendSoftwareList(_sw.Enumerate().ToArray()));
    }

    private void OnViewerMessage(ProtocolMessage msg)
    {
        switch (msg.Type)
        {
            case MessageType.RequestSoftwareList:
                // 主控端每次连接都会发这条（相当于"我上线了"）：必须把接入状态一整套推给它，
                // 否则"上一个主控端刚断开不到 6 秒"时会被 HasViewer 判定为"仍然在线"而跳过推送，
                // 表现为新主控端画面一直是黑的（实测踩到）
                _log.Info("收到软件列表请求（视为主控端上线）");
                OnViewerAttached();
                break;

            case MessageType.MouseMove:
            case MessageType.MouseButton:
            case MessageType.MouseWheel:
            case MessageType.KeyEvent:
            case MessageType.ClipboardText:
            case MessageType.OpenSoftware:
            case MessageType.CloseSoftware:
            case MessageType.StreamReset:
            case MessageType.InputModeHint:
            case MessageType.CaptureModeHint:
            case MessageType.MoveWindowHint:
            case MessageType.MoveAppWindowHint:
            case MessageType.Disconnect:
                if (_ipc.IsWorkerConnected)
                {
                    if (msg.Type == MessageType.InputModeHint && msg.Payload.Length > 0)
                        _log.Info($"主控端选择鼠标模式：{(RemoteInputMode)msg.Payload[0]}");
                    if (msg.Type == MessageType.CaptureModeHint && msg.Payload.Length > 0)
                        _log.Info($"主控端选择画面范围：{(CaptureMode)msg.Payload[0]}");
                    _ipc.Send(msg);
                }
                else if (msg.Type is MessageType.OpenSoftware or MessageType.CloseSoftware)
                {
                    ReportError("Worker 未连接，无法执行软件操作");
                }
                else
                {
                    WarnRateLimited($"Worker 未连接，丢弃 {msg.Type}");
                }
                break;

            default:
                _log.Debug($"忽略来自主控端的消息类型 0x{(byte)msg.Type:X2}");
                break;
        }
    }

    // ---------------------------------------------------------------- IPC 事件

    private void OnWorkerConnected(int _)
    {
        _log.Info("Worker 连接成功，等待就绪上报");
        // 注意：这里**不要**立刻让 Worker 枚举软件（枚举耗时数秒、且在主控端没接入时毫无意义），
        // 实测该调用会让 Worker 在连接后 ~4 秒退出（IPC 读取循环被阻塞/中断），
        // 表现为"Worker 起来就死 → 画面 0 帧"。软件列表改由主控端接入时按需请求（OnViewerAttached/PushSoftwareList）。
        _log.Debug("（跳过启动时的软件列表预取，改为主控端接入时按需请求）");
    }

    private void OnWorkerDisconnected(string reason)
    {
        _log.Warn($"Worker 断开：{reason}");
        if (!_cfg.RestartWorkerOnExit || _cts.IsCancellationRequested) return;
        _ = Task.Run(async () =>
        {
            for (int i = 1; i <= 5 && !_cts.IsCancellationRequested; i++)
            {
                await Task.Delay(TimeSpan.FromSeconds(5 * i));
                if (_ipc.IsWorkerConnected) return;
                // RDP 会话不在或正在重建时不要抢：会话没了要重登才有显示表面，
                // 这里拿旧会话号重试只会刷一串失败日志。交给会话看护，会话好了它会拉起 Worker。
                if (_cfg.UseRdpSession &&
                    (_rdp.IsEstablishing || !_rdp.IsSessionActive(_rdp.WorkerSessionId)))
                {
                    _log.Info("Worker 已退出但 RDP 会话不可用，交由会话看护重建后再拉起");
                    return;
                }
                _log.Info($"尝试重启 Worker（第 {i} 次）");
                LaunchWorker();
                await Task.Delay(TimeSpan.FromSeconds(8));
                if (_ipc.IsWorkerConnected) return;
            }
        });
    }

    private void OnWorkerMessage(ProtocolMessage msg)
    {
        switch (msg.Type)
        {
            case MessageType.WorkerReady:
            {
                _workerReady = JsonCodec.Decode<WorkerReadyInfo>(msg.Payload);
                _log.Info($"Worker 就绪：{_workerReady?.Backend} {_workerReady?.Width}x{_workerReady?.Height} " +
                          $"monitor={_workerReady?.Monitor} session={_workerReady?.SessionId}");
                _tray?.SetTooltip($"远程控制 Agent（Worker 已就绪 {_workerReady?.Width}x{_workerReady?.Height}）");
                PushMonitorInfo();
                break;
            }
            case MessageType.MonitorInfo:
            {
                _monitor = JsonCodec.Decode<MonitorInfo>(msg.Payload);
                if (_monitor != null)
                {
                    _log.Info($"远程显示器：{_monitor.Width}x{_monitor.Height} @({_monitor.Left},{_monitor.Top}) count={_monitor.Count}");
                    SendToViewer(msg);
                }
                break;
            }
            case MessageType.ClipboardText:
                _log.Debug($"剪贴板文本（{msg.Payload.Length} 字节）→ 主控端");
                SendToViewer(msg);
                break;

            case MessageType.ClipboardFile:
            {
                // Worker 传的是本地文件路径；这里负责上传到服务器再通知主控端
                var file = JsonCodec.Decode<SingleFilePayload>(msg.Payload);
                if (file != null) _ = Task.Run(() => UploadAndNotifyAsync(file));
                break;
            }

            case MessageType.StreamReset:
                // Worker 重建了码流 → 1) 丢弃共享内存里旧码流的残帧 2) 让主控端同步重建解码器
                DrainFrameRing();
                _log.Info("Worker 重建码流，已清空残帧并通知主控端重建解码器");
                SendToViewer(msg);
                break;

            case MessageType.WorkerStatus:
            {
                // 补上"视频实际走了哪条路"的计数：主控端据此显示直连/中继，
                // 验收脚本据此断言 P2P 是否真的生效（中继块数应停止增长）。
                var st = JsonCodec.Decode<WorkerStatusInfo>(msg.Payload) ?? new WorkerStatusInfo();
                st.DirectFrames = _direct.FramesSent;
                st.DirectBytes = _direct.BytesSent;
                st.DirectActive = _direct.HasViewer;
                st.RelayChunks = Interlocked.Read(ref _relayVideoChunks);
                // 中继这条腿的积压/丢弃：主控端据此显示"网络上是不是堵了"
                st.RelayQueued = _relay.VideoPending;
                st.RelayDropped = _relay.VideoDropped;
                // 视频出口拥塞 → 立刻告诉 Worker 降码率。
                // Worker 自己看不到中继这条腿有多堵（它只看得到帧环），所以这个信号必须由协调器给。
                bool congested = _relay.VideoPending >= 4 || _relay.VideoDropped > _lastVideoDropped;
                _lastVideoDropped = _relay.VideoDropped;
                _ipc.Send(new ProtocolMessage(MessageType.IpcBitrateHint, JsonCodec.Encode(new BitrateHintInfo
                {
                    Congested = congested,
                    VideoQueued = _relay.VideoPending,
                    VideoDropped = _relay.VideoDropped,
                })));
                // 授权状态随状态上报一起发出（界面不展示，供厂商侧排障/工具使用）
                var lp = _license.Payload;
                st.LicenseId = lp?.Lic ?? "";
                st.LicenseSub = lp?.Sub ?? "";
                st.LicenseExpUnix = lp?.Exp ?? 0;
                st.LicenseDaysLeft = lp != null ? lp.DaysLeft() : 0;
                st.LicenseLocked = !_license.IsUsable;
                st.LicenseLockReason = _license.LockReason;
                // 主动上报自身信息，省得主控端只知道一个 ID
                st.HostName = Environment.MachineName;
                st.UserName = Environment.UserName;
                st.OsVersion = _osVersion ??= DescribeOs();
                st.AgentVersion = _workerReady?.Version ?? "1.0.0";
                SendToViewer(new ProtocolMessage(MessageType.WorkerStatus, JsonCodec.Encode(st)));
                break;
            }

            case MessageType.SoftwareState:
                SendToViewer(msg);
                break;

            case MessageType.Error:
                _log.Error($"Worker 报错：{PayloadCodec.DecodeText(msg.Payload)}");
                SendToViewer(msg);
                break;

            default:
                _log.Debug($"忽略 Worker 消息 0x{(byte)msg.Type:X2}");
                SendToViewer(msg);
                break;
        }
    }

    /// <summary>丢弃环形缓冲里尚未发送的旧码流残帧（重建码流时用）</summary>
    private void DrainFrameRing()
    {
        try
        {
            int dropped = 0;
            while (_ipc.Ring?.Read(0) != null) dropped++;
            if (dropped > 0) _log.Info($"已丢弃 {dropped} 个旧码流块");
        }
        catch (Exception ex) { _log.Debug($"清空残帧失败：{ex.Message}"); }
    }

    private void OnFrame(byte[] chunk)
    {
        if (!HasViewer) return;   // 无主控端时不做无用功（中继上没有接收方）
        if (!_license.IsUsable) return;   // 授权不可用：一个字节都不往外送（含 P2P）
        var payload = PayloadCodec.EncodeVideoFrame(Environment.TickCount64, chunk);
        var msg = new ProtocolMessage(MessageType.VideoFrame, payload);
        // P2P 直连优先：视频走直连，控制/剪贴板仍走中继；直连断了自动回到中继。
        // 但**有多个主控端时不能只发直连**：那条通道只服务一个主控端，
        // 其他主控端会一直黑屏（实测缺陷）→ 这时统一走中继，人人有画面。
        if (_direct.HasViewer && _viewerCount <= 1)
        {
            _direct.Send(msg);
            return;
        }
        Interlocked.Increment(ref _relayVideoChunks);
        _relay.QueueVideo(msg);
    }

    // ---------------------------------------------------------------- 文件上传

    private sealed class SingleFilePayload
    {
        public string LocalPath { get; set; } = "";
        public string FileName { get; set; } = "";
        public long FileSize { get; set; }
    }

    private async Task UploadAndNotifyAsync(SingleFilePayload file)
    {
        var info = await _uploader.UploadAsync(file.LocalPath, file.FileName);
        if (info == null)
        {
            ReportError($"文件上传失败：{file.FileName}");
            return;
        }
        SendToViewer(new ProtocolMessage(MessageType.ClipboardFile, JsonCodec.Encode(info)));
        _log.Info($"已通知主控端可下载：{info.FileName}（fileId={info.FileId}）");
    }

    // ---------------------------------------------------------------- 发送

    /// <summary>
    /// 把"发给主控端"的消息送出去：**直连（P2P）已建立就优先走直连**，否则回退中继。
    ///
    /// 为什么必须这样：以前只有视频走直连，鼠标键盘、软件列表、状态上报、剪贴板全都挤在中继上。
    /// 一旦中继那条腿出问题（实测：机房侧把出站压到 15KB/s → 主控端下载只有 2KB/s），
    /// "切到 P2P" 就只能救画面，操作和列表照样卡死。现在所有发给主控端的东西都优先走直连，
    /// 直连断了自动回退中继，不需要人工干预。
    /// </summary>
    private void SendToViewer(ProtocolMessage msg)
    {
        if (_direct.HasViewer && _viewerCount <= 1)
        {
            _direct.Send(msg);
            return;
        }
        _relay.Queue(msg);
    }

    /// <summary>把直连候选地址推给主控端（改过公网地址/开关后立刻重推，不用重启）</summary>
    private void PushDirectCandidates()
    {
        try
        {
            _relay.Queue(new ProtocolMessage(MessageType.DirectCandidates, _direct.CandidatesJson()));
        }
        catch (Exception ex) { _log.Debug($"推送直连候选失败：{ex.Message}"); }
    }

    /// <summary>托盘上显示的直连状态（一句话）</summary>
    private string DirectStatusText()
    {
        if (!_cfg.DirectLinkEnabled) return "已关闭";
        if (_direct.HasViewer)
            return $"已建立 ← {_direct.ViewerEndpoint}（{_direct.FramesSent}帧/{_direct.BytesSent / 1024}KB）";
        if (!string.IsNullOrEmpty(_direct.PublicAddress)) return $"待接入（公网 {_direct.PublicAddress}）";
        if (_direct.UpnpMapped) return "待接入（UPnP 已映射）";
        if (_direct.Port > 0) return $"待接入（仅内网 {string.Join("/", _direct.LocalCandidates())}）";
        return "未启动（端口被占用？）";
    }

    /// <summary>托盘：开/关直连。关 = 拒绝新的直连握手；开 = 允许（必要时现场启动监听）。都是立即生效。</summary>
    private void SetDirectEnabled(bool on)
    {
        _cfg.DirectLinkEnabled = on;
        try { _cfg.Save(); } catch (Exception ex) { _log.Warn($"保存配置失败：{ex.Message}"); }
        if (on)
        {
            if (_direct.Port <= 0)
            {
                try { _direct.Start(); }
                catch (Exception ex) { _log.Warn($"启动直连监听失败：{ex.Message}"); }
            }
            _log.Info("托盘：已开启 P2P 直连");
        }
        else
        {
            _direct.StopServing("托盘关闭了直连");
            _log.Info("托盘：已关闭 P2P 直连（监听端口保留，但拒绝新的直连握手）");
        }
        PushDirectCandidates();
    }

    /// <summary>托盘：设置直连公网地址（做完端口映射后填这里，立即生效并重推候选）</summary>
    private void SetDirectPublicAddress(string address)
    {
        var v = (address ?? "").Trim();
        _cfg.DirectLinkPublicAddress = v;
        try { _cfg.Save(); } catch (Exception ex) { _log.Warn($"保存配置失败：{ex.Message}"); }
        if (v.Length == 0)
        {
            _log.Info("托盘：已清空直连公网地址（回退到 UPnP/内网候选；清空要重启被控端才完全生效）");
        }
        else
        {
            _direct.SetManualPublic(v);
            _log.Info($"托盘：直连公网地址已设为 {v}");
        }
        PushDirectCandidates();
    }

    private void PushMonitorInfo()
    {
        MonitorInfo mi;
        if (_monitor != null) mi = _monitor;
        else if (_workerReady != null)
            mi = new MonitorInfo { Width = _workerReady.Width, Height = _workerReady.Height, Count = 1 };
        else return;
        SendToViewer(new ProtocolMessage(MessageType.MonitorInfo, JsonCodec.Encode(mi)));
    }

    /// <summary>上一次发给主控端的软件列表（实时监测拿它做变化比对）</summary>
    private List<SoftwareInfo> _lastSoftwareList = new();
    private readonly object _swListLock = new();

    /// <summary>
    /// 实时运行状态监测：每 3 秒轻量枚举一次进程，**只把有变化的项**推给主控端。
    /// 为什么要有它：光靠"主控端打开/关闭"的事件推状态，用户在被控机上手动开关程序时，
    /// 列表状态就永远不刷新（现场反馈："我都关了它还显示开着"）。
    /// 只推变化项是为了省流量 —— 每 3 秒把 28 项全推一遍在弱网上是浪费。
    /// </summary>
    private async Task WatchSoftwareStatesAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(3000, ct);
                if (!HasViewer) continue;
                List<SoftwareInfo> snapshot;
                lock (_swListLock) snapshot = _lastSoftwareList;
                if (snapshot.Count == 0) continue;

                var (byPath, byName) = SoftwareEnumerator.GetRunningStates();
                var changed = new List<SoftwareStateInfo>();
                foreach (var sw in snapshot)
                {
                    int pid = 0;
                    bool found = byPath.TryGetValue(sw.ExePath, out pid);
                    if (!found && !string.IsNullOrEmpty(Path.GetFileName(sw.ExePath)))
                        found = byName.TryGetValue(Path.GetFileName(sw.ExePath), out pid);
                    bool running = found && pid > 0;
                    if (running == sw.IsRunning && pid == sw.Pid) continue;
                    sw.IsRunning = running;
                    sw.Pid = running ? pid : 0;
                    changed.Add(new SoftwareStateInfo { ExePath = sw.ExePath, Pid = sw.Pid, IsRunning = running });
                }
                foreach (var c in changed)
                    SendToViewer(new ProtocolMessage(MessageType.SoftwareState, JsonCodec.Encode(c)));
                if (changed.Count > 0)
                    _log.Info($"软件运行状态变化 {changed.Count} 项（实时监测，已推给主控端）");
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { _log.Debug($"软件状态监测异常：{ex.Message}"); }
        }
    }

    private void SendSoftwareList(SoftwareInfo[] list)
    {
        var payload = JsonCodec.Encode(list);
        lock (_swListLock) _lastSoftwareList = list.ToList();   // 实时监测拿它做比对
        _log.Info($"上报软件列表：{list.Length} 项（{payload.Length / 1024}KB）");
        SendToViewer(new ProtocolMessage(MessageType.SoftwareList, payload));
    }

    private void ReportError(string message)
    {
        SendToViewer(new ProtocolMessage(MessageType.Error, PayloadCodec.EncodeText(message)));
    }

    private void WarnRateLimited(string msg)
    {
        if (DateTime.UtcNow - _lastInputWarn < TimeSpan.FromSeconds(5)) return;
        _lastInputWarn = DateTime.UtcNow;
        _log.Warn(msg);
    }

    // ---------------------------------------------------------------- 退出

    private void Shutdown()
    {
        _log.Info("收到退出请求，开始关闭…");
        try { _cts.Cancel(); } catch { }

        // 1. 通知 Worker 退出
        try
        {
            if (_ipc.IsWorkerConnected)
            {
                _ipc.Send(new ProtocolMessage(MessageType.Stop, PayloadCodec.Empty()));
                Thread.Sleep(800);
            }
        }
        catch { }

        // 2. 杀掉 Worker 进程
        try
        {
            var procs = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(_cfg.WorkerExe));
            foreach (var p in procs)
            {
                try
                {
                    p.Kill(entireProcessTree: true);
                    _log.Info($"已结束 Worker pid={p.Id}");
                }
                catch { }
                finally { p.Dispose(); }
            }
        }
        catch (Exception ex) { _log.Warn($"结束 Worker 失败：{ex.Message}"); }

        // 3. 注销 RDP 会话 + 关掉 mstsc
        try { _rdp.CloseLoopbackSession(); } catch (Exception ex) { _log.Warn(ex.Message); }

        // 4. 收尾
        _log.Info("Agent Coordinator 已退出");
        try { _tray?.Dispose(); } catch { }
        Application.ExitThread();
    }

    public void Dispose()
    {
        try { _relay.Dispose(); } catch { }
        try { _ipc.Dispose(); } catch { }
        try { _uploader.Dispose(); } catch { }
        try { _direct.Dispose(); } catch { }
        try { _tray?.Dispose(); } catch { }
        _cts.Dispose();
    }
}
