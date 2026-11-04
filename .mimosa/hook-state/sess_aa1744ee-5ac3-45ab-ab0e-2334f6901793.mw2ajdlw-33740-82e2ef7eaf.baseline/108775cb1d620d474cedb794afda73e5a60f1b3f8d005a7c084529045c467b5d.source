using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Agent.Common;

namespace E2E;

/// <summary>无头主控端：连接中继、收发协议消息、解码画面（验收测试用）</summary>
public sealed class ViewerHarness : IDisposable
{
    private readonly Logger _log;
    private readonly Channel<ProtocolMessage> _sendQueue =
        Channel.CreateBounded<ProtocolMessage>(new BoundedChannelOptions(4096)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
    private readonly H264StreamDecoder _decoder;

    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private long _videoBytes, _videoChunks, _relayBytes, _directVideoBytes;
    private byte[]? _firstFrame;
    private readonly object _stateLock = new();

    public string AgentId { get; }
    public string ServerUrl { get; }
    public string FfmpegPath { get; }
    /// <summary>中继要求的共享令牌（空 = 不鉴权）</summary>
    public string Token { get; set; } = "";

    public List<SoftwareInfo> Software { get; } = new();
    public MonitorInfo? Monitor { get; private set; }
    public List<string> ClipboardTexts { get; } = new();
    public List<ClipboardFileInfo> ClipboardFiles { get; } = new();
    public List<SoftwareStateInfo> SoftwareStates { get; } = new();
    /// <summary>被控端上报的直连候选</summary>
    public List<string> DirectCandidates { get; } = new();
    public DirectLinkClient? Direct { get; private set; }
    public string DirectStatus { get; private set; } = "未尝试";
    public List<string> Errors { get; } = new();
    public WorkerStatusInfo? LastStatus { get; private set; }
    public volatile bool IsConnected;
    public volatile bool StreamResetSeen;
    public long LatencyMs { get; private set; }
    public long VideoBytes => Interlocked.Read(ref _videoBytes);
    /// <summary>只统计**中继**侧收到的视频字节（P2P 断言要看的量，不含直连）</summary>
    public long RelayVideoBytes => Interlocked.Read(ref _relayBytes);
    /// <summary>只统计**直连**侧收到的视频字节</summary>
    public long DirectVideoBytes => Interlocked.Read(ref _directVideoBytes);
    public long VideoChunks => Interlocked.Read(ref _videoChunks);
    public long DecodedFrames => _decoder.DecodedFrames;
    public long DroppedChunks => _decoder.DroppedChunks;
    public int DecodeWidth => _decoder.Width;
    public int DecodeHeight => _decoder.Height;

    /// <summary>解码端发生丢块 → 需要请求被控端重建码流</summary>
    public event Action? RequestStreamReset;

    public ViewerHarness(string serverUrl, string agentId, string ffmpegPath, Logger log)
    {
        ServerUrl = serverUrl;
        AgentId = agentId;
        FfmpegPath = ffmpegPath;
        _log = log;
        _decoder = new H264StreamDecoder(ffmpegPath, log);
        _decoder.StreamResetNeeded += reason =>
        {
            _log.Warn($"[harness] 解码端积压（{reason}）→ 请求被控端重建码流");
            try { RequestStreamReset?.Invoke(); } catch { }
        };
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _runTask = Task.Run(() => RunAsync(_cts.Token));
        _ = Task.Run(SendPumpAsync);
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _ws?.Abort(); } catch { }
    }

    private string BuildUrl()
    {
        var sep = ServerUrl.Contains('?') ? '&' : '?';
        var token = string.IsNullOrEmpty(Token) ? "" : $"&token={Uri.EscapeDataString(Token)}";
        return $"{ServerUrl}{sep}role=viewer&target={Uri.EscapeDataString(AgentId)}{token}";
    }

    private async Task RunAsync(CancellationToken ct)
    {
        int attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _ws = new ClientWebSocket();
                _ws.Options.SetBuffer(512 * 1024, 512 * 1024);
                await _ws.ConnectAsync(new Uri(BuildUrl()), ct);
                attempt = 0;
                IsConnected = true;
                _log.Info($"[harness] 已连接 {BuildUrl()}");
                Queue(new ProtocolMessage(MessageType.RequestSoftwareList, PayloadCodec.Empty()));
                var hb = Task.Run(() => HeartbeatLoop(ct), ct);
                await ReceiveLoopAsync(ct);
                await hb;
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.Warn($"[harness] 连接异常：{ex.Message}  URL={BuildUrl()}");
            }
            finally
            {
                IsConnected = false;
                try { _ws?.Dispose(); } catch { }
                _ws = null;
            }
            if (ct.IsCancellationRequested) break;
            attempt++;
            try { await Task.Delay(Math.Min(5000, 500 * attempt), ct); } catch { break; }
        }
    }

    private async Task HeartbeatLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(500, ct);
                Queue(new ProtocolMessage(MessageType.Heartbeat, PayloadCodec.EncodeHeartbeat(Environment.TickCount64)));
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
            WebSocketReceiveResult r;
            do
            {
                r = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (r.MessageType == WebSocketMessageType.Close) return;
                acc.Write(buffer, 0, r.Count);
            } while (!r.EndOfMessage);

            if (r.MessageType != WebSocketMessageType.Binary)
            {
                var text = Encoding.UTF8.GetString(acc.GetBuffer(), 0, (int)acc.Length);
                _log.Warn($"[harness] 服务器文本：{text}");
                if (text.Contains("agent_offline")) Errors.Add("agent_offline");
                continue;
            }
            HandleFrames(acc.GetBuffer(), (int)acc.Length);
        }
    }

    private void HandleFrames(byte[] data, int length)
    {
        int pos = 0;
        while (pos < length)
        {
            var msg = ProtocolMessage.TryParse(new ReadOnlySpan<byte>(data, pos, length - pos), out int consumed);
            if (msg == null) break;
            pos += consumed;
            Handle(msg);
        }
    }

    private void Handle(ProtocolMessage msg)
    {
        // 视频走非阻塞路径（不持锁、不阻塞接收循环，否则中继会因写超时踢连接）
        if (msg.Type == MessageType.VideoFrame)
        {
            var (_, payload) = PayloadCodec.DecodeVideoFrame(msg.Payload);
            Interlocked.Increment(ref _videoChunks);
            Interlocked.Add(ref _videoBytes, payload.Length);
            Interlocked.Add(ref _relayBytes, payload.Length);   // 中继侧视频（直连断言只用这个）
            _decoder.Feed(payload, 0, payload.Length);
            return;
        }

        lock (_stateLock)
        {
            switch (msg.Type)
            {
                case MessageType.MonitorInfo:
                {
                    var mi = JsonCodec.Decode<MonitorInfo>(msg.Payload);
                    if (mi != null)
                    {
                        Monitor = mi;
                        _decoder.Configure(mi.Width, mi.Height);
                    }
                    break;
                }
                case MessageType.StreamReset:
                    StreamResetSeen = true;
                    _log.Info("[harness] 收到 StreamReset（对端重建码流）→ 重建解码器");
                    _decoder.Reset("对端重建码流");
                    break;
                case MessageType.SoftwareList:
                {
                    var list = JsonCodec.Decode<List<SoftwareInfo>>(msg.Payload);
                    Software.Clear();
                    if (list != null) Software.AddRange(list);
                    break;
                }
                case MessageType.SoftwareState:
                {
                    var st = JsonCodec.Decode<SoftwareStateInfo>(msg.Payload);
                    if (st != null) SoftwareStates.Add(st);
                    break;
                }
                case MessageType.ClipboardText:
                    ClipboardTexts.Add(PayloadCodec.DecodeText(msg.Payload));
                    break;
                case MessageType.ClipboardFile:
                {
                    var f = JsonCodec.Decode<ClipboardFileInfo>(msg.Payload);
                    if (f != null) ClipboardFiles.Add(f);
                    break;
                }
                case MessageType.DirectCandidates:
                {
                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(msg.Payload);
                        var root = doc.RootElement;
                        DirectCandidates.Clear();
                        if (root.TryGetProperty("tcp", out var arr))
                            foreach (var e in arr.EnumerateArray())
                            {
                                var v = e.GetString();
                                if (!string.IsNullOrWhiteSpace(v)) DirectCandidates.Add(v!);
                            }
                        if (root.TryGetProperty("public", out var pv) &&
                            pv.ValueKind == System.Text.Json.JsonValueKind.String)
                        {
                            var pub = pv.GetString();
                            if (!string.IsNullOrEmpty(pub) && !DirectCandidates.Contains(pub))
                                DirectCandidates.Insert(0, pub);
                        }
                        _log.Info($"[harness] 收到直连候选：{string.Join(", ", DirectCandidates)}");
                    }
                    catch (Exception ex) { _log.Warn($"[harness] 解析直连候选失败：{ex.Message}"); }
                    break;
                }
                case MessageType.WorkerStatus:
                    LastStatus = JsonCodec.Decode<WorkerStatusInfo>(msg.Payload);
                    break;
                case MessageType.Error:
                    Errors.Add(PayloadCodec.DecodeText(msg.Payload));
                    break;
                case MessageType.Heartbeat:
                {
                    long ts = PayloadCodec.DecodeHeartbeat(msg.Payload);
                    if (ts != 0) LatencyMs = Environment.TickCount64 - ts;
                    break;
                }
            }
        }
    }

    /// <summary>取一帧已解码的 BGRA 数据（用于存 PNG 证据）</summary>
    public bool TryGetFrameSnapshot(out byte[] frame, out int width, out int height)
    {
        width = _decoder.Width;
        height = _decoder.Height;
        if (_decoder.TryTakeFrame(out var buf, out _))
        {
            try
            {
                if (_firstFrame == null || _firstFrame.Length != buf.Length)
                    _firstFrame = new byte[buf.Length];
                Buffer.BlockCopy(buf, 0, _firstFrame, 0, buf.Length);
            }
            finally { _decoder.ReturnFrame(buf); }
        }
        frame = _firstFrame!;
        return _firstFrame != null && width > 0 && height > 0;
    }

    /// <summary>把解码出来的一帧存成 PNG（借助 ffmpeg），用于验收留证</summary>
    public bool SaveFrameAsPng(string bgraPath, string pngPath, int width, int height)
    {
        try
        {
            var args = $"-hide_banner -loglevel error -y -f rawvideo -pixel_format bgra -video_size {width}x{height} " +
                       $"-i \"{bgraPath}\" -frames:v 1 \"{pngPath}\"";
            var psi = new ProcessStartInfo(FfmpegPath, args)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            p.StandardError.ReadToEnd();
            p.WaitForExit(20000);
            return File.Exists(pngPath);
        }
        catch { return false; }
    }

    public bool Disposed { get; private set; }

    public void Queue(ProtocolMessage msg) => _sendQueue.Writer.TryWrite(msg);

    private async Task SendPumpAsync()
    {
        while (true)
        {
            ProtocolMessage msg;
            try { msg = await _sendQueue.Reader.ReadAsync(); }
            catch { return; }
            try
            {
                // 未连上时不要把消息丢掉（命令类消息丢了会让场景必然失败），最多等 3 秒
                var deadline = Environment.TickCount64 + 3000;
                while (!IsConnected && Environment.TickCount64 < deadline)
                    await Task.Delay(30);
                var ws = _ws;
                if (ws == null || ws.State != WebSocketState.Open)
                {
                    _log.Warn($"[harness] 连接不可用，丢弃 {msg.Type}");
                    continue;
                }
                var frame = msg.ToFrame();
                await ws.SendAsync(new ArraySegment<byte>(frame), WebSocketMessageType.Binary, true, CancellationToken.None);
            }
            catch { }
        }
    }

    /// <summary>尝试与被控端建立 P2P 直连（视频走直连、控制仍走中继）</summary>
    public async Task<bool> TryDirectAsync(TimeSpan timeout)
    {
        if (DirectCandidates.Count == 0) { DirectStatus = "对端未上报候选"; return false; }
        Direct = new DirectLinkClient(_log);
        Direct.MessageReceived += msg =>
        {
            if (msg.Type != MessageType.VideoFrame) return;
            var (_, data) = PayloadCodec.DecodeVideoFrame(msg.Payload);
            Interlocked.Increment(ref _videoChunks);
            Interlocked.Add(ref _videoBytes, data.Length);
            Interlocked.Add(ref _directVideoBytes, data.Length);
            _decoder.Feed(data, 0, data.Length);   // 直连帧同样喂给解码器
        };
        var ok = await Direct.TryConnectAsync(DirectCandidates, AgentId, Token, timeout);
        DirectStatus = ok ? $"已建立（{Direct.ActiveEndpoint}）" : "不可用（回落中继）";
        return ok;
    }

    public void Dispose()
    {
        Disposed = true;
        try { Direct?.Dispose(); } catch { }
        Stop();
        try { _decoder.Dispose(); } catch { }
        _sendQueue.Writer.TryComplete();
    }
}
