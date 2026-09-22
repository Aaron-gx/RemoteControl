using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;

namespace Agent.Common;

/// <summary>
/// 最小可用的 UPnP IGD 端口映射（SSDP 发现 + AddPortMapping + 取外网 IP）。
///
/// 用途：被控端在家庭路由器后面时，自动把直连端口映射到公网，这样主控端就能**直连**它
/// （P2P 的 L1 路径）。路由器关了 UPnP 时会失败，此时自动回落到中继，不影响使用。
/// </summary>
public sealed class UpnpPortMapper
{
    private readonly Logger _log;
    private string? _controlUrl;
    private string? _serviceType;

    public bool Mapped { get; private set; }
    public string? ExternalIP { get; private set; }
    public int ExternalPort { get; private set; }

    public UpnpPortMapper(Logger log) => _log = log;

    /// <summary>发现 IGD（SSDP M-SEARCH）</summary>
    public bool Discover(TimeSpan timeout)
    {
        try
        {
            var msg = "M-SEARCH * HTTP/1.1\r\n" +
                      "HOST: 239.255.255.250:1900\r\n" +
                      "MAN: \"ssdp:discover\"\r\n" +
                      "MX: 2\r\n" +
                      "ST: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\n\r\n";
            using var udp = new UdpClient();
            udp.Client.ReceiveTimeout = (int)timeout.TotalMilliseconds;
            udp.EnableBroadcast = true;
            var data = Encoding.ASCII.GetBytes(msg);
            udp.Send(data, data.Length, new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900));

            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
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
                    if (LoadServices(url)) return true;
                }
                catch (SocketException) { break; }
            }
        }
        catch (Exception ex)
        {
            _log.Debug($"UPnP 发现失败：{ex.Message}");
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
                _log.Warn($"UPnP 端口映射失败：HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
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
        if (!Discover(TimeSpan.FromSeconds(3))) return false;
        var ip = NetHelper.OutboundIPv4();
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
