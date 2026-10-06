namespace Agent.Common;

/// <summary>
/// 流式分帧缓冲：往里面喂任意字节，按线上协议帧格式吐完整消息。
/// 用于 WebSocket / TCP 这类"可能被拆包"的通道。
/// （旧版还有一个 ipcFraming 长度前缀模式，仅供跨进程 Named Pipe 使用 —— 进程合并后已随 IPC 一起删除。）
/// </summary>
public sealed class FrameAccumulator
{
    private byte[] _buf;
    private int _len;

    public FrameAccumulator(int initialCapacity = 64 * 1024)
    {
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
            var msg = ProtocolMessage.TryParse(_buf.AsSpan(pos, _len - pos), out int consumed);
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
