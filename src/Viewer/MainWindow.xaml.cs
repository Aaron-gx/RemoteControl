using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Agent.Common;
using Viewer.Models;
using Viewer.Services;
using Viewer.ViewModels;

namespace Viewer;

/// <summary>主控端主窗口：软件管理 + 远程副屏画面 + 键鼠 + 剪贴板/文件（策划 §3）</summary>
public partial class MainWindow : Window
{
    private readonly ViewerConfig _cfg;
    private readonly Logger _log;
    private readonly MainViewModel _vm = new();
    private readonly WebSocketService _ws;
    private readonly FileDownloader _downloader;
    private VideoDecoder? _decoder;
    private readonly DirectLinkClient _direct = new(Logger.Current ?? new Logger("viewer"));
    private bool _directActive;
    private long _relayVideoBytes;
    private WorkerStatusInfo? _agentStatus;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };

    private readonly List<ClipboardFileInfo> _pendingFiles = new();
    private string _lastLocalClipboard = "";
    private string _suppressClipboard = "";
    private long _framesRendered, _framesAtSample;
    private long _lastSampleTicks, _lastBytes;
    private double _lastKbps;
    private long _lastLatency;
    private HwndSource? _hwndSource;
    private bool _closing;
    /// <summary>启动后的自动连接只做一次（避免每次点「刷新」都抢连）</summary>
    private bool _autoConnectTried;
    /// <summary>中继刚告诉过我们"被控端不在线"（断开提示要说这句而不是泛泛的"未连接"）</summary>
    private bool _agentOffline;
    /// <summary>被控端离线时的面板提示。离线后会一直贴着这句：中继会自动重连，
    /// 但重连成功也只说明"连上了中继"，被控端没回来之前不该改回"正在读取"来回跳</summary>
    private const string OfflineHint = "被控端不在线：点「刷新」看看还有哪台在线";
    /// <summary>被控端是否正处于安全桌面（UAC 提权/锁屏）——只在状态翻转时提示一次</summary>
    private bool _secureDesktop;

    public MainWindow()
    {
        InitializeComponent();
        _cfg = ViewerConfig.Load();
        _log = new Logger("viewer", minLevel: _cfg.MinLogLevel, echoConsole: false);
        _log.Info("========== Viewer 启动 ==========");
        _log.Info($"服务器={_cfg.ServerUrl} 被控端={_cfg.TargetAgentId} 下载目录={_cfg.DownloadDir}");

        DataContext = _vm;
        _ws = new WebSocketService(_log);
        _downloader = new FileDownloader(_log);
        WireEvents();

        ServerBox.Text = _cfg.ServerUrl;
        AgentBox.Text = _cfg.TargetAgentId;
        SelectLinkMode(_cfg.LinkMode);
        Width = Math.Max(900, _cfg.WindowWidth);
        Height = Math.Max(600, _cfg.WindowHeight);
        Screen.ShowPlaceholder("未连接");
        ShowEmptyHint("未连接：点「连接」后显示被控端的软件列表");

        Loaded += OnLoaded;
        Closed += OnClosed;
        CompositionTarget.Rendering += OnRenderTick;
    }

    private string FfmpegPath
    {
        get
        {
            var p = FfmpegLocator.Resolve(_cfg.FfmpegPath);
            if (p == null) _log.Error("未找到 ffmpeg.exe，无法解码远程画面");
            return p ?? "ffmpeg.exe";
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        InstallClipboardListener();
        _ = RefreshAgentsAsync();
        if (_cfg.AutoConnect && !string.IsNullOrWhiteSpace(AgentBox.Text))
            Connect();
    }

    private void WireEvents()
    {
        HelpBtn.Click += (_, _) => new HelpWindow { Owner = this }.ShowDialog();
        LinkModeBox.SelectionChanged += (_, _) =>
        {
            var mode = SelectedLinkMode();
            if (mode == _cfg.LinkMode) return;
            _cfg.LinkMode = mode;
            _cfg.Save();
            _log.Info($"链路偏好改为 {mode}（auto=优先直连 / relay=仅中继 / direct=仅直连）");
            ApplyLinkMode();
        };
        ConnectBtn.Click += (_, _) =>
        {
            if (_ws.IsRunning) Disconnect();
            else Connect();
        };
        RefreshBtn.Click += (_, _) => _ = RefreshAgentsAsync();

        _ws.Connected += () => Dispatcher.Invoke(() =>
        {
            _vm.IsConnected = true;
            _vm.StatusText = "已连接";
            StatusText.Text = "已连接";
            StatusDot.Text = "🟢";
            ConnectBtn.Content = "断开";
            ConnInfo.Text = "";
            Screen.ShowPlaceholder("等待画面…");
            ShowEmptyHint(_agentOffline ? OfflineHint : "已连接，正在读取被控端软件列表…");
            // 请求软件列表
            _ws.Queue(new ProtocolMessage(MessageType.RequestSoftwareList, PayloadCodec.Empty()));
        });

        _ws.Disconnected += reason => Dispatcher.Invoke(() =>
        {
            _vm.IsConnected = false;
            _vm.StatusText = "已断开";
            StatusText.Text = "已断开";
            StatusDot.Text = "🔴";
            ConnectBtn.Content = "连接";
            ConnInfo.Text = "";   // 断开的原因进日志，界面上不铺技术细节
            // P2P 直连只在"中继在线"期间才合法：中继断了（含授权到期被服务器踢掉）就停掉直连，
            // 否则画面会在没有中继/没有授权的情况下继续走直连。
            _direct.Stop();
            _directActive = false;
            _agentStatus = null;
            _secureDesktop = false;
            UpdateLinkModeText();
            _decoder?.Stop();
            Screen.ShowPlaceholder("连接已断开，正在重连…");
            ShowEmptyHint(_agentOffline ? OfflineHint : "未连接：点「连接」后显示被控端的软件列表");
        });

        _ws.StatusText += text => Dispatcher.Invoke(() =>
        {
            _log.Info($"状态：{text}");
            if (text.Contains("被控端不在线")) _agentOffline = true;
        });
        // P2P 直连：被控端上报候选就尝试连（公网优先 → 内网），连上后视频走直连
        _ws.DirectCandidatesReceived += (list, pub) =>
        {
            if (_directActive || list.Count == 0) return;
            if (LinkMode == "relay") return;      // 用户选了"仅中继"：不建立直连
            _ = Task.Run(async () =>
            {
                var ok = await _direct.TryConnectAsync(list, _cfg.TargetAgentId, _cfg.AgentToken,
                    TimeSpan.FromSeconds(2));
                await Dispatcher.InvokeAsync(() =>
                {
                    ConnInfo.Text = ok ? "" : (LinkMode == "direct"
                        ? "直连建立失败：对端当前无法直连，请把「链路」改回「自动」"
                        : "");
                });
            });
        };
        _direct.MessageReceived += msg =>
        {
            if (msg.Type != MessageType.VideoFrame) return;
            var (_, data) = PayloadCodec.DecodeVideoFrame(msg.Payload);
            _lastBytes += data.Length;          // 直连的字节也要计入码率统计（否则走直连时码率显示 0）
            _relayVideoBytes += data.Length;
            EnsureDecoder();
            _decoder!.Feed(data, 0, data.Length);
        };
        _direct.Connected += ep => Dispatcher.Invoke(() =>
        {
            _directActive = true;
            UpdateLinkModeText();
            _log.Info($"P2P 直连已启用（{ep}）：视频不再经过中继");
        });
        _direct.Disconnected += ep => Dispatcher.Invoke(() =>
        {
            _directActive = false;
            UpdateLinkModeText();
            _log.Info($"P2P 直连断开（{ep}），视频回落到中继");
        });

        // 授权相关的判定在后台完成：**界面上不出现任何"激活码/授权"字样**
        // （用户不需要知道；失败时只表现为"连不上/已断开"，详情进日志）
        _ws.LicenseStateChanged += (state, reason, payloadText) => Dispatcher.Invoke(() =>
        {
            if (state == LicenseState.Ok)
            {
                _log.Info($"服务器校验通过：{payloadText}");
                return;
            }
            _log.Error($"服务器校验未通过（{state}）：{reason} —— 已断开");
            Disconnect();
        });
        _ws.LatencyMeasured += ms => Dispatcher.Invoke(() =>
        {
            _lastLatency = ms;
            LatencyText.Text = $"延迟: {ms}ms";
        });
        _ws.MessageReceived += OnRemoteMessage;

        Screen.RemoteMouseMove += (x, y) => SendInputMessage(MessageType.MouseMove, PayloadCodec.EncodeMouseMove((ushort)x, (ushort)y));
        Screen.RemoteMouseButton += (x, y, b, up) => SendInputMessage(MessageType.MouseButton, PayloadCodec.EncodeMouseButton((ushort)x, (ushort)y, b, up));
        Screen.RemoteMouseWheel += (x, y, d) => SendInputMessage(MessageType.MouseWheel, PayloadCodec.EncodeMouseWheel((ushort)x, (ushort)y, d));
        Screen.RemoteKey += (vk, up, flags) => SendInputMessage(MessageType.KeyEvent, PayloadCodec.EncodeKey(vk, up, flags));

        SoftwareList.MouseRightButtonUp += (_, _) =>
        {
            var item = SoftwareList.SelectedItem as RemoteSoftware;
            MenuOpen.IsEnabled = item != null && !item.IsRunning;
            MenuClose.IsEnabled = item != null && item.IsRunning && item.Pid > 0;
        };
        MenuOpen.Click += (_, _) => OpenSelectedSoftware();
        MenuClose.Click += (_, _) => CloseSelectedSoftware();

        PreviewKeyDown += OnPreviewKeyDown;
    }

    // ---------------------------------------------------------------- 连接

    private void Connect()
    {
        _cfg.ServerUrl = ServerBox.Text.Trim();
        _cfg.TargetAgentId = AgentBox.Text.Trim();
        _cfg.Save();
        _agentOffline = false;   // 用户重新发起连接：上一轮的离线结论作废
        _secureDesktop = false;
        if (string.IsNullOrEmpty(_cfg.TargetAgentId))
        {
            MessageBox.Show("请先选择或填写被控端 ID（点「刷新」可列出在线被控端）", "远程控制");
            return;
        }
        _pendingFiles.Clear();
        ClipboardHint.Text = "";
        _vm.AgentText = _cfg.TargetAgentId;
        EnsureDecoder();
        _ws.Token = _cfg.AgentToken;
        _ws.Start(_cfg.ServerUrl, _cfg.TargetAgentId);
    }

    /// <summary>创建解码器并接上"积压丢块 → 重建解码器 + 请求被控端重建码流"的反馈环</summary>
    private void EnsureDecoder()
    {
        if (_decoder != null) return;
        _decoder = new VideoDecoder(FfmpegPath, _log);
        _decoder.StreamResetNeeded += reason => Dispatcher.Invoke(() =>
        {
            _log.Warn($"解码端积压丢块（{reason}）：重建解码器并请求被控端重建码流");
            _decoder!.Reset(reason);
            _ws.Queue(new ProtocolMessage(MessageType.StreamReset, PayloadCodec.Empty()));
        });
    }

    private void Disconnect()
    {
        _direct.Stop();
        _directActive = false;
        _agentStatus = null;
        _ws.Stop();
        _decoder?.Stop();
        UpdateLinkModeText();
        Screen.ShowPlaceholder("已断开");
    }

    /// <summary>
    /// 状态栏链路显示：本地判定 + 被控端上报的帧数（被控端侧计数才是"视频到底走了哪条路"的权威证据）。
    /// 直连正常时中继块数应当不再增长。
    /// </summary>
    private void UpdateLinkModeText()
    {
        // 只显示"当前链路"这一件事；帧数等实现细节不面向用户展示
        LinkModeText.Text = _directActive ? "连接: 直连" : "连接: 中继";
    }

    /// <summary>左侧软件列表为空时给一句话：空面板不解释的话，用户只会以为坏了</summary>
    private void ShowEmptyHint(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            EmptyHint.Visibility = Visibility.Collapsed;
            return;
        }
        EmptyHint.Text = text;
        EmptyHint.Visibility = Visibility.Visible;
    }

    // ---------------------------------------------------------------- 链路偏好（自动/仅中继/仅直连）

    /// <summary>当前链路偏好（自动/仅中继/仅直连）</summary>
    private string LinkMode
    {
        get
        {
            var m = (_cfg.LinkMode ?? "auto").Trim().ToLowerInvariant();
            return m is "relay" or "direct" ? m : "auto";
        }
    }

    private string SelectedLinkMode()
    {
        if (LinkModeBox.SelectedItem is System.Windows.Controls.ComboBoxItem it)
            return (it.Tag as string) ?? "auto";
        return "auto";
    }

    private void SelectLinkMode(string mode)
    {
        int idx = (mode ?? "auto").Trim().ToLowerInvariant() switch { "relay" => 1, "direct" => 2, _ => 0 };
        LinkModeBox.SelectedIndex = idx;
    }

    /// <summary>应用链路偏好：切到"仅中继"时立刻断开已有的直连；切回来则等下一次候选推送重试</summary>
    private void ApplyLinkMode()
    {
        if (LinkMode == "relay" && _directActive)
        {
            _direct.Stop();
            _directActive = false;
            _log.Info("已切到仅中继：断开 P2P 直连，视频改走中继");
        }
        UpdateLinkModeText();
    }

    private async Task RefreshAgentsAsync()
    {
        try
        {
            var baseUrl = _cfg.ResolveFileServerUrl();
            var url = $"{ServerBox.Text.Trim().Replace("ws://", "http://").Replace("wss://", "https://").TrimEnd('/')}";
            var agentsQ = string.IsNullOrWhiteSpace(_cfg.AgentToken) ? "" : $"?token={Uri.EscapeDataString(_cfg.AgentToken)}";
            var uri = baseUrl.Length > 0 ? $"{baseUrl}/api/agents{agentsQ}" : $"{url}/api/agents{agentsQ}";
            var text = await _http.GetStringAsync(uri);
            using var doc = JsonDocument.Parse(text);
            var ids = new List<string>();
            if (doc.RootElement.TryGetProperty("agents", out var arr))
                foreach (var a in arr.EnumerateArray())
                    if (a.TryGetProperty("id", out var id)) ids.Add(id.GetString() ?? "");
            AgentBox.ItemsSource = ids;
            if (ids.Count > 0 && string.IsNullOrWhiteSpace(AgentBox.Text))
                AgentBox.Text = ids[0];
            ConnInfo.Text = ids.Count > 0 ? $"在线被控端 {ids.Count} 个" : "没有在线被控端";
            _log.Info($"在线被控端：{(ids.Count == 0 ? "(无)" : string.Join(", ", ids))}");
            // 打开就该有东西看：在线只有一台时直接接上去（软件列表和画面都是"接上才有"，
            // 否则左侧永远空着，用户只会以为坏了）。多台在线不自动选——那是用户要自己决定的。
            if (!_autoConnectTried && !_ws.IsRunning && ids.Count == 1)
            {
                _autoConnectTried = true;
                AgentBox.Text = ids[0];
                _log.Info($"在线只有 {ids[0]} 一台，自动连接");
                Connect();
            }
        }
        catch (Exception ex)
        {
            // 短句面向用户，具体原因写日志并挂到 ToolTip（原来直接贴异常，长了会被截断）
            var unauthorized = ex.Message.Contains("401") || ex.Message.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase);
            if (unauthorized && string.IsNullOrWhiteSpace(_cfg.AgentToken))
            {
                // 正常分发的主控端包会把令牌预置好（viewer-defaults.txt），走到这里说明包里少了预置值
                ConnInfo.Text = "未配置连接令牌，请联系服务提供方（本机缺少预置的 viewer-defaults.txt）";
            }
            else if (unauthorized)
            {
                ConnInfo.Text = "与服务端的连接令牌不一致，请联系服务提供方";
            }
            else
            {
                ConnInfo.Text = "无法获取被控端列表：请检查服务器地址与网络";
            }
            ConnInfo.ToolTip = ex.Message;
            _log.Error($"获取被控端列表失败：{ex.Message}");
            _log.Warn($"查询 /api/agents 失败：{ex.Message}");
        }
    }

    private void SendInputMessage(MessageType type, byte[] payload)
    {
        if (!_ws.IsConnected) return;
        _ws.Queue(new ProtocolMessage(type, payload));
    }

    // ---------------------------------------------------------------- 远端消息

    private void OnRemoteMessage(ProtocolMessage msg)
    {
        switch (msg.Type)
        {
            case MessageType.VideoFrame:
            {
                var (_, data) = PayloadCodec.DecodeVideoFrame(msg.Payload);
                if (_directActive || LinkMode == "direct")
                {
                    // 直连已启用（或用户选了"仅直连"）：忽略中继来的视频
                    _relayVideoBytes += data.Length;
                    if (!_directActive)
                        Dispatcher.Invoke(() => Screen.ShowPlaceholder("仅直连模式：等待 P2P 直连建立…"));
                    break;
                }
                _lastBytes += data.Length;
                EnsureDecoder();
                _decoder!.Feed(data, 0, data.Length);
                break;
            }
            case MessageType.MonitorInfo:
            {
                var mi = JsonCodec.Decode<MonitorInfo>(msg.Payload);
                if (mi == null) break;
                Dispatcher.Invoke(() =>
                {
                    ResolutionText.Text = $"分辨率: {mi.Width}×{mi.Height}";
                    Screen.ShowPlaceholder($"{mi.Width}×{mi.Height} 等待画面…");
                });
                EnsureDecoder();
                if (!_decoder!.Configure(mi.Width, mi.Height))
                    _log.Error("解码器初始化失败");
                break;
            }
            case MessageType.StreamReset:
                _decoder?.Reset("对端重建码流");
                break;

            case MessageType.SoftwareList:
            {
                var list = JsonCodec.Decode<List<SoftwareInfo>>(msg.Payload) ?? new List<SoftwareInfo>();
                _log.Info($"收到软件列表 {list.Count} 项");
                Dispatcher.Invoke(() =>
                {
                    _vm.SoftwareList.Clear();
                    foreach (var s in list) _vm.SoftwareList.Add(RemoteSoftware.From(s));
                    SwCount.Text = $"{list.Count} 项";
                    ShowEmptyHint(list.Count == 0 ? "被控端没有返回软件列表" : null);
                    _agentOffline = false;   // 列表都回来了，说明被控端确实在线
                });
                break;
            }
            case MessageType.SoftwareState:
            {
                var st = JsonCodec.Decode<SoftwareStateInfo>(msg.Payload);
                if (st == null) break;
                Dispatcher.Invoke(() =>
                {
                    var item = _vm.SoftwareList.FirstOrDefault(s =>
                        string.Equals(s.ExePath, st.ExePath, StringComparison.OrdinalIgnoreCase));
                    if (item != null)
                    {
                        item.IsRunning = st.IsRunning;
                        item.Pid = st.Pid;
                    }
                });
                break;
            }
            case MessageType.WorkerStatus:
            {
                var ws = JsonCodec.Decode<WorkerStatusInfo>(msg.Payload);
                if (ws == null) break;
                _agentStatus = ws;
                if (ws.CaptureFps > 0)
                    _log.Debug($"远端采集 {ws.CaptureFps}fps 后端={ws.Backend}");
                // 安全桌面（UAC 提权/锁屏）：这期间被控端抓不到画面，主控端看到的是黑屏；
                // 明确说出来，否则用户只会以为软件坏了（实测反馈：以为"打开应用就黑屏卡了"）
                if (_secureDesktop != ws.SecureDesktop)
                {
                    _secureDesktop = ws.SecureDesktop;
                    if (_secureDesktop)
                    {
                        _log.Warn("被控端进入安全桌面（UAC 提权窗口/锁屏）");
                        Dispatcher.Invoke(() => Screen.ShowPlaceholder(
                            "被控端正在显示 UAC 提权窗口或锁屏（安全桌面）\n画面与操作暂时不可用，等它消失即可自动恢复"));
                    }
                    else
                    {
                        _log.Info("被控端已回到正常桌面");
                    }
                }
                // 本条消息在 WebSocket 接收线程上处理：凡是碰控件都必须回到 UI 线程，
                // 否则每秒抛一次 InvalidOperationException，主机名永远填不上（实测踩到）
                Dispatcher.Invoke(() =>
                {
                    // 显示"这是哪台电脑"（被控端主动上报）
                    if (!string.IsNullOrEmpty(ws.HostName))
                        HostText.Text = $"{ws.HostName}（{ws.UserName}）{ws.OsVersion}";
                    UpdateLinkModeText();
                });
                break;
            }
            case MessageType.ClipboardText:
            {
                var text = PayloadCodec.DecodeText(msg.Payload);
                _log.Info($"收到远程剪贴板文本（{text.Length} 字符）→ 写入本机剪贴板");
                Dispatcher.Invoke(() => SetLocalClipboard(text));
                break;
            }
            case MessageType.ClipboardFile:
            {
                var info = JsonCodec.Decode<ClipboardFileInfo>(msg.Payload);
                if (info == null) break;
                _log.Info($"远程剪贴板文件：{info.FileName}（{info.FileSize} 字节）");
                Dispatcher.Invoke(() =>
                {
                    _pendingFiles.RemoveAll(f => f.FileId == info.FileId);
                    _pendingFiles.Add(info);
                    UpdateClipboardHint();
                });
                break;
            }
            case MessageType.Error:
            {
                var err = PayloadCodec.DecodeText(msg.Payload);
                _log.Error($"被控端错误：{err}");
                Dispatcher.Invoke(() => ConnInfo.Text = err);
                break;
            }
            case MessageType.Disconnect:
                _log.Info("被控端主动断开");
                break;
            default:
                _log.Debug($"忽略消息 0x{(byte)msg.Type:X2}");
                break;
        }
    }

    private void UpdateClipboardHint()
    {
        if (_pendingFiles.Count == 0)
        {
            ClipboardHint.Text = "";
            return;
        }
        var f = _pendingFiles[0];
        var more = _pendingFiles.Count > 1 ? $" 等 {_pendingFiles.Count} 个文件" : "";
        ClipboardHint.Text = $"📋 远程文件可粘贴: {f.FileName}{more} — 在画面内按 Ctrl+V 下载";
    }

    // ---------------------------------------------------------------- 画面渲染

    private void OnRenderTick(object? sender, EventArgs e)
    {
        var dec = _decoder;
        if (dec == null) return;
        if (!dec.TryTakeFrame(out var buf, out int len)) return;
        try
        {
            Screen.UpdateFrame(buf, dec.Width, dec.Height);
            _framesRendered++;
        }
        finally
        {
            dec.ReturnFrame(buf);
        }

        long now = Environment.TickCount64;
        if (_lastSampleTicks == 0) _lastSampleTicks = now;
        if (now - _lastSampleTicks >= 1000)
        {
            double sec = (now - _lastSampleTicks) / 1000.0;
            double fps = (_framesRendered - _framesAtSample) / sec;
            _framesAtSample = _framesRendered;
            _lastSampleTicks = now;
            double kbps = (_lastBytes - _lastBytesAtSample()) * 8 / 1000.0 / sec;
            _lastKbps = kbps;
            FpsText.Text = $"FPS: {fps:F0}";
            BitrateText.Text = $"码率: {kbps:F0}kbps";
        }
    }

    private long _lastBytesSample;
    private long _lastBytesAtSample() { long v = _lastBytesSample; _lastBytesSample = _lastBytes; return v; }

    // ---------------------------------------------------------------- 软件操作

    private void OpenSelectedSoftware()
    {
        if (SoftwareList.SelectedItem is not RemoteSoftware item) return;
        if (!_ws.IsConnected)
        {
            MessageBox.Show("尚未连接被控端", "远程控制");
            return;
        }
        _log.Info($"请求打开 {item.Name}（{item.ExePath}）");
        _ws.Queue(new ProtocolMessage(MessageType.OpenSoftware, PayloadCodec.EncodeText(item.ExePath)));
        ConnInfo.Text = $"已请求打开 {item.Name}";
    }

    private void CloseSelectedSoftware()
    {
        if (SoftwareList.SelectedItem is not RemoteSoftware item) return;
        if (!_ws.IsConnected) return;
        int pid = item.Pid;
        if (pid <= 0)
        {
            MessageBox.Show($"「{item.Name}」当前没有可关闭的进程", "远程控制");
            return;
        }
        if (MessageBox.Show($"确定要关闭远程的「{item.Name}」(pid={pid}) 吗？", "远程控制",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _log.Info($"请求关闭 {item.Name} pid={pid}");
        _ws.Queue(new ProtocolMessage(MessageType.CloseSoftware, PayloadCodec.EncodeCloseSoftware(pid)));
    }

    // ---------------------------------------------------------------- 剪贴板

    private void InstallClipboardListener()
    {
        var helper = new WindowInteropHelper(this);
        _hwndSource = HwndSource.FromHwnd(helper.Handle);
        _hwndSource?.AddHook(WndProc);
        if (!NativeApi.AddClipboardFormatListener(helper.Handle))
            _log.Warn($"注册剪贴板监听失败：{NativeApi.LastWin32Error()}");
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeApi.WM_CLIPBOARDUPDATE)
        {
            try { OnLocalClipboardChanged(); }
            catch (Exception ex) { _log.Debug($"剪贴板处理异常：{ex.Message}"); }
        }
        return IntPtr.Zero;
    }

    private void OnLocalClipboardChanged()
    {
        if (!_cfg.ClipboardSyncEnabled || !_ws.IsConnected) return;
        try
        {
            if (!Clipboard.ContainsText()) return;
            var text = Clipboard.GetText();
            if (string.IsNullOrEmpty(text) || text == _lastLocalClipboard) return;
            _lastLocalClipboard = text;
            if (text == _suppressClipboard)
            {
                _log.Debug("剪贴板内容来自远程，不回传");
                return;
            }
            _log.Info($"本机剪贴板变化（{text.Length} 字符）→ 同步到远程会话");
            _ws.Queue(new ProtocolMessage(MessageType.ClipboardText, PayloadCodec.EncodeText(text)));
        }
        catch (Exception ex)
        {
            _log.Debug($"读取本机剪贴板失败：{ex.Message}");
        }
    }

    private void SetLocalClipboard(string text)
    {
        _suppressClipboard = text;
        _lastLocalClipboard = text;
        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            _log.Warn($"写入本机剪贴板失败：{ex.Message}");
        }
    }

    /// <summary>画面区域内 Ctrl+V：有远程文件就先下载（策划 §3.2）</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.V || (Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        if (_pendingFiles.Count == 0) return;
        e.Handled = true;
        _ = DownloadPendingFilesAsync();
    }

    private async Task DownloadPendingFilesAsync()
    {
        var files = _pendingFiles.ToList();
        _pendingFiles.Clear();
        UpdateClipboardHint();
        var baseUrl = _cfg.ResolveFileServerUrl();
        foreach (var f in files)
        {
            ConnInfo.Text = $"正在下载 {f.FileName}…";
            ClipboardHint.Text = $"⬇ 正在下载 {f.FileName}…";
            var path = await _downloader.DownloadAsync(baseUrl, f, _cfg.DownloadDir,
                new Progress<double>(p => ClipboardHint.Text = $"⬇ {f.FileName} {p * 100:F0}%"),
                token: _cfg.AgentToken);
            if (path != null)
            {
                ClipboardHint.Text = $"✅ 已保存: {path}";
                _log.Info($"已保存到 {path}");
            }
            else
            {
                ClipboardHint.Text = $"❌ 下载失败: {f.FileName}";
            }
        }
        await Task.Delay(6000);
        if (_pendingFiles.Count == 0) ClipboardHint.Text = "";
    }

    // ---------------------------------------------------------------- 关闭

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_closing) return;
        _closing = true;
        CompositionTarget.Rendering -= OnRenderTick;
        try { NativeApi.RemoveClipboardFormatListener(new WindowInteropHelper(this).Handle); } catch { }
        _cfg.WindowWidth = Width;
        _cfg.WindowHeight = Height;
        _cfg.Save();
        _ws.Dispose();
        _decoder?.Dispose();
        _downloader.Dispose();
        _http.Dispose();
        _log.Info("Viewer 已退出");
    }
}
