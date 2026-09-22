using Agent.Common;

namespace Viewer.Services;

/// <summary>
/// 主控端 H.264 解码器（策划 §3.3：WebSocket 收帧 → 解码 → 渲染）。
/// 实现放在 Agent.Common.H264StreamDecoder —— 非阻塞喂流 + 最新帧胜出，
/// 与端到端验收工具共用同一套管线，避免两处实现行为不一致。
/// </summary>
public sealed class VideoDecoder : H264StreamDecoder
{
    public VideoDecoder(string ffmpegPath, Logger log) : base(ffmpegPath, log) { }
}
