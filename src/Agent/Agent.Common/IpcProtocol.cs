using System.Buffers.Binary;

namespace Agent.Common;

/// <summary>
/// 跨会话 IPC（Named Pipe）帧格式： [4B BE 长度][ProtocolMessage 帧(1B Type+4B Len+Payload)]
/// 也就是把线上协议原样桥接一次，避免二次定义。
/// </summary>
public static class IpcProtocol
{
    public const string PipeName = "AgentControl";
    public const string FullPipeName = @"\\.\pipe\AgentControl";

    public const string FrameBufferName = @"Global\FrameBuffer";
    public const string FrameReadyEvent = @"Global\FrameReady";
    public const string FrameConsumedEvent = @"Global\FrameConsumed";

    /// <summary>帧通道默认容量 4MB（与策划一致）</summary>
    public const int FrameBufferSize = 4 * 1024 * 1024;

    public const int MaxIpcPayload = 64 * 1024 * 1024;

    public static byte[] Wrap(ProtocolMessage msg)
    {
        var frame = msg.ToFrame();
        var buf = new byte[4 + frame.Length];
        BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(0, 4), frame.Length);
        frame.CopyTo(buf, 4);
        return buf;
    }

    public static byte[] WrapHeader(int frameLength)
    {
        var buf = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buf, frameLength);
        return buf;
    }

    /// <summary>从缓冲区解析一条 IPC 消息，数据不足返回 null</summary>
    public static ProtocolMessage? TryParse(ReadOnlySpan<byte> buffer, out int consumed)
    {
        consumed = 0;
        if (buffer.Length < 4) return null;
        int len = BinaryPrimitives.ReadInt32BigEndian(buffer.Slice(0, 4));
        if (len < ProtocolMessage.HeaderSize || len > MaxIpcPayload)
            throw new ProtocolException($"非法 IPC 帧长度 {len}");
        if (buffer.Length < 4 + len) return null;
        var msg = ProtocolMessage.TryParse(buffer.Slice(4, len), out _)
                  ?? throw new ProtocolException("IPC 帧不完整");
        consumed = 4 + len;
        return msg;
    }

    public static ProtocolMessage? ReadFrom(Stream stream)
    {
        var lenBuf = ReadExactly(stream, 4);
        if (lenBuf == null) return null;
        int len = BinaryPrimitives.ReadInt32BigEndian(lenBuf);
        if (len < ProtocolMessage.HeaderSize || len > MaxIpcPayload)
            throw new ProtocolException($"非法 IPC 帧长度 {len}");
        var frame = ReadExactly(stream, len) ?? throw new ProtocolException("IPC 帧被截断");
        return ProtocolMessage.TryParse(frame, out _) ?? throw new ProtocolException("IPC 帧不完整");
    }

    public static void WriteTo(Stream stream, ProtocolMessage msg)
    {
        var buf = Wrap(msg);
        stream.Write(buf, 0, buf.Length);
        stream.Flush();
    }

    private static byte[]? ReadExactly(Stream s, int count)
    {
        var buf = new byte[count];
        int off = 0;
        while (off < count)
        {
            int n = s.Read(buf, off, count - off);
            if (n <= 0) return off == 0 ? null : throw new ProtocolException("IPC 连接提前关闭");
            off += n;
        }
        return buf;
    }
}
