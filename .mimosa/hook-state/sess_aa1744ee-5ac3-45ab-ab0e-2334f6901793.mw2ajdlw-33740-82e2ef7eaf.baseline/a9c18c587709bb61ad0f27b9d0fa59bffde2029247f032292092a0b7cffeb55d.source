using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace Agent.Common;

/// <summary>
/// RFC 5389 STUN Binding 客户端（只做一件事：问服务器"你看到我的公网映射地址是什么"）。
/// 【2026-10-10 UU 式直连】跨网络的主控/被控要 UDP 打洞，双方得先知道 NAT 给自己分的
/// 公网 ip:port —— 从**打洞用的同一个 UDP socket** 发 Binding Request，STUN 服务器返回的
/// XOR-MAPPED-ADDRESS 就是这条 socket 的映射（对端口受限锥形 NAT 也成立）。
/// 服务器选型：腾讯（国内任意播，最稳）→ Cloudflare → Syncthing，全部失败才放弃打洞。
/// </summary>
public static class StunClient
{
    private static readonly byte[] MagicCookie = { 0x21, 0x12, 0xA4, 0x42 };

    /// <summary>按优先级排列的公共 STUN 服务器。
    /// 实测（2026-10-10，国内宽带）：Cloudflare 任意播最稳（成功返回运营商出口映射），
    /// 腾讯/小米时好时坏，Syncthing 基本不可达——把可用的排在前面，收集要快很多。</summary>
    public static readonly (string host, int port)[] Servers =
    {
        ("stun.cloudflare.com", 3478),
        ("stun.qq.com", 3478),
        ("stun.miwifi.com", 3478),
        ("stun.syncthing.net", 3478),
    };

    /// <summary>
    /// 在 udp 这个 socket 上向 STUN 服务器发 Binding Request，返回观测到的公网映射地址。
    /// 返回 null = 超时/不可达。注意：调用期间**不能有别的线程在同一个 socket 上收数据**
    /// （UdpPunchChannel 保证 STUN 阶段在打洞开始前完成）。
    /// </summary>
    public static async Task<IPEndPoint?> QueryAsync(Socket udp, string host, int port, int timeoutMs = 1200)
    {
        try
        {
            if (!IPAddress.TryParse(host, out var ip))
            {
                var addrs = await Dns.GetHostAddressesAsync(host);
                ip = addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                if (ip == null) return null;
            }

            var tid = RandomNumberGenerator.GetBytes(12);
            var req = new byte[20];
            req[1] = 0x01;                                  // Binding Request
            Array.Copy(MagicCookie, 0, req, 4, 4);
            Array.Copy(tid, 0, req, 8, 12);

            var remote = new IPEndPoint(ip, port);
            // 同步 SendTo（20 字节一次，微秒级）：SendAsync 的 byte[] 重载没有带 EndPoint 的版本
            udp.SendTo(req, SocketFlags.None, remote);

            var buf = new byte[1280];
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                int remain = (int)(timeoutMs - sw.ElapsedMilliseconds);
                if (!udp.Poll(Math.Max(1, remain) * 1000, SelectMode.SelectRead)) break;
                // Poll 返回 true = 有数据可读，同步 Receive 不会阻塞；
                // 读到的可能不是 STUN 响应（迟到的心跳等），解析失败就继续等
                EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                int n;
                try { n = udp.ReceiveFrom(buf, SocketFlags.None, ref from); }
                catch { break; }
                var ep = Parse(buf, n, tid);
                if (ep != null) return ep;
            }
            return null;
        }
        catch { return null; }
    }

    /// <summary>解析 Binding Response，取 XOR-MAPPED-ADDRESS(0x0020)/MAPPED-ADDRESS(0x0001)。
    /// 公开给 UdpPunchChannel 复用（通道的接收循环替 STUN 查询收包，见其 QueryStunAsync）。</summary>
    public static IPEndPoint? Parse(byte[] d, int len, byte[] tid)
    {
        if (len < 20 || d[0] != 0x01 || d[1] != 0x01) return null;      // 只要成功的 Binding Response
        for (int i = 0; i < 4; i++)
            if (d[4 + i] != MagicCookie[i]) return null;                 // 魔数在 4..7：对不上就不是对 STUN 请求的响应
        for (int i = 0; i < 12; i++)
            if (d[8 + i] != tid[i]) return null;                         // 事务 ID 在 8..19：对不上不是我们这次请求的响应

        int pos = 20;
        while (pos + 4 <= len)
        {
            int atype = (d[pos] << 8) | d[pos + 1];
            int alen = (d[pos + 2] << 8) | d[pos + 3];
            int body = pos + 4;
            if (body + alen > len) break;
            if ((atype == 0x0020 || atype == 0x0001) && alen >= 8 && d[body + 1] == 0x01)  // IPv4
            {
                int port = ((d[body + 2] << 8) | d[body + 3]);
                var addr = new byte[4];
                Array.Copy(d, body + 4, addr, 0, 4);
                if (atype == 0x0020)
                {
                    port ^= 0x2112;
                    for (int i = 0; i < 4; i++) addr[i] ^= MagicCookie[i];
                }
                return new IPEndPoint(new IPAddress(addr), (ushort)port);
            }
            pos = body + alen + ((4 - alen % 4) % 4);                    // 属性 4 字节对齐
        }
        return null;
    }
}
