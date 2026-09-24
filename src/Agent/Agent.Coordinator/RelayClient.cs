using System.Net.WebSockets;
using System.Threading.Channels;
using Agent.Common;

namespace Agent.Coordinator;

/// <summary>
/// 与香港中继服务器的 WebSocket 客户端（Agent 角色）。
/// - 自动重连（指数退避）
/// - 单写者队列（ClientWebSocket 不允许并发 SendAsync）
/// - 分帧接收（TCP 流可能把一条消息拆成多次 Receive）
/// - 心跳：收到 ts != 0 的心跳原样回显（供主控端测 RTT）；每 15s 发 ts = 0 保活
/// </summary>
public sealed class RelayClient : IDisposable
{
    private readonly AgentConfig _cfg;
    private readonly Logger _log;

    // 控制类消息（心跳回声/授权/软件列表/剪贴板/键鼠指令）走这条队列 —— 发送时永远优先。
    // 为什么要单独一条：见 QueueVideo 的注释（实测踩过的坑）。
    private readonly Channel<ProtocolMessage> _ctrlQueue =
        Channel.CreateBounded<ProtocolMessage>(new BoundedChannelOptions(512)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

    /// <summary>视频队列深度：只留最近约 1 秒的帧量，满了丢最旧的</summary>
    private const int VideoQueueDepth = 24;
    private readonly Channel<ProtocolMessage> _videoQueue =
        Channel.CreateBounded<ProtocolMessage>(new BoundedChannelOptions(VideoQueueDepth)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
    private long _videoDropped;
    private long _videoQueued;

    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Task? _runTask;

    public event Action? Connected;
    public event Action<string>? Disconnected;
    public event Action<ProtocolMessage>? MessageReceived;
    /// <summary>任何入站消息（含心跳回声）——中继是透传的，这是判断"主控端在场"的唯一可靠依据</summary>
    public event Action? InboundTraffic;
    public event Action<string>? Log;

    /// <summary>最近一次收到入站消息的时间</summary>
    public DateTime LastInboundUtc { get; private set; } = DateTime.MinValue;

    /// <summary>
    /// 服务器因为**连接口令不对/没填**拒绝了握手（HTTP 401）。
    ///
    /// 【为什么要单独识别出来】这条路上出的问题在现场最难查：被控端只是"未连接服务器、
    /// 会自动重试"，没有别的线索；用户看到的是"被控端已经运行了但主控端列表里没有它"，
    /// 只能靠猜。401 是唯一能把"口令不一致"和"网络不通"分开的信号 —— 识别出来就能
    /// 在托盘和日志里直接说明原因，省掉一轮来回排查。
    /// </summary>
    public bool AuthRejected { get; private set; }
    /// <summary>最近一次连接失败的原因（面向人的一句话）</summary>
    public string LastFailure { get; private set; } = "";
    private long _lastAuthLogTick;

    public bool IsConnected => _ws?.State == WebSocketState.Open;

    /// <summary>待发送队列深度（控制 + 视频）</summary>
    public int PendingSends => _ctrlQueue.Reader.Count + _videoQueue.Reader.Count;
    /// <summary>视频队列当前积压：持续 &gt; 0 说明链路发不出去（拥塞判定的依据）</summary>
    public int VideoPending => _videoQueue.Reader.Count;
    /// <summary>被挤掉的视频块数（含 DropOldest 静默丢弃的，单调递增）</summary>
    public long VideoDropped => Interlocked.Read(ref _videoDropped);
    public long QueuedCount;
    public long DroppedCount;

    public RelayClient(AgentConfig cfg, Logger log)
    {
        _cfg = cfg;
        _log = log;
        _ = Task.Run(SendPumpAsync);
    }

    /// <summary>上次连成功过的端点（优先复用，避免每次重连都从主地址试起）</summary>
    private string? _lastGoodBase;

    public string ConnectionUrl => BuildUrl(CurrentBase);

    private string CurrentBase => _lastGoodBase ?? Candidates[0];

    /// <summary>
    /// 候选端点：主地址 →（配置的备用）→ 同主机 443 / 8443。
    /// 为什么需要：部分网络（公司/校园/运营商）会封 8080，但放行 443；
    /// 被控端自动换端口重试，用户不用改任何配置。
    /// </summary>
    private List<string> Candidates
    {
        get
        {
            var list = new List<string>();
            void Add(string? u)
            {
                if (string.IsNullOrWhiteSpace(u)) return;
                u = u.Trim();
                if (!u.StartsWith("ws")) u = "ws://" + u;
                if (!u.EndsWith("/ws")) u = u.TrimEnd('/') + "/ws";
                if (!list.Contains(u, StringComparer.OrdinalIgnoreCase)) list.Add(u);
            }

            Add(_cfg.ServerUrl);
            foreach (var extra in (_cfg.ServerUrlFallbacks ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)) Add(extra);
            try
            {
                var u = new Uri(list.Count > 0 ? list[0] : _cfg.ServerUrl);
                if (u.Port != 443) Add($"ws://{u.Host}:443/ws");
                if (u.Port != 8443) Add($"ws://{u.Host}:8443/ws");
            }
            catch { }
            return list;
        }
    }

    private string BuildUrl(string baseUrl)
    {
        var url = baseUrl;
        var sep = url.Contains('?') ? '&' : '?';
        var token = string.IsNullOrWhiteSpace(_cfg.AgentToken) ? "" : $"&token={Uri.EscapeDataString(_cfg.AgentToken)}";
        return $"{url}{sep}role=agent&id={Uri.EscapeDataString(_cfg.AgentId)}{token}";
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _runTask = Task.Run(() => RunAsync(_cts.Token));
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _ws?.Abort(); } catch { }
    }

    /// <summary>重连（授权停机后定期调用：服务器装好新激活码即可自动恢复）</summary>
    public void Restart()
    {
        Stop();
        Thread.Sleep(300);
        Start();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        int attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // 按候选端点依次尝试（主地址 → 备用 → 443/8443），谁先通用谁
                var candidates = Candidates;
                Exception? lastEx = null;
                bool connected = false;
                for (int i = 0; i < candidates.Count && !ct.IsCancellationRequested; i++)
                {
                    var baseUrl = candidates[i];
                    try
                    {
                        _ws = new ClientWebSocket();
                        // 关掉 WebSocket 自带的保活：它的 pong 宽限默认跟着 KeepAliveInterval（20s），
                        // 而弱网下"被控端→香港"的往返动辄十几秒，一到点客户端就把连接自己掐了 ——
                        // 表现是"连服务器也不行"、反复断线重连（实测：心跳往返 19.8 秒）。
                        // 改成关掉它，靠 TCP 自身超时 + 应用层心跳（主控端心跳回声）判断链路。
                        _ws.Options.KeepAliveInterval = Timeout.InfiniteTimeSpan;
                        _ws.Options.SetBuffer(256 * 1024, 256 * 1024);
                        using var one = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        one.CancelAfter(TimeSpan.FromSeconds(8));
                        _log.Info($"连接中继服务器 {BuildUrl(baseUrl)}" + (i > 0 ? $"（备用端点 {i}）" : ""));
                        await _ws.ConnectAsync(new Uri(BuildUrl(baseUrl)), one.Token);
                        _lastGoodBase = baseUrl;
                        connected = true;
                        break;
                    }
                    catch (Exception ex)
                    {
                        lastEx = ex;
                        try { _ws?.Dispose(); } catch { }
                        _ws = null;
                        if (i + 1 < candidates.Count)
                            _log.Warn($"端点 {baseUrl} 连不上（{ex.Message}），换下一个试试…");
                    }
                }
                if (!connected) throw lastEx ?? new IOException("所有端点都连不上");
                attempt = 0;
                AuthRejected = false;
                LastFailure = "";
                _log.Info($"已连接中继服务器（{_lastGoodBase}）");
                Connected?.Invoke();
                // 心跳必须跟着"这一条会话"结束，否则 ReceiveLoop 返回后 await hb 会永远等下去：
                // 不触发 Disconnected、不再重连，被控端会以"在线"的假象卡死（与主控端同一处坑）
                using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var hb = Task.Run(() => HeartbeatLoopAsync(sessionCts.Token), sessionCts.Token);
                await ReceiveLoopAsync(ct);
                sessionCts.Cancel();
                try { await hb; } catch (OperationCanceledException) { }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                // 把"口令不对"和"网络不通"分开说：前者用户自己就能改（重装填口令），
                // 后者只能等网络/服务器。混在一句"中继连接中断"里，现场只能靠猜（实测踩到）。
                var msg = $"{ex.GetType().Name}: {ex.Message}";
                bool auth = msg.Contains("401", StringComparison.Ordinal) ||
                            msg.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase);
                AuthRejected = auth;
                LastFailure = auth
                    ? "连接口令不一致或未填（服务器返回 401）"
                    : (string.IsNullOrWhiteSpace(_cfg.AgentToken) ? "连接不上服务器（本机也没配置连接口令）" : "连接不上服务器（网络或服务器不可用）");
                if (auth)
                {
                    long now = Environment.TickCount64;
                    if (now - _lastAuthLogTick > 30000)
                    {
                        _lastAuthLogTick = now;
                        _log.Error("服务器拒绝了连接（HTTP 401）：连接口令（config\\agent.json 的 AgentToken）" +
                                   "与服务端 RC_TOKEN 不一致或未填写 —— 在被控端列表里会显示为「不在线」。\n" +
                                   "  改法：重新运行安装程序并在「连接设置」里填入口令，或直接改 agent.json 的 AgentToken 后重启被控端。");
                    }
                }
                else
                {
                    _log.Warn($"中继连接中断：{msg}"
                              + (string.IsNullOrWhiteSpace(_cfg.AgentToken) ? "（提示：本机没有配置连接口令，服务器若启用了鉴权会拒绝连接）" : ""));
                }
            }
            finally
            {
                var url = "";
                try { url = _ws?.CloseStatusDescription ?? _ws?.CloseStatus?.ToString() ?? ""; } catch { }
                Disconnected?.Invoke(url);
                try { _ws?.Dispose(); } catch { }
                _ws = null;
            }

            if (ct.IsCancellationRequested) break;
            attempt++;
            int delay = Math.Min(30, (int)Math.Pow(2, Math.Min(5, attempt)));
            _log.Info($"{delay}s 后重连中继服务器（第 {attempt} 次）");
            try { await Task.Delay(TimeSpan.FromSeconds(delay), ct); } catch { break; }
        }
        _log.Info("中继客户端已停止");
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), ct);
                Queue(new ProtocolMessage(MessageType.Heartbeat, PayloadCodec.EncodeHeartbeat(0)));
            }
            catch (OperationCanceledException) { return; }
            catch { }
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[256 * 1024];
        using var acc = new MemoryStream();
        while (!ct.IsCancellationRequested && _ws?.State == WebSocketState.Open)
        {
            acc.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _log.Info("服务器关闭了连接");
                    return;
                }
                acc.Write(buffer, 0, result.Count);
                if (acc.Length > ProtocolMessage.MaxPayload)
                    throw new ProtocolException("接收消息超过上限");
            } while (!result.EndOfMessage);

            if (result.MessageType != WebSocketMessageType.Binary)
            {
                var text = System.Text.Encoding.UTF8.GetString(acc.ToArray());
                if (HandleControlText(text)) continue;
                _log.Warn($"收到非二进制消息，忽略：{Truncate(text, 200)}");
                continue;
            }

            HandleFrames(acc.GetBuffer(), (int)acc.Length);
        }
    }

    /// <summary>把一段字节按帧解析出来（独立方法：异步方法里不能持有 Span 局部变量）</summary>
    private void HandleFrames(byte[] data, int length)
    {
        int consumedTotal = 0;
        while (consumedTotal < length)
        {
            var msg = ProtocolMessage.TryParse(new ReadOnlySpan<byte>(data, consumedTotal, length - consumedTotal),
                out int consumed);
            if (msg == null) break;
            consumedTotal += consumed;
            HandleIncoming(msg);
        }
        if (consumedTotal < length)
            _log.Warn($"收到不完整帧，丢弃 {length - consumedTotal} 字节");
    }

    private void HandleIncoming(ProtocolMessage msg)
    {
        LastInboundUtc = DateTime.UtcNow;
        try { InboundTraffic?.Invoke(); } catch (Exception ex) { _log.Warn($"入站活动处理异常：{ex.Message}"); }

        if (msg.Type == MessageType.Heartbeat)
        {
            long ts = PayloadCodec.DecodeHeartbeat(msg.Payload);
            if (ts != 0)
            {
                // 回声请求 → 原样回显，主控端据此算 RTT
                Queue(msg);
            }
            return;
        }
        MessageReceived?.Invoke(msg);
    }

    /// <summary>排队发送控制类消息（线程安全，非阻塞）</summary>
    public void Queue(ProtocolMessage msg)
    {
        Interlocked.Increment(ref QueuedCount);
        if (!_ctrlQueue.Writer.TryWrite(msg))
        {
            Interlocked.Increment(ref DroppedCount);
            _log.Warn($"发送队列已满，丢弃 {msg.Type}");
        }
    }

    /// <summary>
    /// 排队发送视频块。**必须和控制消息分开排队** —— 实测踩过的坑：
    /// 两者混在一条 512 深、DropOldest 的队列里，而被控端上行只有 100~250kbps、编码却在 4Mbps，
    /// 队列瞬间被视频灌满，于是三件事同时发生：
    ///   ① 心跳回声被压在 20 秒的视频后面 → 主控端判定"服务器无响应"→ 反复断开重连（日志里一分钟连了 5 次）；
    ///   ② DropOldest 丢的是**最旧的**，控制消息也会被静默丢掉；
    ///   ③ DroppedCount 只统计 TryWrite 失败，而 DropOldest 永不失败 → 丢弃数一直显示 0，看着像没丢包。
    /// 拆开后：控制永远优先发；视频只留最近约 1 秒，丢最旧的 —— 拥塞时画面会卡，
    /// 但控制通道不再被拖死，主控端也不会再自己断开。
    /// </summary>
    public void QueueVideo(ProtocolMessage msg)
    {
        Interlocked.Increment(ref QueuedCount);
        Interlocked.Increment(ref _videoQueued);
        // DropOldest 不会失败；按"写入时已满"近似统计被挤掉的帧数（只作指标与拥塞信号）
        if (_videoQueue.Reader.Count >= VideoQueueDepth) Interlocked.Increment(ref _videoDropped);
        _videoQueue.Writer.TryWrite(msg);
    }

    private async Task SendPumpAsync()
    {
        while (true)
        {
            ProtocolMessage msg;
            try
            {
                // 控制优先：控制队列里还有东西，就先把它送完，视频才轮得到
                if (_ctrlQueue.Reader.TryRead(out var c)) msg = c;
                else if (_videoQueue.Reader.TryRead(out var v)) msg = v;
                else
                {
                    // 两条都空：等任意一条来数据（未完成的那条等待会在下次有数据时自然结束）
                    await Task.WhenAny(
                        _ctrlQueue.Reader.WaitToReadAsync().AsTask(),
                        _videoQueue.Reader.WaitToReadAsync().AsTask());
                    continue;
                }
            }
            catch { return; }
            try
            {
                var ws = _ws;
                if (ws == null || ws.State != WebSocketState.Open) continue;
                var frame = msg.ToFrame();
                await ws.SendAsync(new ArraySegment<byte>(frame), WebSocketMessageType.Binary, true, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _log.Warn($"发送 {msg.Type} 失败：{ex.Message}");
            }
        }
    }

    /// <summary>
    /// 处理中继推送的控制消息：重点是 `license`。
    /// 中继会把授权状态与**原始激活码**一起发来，这里用内置公钥本地验签 ——
    /// 即使有人改了服务器上的中继二进制绕过授权，被控端这一层仍然会拒绝工作。
    /// </summary>
    private bool HandleControlText(string text)
    {
        // 中继推来的"当前主控端数量"：多于一个时被控端改走中继，保证人人有画面
        if (text.Contains("\"viewers\""))
        {
            try
            {
                using var d = System.Text.Json.JsonDocument.Parse(text);
                if (d.RootElement.TryGetProperty("type", out var t2) && t2.GetString() == "viewers"
                    && d.RootElement.TryGetProperty("count", out var c)
                    && c.TryGetInt32(out int n))
                {
                    ViewerCountChanged?.Invoke(n);
                    return true;
                }
            }
            catch (Exception ex) { _log.Debug($"解析主控端数量失败：{ex.Message}"); }
        }
        if (!text.Contains("license")) return false;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var t) || t.GetString() != "license") return false;

            var code = root.TryGetProperty("code", out var c) ? c.GetString() ?? "" : "";
            LicenseState state;
            string detail;
            var stateText = root.TryGetProperty("state", out var st) ? st.GetString() ?? "" : "";
            if (!string.IsNullOrEmpty(code))
            {
                var v = LicenseCodec.Verify(code, DateTime.UtcNow);
                state = v.State;
                detail = v.Ok && v.Payload != null
                    ? $"编号 {v.Payload.Lic}，至 {v.Payload.ExpiresAt():yyyy-MM-dd}，剩余 {v.Payload.DaysLeft():F0} 天"
                    : v.Error;
            }
            else
            {
                detail = root.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "";
                // 服务器在授权不可用时会**只发状态、不带激活码**（含"已撤销"）。
                // 认不出的状态一律按"不可用"处理（失败即锁），绝不当成通过。
                state = stateText switch
                {
                    "ok" => LicenseState.Ok,
                    "expired" => LicenseState.Expired,
                    "not_yet" => LicenseState.NotYet,
                    "missing" => LicenseState.Missing,
                    _ => LicenseState.Invalid,
                };
            }

            if (state == LicenseState.Ok)
            {
                _log.Info($"服务器授权校验通过：{detail}");
                if (!string.IsNullOrEmpty(code)) LicenseVerified?.Invoke(code);   // 交给本地闸门缓存
                return true;
            }

            // 日志/提示里优先用服务器给的中文原因（撤销时会写明"已被厂商撤销"），
            // 原因缺失时退回状态名，保证提示不为空。
            var label = string.IsNullOrWhiteSpace(detail)
                ? (string.IsNullOrEmpty(stateText) ? state.ToString() : stateText)
                : detail;
            _log.Error($"服务器授权不可用（{(string.IsNullOrEmpty(stateText) ? state.ToString() : stateText)}）：{label} —— Agent 停止与中继通信，请续期后重启");
            LicenseRejected?.Invoke(label);
            Stop();
            return true;
        }
        catch (Exception ex)
        {
            _log.Warn($"解析授权状态失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>授权不可用时触发（上层据此提示用户）</summary>
    public event Action<string>? LicenseRejected;

    /// <summary>授权校验通过时把**原始激活码**交给上层（本地闸门要缓存它，用于断网后独立判到期）</summary>
    public event Action<string>? LicenseVerified;

    /// <summary>当前与该被控端配对的主控端数量发生变化（中继推送）</summary>
    public event Action<int>? ViewerCountChanged;

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "...";

    public void Dispose()
    {
        Stop();
        try { _runTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _ctrlQueue.Writer.TryComplete();
        _videoQueue.Writer.TryComplete();
        _cts?.Dispose();
    }
}
