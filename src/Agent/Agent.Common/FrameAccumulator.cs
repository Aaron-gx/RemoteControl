namespace Agent.Common;

/// <summary>
/// 流式分帧缓冲：往里面喂任意字节，按帧格式吐完整消息。
/// 用于 Named Pipe / WebSocket 等"可能被拆包"的通道。
/// </summary>
public sealed class FrameAccumulator
{
    private byte[] _buf;
    private int _len;

    /// <summary>IPC 帧（4 字节长度前缀 + 协议帧）还是裸协议帧</summary>
    public bool IpcFraming { get; }

    public FrameAccumulator(bool ipcFraming, int initialCapacity = 64 * 1024)
    {
        IpcFraming = ipcFraming;
        _buf = new byte[initialCapacity];
        _len = 0;
    }

    public void Append(byte[] data, int offset, int count)
    {
        Ensure(_len + count);
        Buffer.BlockCopy(data, offset, _buf, _len, count);
        _len += count;
    }

    /// <summary>取出所有完整消息</summary>
    public void Drain(List<ProtocolMessage> output)
    {
        int pos = 0;
        while (pos < _len)
        {
            ProtocolMessage? msg;
            int consumed;
            if (IpcFraming)
                msg = IpcProtocol.TryParse(_buf.AsSpan(pos, _len - pos), out consumed);
            else
                msg = ProtocolMessage.TryParse(_buf.AsSpan(pos, _len - pos), out consumed);
            if (msg == null) break;
            output.Add(msg);
            pos += consumed;
        }
        if (pos > 0)
        {
            _len -= pos;
            if (_len > 0) Buffer.BlockCopy(_buf, pos, _buf, 0, _len);
        }
    }

    public int BufferedBytes => _len;

    private void Ensure(int needed)
    {
        if (_buf.Length >= needed) return;
        int cap = _buf.Length;
        while (cap < needed) cap *= 2;
        Array.Resize(ref _buf, cap);
    }
}
