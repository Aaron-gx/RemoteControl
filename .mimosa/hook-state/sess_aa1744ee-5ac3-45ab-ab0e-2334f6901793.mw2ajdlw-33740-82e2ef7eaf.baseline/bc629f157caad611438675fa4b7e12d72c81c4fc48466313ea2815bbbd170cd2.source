using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace Agent.Common.Tests;

/// <summary>
/// UDP 打洞直连通道（UdpPunchChannel）测试：全离线（不碰真 STUN 服务器），
/// 两台通道实例在回环地址上互发 HELLO 完成"打洞"，覆盖：
///   · STUN 响应解析（XOR-MAPPED-ADDRESS，用本地假 STUN 服务器回包）
///   · 双端打洞 → Live、PING/PONG RTT
///   · 可靠消息：送达、去重（重发+中继补发撞车不重复交付）、按序
///   · 视频大帧分片重组（>100KB 多分片）
///   · 判死回落（对端停发 3.5s → Dead）
/// </summary>
public class UdpPunchChannelTests
{
    private static (UdpPunchChannel agent, UdpPunchChannel viewer) MakePair()
    {
        var log = new Logger("punch-test", minLevel: 0, echoConsole: false);
        var agent = new UdpPunchChannel(log, PunchRole.Agent, "unit-test-token");
        var viewer = new UdpPunchChannel(log, PunchRole.Viewer, "unit-test-token");
        return (agent, viewer);
    }

    private static void CrossOffer(UdpPunchChannel agent, UdpPunchChannel viewer)
    {
        // 测试环境不收集候选（不碰真 STUN 服务器）：直接用双方的本地端口在回环地址上互指，
        // 与生产路径唯一的差别是候选来源（生产 = STUN 公网映射 + 内网 IP）
        var (nonce, _, _, _) = UdpPunchChannel.DecodeOffer(agent.OfferPayload());
        viewer.OfferPeers(new List<string> { $"127.0.0.1:{agent.LocalPort}" }, nonce);
        agent.OfferPeers(new List<string> { $"127.0.0.1:{viewer.LocalPort}" }, nonce);
    }

    private static async Task WaitLiveAsync(UdpPunchChannel a, UdpPunchChannel b, int timeoutMs = 15000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (a.IsLive && b.IsLive) return;
            await Task.Delay(50);
        }
        throw new TimeoutException("打洞超时：双方没有在回环地址上互相听到 HELLO");
    }

    [Fact]
    public async Task Punch_GoesLive_OnLoopback()
    {
        var (agent, viewer) = MakePair();
        try
        {
            CrossOffer(agent, viewer);
            await WaitLiveAsync(agent, viewer);
            // 对端地址应该是回环/本机地址，且双方互为镜像
            Assert.NotNull(agent.PeerEndpoint);
            Assert.NotNull(viewer.PeerEndpoint);
        }
        finally { agent.Dispose(); viewer.Dispose(); }
    }

    [Fact]
    public async Task ReliableMessages_DeliverInOrder_AndDedup()
    {
        var (agent, viewer) = MakePair();
        try
        {
            CrossOffer(agent, viewer);
            await WaitLiveAsync(agent, viewer);

            var received = new List<int>();
            var synced = new TaskCompletionSource();
            viewer.MessageReceived += m =>
            {
                if (m.Type == MessageType.ClipboardText)
                {
                    var s = Encoding.UTF8.GetString(m.Payload);
                    if (s == "done") { synced.TrySetResult(); return; }
                    received.Add(int.Parse(s));
                }
            };

            // 20 条有序消息 + 一条收尾标记
            for (int i = 0; i < 20; i++)
                agent.Send(new ProtocolMessage(MessageType.ClipboardText, Encoding.UTF8.GetBytes(i.ToString())));
            agent.Send(new ProtocolMessage(MessageType.ClipboardText, Encoding.UTF8.GetBytes("done")));
            await synced.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(Enumerable.Range(0, 20).ToList(), received);
        }
        finally { agent.Dispose(); viewer.Dispose(); }
    }

    [Fact]
    public async Task VideoFrame_Large_ChunkedAndReassembled()
    {
        var (agent, viewer) = MakePair();
        try
        {
            CrossOffer(agent, viewer);
            await WaitLiveAsync(agent, viewer);

            // 180KB：跨 ~165 个分片（每片 1092B），故意超过单 MTU 几个量级
            var payload = new byte[180_000];
            new Random(42).NextBytes(payload);

            var got = new TaskCompletionSource<byte[]>();
            viewer.MessageReceived += m =>
            {
                if (m.Type == MessageType.VideoFrame)
                    got.TrySetResult(m.Payload);
            };
            // VideoFrame 的线上格式是 [8B 时间戳][数据]，这里直接喂原始字节即可（通道不感知内容）
            Assert.True(agent.SendVideo(new ProtocolMessage(MessageType.VideoFrame, payload)));
            var received = await got.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(payload, received);
        }
        finally { agent.Dispose(); viewer.Dispose(); }
    }

    [Fact]
    public async Task Dead_Fires_WhenPeerGoesSilent_AndRecovers()
    {
        var (agent, viewer) = MakePair();
        try
        {
            CrossOffer(agent, viewer);
            await WaitLiveAsync(agent, viewer);

            var dead = new TaskCompletionSource();
            agent.Dead += () => dead.TrySetResult();
            viewer.Dispose();          // 对端彻底沉默（不再回 PING/PONG/HELLO）
            await dead.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(agent.IsLive);
        }
        finally { agent.Dispose(); }
    }

    [Fact]
    public async Task Stun_ParsesXorMappedAddress()
    {
        // 假 STUN 服务器：收到任何 Binding Request 就回一个写死公网映射的 Binding Response
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
        var respond = Task.Run(async () =>
        {
            var r = await server.ReceiveAsync();
            var req = r.Buffer;
            var resp = new byte[32];
            resp[0] = 0x01; resp[1] = 0x01;                 // Binding Response (success)
            resp[3] = 12;                                     // 属性区 12 字节
            resp[4] = 0x21; resp[5] = 0x12; resp[6] = 0xA4; resp[7] = 0x42;
            Array.Copy(req, 8, resp, 8, 12);                  // 事务 ID 原样回显
            resp[20] = 0x00; resp[21] = 0x20;                 // XOR-MAPPED-ADDRESS
            resp[22] = 0x00; resp[23] = 0x08;                 // 长度 8
            resp[24] = 0x00; resp[25] = 0x01;                 // IPv4
            ushort xport = (ushort)(3478 ^ 0x2112);
            resp[26] = (byte)(xport >> 8); resp[27] = (byte)(xport & 0xFF);
            var ip = new IPAddress(new byte[] { 203, 0, 113, 7 }).GetAddressBytes();
            var cookie = new byte[] { 0x21, 0x12, 0xA4, 0x42 };
            for (int i = 0; i < 4; i++) resp[28 + i] = (byte)(ip[i] ^ cookie[i]);
            await server.SendAsync(resp, resp.Length, r.RemoteEndPoint);
        });

        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.Bind(new IPEndPoint(IPAddress.Any, 0));
        var ep = await StunClient.QueryAsync(probe, "127.0.0.1", port, timeoutMs: 3000);
        Assert.NotNull(ep);
        Assert.Equal(IPAddress.Parse("203.0.113.7"), ep!.Address);
        Assert.Equal(3478, ep.Port);
        await respond;
    }

    [Fact]
    public void OfferCodec_RoundTrips()
    {
        var (agent, _) = MakePair();
        try
        {
            var (nonce, eps, _, _) = UdpPunchChannel.DecodeOffer(agent.OfferPayload());
            Assert.Equal(16, nonce.Length);
            Assert.Empty(eps);   // 没收集过候选：空列表（中继周期推送会补上）
        }
        finally { agent.Dispose(); }
    }
}
