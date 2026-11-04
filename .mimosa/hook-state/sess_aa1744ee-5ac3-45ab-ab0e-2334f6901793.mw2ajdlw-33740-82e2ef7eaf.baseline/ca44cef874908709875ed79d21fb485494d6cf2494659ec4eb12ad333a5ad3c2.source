using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Agent.Common;

/// <summary>网络辅助：本机内网地址枚举、外网可达性判断</summary>
public static class NetHelper
{
    /// <summary>本机所有可用 IPv4（排除回环/虚拟网卡常见噪音）</summary>
    public static List<string> LocalIPv4()
    {
        var result = new List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var s = addr.Address.ToString();
                    if (s.StartsWith("127.")) continue;
                    if (s.StartsWith("169.254.")) continue;   // APIPA
                    if (!result.Contains(s)) result.Add(s);
                }
            }
        }
        catch { }
        // 常见私网地址优先（同局域网最容易命中的先试）
        result.Sort((a, b) => Score(a).CompareTo(Score(b)));
        return result;
    }

    private static int Score(string ip)
        => ip.StartsWith("192.168.") ? 0 :
           ip.StartsWith("10.") ? 1 :
           ip.StartsWith("172.") ? 2 : 3;

    /// <summary>是不是私网地址（用于判断"是否需要 UPnP 才能被外网访问"）</summary>
    public static bool IsPrivate(string ip)
    {
        if (!IPAddress.TryParse(ip, out var a)) return true;
        var b = a.GetAddressBytes();
        return b[0] == 10
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168)
            || b[0] == 127;
    }

    /// <summary>取本机默认出口 IP（连一个外部地址看源地址，不实际发数据）</summary>
    public static string? OutboundIPv4(string probeHost = "223.5.5.5", int probePort = 53)
    {
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Connect(probeHost, probePort);
            if (s.LocalEndPoint is IPEndPoint ep && !IPAddress.IsLoopback(ep.Address))
                return ep.Address.ToString();
        }
        catch { }
        var list = LocalIPv4();
        return list.Count > 0 ? list[0] : null;
    }
}
