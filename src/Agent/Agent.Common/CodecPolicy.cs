namespace Agent.Common;

/// <summary>
/// 视频编码协商策略（2026-10-10，对标 UU 远程 SDP 属性的 min/AND 协商语义）。
///
/// 为什么需要协商而不是"被控端自己说了算"：热更新灰度期间新旧版本会互连 ——
/// 新被控端能编 H.265，但旧主控端的解码器只会 `-f h264`，直接切过去就是黑屏。
/// 规则是"**双端都支持才启用**"（AND 语义）：主控端连上后上报 CapabilitiesInfo，
/// 被控端据此决定编码；旧主控端不上报 → 永远 H.264，新旧互通不受灰度影响。
/// </summary>
public static class CodecPolicy
{
    public const string H264 = "h264";
    public const string H265 = "h265";

    /// <summary>
    /// 决定本次会话用的编码。
    ///   · 配置 h264 → 永远 H.264（显式关闭 H.265 的运维开关）；
    ///   · 配置 h265 → 主控端支持 H.265 解码才启用（旧主控端收不了，退 H.264）；
    ///   · auto（默认）→ 主控端支持 **且本机有 H.265 硬件编码器**才启用。
    ///     auto 模式不用 libx265：软编 H.265 比 libx264 重得多，弱 CPU 连 1080p30 都跟不上，
    ///     "同画质省一半带宽"的前提是硬件编码（这也是 UU 按机器探测结果启用的做法）。
    /// </summary>
    public static string Decide(string configured, bool viewerH265, bool hwH265Encoder)
    {
        var cfg = (configured ?? "auto").Trim().ToLowerInvariant();
        if (cfg == H265) return viewerH265 ? H265 : H264;
        if (cfg == H264) return H264;
        return viewerH265 && hwH265Encoder ? H265 : H264;
    }

    public static bool IsH265(string? codec) =>
        string.Equals(codec, H265, StringComparison.OrdinalIgnoreCase);
}
