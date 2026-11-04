using Agent.Common;
using Xunit;

namespace Agent.Common.Tests;

/// <summary>
/// EncoderCapabilityCache 单测（2026-10-14 重构，对标 UU codec capability cache 的准入规则）：
///  · 硬件成功落盘、软编仅内存、失败带 TTL；
///  · 环境变（换 ffmpeg/显卡）旧条目整体作废（铁律 23：缓存键必须覆盖"会改变结果"的全部维度）；
///  · 文件损坏按"没缓存过"处理。
/// </summary>
public sealed class EncoderCapabilityTests : IDisposable
{
    private readonly string _dir;

    public EncoderCapabilityTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "rc-encache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    private string PersistPath => Path.Combine(_dir, "encoder-cache.json");

    private static EncoderCapabilityId Id(string ffmpeg = "ffmpeg|1|1", string adapter = "gpu-a",
        string family = "h264", string encoder = "h264_nvenc", bool selfCapture = false) =>
        new(ffmpeg, adapter, family, encoder, selfCapture);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void HardwareOk_PersistedAcrossInstances_SoftwareAndFail_NotPersisted()
    {
        long now = 1_000_000;
        Func<long> clock = () => now;
        var a = new EncoderCapabilityCache(PersistPath, clock);
        {
            a.Record(Id(encoder: "h264_nvenc"), true);     // 硬件成功 → 落盘
            a.Record(Id(encoder: "libx264"), true);        // 软编成功 → 仅内存（UU 规则）
            a.Record(Id(encoder: "h264_amf"), false);      // 探测失败 → 仅内存 + TTL
        }
        // 重新加载（等价引擎被看门狗重启）：只有硬件 OK 活下来
        var b = new EncoderCapabilityCache(PersistPath, () => now + 1);
        Assert.True(b.Lookup(Id(encoder: "h264_nvenc")));
        Assert.Null(b.Lookup(Id(encoder: "libx264")));     // 软编没落盘 → 要重探
        Assert.Null(b.Lookup(Id(encoder: "h264_amf")));    // 失败没落盘 → 要重探
    }

    [Fact]
    public void NegativeResult_ExpiresAfterTtl()
    {
        long now = 1_000_000;
        var cache = new EncoderCapabilityCache(null, () => now);
        cache.Record(Id(encoder: "h264_qsv"), false);
        Assert.False(cache.Lookup(Id(encoder: "h264_qsv")));              // TTL 内 = 失败
        now += EncoderCapabilityCache.NegativeTtlMs;                      // 过期
        Assert.Null(cache.Lookup(Id(encoder: "h264_qsv")));               // 允许重探
    }

    [Fact]
    public void OkVerdict_DoesNotExpire()
    {
        long now = 1_000_000;
        var cache = new EncoderCapabilityCache(null, () => now);
        cache.Record(Id(encoder: "h264_nvenc"), true);
        now += EncoderCapabilityCache.NegativeTtlMs * 100;                // 再久也有效
        Assert.True(cache.Lookup(Id(encoder: "h264_nvenc")));
    }

    [Fact]
    public void EnvironmentChange_InvalidatesPersistedEntries()
    {
        // 环境 A（显卡 gpu-a）探测成功并落盘
        var a = new EncoderCapabilityCache(PersistPath);
        {
            a.Record(Id(adapter: "gpu-a", encoder: "h264_nvenc"), true);
        }
        // 环境 B（换了显卡 gpu-b）：旧条目必须作废（换卡后 nvenc 可用性完全可能变化）
        var b = new EncoderCapabilityCache(PersistPath);
        Assert.Null(b.Lookup(Id(adapter: "gpu-b", encoder: "h264_nvenc")));
        // 环境 B 自己的结论照常落盘，且覆盖掉 A 的条目（文件只描述一个环境）
        b.Record(Id(adapter: "gpu-b", encoder: "hevc_nvenc"), true);
        var c = new EncoderCapabilityCache(PersistPath);
        Assert.True(c.Lookup(Id(adapter: "gpu-b", encoder: "hevc_nvenc")));
        Assert.Null(c.Lookup(Id(adapter: "gpu-a", encoder: "h264_nvenc")));
    }

    [Fact]
    public void FfmpegChange_InvalidatesPersistedEntries()
    {
        var a = new EncoderCapabilityCache(PersistPath);
        {
            a.Record(Id(ffmpeg: "ffmpeg|old", encoder: "h264_nvenc"), true);
        }
        var b = new EncoderCapabilityCache(PersistPath);
        Assert.Null(b.Lookup(Id(ffmpeg: "ffmpeg|new|newer", encoder: "h264_nvenc")));
    }

    [Fact]
    public void FamilyAndSelfCapture_AreDistinctDimensions()
    {
        // 编码族维度（2026-10-11 H.265 黑屏事故根因）：h264 的结论绝不能喂给 h265
        long now = 1_000_000;
        var cache = new EncoderCapabilityCache(null, () => now);
        cache.Record(Id(family: "h264", encoder: "h264_qsv"), true);
        Assert.Null(cache.Lookup(Id(family: "h265", encoder: "h264_qsv")));
        // dda 维度：管道模式可用的 qsv ≠ dda 模式可用（dda 候选集不含 qsv）
        cache.Record(Id(family: "h264", encoder: "h264_nvenc", selfCapture: false), true);
        Assert.Null(cache.Lookup(Id(family: "h264", encoder: "h264_nvenc", selfCapture: true)));
    }

    [Fact]
    public void CorruptFile_TreatedAsNoCache()
    {
        File.WriteAllText(PersistPath, "{ this is not json !!!");
        var cache = new EncoderCapabilityCache(PersistPath);
        Assert.Null(cache.Lookup(Id()));
        cache.Record(Id(), true);   // 写入要能自愈（覆盖坏文件）
        var b = new EncoderCapabilityCache(PersistPath);
        Assert.True(b.Lookup(Id()));
    }

    [Fact]
    public void ForcedSoftware_RecordedToDiskForOps()
    {
        var a = new EncoderCapabilityCache(PersistPath);
        {
            a.RecordForcedSoftware("h264");
        }
        Assert.Contains("\"h264\"", File.ReadAllText(PersistPath));   // 运维可见（config/encoder-cache.json）
    }

    [Fact]
    public void IsSoftwareEncoder_CoversLibxFamily()
    {
        Assert.True(EncoderCapabilityCache.IsSoftwareEncoder("libx264"));
        Assert.True(EncoderCapabilityCache.IsSoftwareEncoder("libx265"));
        Assert.False(EncoderCapabilityCache.IsSoftwareEncoder("h264_qsv"));
        Assert.False(EncoderCapabilityCache.IsSoftwareEncoder("hevc_nvenc"));
    }
}
