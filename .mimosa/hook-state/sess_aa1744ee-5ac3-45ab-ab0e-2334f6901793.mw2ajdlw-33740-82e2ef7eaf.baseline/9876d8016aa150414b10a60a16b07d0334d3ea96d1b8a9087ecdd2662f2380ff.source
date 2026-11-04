using System.Net;
using System.Reflection;
using System.Text;
using Xunit;

namespace Agent.Common.Tests;

/// <summary>
/// UDP 通道升级（2026-10-14，对标 UU dckcp）的离线回环测试：
///   · 能力协商：双端报 cap → KCP/RS 生效；任一端旧（cap=0）→ 完整走旧路径（铁律 23：
///     协商类功能必须有"另一侧在场"的验证，也要有"另一侧缺席"的验证）；
///   · KCP 可靠路径：保序送达、对端死后的中继补发兜底（铁律 22：切换路径必须对端确认，
///     确认不来就得有兜底）；
///   · RS-FEC：组内丢 1~2 片零重传恢复、180KB 大帧补零-裁剪字节级还原、丢片超出冗余走 NACK。
/// </summary>
public class UdpKcpFecTests
{
    private static (UdpPunchChannel agent, UdpPunchChannel viewer) MakePair()
    {
        var log = new Logger("kcp-test", minLevel: 0, echoConsole: false);
        return (new UdpPunchChannel(log, PunchRole.Agent, "unit-test-token"),
                new UdpPunchChannel(log, PunchRole.Viewer, "unit-test-token"));
    }

    /// <summary>cap：3 = 双端新版（KCP+RS），0 = 旧端。默认 0（回归旧路径语义）。</summary>
    private static void CrossOffer(UdpPunchChannel agent, UdpPunchChannel viewer, int cap = 0)
    {
        var (nonce, _, _, _) = UdpPunchChannel.DecodeOffer(agent.OfferPayload());
        viewer.OfferPeers(new List<string> { $"127.0.0.1:{agent.LocalPort}" }, nonce, cap);
        agent.OfferPeers(new List<string> { $"127.0.0.1:{viewer.LocalPort}" }, nonce, cap);
    }

    private static async Task WaitLiveAsync(UdpPunchChannel a, UdpPunchChannel b, int timeoutMs = 15000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (a.IsLive && b.IsLive) return;
            await Task.Delay(50);
        }
        throw new TimeoutException("打洞超时");
    }

    /// <summary>把发送端 RS 冗余度窗口的丢包估计顶到 ≥5%（r=2），测试多片丢失恢复</summary>
    private static void ForceFecLoss(UdpPunchChannel sender, double frac)
    {
        var f = typeof(UdpPunchChannel).GetField("_fecLossFrac", BindingFlags.NonPublic | BindingFlags.Instance)!;
        f.SetValue(sender, frac);
    }

    [Fact]
    public void OfferPayload_CarriesLocalCap()
    {
        var (agent, _) = MakePair();
        try
        {
            var (_, _, _, cap) = UdpPunchChannel.DecodeOffer(agent.OfferPayload());
            Assert.Equal(UdpPunchChannel.LocalCap, cap);   // bit0=KCP bit1=RS，当前 = 3
        }
        finally { agent.Dispose(); }
    }

    [Fact]
    public async Task Caps_Negotiated_BothSides_KcpActive()
    {
        var (agent, viewer) = MakePair();
        try
        {
            CrossOffer(agent, viewer, cap: UdpPunchChannel.LocalCap);
            await WaitLiveAsync(agent, viewer);
            Assert.True(agent.KcpActive);
            Assert.True(viewer.KcpActive);
        }
        finally { agent.Dispose(); viewer.Dispose(); }
    }

    [Fact]
    public async Task LegacyPeer_Cap0_FallsBackToOldPath()
    {
        // 被控端新版、主控端旧（cap=0）：KCP/RS 都不启用，可靠消息走旧 ARQ 仍然可用
        var (agent, viewer) = MakePair();
        try
        {
            CrossOffer(agent, viewer, cap: 0);
            await WaitLiveAsync(agent, viewer);
            Assert.False(agent.KcpActive);
            Assert.False(viewer.KcpActive);
            Assert.Equal(0, agent.FecParityR);   // 非 RS 模式诊断口径 = 0

            var got = new TaskCompletionSource();
            viewer.MessageReceived += m =>
            {
                if (m.Type == MessageType.ClipboardText &&
                    Encoding.UTF8.GetString(m.Payload) == "legacy-ok") got.TrySetResult();
            };
            agent.Send(new ProtocolMessage(MessageType.ClipboardText, Encoding.UTF8.GetBytes("legacy-ok")));
            await got.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { agent.Dispose(); viewer.Dispose(); }
    }

    [Fact]
    public async Task KcpReliable_DeliversInOrder()
    {
        var (agent, viewer) = MakePair();
        try
        {
            CrossOffer(agent, viewer, cap: UdpPunchChannel.LocalCap);
            await WaitLiveAsync(agent, viewer);
            Assert.True(agent.KcpActive);

            var received = new List<int>();
            var done = new TaskCompletionSource();
            viewer.MessageReceived += m =>
            {
                if (m.Type != MessageType.ClipboardText) return;
                var s = Encoding.UTF8.GetString(m.Payload);
                if (s == "done") { done.TrySetResult(); return; }
                received.Add(int.Parse(s));
            };
            for (int i = 0; i < 30; i++)
                agent.Send(new ProtocolMessage(MessageType.ClipboardText, Encoding.UTF8.GetBytes(i.ToString())));
            agent.Send(new ProtocolMessage(MessageType.ClipboardText, Encoding.UTF8.GetBytes("done")));
            await done.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(Enumerable.Range(0, 30).ToList(), received);   // KCP 流保序
        }
        finally { agent.Dispose(); viewer.Dispose(); }
    }

    [Fact]
    public async Task KcpRelayFallback_Fires_WhenPeerDies()
    {
        // 铁律 22：KCP 路径的交付确认（带外 KcpAck）不来 → 400ms 后必须交中继补发
        var (agent, viewer) = MakePair();
        try
        {
            CrossOffer(agent, viewer, cap: UdpPunchChannel.LocalCap);
            await WaitLiveAsync(agent, viewer);

            viewer.Dispose();   // 对端彻底消失（不会回 KcpAck）
            var fallback = new TaskCompletionSource<ProtocolMessage>();
            var msg = new ProtocolMessage(MessageType.MouseButton, new byte[] { 1, 2, 3, 1 });
            agent.Send(msg, m => fallback.TrySetResult(m));
            var fired = await fallback.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(msg.Payload, fired.Payload);
        }
        finally { agent.Dispose(); }
    }

    [Fact]
    public async Task RsFec_SingleChunkLoss_RecoveredWithoutRetransmit()
    {
        var (agent, viewer) = MakePair();
        try
        {
            CrossOffer(agent, viewer, cap: UdpPunchChannel.LocalCap);
            await WaitLiveAsync(agent, viewer);

            // 20KB ≈ 19 片 → 2 组；丢 idx=5（第 0 组），r=1 即可零重传恢复
            var payload = new byte[20_000];
            new Random(7).NextBytes(payload);
            var got = new TaskCompletionSource<byte[]>();
            viewer.MessageReceived += m => { if (m.Type == MessageType.VideoFrame) got.TrySetResult(m.Payload); };
            agent.TestDropVideoChunk = (seq, idx) => idx == 5;

            Assert.True(agent.SendVideo(new ProtocolMessage(MessageType.VideoFrame, payload)));
            var received = await got.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(payload, received);
            Assert.True(viewer.FecRecoveredChunks >= 1, "应由 RS-FEC 零重传恢复（而非等待 NACK）");
        }
        finally { agent.Dispose(); viewer.Dispose(); }
    }

    [Fact]
    public async Task RsFec_TwoChunkLossInGroup_Recovered_WithAdaptiveR2()
    {
        var (agent, viewer) = MakePair();
        try
        {
            CrossOffer(agent, viewer, cap: UdpPunchChannel.LocalCap);
            await WaitLiveAsync(agent, viewer);
            ForceFecLoss(agent, 0.06);   // r = ParityForLoss(0.06) = 2

            var payload = new byte[20_000];
            new Random(8).NextBytes(payload);
            var got = new TaskCompletionSource<byte[]>();
            viewer.MessageReceived += m => { if (m.Type == MessageType.VideoFrame) got.TrySetResult(m.Payload); };
            agent.TestDropVideoChunk = (seq, idx) => idx == 3 || idx == 4;   // 同组丢 2 片

            Assert.True(agent.SendVideo(new ProtocolMessage(MessageType.VideoFrame, payload)));
            var received = await got.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(payload, received);
            Assert.True(viewer.FecRecoveredChunks >= 2, "同组 2 片丢失应由 RS（r=2）恢复");
        }
        finally { agent.Dispose(); viewer.Dispose(); }
    }

    [Fact]
    public async Task RsFec_LossBeyondParity_NackStillRecovers()
    {
        var (agent, viewer) = MakePair();
        try
        {
            CrossOffer(agent, viewer, cap: UdpPunchChannel.LocalCap);
            await WaitLiveAsync(agent, viewer);
            // r=1（无历史丢包）但同组丢 3 片：RS 不够 → NACK 重传兜底（80ms 宽限 + RTT）
            var payload = new byte[20_000];
            new Random(9).NextBytes(payload);
            var got = new TaskCompletionSource<byte[]>();
            viewer.MessageReceived += m => { if (m.Type == MessageType.VideoFrame) got.TrySetResult(m.Payload); };
            agent.TestDropVideoChunk = (seq, idx) => idx is 2 or 6 or 9;

            Assert.True(agent.SendVideo(new ProtocolMessage(MessageType.VideoFrame, payload)));
            var received = await got.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(payload, received);
        }
        finally { agent.Dispose(); viewer.Dispose(); }
    }

    [Fact]
    public async Task RsFec_LargeFrame_ZeroPadTrim_ByteIdentical()
    {
        var (agent, viewer) = MakePair();
        try
        {
            CrossOffer(agent, viewer, cap: UdpPunchChannel.LocalCap);
            await WaitLiveAsync(agent, viewer);

            // 180KB ≈ 165 片（末片补零 → 裁剪路径），无丢包全量对照
            var payload = new byte[180_000];
            new Random(42).NextBytes(payload);
            var got = new TaskCompletionSource<byte[]>();
            viewer.MessageReceived += m => { if (m.Type == MessageType.VideoFrame) got.TrySetResult(m.Payload); };
            Assert.True(agent.SendVideo(new ProtocolMessage(MessageType.VideoFrame, payload)));
            var received = await got.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(payload, received);
        }
        finally { agent.Dispose(); viewer.Dispose(); }
    }
}
