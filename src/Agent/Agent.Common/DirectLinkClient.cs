using System.IO;
using System.Net.Sockets;
using System.Text;

namespace Agent.Common;

/// <summary>
/// 主控端 P2P 直连客户端（L1）：
///   并行尝试被控端给出的候选地址（内网 IP / UPnP 公网地址），谁先连上就用谁；
///   连上后**视频**改从这条直连收，控制/输入/剪贴板仍走中继；直连断了自动回落到中继视频。
/// </summary>
public sealed class DirectLinkClient : IDisposable
{
    private readonly Logger _log;
    private CancellationTokenSource? _cts;
    private TcpClient? _client;
    private Stream? _stream;
    private Thread? _readThread;

    /// <summary>直连收到的消息（目前只有视频帧）</summary>
    public event Action<ProtocolMessage>? MessageReceived;
    public event Action<string>? Connected;
    public event Action<string>? Disconnected;

    public bool IsConnected { get; private set; }
    public string? ActiveEndpoint { get; private set; }
    public long BytesReceived { get; private set; }
    public string CandidatesTried { get; private set; } = "";

    public DirectLinkClient(Logger log) => _log = log;

    /// <summary>并行尝试候选；成功后事件通知。返回是否已连上</summary>
    public async Task<bool> TryConnectAsync(IEnumerable<string> candidates, string agentId, string token,
        TimeSpan timeout, CancellationToken outer = default)
    {
        Stop();
        var list = candidates.Where(c => !string.IsNullOrWhiteSpace(c)).Distinct().ToList();
        CandidatesTried = string.Join(", ", list);
        if (list.Count == 0) return false;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        var cts = _cts;
        var tasks = new List<Task<(bool ok, TcpClient? client, string ep, string err)>>();
        foreach (var c in list)
        {
            var ep = c;
            tasks.Add(Task.Run(async () =>
            {
                var cli = new TcpClient();
                try
                {
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                    timeoutCts.CancelAfter(timeout);
                    await cli.ConnectAsync(ParseHost(ep), ParsePort(ep), timeoutCts.Token);
                    return (true, cli, ep, "");
                }
                catch (Exception ex)
                {
                    try { cli.Dispose(); } catch { }
                    return (false, null, ep, ex.Message);
                }
            }, cts.Token));
        }

        while (tasks.Count > 0)
        {
            var done = await Task.WhenAny(tasks);
            tasks.Remove(done);
            var (ok, client, ep, err) = await done;
            if (!ok)
            {
                _log.Debug($"P2P 候选 {ep} 连接失败：{err}");
                continue;
            }
            // 握手
            if (!await HandshakeAsync(client!, agentId, token, cts.Token))
            {
                try { client!.Close(); } catch { }
                continue;
            }
            // 其余候选取消
            try { cts.Cancel(); } catch { }
            foreach (var t in tasks)
            {
                try
                {
                    var (o, c2, _, _) = await t;
                    if (o && c2 != null) c2.Close();
                }
                catch { }
            }

            _client = client;
            _stream = client!.GetStream();
            client.NoDelay = true;
            ActiveEndpoint = ep;
            IsConnected = true;
            _readThread = new Thread(ReadLoop) { IsBackground = true, Name = "direct-read" };
            _readThread.Start();
            _log.Info($"P2P 直连成功：{ep}（视频走直连，控制仍走中继）");
            Connected?.Invoke(ep);
            return true;
        }

        _log.Info($"P2P 直连不可用（尝试了 {CandidatesTried}），继续使用中继");
        return false;
    }

    private async Task<bool> HandshakeAsync(TcpClient client, string agentId, string token, CancellationToken ct)
    {
        try
        {
            var payload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { agentId, token });
            var b64 = Convert.ToBase64String(payload).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var line = Encoding.ASCII.GetBytes(DirectLinkHandshake.Prefix + b64 + "\n");
            var stream = client.GetStream();
            await stream.WriteAsync(line, ct);
            await stream.FlushAsync(ct);

            var buf = new byte[1];
            var sb = new StringBuilder();
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline && sb.Length < 256)
            {
                int n = await stream.ReadAsync(buf.AsMemory(0, 1), ct);
                if (n <= 0) return false;
                if (buf[0] == (byte)'\n') break;
                sb.Append((char)buf[0]);
            }
            var resp = sb.ToString();
            if (resp.StartsWith(DirectLinkHandshake.Ok)) return true;
            _log.Warn($"P2P 直连被拒绝：{resp}");
            return false;
        }
        catch (Exception ex)
        {
            _log.Warn($"P2P 直连握手失败：{ex.Message}");
            return false;
        }
    }

    private void ReadLoop()
    {
        var acc = new FrameAccumulator(ipcFraming: false);
        var buf = new byte[64 * 1024];
        var pending = new List<ProtocolMessage>(64);
        try
        {
            while (_stream != null && IsConnected)
            {
                int n = _stream.Read(buf, 0, buf.Length);
                if (n <= 0) break;
                acc.Append(buf, 0, n);
                pending.Clear();
                acc.Drain(pending);
                foreach (var m in pending)
                {
                    if (m.Type == MessageType.VideoFrame)
                        BytesReceived += m.Payload.Length;
                    try { MessageReceived?.Invoke(m); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            if (IsConnected) _log.Debug($"P2P 直连读取结束：{ex.Message}");
        }
        finally
        {
            var was = IsConnected;
            IsConnected = false;
            var ep = ActiveEndpoint;
            ActiveEndpoint = null;
            if (was)
            {
                _log.Info($"P2P 直连已断开（{ep}），视频回落到中继");
                Disconnected?.Invoke(ep ?? "");
            }
        }
    }

    private static string ParseHost(string endpoint)
    {
        var i = endpoint.LastIndexOf(':');
        return i > 0 ? endpoint[..i] : endpoint;
    }

    private static int ParsePort(string endpoint)
    {
        var i = endpoint.LastIndexOf(':');
        return i > 0 && int.TryParse(endpoint[(i + 1)..], out var p) ? p : 47890;
    }

    public void Stop()
    {
        IsConnected = false;
        try { _cts?.Cancel(); } catch { }
        try { _stream?.Dispose(); } catch { }
        try { _client?.Close(); } catch { }
        _stream = null;
        _client = null;
        ActiveEndpoint = null;
    }

    public void Dispose()
    {
        Stop();
        try { _cts?.Dispose(); } catch { }
        _cts = null;
    }
}

/// <summary>与 Agent 侧保持一致的握手常量</summary>
public static class DirectLinkHandshake
{
    public const string Prefix = "RC-DIRECT-1 ";
    public const string Ok = "RC-DIRECT-OK";
    public const string Deny = "RC-DIRECT-DENY";
}
