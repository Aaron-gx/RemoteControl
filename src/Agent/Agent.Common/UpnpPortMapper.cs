using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;

namespace Agent.Common;

/// <summary>
/// 最小可用的 UPnP IGD 端口映射（SSDP 发现 + AddPortMapping + 取外网 IP）。
///
/// 用途：被控端在家庭路由器后面时，自动把直连端口映射到公网，这样主控端就能**直连**它
/// （P2P 的 L1 路径）。路由器关了 UPnP 时会失败，此时自动回落到中继，不影响使用。
///
/// 【2026-10-10 重写发现逻辑】现场（客户机器装着 LeapVPN 等）老实现三处硬伤：
///  ① 组播只从默认路由口发——VPN 虚拟网卡一抢默认路由，M-SEARCH 全进隧道，路由器根本收不到；
///  ② 只搜 IGD:1——相当一部分新路由只应答 IGD:2 / WANIPConnection:2；
///  ③ 组播常被 AP 隔离/防火墙丢弃——补一路**单播到本口网关 1900**，绝大多数路由器都应答。
/// 现在逐网卡各发一份（组播+网关单播、IGD:1+IGD:2），谁应答用谁，并记住命中的本地 IP
/// 作为映射的 NewInternalClient（不再用"默认出口 IP"——那就是 VPN 陷阱）。
/// </summary>
public sealed class UpnpPortMapper
{
    private readonly Logger _log;
    private string? _controlUrl;
    private string? _serviceType;
    /// <summary>命中网关的那个本地 IP（映射必须写它，写 VPN 口的 IP 等于白映射）</summary>
    private string? _discoveredLocalIp;

    public bool Mapped { get; private set; }
    public string? ExternalIP { get; private set; }
    public int ExternalPort { get; private set; }

    public UpnpPortMapper(Logger log) => _log = log;

    private static readonly string[] SearchTargets =
    {
        "urn:schemas-upnp-org:device:InternetGatewayDevice:1",
        "urn:schemas-upnp-org:device:InternetGatewayDevice:2",
    };

    /// <summary>发现 IGD（SSDP M-SEARCH）：逐网卡（组播 + 该口网关单播）× 双 ST</summary>
    public bool Discover(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var sockets = new List<UdpClient>();
        try
        {
            // 每个在用网卡的每个 IPv4 各开一个绑定 socket；发组播 + 发网关单播
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var ips = ni.GetIPProperties().UnicastAddresses
                    .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork &&
                                !a.Address.ToString().StartsWith("169.254."))
                    .Select(a => a.Address.ToString()).ToList();
                if (ips.Count == 0) continue;
                var gateways = ni.GetIPProperties().GatewayAddresses
                    .Where(g => g.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(g => g.Address).ToList();

                foreach (var ip in ips)
                {
                    UdpClient? udp = null;
                    try
                    {
                        udp = new UdpClient(new IPEndPoint(IPAddress.Parse(ip), 0));
                        udp.Client.ReceiveTimeout = 500;
                        udp.EnableBroadcast = true;
                    }
                    catch { udp?.Dispose(); continue; }
                    sockets.Add(udp);
                    var data = new List<byte[]>();
                    foreach (var st in SearchTargets)
                    {
                        var msg = "M-SEARCH * HTTP/1.1\r\n" +
                                  $"HOST: 239.255.255.250:1900\r\n" +
                                  "MAN: \"ssdp:discover\"\r\n" +
                                  "MX: 2\r\n" +
                                  $"ST: {st}\r\n\r\n";
                        data.Add(Encoding.ASCII.GetBytes(msg));
                    }
                    foreach (var d in data)
                    {
                        try { udp.Send(d, d.Length, new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900)); } catch { }
                        foreach (var gw in gateways)
                        {
                            try { udp.Send(d, d.Length, new IPEndPoint(gw, 1900)); } catch { }
                        }
                    }
                }
            }
            if (sockets.Count == 0) return false;

            while (DateTime.UtcNow < deadline)
            {
                // 哪个 socket 有响应读哪个（简单轮询：总时长通常 <1s）
                foreach (var udp in sockets)
                {
                    try
                    {
                        var from = new IPEndPoint(IPAddress.Any, 0);
                        var resp = udp.Receive(ref from);
                        var text = Encoding.ASCII.GetString(resp);
                        var loc = text.Split('\n')
                            .FirstOrDefault(l => l.TrimStart().StartsWith("LOCATION", StringComparison.OrdinalIgnoreCase));
                        if (loc == null) continue;
                        var url = loc[(loc.IndexOf(':') + 1)..].Trim();
                        if (LoadServices(url))
                        {
                            _discoveredLocalIp = (udp.Client.LocalEndPoint as IPEndPoint)?.Address.ToString();
                            _log.Info($"UPnP：网关应答来自 {from.Address}（本地口 {_discoveredLocalIp}）");
                            return true;
                        }
                    }
                    catch (SocketException) { /* 该口暂无数据，继续 */ }
                    catch { }
                }
                Thread.Sleep(150);
            }
        }
        catch (Exception ex)
        {
            _log.Debug($"UPnP 发现失败：{ex.Message}");
        }
        finally
        {
            foreach (var s in sockets) try { s.Dispose(); } catch { }
        }
        return false;
    }

    private bool LoadServices(string location)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var xml = http.GetStringAsync(location).GetAwaiter().GetResult();
            var doc = XDocument.Parse(xml);
            XNamespace ns = "urn:schemas-upnp-org:device-1-0";
            foreach (var svc in doc.Descendants(ns + "service"))
            {
                var type = svc.Element(ns + "serviceType")?.Value ?? "";
                var ctrl = svc.Element(ns + "controlURL")?.Value ?? "";
                if (!type.Contains("WANIPConnection") && !type.Contains("WANPPPConnection")) continue;
                var baseUri = new Uri(location);
                _controlUrl = new Uri(baseUri, ctrl).ToString();
                _serviceType = type;
                _log.Info($"找到 UPnP 网关，控制地址 {_controlUrl}");
                return true;
            }
        }
        catch (Exception ex)
        {
            _log.Debug($"解析 UPnP 描述失败：{ex.Message}");
        }
        return false;
    }

    /// <summary>把 internalPort 映射到公网 externalPort（TCP）</summary>
    public bool AddMapping(int internalPort, int externalPort, string internalIP, string description)
    {
        if (_controlUrl == null || _serviceType == null) return false;
        try
        {
            var body =
                $"""
                <?xml version="1.0"?>
                <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/" s:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/">
                <s:Body><u:AddPortMapping xmlns:u="{_serviceType}">
                <NewRemoteHost></NewRemoteHost><NewExternalPort>{externalPort}</NewExternalPort><NewProtocol>TCP</NewProtocol>
                <NewInternalPort>{internalPort}</NewInternalPort><NewInternalClient>{internalIP}</NewInternalClient>
                <NewEnabled>1</NewEnabled><NewPortMappingDescription>{description}</NewPortMappingDescription>
                <NewLeaseDuration>0</NewLeaseDuration></u:AddPortMapping></s:Body></s:Envelope>
                """;
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var req = new HttpRequestMessage(HttpMethod.Post, _controlUrl)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/xml"),
            };
            req.Headers.TryAddWithoutValidation("SOAPAction", $"\"{_serviceType}#AddPortMapping\"");
            var resp = http.SendAsync(req).GetAwaiter().GetResult();
            if (!resp.IsSuccessStatusCode)
            {
                var detail = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                _log.Warn($"UPnP 端口映射失败：HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}" +
                          (detail.Contains("Conflict") || (int)resp.StatusCode == 500 ? "（端口已被别的映射占用，可换 DirectLinkPort 重试）" : ""));
                return false;
            }
            ExternalIP = GetExternalIP();
            ExternalPort = externalPort;
            Mapped = true;
            _log.Info($"UPnP 已映射 公网:{externalPort} → {internalIP}:{internalPort}（外网 IP {ExternalIP}）");
            return true;
        }
        catch (Exception ex)
        {
            _log.Warn($"UPnP 端口映射异常：{ex.Message}");
            return false;
        }
    }

    public string? GetExternalIP()
    {
        if (_controlUrl == null || _serviceType == null) return null;
        try
        {
            var body =
                $"""
                <?xml version="1.0"?>
                <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/" s:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/">
                <s:Body><u:GetExternalIPAddress xmlns:u="{_serviceType}"></u:GetExternalIPAddress></s:Body></s:Envelope>
                """;
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var req = new HttpRequestMessage(HttpMethod.Post, _controlUrl)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/xml"),
            };
            req.Headers.TryAddWithoutValidation("SOAPAction", $"\"{_serviceType}#GetExternalIPAddress\"");
            var resp = http.SendAsync(req).GetAwaiter().GetResult();
            var text = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            var doc = XDocument.Parse(text);
            var ip = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "NewExternalIPAddress")?.Value;
            return string.IsNullOrWhiteSpace(ip) ? null : ip;
        }
        catch { return null; }
    }

    /// <summary>尝试映射（发现 + 映射一步到位）</summary>
    public bool TryMap(int port, Logger log)
    {
        if (!Discover(TimeSpan.FromSeconds(4))) return false;
        // 映射写"命中网关的那个本地 IP"——默认出口 IP 可能是 VPN 的（映射了也到不了本机）
        var ip = _discoveredLocalIp ?? NetHelper.OutboundIPv4();
        if (ip == null) return false;
        if (!NetHelper.IsPrivate(ip))
        {
            // 本机就是公网地址，不需要映射
            ExternalIP = ip;
            ExternalPort = port;
            Mapped = true;
            log.Info($"本机为公网地址 {ip}，直连端口无需映射");
            return true;
        }
        return AddMapping(port, port, ip, "RemoteControl Direct Link");
    }
}
