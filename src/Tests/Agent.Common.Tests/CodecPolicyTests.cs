using Xunit;

namespace Agent.Common.Tests;

/// <summary>
/// 视频编码协商（CodecPolicy，2026-10-10 H.265）与 StreamReset 编码字节的单元测试。
/// 核心语义（对标 UU 的 min/AND 协商）：**双端都支持才启用**，任何一端不支持/不认识都回落 H.264
/// —— 热更新灰度期间新旧版本互连不受影响。
/// </summary>
public class CodecPolicyTests
{
    [Theory]
    [InlineData("auto", false, false, "h264")]   // 主控端不支持 → H.264
    [InlineData("auto", false, true, "h264")]
    [InlineData("auto", true, false, "h264")]    // 本机没有 H.265 硬编（auto 不用软编）→ H.264
    [InlineData("auto", true, true, "h265")]     // 双端都支持 → H.265
    [InlineData("", true, true, "h265")]         // 空 = auto
    [InlineData("AUTO", true, true, "h265")]     // 大小写不敏感
    [InlineData("h264", true, true, "h264")]     // 显式关闭 H.265 的运维开关
    [InlineData("h265", true, false, "h265")]    // 显式配置允许软编（libx265）
    [InlineData("h265", false, true, "h264")]    // 旧主控端解不了 H.265 → 强制也没用
    public void Decide_FollowsAndSemantics(string configured, bool viewerH265, bool hwH265, string expected)
    {
        Assert.Equal(expected, CodecPolicy.Decide(configured, viewerH265, hwH265));
    }

    [Fact]
    public void IsH265_IgnoresCaseAndNull()
    {
        Assert.True(CodecPolicy.IsH265("H265"));
        Assert.True(CodecPolicy.IsH265("h265"));
        Assert.False(CodecPolicy.IsH265("h264"));
        Assert.False(CodecPolicy.IsH265(null));
    }

    [Fact]
    public void StreamResetPayload_EmptyMeansH264()
    {
        // 旧版两端发的 StreamReset 都是空负载：必须解释成 H.264（向后兼容的关键）
        Assert.False(PayloadCodec.DecodeStreamReset(Array.Empty<byte>()));
        Assert.False(PayloadCodec.DecodeStreamReset(PayloadCodec.EncodeStreamReset(false)));
        Assert.True(PayloadCodec.DecodeStreamReset(PayloadCodec.EncodeStreamReset(true)));
        Assert.Equal(0, PayloadCodec.EncodeStreamReset(false).Length);
        Assert.Equal(1, PayloadCodec.EncodeStreamReset(true).Length);
    }

    [Fact]
    public void CapabilitiesInfo_RoundTrips()
    {
        var caps = new CapabilitiesInfo { CapVer = 1, H265Decode = true };
        var decoded = JsonCodec.Decode<CapabilitiesInfo>(JsonCodec.Encode(caps));
        Assert.NotNull(decoded);
        Assert.Equal(1, decoded!.CapVer);
        Assert.True(decoded.H265Decode);

        // 旧字段缺失时的默认值：主控端没上报能力 = 不支持 H.265
        var bare = JsonCodec.Decode<CapabilitiesInfo>(System.Text.Encoding.UTF8.GetBytes("{}"));
        Assert.NotNull(bare);
        Assert.False(bare!.H265Decode);
    }
}
