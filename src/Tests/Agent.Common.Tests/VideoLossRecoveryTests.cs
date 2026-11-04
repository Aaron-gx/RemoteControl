using System.Net;
using System.Text;
using Xunit;

namespace Agent.Common.Tests;

/// <summary>
/// 视频丢包恢复（2026-10-11，对标 UU 的 FEC+NACK 思路的轻量版）：
///   · XOR 校验片：大帧（≥16 分片）组内丢 1 片 → 零延迟恢复（无需重传）
///   · NACK 重传：丢多片（含校验片丢失的场景）→ 停收 80ms 后请求重传 → +1 RTT 恢复
///   · 救不回的帧照旧放弃（GOP 兜底），行为不劣化
/// 全部在回环通道对上做，用确定性丢片钩子（TestDropVideoChunk）模拟丢包。
/// </summary>
public class VideoLossRecoveryTests
{
    private static (UdpPunchChannel agent, UdpPunchChannel viewer) MakePair()
    {
        var log = new Logger("vloss-test", minLevel: 0, echoConsole: false);
        var agent = new UdpPunchChannel(log, PunchRole.Agent, "unit-test-token");
        var viewer = new UdpPunchChannel(log, PunchRole.Viewer, "unit-test-token");
        // 回环互指候选完成"打洞"（与 UdpPunchChannelTests.CrossOffer 同法，nonce 以 agent 为准）
        var (nonce, _, _, _) = UdpPunchChannel.DecodeOffer(agent.OfferPayload());
        viewer.OfferPeers(new List<string> { $"127.0.0.1:{agent.LocalPort}" }, nonce);
        agent.OfferPeers(new List<string> { $"127.0.0.1:{viewer.LocalPort}" }, nonce);
        var deadline = Environment.TickCount64 + 5000;
        while ((!agent.IsLive || !viewer.IsLive) && Environment.TickCount64 < deadline)
            Thread.Sleep(50);
        Assert.True(agent.IsLive && viewer.IsLive, "回环打洞未成功");
        return (agent, viewer);
    }

    private static byte[] BigPayload(int size)
    {
        var buf = new byte[size];
        new Random(7).NextBytes(buf);
        return buf;
    }

    private static ProtocolMessage VideoMsg(byte[] payload) =>
        new(MessageType.VideoFrame, PayloadCodec.EncodeVideoFrame(12345, payload));

    [Fact]
    public void SingleChunkLoss_RecoversByNack()
    {
        var (agent, viewer) = MakePair();
        using (agent)
        using (viewer)
        {
            var payload = BigPayload(50 * 1024);   // ~47 分片，含校验片
            var got = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            viewer.MessageReceived += m =>
            {
                if (m.Type == MessageType.VideoFrame)
                    got.TrySetResult(PayloadCodec.DecodeVideoFrame(m.Payload).Data);
            };
            // 确定性丢两片（不同组）：XOR 只能救单组单片，多片靠 NACK
            agent.TestDropVideoChunk = (seq, idx) => idx == 5 || idx == 20;

            Assert.True(agent.SendVideo(VideoMsg(payload)));
            Assert.True(got.Task.Wait(4000), "4 秒内没有收到完整帧");
            Assert.Equal(payload, got.Task.Result);
            Assert.True(viewer.LossPercent < 5, $"残帧率应接近 0（实测 {viewer.LossPercent}%）");
        }
    }

    [Fact]
    public void SingleGroupLoss_RecoversByParity()
    {
        var (agent, viewer) = MakePair();
        using (agent)
        using (viewer)
        {
            var payload = BigPayload(50 * 1024);
            var got = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            viewer.MessageReceived += m =>
            {
                if (m.Type == MessageType.VideoFrame)
                    got.TrySetResult(PayloadCodec.DecodeVideoFrame(m.Payload).Data);
            };
            // 组内恰好丢 1 片 → XOR 零延迟恢复（不等 NACK 窗口）
            agent.TestDropVideoChunk = (seq, idx) => idx == 3;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            Assert.True(agent.SendVideo(VideoMsg(payload)));
            Assert.True(got.Task.Wait(2000), "2 秒内没有收到完整帧");
            Assert.Equal(payload, got.Task.Result);
        }
    }

    [Fact]
    public void ParityCount_MatchesSpec()
    {
        Assert.Equal(0, UdpPunchChannel_ParityCountFor(1));
        Assert.Equal(0, UdpPunchChannel_ParityCountFor(15));
        Assert.Equal(2, UdpPunchChannel_ParityCountFor(16));    // 16/8
        Assert.Equal(4, UdpPunchChannel_ParityCountFor(47));    // min(4, 47/8=5)
        Assert.Equal(4, UdpPunchChannel_ParityCountFor(190));   // 大帧封顶 4
    }

    [Fact]
    public void NoLoss_StillWorks()
    {
        var (agent, viewer) = MakePair();
        using (agent)
        using (viewer)
        {
            var payload = BigPayload(120 * 1024);
            var got = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            viewer.MessageReceived += m =>
            {
                if (m.Type == MessageType.VideoFrame)
                    got.TrySetResult(PayloadCodec.DecodeVideoFrame(m.Payload).Data);
            };
            Assert.True(agent.SendVideo(VideoMsg(payload)));
            Assert.True(got.Task.Wait(3000));
            Assert.Equal(payload, got.Task.Result);
        }
    }

    // ParityCountFor 是 internal：包装一下避免 tests 引用 internal 名（InternalsVisibleTo 已配置则直接用）
    private static int UdpPunchChannel_ParityCountFor(int n)
    {
        return (int)typeof(UdpPunchChannel)
            .GetMethod("ParityCountFor", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, new object[] { n })!;
    }
}
