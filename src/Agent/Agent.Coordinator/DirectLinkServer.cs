using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Agent.Common;

namespace Agent.Coordinator;

/// <summary>
/// 被控端 P2P 直连服务（L1）：
///   监听一个 TCP 端口，主控端可以**绕过中继**直接连上来接收视频帧。
///   - 控制/输入/剪贴板仍走中继（低带宽、走中继更稳），只有视频走直连 —— 收益的大头在这里
///   - 家庭路由器后面时会尝试 UPnP 自动端口映射，映射成功即可跨公网直连
///   - 直连断了会自动回到中继，主控端无感
///
/// 握手：主控端发一行 `RC-DIRECT-1 &lt;base64url(json)&gt;\n`（含 agentId 与共享令牌），
/// 校验通过回 `RC-DIRECT-OK\n`，随后本端开始单向推视频帧（帧格式与线上协议一致）。
/// </summary>
public sealed class DirectLinkServer : IDisposable
{
    public const string HandshakePrefix = "RC-DIRECT-1 ";
    public const string HandshakeOk = "RC-DIRECT-OK";
    public const string HandshakeDeny = "RC-DIRECT-DENY";

    private readonly AgentConfig _cfg;
    private readonly Logger _log;
    private readonly CancellationTokenSource _cts = new();
    private TcpListener? _listener;
    private TcpClient? _viewer;
    private Stream? _viewerStream;
    private readonly object _sendLock = new();
    private long _bytesSent;
    private long _framesSent;
    private readonly UpnpPortMapper _upnp;

    public bool HasViewer { get; private set; }
    public string? ViewerEndpoint { get; private set; }
    public long BytesSent => Interlocked.Read(ref _bytesSent);
    public long FramesSent => Interlocked.Read(ref _framesSent);
    public int Port { get; private set; }
    public bool UpnpMapped => _upnp.Mapped;
    public string? PublicAddress { get; private set; }

    /// <summary>
    /// 授权闸门：返回 false 时拒绝一切直连握手（含正在等待握手的连接）。
    /// 由协调器提供 —— P2P 流量不经过中继服务器，所以授权必须在被控端本地也拦一次。
    /// </summary>
    public Func<bool>? LicenseCheck { get; set; }

    public DirectLinkServer(AgentConfig cfg, Logger log)
    {
        _cfg = cfg;
        _log = log;
        _upnp = new UpnpPortMapper(log);
    }

    /// <summary>本机内网候选（ip:port），主控端会按顺序尝试</summary>
    public List<string> LocalCandidates()
    {
        var list = new List<string>();
        if (Port <= 0) return list;
        foreach (var ip in NetHelper.LocalIPv4()) list.Add($"{ip}:{Port}");
        return list;
    }

    public bool Start()
    {
        if (!_cfg.DirectLinkEnabled)
        {
            _log.Info("P2P 直连：配置已关闭");
            return false;
        }
        Port = _cfg.DirectLinkPort;
        try
        {
            _listener = new TcpListener(IPAddress.Any, Port);
            _listener.Start();
            _log.Info($"P2P 直连监听已启动：0.0.0.0:{Port}（内网候选 {string.Join(", ", LocalCandidates())}）");
        }
        catch (Exception ex)
        {
            _log.Warn($"P2P 直连监听启动失败（端口 {Port} 被占用？）：{ex.Message}");
            return false;
        }

        _ = Task.Run(() => AcceptLoop(_cts.Token));
        _ = Task.Run(() => LivenessLoop(_cts.Token));

        // 【2026-10-09 流畅性关键修复】Windows 防火墙默认拦入站 TCP——主控端与被控端同局域网时，
        // 直连候选能收到（SYN 被丢=连接超时），视频只能绕道香港中继（实测中继 RTT ~170ms，
        // 高峰期还拥塞降档，"卡"的主要来源）。这里自加放行规则（幂等：已存在就不重复加）。
        // 被控端以管理员运行，netsh 可用；失败只记日志不影响其它功能。
        _ = Task.Run(() => EnsureFirewallRule());

        // UPnP 自动映射（失败不影响中继可用）
        if (_cfg.UpnpEnabled)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    if (_upnp.TryMap(Port, _log))
                    {
                        var ip = _upnp.ExternalIP;
                        if (!string.IsNullOrEmpty(ip))
                        {
                            PublicAddress = $"{ip}:{_upnp.ExternalPort}";
                            _log.Info($"P2P 公网候选：{PublicAddress}（主控端可直连）");
                        }
                    }
                    else
                    {
                        _log.Info("P2P 直连：UPnP 不可用（路由器未开启/不支持），跨网将走中继，局域网仍可直连");
                    }
                }
                catch (Exception ex) { _log.Debug($"UPnP 异常：{ex.Message}"); }
            });
            // 【2026-10-09】UPnP 失败后每 10 分钟重试一次：路由器重启/晚开 UPnP/换网络后，
            // 只在启动试一次就意味着跨网直连永远没机会（公网候选一直是 null，视频永远绕中继）。
            _ = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    try { await Task.Delay(TimeSpan.FromMinutes(10), _cts.Token); } catch { return; }
                    if (_upnp.Mapped || !string.IsNullOrEmpty(PublicAddress)) continue;
                    try
                    {
                        if (_upnp.TryMap(Port, _log))
                        {
                            var ip = _upnp.ExternalIP;
                            if (!string.IsNullOrEmpty(ip))
                            {
                                PublicAddress = $"{ip}:{_upnp.ExternalPort}";
                                _log.Info($"P2P 公网候选（重试成功）：{PublicAddress}——跨网主控端可直连");
                            }
                        }
                    }
                    catch (Exception ex) { _log.Debug($"UPnP 重试异常：{ex.Message}"); }
                }
            });
        }
        return true;
    }

    /// <summary>
    /// 直连存活检测：TCP 对端异常退出时，只有"发送失败"才发现得了；
    /// 没有视频流量时连接会一直挂着（实测：主控端退出后仍显示"已建立"）。
    /// 这里每 3 秒探测一次：可读且无数据 = 对端已关闭。
    /// </summary>
    private async Task LivenessLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(3000, ct);
                var client = _viewer;
                if (!HasViewer || client?.Client == null) continue;
                bool dead;
                try
                {
                    dead = client.Client.Poll(0, SelectMode.SelectRead) && client.Client.Available == 0;
                }
                catch { dead = true; }
                if (dead)
                {
                    _log.Info("P2P 直连：对端已关闭，回到中继");
                    ClearViewer();
                }
            }
            catch (OperationCanceledException) { return; }
            catch { }
        }
    }

    /// <summary>防火墙放行规则名（产品对外名称用"虚拟服务器"，不出现敏感字样）</summary>
    private const string FirewallRuleName = "虚拟服务器 P2P 直连";

    private void EnsureFirewallRule()
    {
        try
        {
            // 已有同名规则就不再加（netsh add 不会去重，重复加会堆积）
            var check = new System.Diagnostics.ProcessStartInfo("netsh",
                $"advfirewall firewall show rule name=\"{FirewallRuleName}\"")
            {
                RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
            };
            using (var p = System.Diagnostics.Process.Start(check))
            {
                var output = p?.StandardOutput.ReadToEnd() ?? "";
                p?.WaitForExit(10000);
                if (output.Contains("Rule Name", StringComparison.OrdinalIgnoreCase) ||
                    output.Contains("规则名称", StringComparison.Ordinal))
                {
                    _log.Info($"P2P 直连：防火墙规则已存在（{FirewallRuleName}）");
                    return;
                }
            }
            var add = new System.Diagnostics.ProcessStartInfo("netsh",
                $"advfirewall firewall add rule name=\"{FirewallRuleName}\" dir=in action=allow " +
                $"protocol=TCP localport={Port}")
            {
                RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
            };
            using (var p = System.Diagnostics.Process.Start(add))
            {
                var output = p?.StandardOutput.ReadToEnd() ?? "";
                p?.WaitForExit(10000);
                _log.Info($"P2P 直连：已自加防火墙入站放行规则（TCP {Port}）→ {(output.Trim())}");
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"P2P 直连：自加防火墙规则失败（{ex.Message}）——同网主控端将连不上直连端口，" +
                      "视频继续走中继。可手动放行 TCP " + Port);
        }
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient? client = null;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(ct);
                var ep = client.Client.RemoteEndPoint?.ToString() ?? "?";
                _log.Info($"P2P 直连：收到来自 {ep} 的连接，等待握手…");
                if (!await HandshakeAsync(client, ct))
                {
                    try { client.Close(); } catch { }
                    continue;
                }
                // 只保留最新的一个直连主控端
                ClearViewer();
                client.NoDelay = true;
                _viewer = client;
                _viewerStream = client.GetStream();
                ViewerEndpoint = ep;
                HasViewer = true;
                _log.Info($"P2P 直连已建立：{ep}（视频改走直连，控制仍走中继）");
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.Warn($"P2P 直连接收失败：{ex.Message}");
                try { client?.Close(); } catch { }
                await Task.Delay(500, ct);
            }
            // 等这个连接结束（写完自然会失败）再接受下一个
            while (HasViewer && !ct.IsCancellationRequested) await Task.Delay(500, ct);
        }
    }

    private async Task<bool> HandshakeAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            // 冷却期内拒绝（防"建立→秒断→再建"横跳，见 Send 失败处的注释）
            if (Environment.TickCount64 < Interlocked.Read(ref _cooldownUntilTick))
            {
                _log.Info("P2P 直连：冷却期内（上次发送失败后 2 分钟），拒绝新握手，视频继续走中继");
                await ReplyAsync(client.GetStream(), HandshakeDeny + " cooldown\n");
                return false;
            }
            var stream = client.GetStream();
            var buf = new byte[1];
            var sb = new StringBuilder();
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline && sb.Length < 4096)
            {
                int n = await stream.ReadAsync(buf.AsMemory(0, 1), ct);
                if (n <= 0) return false;
                if (buf[0] == (byte)'\n') break;
                sb.Append((char)buf[0]);
            }
            var line = sb.ToString();
            if (LicenseCheck != null && !LicenseCheck())
            {
                _log.Warn("P2P 直连：本机授权不可用，拒绝握手");
                await ReplyAsync(stream, HandshakeDeny + " license\n");
                return false;
            }
            if (!line.StartsWith(HandshakePrefix))
            {
                _log.Warn("P2P 直连：握手格式不对，拒绝");
                await ReplyAsync(stream, HandshakeDeny + " bad-handshake\n");
                return false;
            }
            var json = Encoding.UTF8.GetString(FromBase64Url(line[HandshakePrefix.Length..].Trim()));
            using var doc = JsonDocument.Parse(json);
            var id = doc.RootElement.TryGetProperty("agentId", out var a) ? a.GetString() ?? "" : "";
            var token = doc.RootElement.TryGetProperty("token", out var t) ? t.GetString() ?? "" : "";
            if (!string.Equals(id, _cfg.AgentId, StringComparison.Ordinal))
            {
                _log.Warn($"P2P 直连：被控端 ID 不匹配（{id}），拒绝");
                await ReplyAsync(stream, HandshakeDeny + " agent-mismatch\n");
                return false;
            }
            if (!string.IsNullOrEmpty(_cfg.AgentToken) &&
                !string.Equals(token, _cfg.AgentToken, StringComparison.Ordinal))
            {
                _log.Warn("P2P 直连：令牌不匹配，拒绝");
                await ReplyAsync(stream, HandshakeDeny + " bad-token\n");
                return false;
            }
            await ReplyAsync(stream, HandshakeOk + "\n");
            return true;
        }
        catch (Exception ex)
        {
            _log.Warn($"P2P 直连握手异常：{ex.Message}");
            return false;
        }
    }

    private static async Task ReplyAsync(Stream s, string text)
    {
        var b = Encoding.ASCII.GetBytes(text);
        await s.WriteAsync(b);
        await s.FlushAsync();
    }

    /// <summary>把一个消息（视频帧）发给直连主控端；失败即判定直连断开</summary>
    public void Send(ProtocolMessage msg)
    {
        var stream = _viewerStream;
        if (!HasViewer || stream == null) return;
        try
        {
            var frame = msg.ToFrame();
            lock (_sendLock)
            {
                stream.Write(frame, 0, frame.Length);
                stream.Flush();
                Interlocked.Add(ref _bytesSent, frame.Length);
                Interlocked.Increment(ref _framesSent);
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"P2P 直连发送失败，回落到中继：{ex.Message}");
            // 【2026-10-09 反震荡】主控端侧的直连探测/重建逻辑不稳时会出现"建立→秒断→再建"横跳，
            // 每次切换都伴随码流重建，主控端画面反复冻住（OG1H 现场 10 秒内断两次）。
            // 发送失败后冷却 2 分钟：期间拒绝新直连握手，视频稳定走中继。
            Interlocked.Exchange(ref _cooldownUntilTick, Environment.TickCount64 + 120_000);
            ClearViewer();
        }
    }

    /// <summary>发送失败后的直连冷却截止时刻（TickCount64；之前拒绝新握手，防建立/断开横跳）</summary>
    private long _cooldownUntilTick;

    private void ClearViewer()
    {
        if (!HasViewer && _viewer == null) return;
        HasViewer = false;
        ViewerEndpoint = null;
        try { _viewerStream?.Dispose(); } catch { }
        try { _viewer?.Close(); } catch { }
        _viewerStream = null;
        _viewer = null;
    }

    /// <summary>
    /// 停止对外服务（授权停用时调用）：断开已接入的直连主控端。
    /// 不关掉监听端口 —— 配合 <see cref="LicenseCheck"/> 拒绝新握手，续期后可立即恢复。
    /// </summary>
    public void StopServing(string reason)
    {
        if (HasViewer || _viewer != null)
        {
            _log.Warn($"P2P 直连：停止对主控端服务（{reason}）");
            ClearViewer();
        }
    }

    /// <summary>手动指定公网地址（已自行做端口映射时使用）</summary>
    public void SetManualPublic(string address)
    {
        if (string.IsNullOrWhiteSpace(address)) return;
        if (PublicAddress == address) return;
        PublicAddress = address.Trim();
        _log.Info($"P2P 公网候选（手动配置）：{PublicAddress}");
    }

    /// <summary>把候选地址告诉主控端（经中继转发）</summary>
    public byte[] CandidatesJson()
    {
        var obj = new
        {
            type = "candidates",
            port = Port,
            tcp = LocalCandidates(),
            @public = PublicAddress,     // UPnP 映射后的公网 ip:port（没有则 null）
            relay = true,                // 提示：本机也随时可用中继
        };
        return JsonSerializer.SerializeToUtf8Bytes(obj);
    }

    private static byte[] FromBase64Url(string s)
    {
        var t = s.Replace('-', '+').Replace('_', '/');
        switch (t.Length % 4)
        {
            case 2: t += "=="; break;
            case 3: t += "="; break;
        }
        return Convert.FromBase64String(t);
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        ClearViewer();
        try { _listener?.Stop(); } catch { }
        _cts.Dispose();
    }
}
