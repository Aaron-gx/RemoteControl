using System.IO;
using System.Net.WebSockets;
using System.Threading.Channels;
using Agent.Common;

namespace Viewer.Services;

/// <summary>
/// 主控端与中继服务器的 WebSocket 服务（策划 §4.1 viewer 角色）
/// - 自动重连（指数退避）
/// - 1s 心跳测 RTT（延迟显示）
/// - 单写者发送队列
/// </summary>
public sealed class WebSocketService : IDisposable
{
    private readonly Logger _log;
    private readonly Channel<ProtocolMessage> _sendQueue =
        Channel.CreateBounded<ProtocolMessage>(new BoundedChannelOptions(1024)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private string _url = "";
    private string _target = "";

    public event Action? Connected;
    public event Action<string>? Disconnected;
    public event Action<ProtocolMessage>? MessageReceived;
    public event Action<long>? LatencyMeasured;
    public event Action<string>? StatusText;

    /// <summary>服务器授权状态变化（客户端本地验签后的结论）</summary>
    public event Action<LicenseState, string, string>? LicenseStateChanged;

    /// <summary>被控端上报的 P2P 直连候选（内网 IP / UPnP 公网地址）</summary>
    public event Action<List<string>, string?>? DirectCandidatesReceived;

    public bool IsConnected => _ws?.State == WebSocketState.Open;
    public bool IsRunning => _cts is { IsCancellationRequested: false };

    public WebSocketService(Logger log) => _log = log;

    public void Start(string serverUrl, string targetAgentId)
    {
        Stop();
        _url = serverUrl.Trim();
        _target = targetAgentId.Trim();
        _cts = new CancellationTokenSource();
        _runTask = Task.Run(() => RunAsync(_cts.Token));
        _ = Task.Run(SendPumpAsync);
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _ws?.Abort(); } catch { }
        _cts?.Dispose();
        _cts = null;
    }

    /// <summary>中继要求的共享令牌（空 = 不鉴权）</summary>
    public string Token { get; set; } = "";

    private string BuildUrl()
    {
        var sep = _url.Contains('?') ? '&' : '?';
        var token = string.IsNullOrEmpty(Token) ? "" : $"&token={Uri.EscapeDataString(Token)}";
        return $"{_url}{sep}role=viewer&target={Uri.EscapeDataString(_target)}{token}";
    }

    private async Task RunAsync(CancellationToken ct)
    {
        int attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            string? closeInfo = null;
            try
            {
                _ws = new ClientWebSocket();
                _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                _ws.Options.SetBuffer(512 * 1024, 512 * 1024);
                StatusText?.Invoke("连接中…");
                await _ws.ConnectAsync(new Uri(BuildUrl()), ct);
                attempt = 0;
                _log.Info($"已连接中继 {BuildUrl()}");
                StatusText?.Invoke("已连接");
                Connected?.Invoke();
                // 心跳必须跟着"这一条会话"结束：ReceiveLoop 返回（服务器关连接/出错）之后如果还
                // await hb，就会一直等下去 —— Disconnected 不触发、重连循环也不再进，
                // 界面卡在"已连接"却什么都收不到（实测踩到：中继因被控端离线关掉连接后卡死）
                using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var hb = Task.Run(() => HeartbeatLoopAsync(sessionCts.Token), sessionCts.Token);
                await ReceiveLoopAsync(ct);
                sessionCts.Cancel();
                try { await hb; } catch (OperationCanceledException) { }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                closeInfo = $"{ex.GetType().Name}: {ex.Message}";
                _log.Warn($"WebSocket 异常：{closeInfo}");
            }
            finally
            {
                var reason = closeInfo ?? "连接已关闭";
                Disconnected?.Invoke(reason);
                try { _ws?.Dispose(); } catch { }
                _ws = null;
            }

            if (ct.IsCancellationRequested) break;
            attempt++;
            int delay = Math.Min(20, (int)Math.Pow(2, Math.Min(4, attempt)));
            StatusText?.Invoke($"{delay}s 后重连…");
            try { await Task.Delay(TimeSpan.FromSeconds(delay), ct); } catch { break; }
        }
        _log.Info("WebSocket 服务已停止");
        StatusText?.Invoke("已断开");
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1000, ct);
                Queue(new ProtocolMessage(MessageType.Heartbeat,
                    PayloadCodec.EncodeHeartbeat(Environment.TickCount64)));
            }
            catch (OperationCanceledException) { return; }
            catch { }
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[512 * 1024];
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
                    throw new ProtocolException("接收消息过大");
            } while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Text)
            {
                var text = System.Text.Encoding.UTF8.GetString(acc.GetBuffer(), 0, (int)acc.Length);
                if (HandleControlText(text)) continue;
                _log.Warn($"服务器文本消息：{Truncate(text, 300)}");
                if (text.Contains("agent_offline"))
                    StatusText?.Invoke("被控端不在线");
                continue;
            }

            HandleFrames(acc.GetBuffer(), (int)acc.Length);
        }
    }

    private void HandleFrames(byte[] data, int length)
    {
        int consumedTotal = 0;
        while (consumedTotal < length)
        {
            var msg = ProtocolMessage.TryParse(new ReadOnlySpan<byte>(data, consumedTotal, length - consumedTotal),
                out int consumed);
            if (msg == null) break;
            consumedTotal += consumed;
            if (msg.Type == MessageType.Heartbeat)
            {
                long ts = PayloadCodec.DecodeHeartbeat(msg.Payload);
                if (ts != 0)
                    LatencyMeasured?.Invoke(Environment.TickCount64 - ts);
                continue;
            }
            if (msg.Type == MessageType.DirectCandidates)
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(msg.Payload);
                    var root = doc.RootElement;
                    var list = new List<string>();
                    if (root.TryGetProperty("tcp", out var arr))
                        foreach (var e in arr.EnumerateArray())
                        {
                            var v = e.GetString();
                            if (!string.IsNullOrWhiteSpace(v)) list.Add(v!);
                        }
                    string? pub = root.TryGetProperty("public", out var pv) && pv.ValueKind == System.Text.Json.JsonValueKind.String
                        ? pv.GetString() : null;
                    if (!string.IsNullOrEmpty(pub) && !list.Contains(pub)) list.Insert(0, pub);   // 公网地址优先试
                    DirectCandidatesReceived?.Invoke(list, pub);
                }
                catch (Exception ex) { _log.Warn($"解析直连候选失败：{ex.Message}"); }
                continue;
            }
            try { MessageReceived?.Invoke(msg); }
            catch (Exception ex) { _log.Error($"处理 {msg.Type} 失败", ex); }
        }
    }

    public void Queue(ProtocolMessage msg)
    {
        if (!_sendQueue.Writer.TryWrite(msg))
            _log.Warn($"发送队列已满，丢弃 {msg.Type}");
    }

    private async Task SendPumpAsync()
    {
        while (true)
        {
            ProtocolMessage msg;
            try { msg = await _sendQueue.Reader.ReadAsync(); }
            catch { return; }
            try
            {
                // 刚点"连接"时可能还没握手完：命令类消息等一会儿再发，避免静默丢失
                if (msg.Type != MessageType.VideoFrame)
                {
                    var deadline = Environment.TickCount64 + 3000;
                    while (!IsConnected && Environment.TickCount64 < deadline)
                        await Task.Delay(30);
                }
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
    /// 处理中继推送的控制消息。重点是 `license`：中继会把授权状态**和原始激活码**一起发来，
    /// 客户端用内置公钥再验一次签名与时间窗 —— 这样"只改中继二进制绕过授权"也不成立。
    /// </summary>
    private bool HandleControlText(string text)
    {
        if (!text.Contains("license")) return false;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var t) || t.GetString() != "license") return false;

            var stateText = root.TryGetProperty("state", out var st) ? st.GetString() ?? "" : "";
            var reason = root.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "";
            var code = root.TryGetProperty("code", out var c) ? c.GetString() ?? "" : "";

            LicenseState state;
            string detail = "";
            if (!string.IsNullOrEmpty(code))
            {
                // 以本地验签结果为准（不信任服务器自报的状态）
                var v = Agent.Common.LicenseCodec.Verify(code, DateTime.UtcNow);
                state = v.State;
                detail = v.Error;
                if (v.Ok && v.Payload != null)
                    detail = $"(编号 {v.Payload.Lic}，至 {v.Payload.ExpiresAt():yyyy-MM-dd}，" +
                             $"剩余 {v.Payload.DaysLeft():F0} 天)";
            }
            else
            {
                state = stateText switch
                {
                    "ok" => LicenseState.Ok,
                    "expired" => LicenseState.Expired,
                    "not_yet" => LicenseState.NotYet,
                    "missing" => LicenseState.Missing,
                    _ => LicenseState.Invalid,
                };
                detail = reason;
            }

            _log.Info($"服务器授权状态：{state} {detail}");
            LicenseStateChanged?.Invoke(state, detail, detail);
            return true;
        }
        catch (Exception ex)
        {
            _log.Warn($"解析授权状态失败：{ex.Message}");
            return false;
        }
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];

    public void Dispose()
    {
        Stop();
        _sendQueue.Writer.TryComplete();
    }
}
