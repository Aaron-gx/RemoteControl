using System.Threading.Channels;

namespace Agent.Common;

/// <summary>
/// 进程内帧通道：取代旧架构里跨会话的共享内存帧环（SharedFrameRing）。
/// 语义与旧实现一一对应，调用方几乎不用改：
///  - Write 按字节数限容，写不进就在 timeoutMs 内等，等不到返回 false（调用方丢弃并计数，交给码率自适应降档）；
///  - Read 带超时地取一块（取不到返回 null）；
///  - Reset 丢弃缓冲里所有残帧（重建码流时清掉旧码流）；
///  - FillRatio / DropCount 供诊断日志使用。
/// </summary>
public sealed class FrameChannel : IDisposable
{
    private readonly object _gate = new();
    private readonly Queue<byte[]> _queue = new();
    private readonly int _capacityBytes;
    private long _usedBytes;
    private long _dropped;
    private long _delivered;
    private bool _disposed;

    /// <param name="capacityBytes">按字节限容（默认 4MB，与旧共享内存帧环一致）</param>
    public FrameChannel(int capacityBytes = 4 * 1024 * 1024)
        => _capacityBytes = Math.Max(64 * 1024, capacityBytes);

    public int Capacity => _capacityBytes;
    public long DropCount => Interlocked.Read(ref _dropped);
    public long DeliveredCount => Interlocked.Read(ref _delivered);
    public double FillRatio => _capacityBytes <= 0 ? 0 : (double)Volatile.Read(ref _usedBytes) / _capacityBytes;

    /// <summary>
    /// 写入一个码流块（内部拷贝，调用方的缓冲可以复用）。
    /// 缓冲满时最多等 timeoutMs，超时返回 false —— 与旧帧环"写不进就丢这一块并计数"的策略一致：
    /// 不重建码流（重建会让主控端重新等关键帧，慢链路上画面永远出不来），靠码率自适应降档收敛。
    /// </summary>
    public bool Write(ReadOnlySpan<byte> data, int timeoutMs = 250)
    {
        var copy = data.ToArray();
        long deadline = Environment.TickCount64 + Math.Max(0, timeoutMs);
        lock (_gate)
        {
            while (Volatile.Read(ref _usedBytes) + copy.Length > _capacityBytes)
            {
                long wait = deadline - Environment.TickCount64;
                if (wait <= 0)
                {
                    Interlocked.Increment(ref _dropped);
                    return false;
                }
                Monitor.Wait(_gate, (int)Math.Min(wait, 100));
            }
            _queue.Enqueue(copy);
            _usedBytes += copy.Length;
            Monitor.PulseAll(_gate);
            return true;
        }
    }

    /// <summary>取一个码流块；timeoutMs 内没有新块返回 null</summary>
    public byte[]? Read(int timeoutMs)
    {
        long deadline = Environment.TickCount64 + Math.Max(0, timeoutMs);
        lock (_gate)
        {
            while (_queue.Count == 0)
            {
                long wait = deadline - Environment.TickCount64;
                if (wait <= 0) return null;
                Monitor.Wait(_gate, (int)Math.Min(wait, 100));
            }
            var buf = _queue.Dequeue();
            _usedBytes -= buf.Length;
            Interlocked.Increment(ref _delivered);
            Monitor.PulseAll(_gate);
            return buf;
        }
    }

    /// <summary>丢弃缓冲里所有块（重建码流时清残帧用），并唤醒可能在等待的写者</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _queue.Clear();
            _usedBytes = 0;
            Monitor.PulseAll(_gate);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _queue.Clear();
            _usedBytes = 0;
            Monitor.PulseAll(_gate);
        }
    }
}

/// <summary>
/// Coordinator ⇄ Worker 的进程内桥：取代旧架构里跨会话的 Named Pipe 双向通道。
/// 两个方向各自一条**串行分发线程**（Channel 消费者）—— 这不是过度设计：
/// 旧实现里管道读写各自单线程，消息处理器天然串行、永不被发送方阻塞；
/// 沿用同样的模型，WorkerContext / CoordinatorApp 两侧的处理器就**完全不需要考虑并发**，
/// 与合并前的行为逐点一致。队列 2048 深 + DropOldest 也与旧 IpcClient/IpcServer 相同。
/// </summary>
public sealed class WorkerBridge : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<ProtocolMessage> _toWorker =
        Channel.CreateBounded<ProtocolMessage>(new BoundedChannelOptions(2048)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
    private readonly Channel<ProtocolMessage> _toCoordinator =
        Channel.CreateBounded<ProtocolMessage>(new BoundedChannelOptions(2048)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

    /// <summary>码流帧通道（Worker 写、Coordinator 读）。容量取自配置的字节数。</summary>
    public FrameChannel Frames { get; }

    public bool IsWorkerConnected { get; private set; }

    /// <summary>Worker 引擎线程收到 Coordinator 的消息（在分发线程上回调，串行）</summary>
    public event Action<ProtocolMessage>? MessageFromCoordinator;
    /// <summary>Coordinator 收到 Worker 的消息（在分发线程上回调，串行；Heartbeat 一律丢弃，与旧 IPC 一致）</summary>
    public event Action<ProtocolMessage>? MessageFromWorker;
    /// <summary>Worker 引擎线程上线 / 退出（取代旧 IpcServer 的管道连接/断开事件）</summary>
    public event Action? WorkerConnected;
    public event Action<string>? WorkerDisconnected;

    public WorkerBridge(int frameBufferBytes = 4 * 1024 * 1024)
    {
        Frames = new FrameChannel(frameBufferBytes);
        _ = Task.Run(() => PumpAsync(_toWorker, _cts.Token, msg => MessageFromCoordinator?.Invoke(msg), workerSide: true));
        _ = Task.Run(() => PumpAsync(_toCoordinator, _cts.Token, msg =>
        {
            if (msg.Type == MessageType.Heartbeat) return;   // 旧 IpcServer 也会滤掉心跳
            MessageFromWorker?.Invoke(msg);
        }, workerSide: false));
    }

    private async Task PumpAsync(Channel<ProtocolMessage> ch, CancellationToken ct, Action<ProtocolMessage> dispatch, bool workerSide)
    {
        while (!ct.IsCancellationRequested)
        {
            ProtocolMessage msg;
            try { msg = await ch.Reader.ReadAsync(ct); }
            catch (OperationCanceledException) { return; }
            catch { return; }
            try { dispatch(msg); }
            catch (Exception)
            {
                // 与旧实现一致：单条消息处理失败只记日志不中断通道（这里没有 Logger 引用，静默吞掉；
                // 真正的处理方（WorkerContext/CoordinatorApp）自己有 try/catch 记日志）
            }
        }
    }

    /// <summary>Coordinator → Worker（原 IpcServer.Send）</summary>
    public void SendToWorker(ProtocolMessage msg) => _toWorker.Writer.TryWrite(msg);

    /// <summary>Worker → Coordinator（原 IpcClient.Send）</summary>
    public void SendToCoordinator(ProtocolMessage msg) => _toCoordinator.Writer.TryWrite(msg);

    /// <summary>由承载 Worker 引擎的一方调用：引擎线程开始跑（取代旧"管道已连接"）</summary>
    public void MarkWorkerConnected()
    {
        if (IsWorkerConnected) return;
        IsWorkerConnected = true;
        Frames.Reset();
        // 清掉引擎掉线期间积压的旧指令（键鼠/打开软件）：旧管道架构里这些消息会因"管道未连接"直接丢弃，
        // 不清的话会把过期输入投给重启后的新引擎（比如突然打开一个几分钟前点过的程序）。
        while (_toWorker.Reader.TryRead(out _)) { }
        try { WorkerConnected?.Invoke(); } catch { }
    }

    /// <summary>由承载 Worker 引擎的一方调用：引擎线程退出（取代旧"管道断开"）</summary>
    public void MarkWorkerDisconnected(string reason)
    {
        if (!IsWorkerConnected) return;
        IsWorkerConnected = false;
        try { WorkerDisconnected?.Invoke(reason); } catch { }
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        Frames.Dispose();
        _cts.Dispose();
    }
}

/// <summary>当前进程的会话号/是否管理员（原 SessionLauncher 里的一小部分，合并后仍用于日志）</summary>
public static class SessionInfo
{
    public static int CurrentId
    {
        get
        {
            NativeApi.ProcessIdToSessionId(Environment.ProcessId, out int sid);
            return sid;
        }
    }

    public static bool IsElevated
    {
        get
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(id)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
    }
}
