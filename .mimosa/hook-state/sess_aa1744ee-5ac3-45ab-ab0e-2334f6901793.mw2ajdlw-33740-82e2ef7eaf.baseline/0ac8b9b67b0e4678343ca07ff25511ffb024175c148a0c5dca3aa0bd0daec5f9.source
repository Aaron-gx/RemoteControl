using System.Runtime.InteropServices;
using KcpTransport.LowLevel;

namespace Agent.Common;

/// <summary>
/// ikcp 会话包装（2026-10-14，替代 UdpPunchChannel 手搓的"60ms 定时重传"可靠路径，
/// 参数对标 UU 远程 dckcp 的 Start 实测值——见 uu-reverse/docs/transport/kcp.md）：
///   ikcp_wndsize(snd=256, rcv=256)
///   ikcp_nodelay(nodelay=1, interval=5ms, resend=2, nc=1)   ← nc=1 关拥塞窗：
///       拥塞控制让位给上层（BitrateGovernor 码率阶梯），避免双层拥塞互相拉扯
///   rx_minrto = 10ms                                        ← 丢包后最快 10ms 就重传
/// 流式消息成帧：[u32 msgId][4B 帧长][ProtocolMessage 帧字节]。
/// 拥塞窗关闭 + 快重传 2：游戏级低延迟配置，丢 1 包通常一个 RTT 内恢复。
/// </summary>
internal sealed unsafe class KcpSession : IDisposable
{
    /// <summary>KCP MTU：UDP 数据报 1100B 减 1 字节 KindKcp 多路标签</summary>
    public const int Mtu = 1099;
    /// <summary>发送队列深度保险丝（waitsnd 超过即拒绝入队，调用方立刻走中继补发）</summary>
    public const int MaxWaitSend = 512;

    private readonly object _gate = new();
    private readonly IKCPCB* _kcp;
    private readonly GCHandle _user;          // output 回调拿回 this
    private readonly Thread _tick;
    private volatile bool _disposed;

    /// <summary>KCP 输出回调：把 KCP 段作为 [KindKcp] 数据报发出去（由宿主通道实现）</summary>
    public delegate void SendSegment(ReadOnlySpan<byte> segment);
    /// <summary>完整收到一条成帧消息（msgId + 帧字节，已在流上保序保可靠）</summary>
    public event Action<uint, byte[]>? FrameReceived;

    public long BytesSent { get; private set; }
    public long BytesRecv { get; private set; }

    private readonly SendSegment _sender;

    private KcpSession(uint conv, SendSegment sender)
    {
        _sender = sender;
        _user = GCHandle.Alloc(this);
        _kcp = KcpMethods.ikcp_create(conv, (void*)GCHandle.ToIntPtr(_user));
        KcpMethods.ikcp_wndsize(_kcp, 256, 256);
        KcpMethods.ikcp_nodelay(_kcp, nodelay: 1, interval: 5, resend: 2, nc: 1);
        _kcp->rx_minrto = 10;
        KcpMethods.ikcp_setmtu(_kcp, Mtu);
        KcpMethods.ikcp_setoutput(_kcp, &KcpOutputThunk);
        _tick = new Thread(TickLoop) { IsBackground = true, Name = "udp-kcp-tick" };
        _tick.Start();
    }

    public static KcpSession Create(uint conv, SendSegment sender) => new(conv, sender);

    /// <summary>ikcp 的 output 回调（静态函数指针，user 句柄找回会话）</summary>
    private static int KcpOutputThunk(byte* buf, int len, IKCPCB* kcp, void* user)
    {
        var session = (KcpSession?)GCHandle.FromIntPtr((nint)user).Target;
        if (session == null || session._disposed) return -1;
        var seg = new ReadOnlySpan<byte>(buf, len);
        session._sender(seg);
        session.BytesSent += len;
        return 0;
    }

    /// <summary>
    /// 发一条可靠消息（msgId 由调用方编号；返回 false = 发送队列积压，调用方应立即中继补发）。
    /// KCP 流本身保序保可靠，这里不再做应用层重传。
    /// </summary>
    public bool Send(uint msgId, byte[] frame)
    {
        var buf = new byte[4 + 4 + frame.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(buf, msgId);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(4), frame.Length);
        frame.CopyTo(buf, 8);
        lock (_gate)
        {
            if (_disposed) return false;
            if (KcpMethods.ikcp_waitsnd(_kcp) > MaxWaitSend) return false;
            fixed (byte* p = buf)
            {
                return KcpMethods.ikcp_send(_kcp, p, buf.Length) >= 0;
            }
        }
    }

    /// <summary>收到 [KindKcp] 数据报：喂给 KCP，把完整成帧消息抽出来回调</summary>
    public void Input(ReadOnlySpan<byte> dgram)
    {
        List<(uint Id, byte[] Frame)>? ready = null;
        lock (_gate)
        {
            if (_disposed || dgram.IsEmpty) return;
            BytesRecv += dgram.Length;
            fixed (byte* p = dgram)
            {
                KcpMethods.ikcp_input(_kcp, p, dgram.Length);
            }
            // 排出所有已就绪的完整消息（ikcp_peeksize = 下一条消息的字节数）
            while (true)
            {
                int size = KcpMethods.ikcp_peeksize(_kcp);
                if (size < (int)8) break;
                var buf = new byte[size];
                fixed (byte* bp = buf)
                {
                    if (KcpMethods.ikcp_recv(_kcp, bp, size) < 0) break;
                }
                uint id = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(buf);
                int len = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(buf.AsSpan(4));
                if (len < 0 || 8 + len > size) break;   // 帧损坏：丢掉后续（流上不该发生）
                var frame = new byte[len];
                Buffer.BlockCopy(buf, 8, frame, 0, len);
                (ready ??= new List<(uint, byte[])>()).Add((id, frame));
            }
        }
        if (ready == null) return;
        foreach (var (id, frame) in ready)
        {
            try { FrameReceived?.Invoke(id, frame); }
            catch { /* 回调异常不放进锁里传播 */ }
        }
    }

    private void TickLoop()
    {
        while (!_disposed)
        {
            Thread.Sleep(5);   // UU interval=5ms
            lock (_gate)
            {
                if (_disposed) break;
                KcpMethods.ikcp_update(_kcp, (uint)Environment.TickCount64);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _tick.Join(200); } catch { }
        lock (_gate)
        {
            try { KcpMethods.ikcp_release(_kcp); } catch { }
        }
        _user.Free();
    }
}
