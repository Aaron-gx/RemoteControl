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
///
/// 【2026-10-09 真机事故加固】WIN-VSAQ 现场：toWorker 泵在一次 dispatch 里卡死
/// （鼠标/键盘/打开软件/剪贴板指令全部石沉大海，视频与状态照常流出——单方向死亡
/// 极难察觉，主控端表现为"画面在动但点什么都没反应，软件也调不出来"）。
/// 旧泵有两个致命点：① ReadAsync 抛非取消异常时 `catch { return; }` 让泵**永久静默死亡**；
/// ② dispatch 卡死时无任何检测。现在：
///   · 泵换成显式线程（栈可转储），每条消息带处理计时，>2s 记日志；
///   · 看门狗线程盯"正在处理"的消息：>15s 判定卡死 → 转储卡死线程栈 → 重建通道换新泵
///     （旧泵自然退出）→ 触发 PumpWedged 让上层硬重启引擎线程；
///   · 任何泵退出（正常关闭除外）都记日志并自动重生。
/// </summary>
public sealed class WorkerBridge : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    // 通道可变：任一方向卡死后换新通道（旧通道 Complete，卡死的旧泵醒来即退出）
    private Channel<ProtocolMessage> _toWorker = CreateChannel();
    private Channel<ProtocolMessage> _toCoordinator = CreateChannel();
    // 世代号：换泵后旧泵发现自己过时就退出，避免与新泵并发分发
    private int _toWorkerGeneration;
    private int _toCoordinatorGeneration;
    // "正在分发"的现场（0/空 = 空闲）：看门狗据此判定卡死（两个方向各一份）
    private long _inflightSinceTick;
    private volatile ProtocolMessage? _inflightMessage;
    private long _inflightToCoordSinceTick;
    private volatile ProtocolMessage? _inflightToCoordMessage;

    private static Channel<ProtocolMessage> CreateChannel()
        => Channel.CreateBounded<ProtocolMessage>(new BoundedChannelOptions(2048)
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
    /// <summary>泵自身的诊断日志（卡死转储/换泵/重生）。两侧 Logger 各自订阅，谁活着谁记。</summary>
    public event Action<string>? PumpLog;
    /// <summary>toWorker 分发卡死（已换新泵之后触发）。上层应转储引擎线程栈并硬重启引擎。</summary>
    public event Action? PumpWedged;

    private static void TryRaise(Action<string>? evt, string text)
    {
        try { evt?.Invoke(text); } catch { }
    }

    public WorkerBridge(int frameBufferBytes = 4 * 1024 * 1024)
    {
        Frames = new FrameChannel(frameBufferBytes);
        StartToWorkerPump(initial: true);
        StartToCoordinatorPump();
        // 看门狗：独立线程，5s 一巡。两个方向谁"正在处理"超时就地处置。
        var wd = new Thread(WatchdogLoop) { IsBackground = true, Name = "bridge-watchdog" };
        wd.Start();
    }

    // ---------------------------------------------------------------- 泵实现

    private void StartToWorkerPump(bool initial)
    {
        var t = new Thread(ToWorkerPumpLoop) { IsBackground = true, Name = "bridge-to-worker" };
        t.Start();
        if (!initial)
            TryRaise(PumpLog, "toWorker 泵已重建（新通道 + 新线程），控制指令恢复投递");
    }

    private void StartToCoordinatorPump()
    {
        var t = new Thread(ToCoordinatorPumpLoop) { IsBackground = true, Name = "bridge-to-coord" };
        t.Start();
    }

    /// <summary>同步读泵（专用线程，续 await 跳线程的问题不存在；WaitToReadAsync 超时醒来便于检查世代号）</summary>
    private void ToWorkerPumpLoop()
    {
        int myGeneration = Volatile.Read(ref _toWorkerGeneration);
        var ch = _toWorker;
        while (!_cts.IsCancellationRequested && myGeneration == Volatile.Read(ref _toWorkerGeneration))
        {
            ProtocolMessage msg;
            try
            {
                if (!ch.Reader.TryRead(out msg))
                {
                    var wait = ch.Reader.WaitToReadAsync(_cts.Token).AsTask();
                    if (!wait.Wait(1000))
                        continue;   // 周期醒来核对世代号（可能已被看门狗换掉）
                    if (!ch.Reader.TryRead(out msg))
                        continue;
                }
            }
            catch (OperationCanceledException) { return; }
            catch
            {
                // 通道被看门狗 Complete 换新（ChannelClosedException）属预期；其余也只在日志里留痕
                return;
            }

            _inflightMessage = msg;
            _inflightSinceTick = Environment.TickCount64;
            long start = _inflightSinceTick;
            try { MessageFromCoordinator?.Invoke(msg); }
            catch (Exception ex)
            {
                // 与旧实现一致：单条消息处理失败不中断通道；异常文本交给日志（不再静默吞）
                TryRaise(PumpLog, $"Worker 消息处理异常（{msg.Type}）：{ex.GetType().Name}: {ex.Message}");
            }
            finally { _inflightMessage = null; }
            long cost = Environment.TickCount64 - start;
            if (cost > 2000)
                TryRaise(PumpLog, $"⚠ Worker 消息 {msg.Type} 处理耗时 {cost / 1000.0:F1}s（>2s，疑似卡顿源）");
        }
    }

    private void ToCoordinatorPumpLoop()
    {
        int myGeneration = Volatile.Read(ref _toCoordinatorGeneration);
        var ch = _toCoordinator;
        while (!_cts.IsCancellationRequested && myGeneration == Volatile.Read(ref _toCoordinatorGeneration))
        {
            ProtocolMessage msg;
            try
            {
                if (!ch.Reader.TryRead(out msg))
                {
                    var wait = ch.Reader.WaitToReadAsync(_cts.Token).AsTask();
                    if (!wait.Wait(1000))
                        continue;
                    if (!ch.Reader.TryRead(out msg))
                        continue;
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                TryRaise(PumpLog, $"toCoordinator 泵异常退出（{ex.GetType().Name}: {ex.Message}），1s 后重生");
                Thread.Sleep(1000);
                StartToCoordinatorPump();
                return;
            }
            _inflightToCoordMessage = msg;
            _inflightToCoordSinceTick = Environment.TickCount64;
            long start = _inflightToCoordSinceTick;
            try
            {
                if (msg.Type == MessageType.Heartbeat) continue;   // 旧 IpcServer 也会滤掉心跳
                MessageFromWorker?.Invoke(msg);
            }
            catch (Exception ex)
            {
                TryRaise(PumpLog, $"Coordinator 消息处理异常（{msg.Type}）：{ex.GetType().Name}: {ex.Message}");
            }
            finally { _inflightToCoordMessage = null; }
            long cost = Environment.TickCount64 - start;
            if (cost > 2000)
                TryRaise(PumpLog, $"⚠ Coordinator 消息 {msg.Type} 处理耗时 {cost / 1000.0:F1}s（>2s，疑似卡顿源）");
        }
    }

    // ---------------------------------------------------------------- 看门狗

    /// <summary>一条消息允许的最长处理时间；超时视为泵卡死（换泵 + 上层硬重启引擎）。测试可调短。</summary>
    public int DispatchStallMs { get; set; } = 15_000;

    private void WatchdogLoop()
    {
        bool workerWedgedReported = false, coordWedgedReported = false;
        while (!_cts.IsCancellationRequested)
        {
            try { Thread.Sleep(5000); } catch { return; }

            // toCoordinator 方向：卡死只换泵（协调器处理器在新泵上继续跑），不动引擎
            var coordMsg = _inflightToCoordMessage;
            if (coordMsg == null) coordWedgedReported = false;
            else if (!coordWedgedReported &&
                     Environment.TickCount64 - Volatile.Read(ref _inflightToCoordSinceTick) >= DispatchStallMs)
            {
                coordWedgedReported = true;
                var dump = $"⚠⚠ toCoordinator 泵卡死（正在处理 {coordMsg.Type}），换新泵继续投递\n";
                TryRaise(PumpLog, dump);
                Interlocked.Increment(ref _toCoordinatorGeneration);
                var oldCoord = _toCoordinator;
                _toCoordinator = CreateChannel();
                try { oldCoord.Writer.TryComplete(); } catch { }
                StartToCoordinatorPump();
            }

            // toWorker 方向：卡死换泵（立刻恢复控制投递）+ 上层硬重启引擎
            var msg = _inflightMessage;
            if (msg == null) { workerWedgedReported = false; continue; }
            long stuckMs = Environment.TickCount64 - Volatile.Read(ref _inflightSinceTick);
            if (stuckMs < DispatchStallMs || workerWedgedReported) continue;
            workerWedgedReported = true;

            var dump2 = $"⚠⚠ toWorker 泵卡死 {stuckMs / 1000.0:F0}s（正在处理 {msg.Type}，负载前 80 字节=" +
                        Convert.ToHexString(msg.Payload is { Length: > 0 } ? msg.Payload[..Math.Min(80, msg.Payload.Length)] : Array.Empty<byte>()) +
                        "）。已换新泵；请拿消息类型对照 OnMessage 各 case 排查该处理器里的阻塞调用。\n";
            TryRaise(PumpLog, dump2);

            // 1) 先换通道换泵：不等引擎重启，控制指令立刻恢复投递
            RespawnToWorkerPump();
            // 2) 再通知上层：转储引擎 STA 线程栈 + 请求硬重启引擎（清掉可能同样卡死的钩子/钩宿主）
            try { PumpWedged?.Invoke(); } catch { }
        }
    }

    private void RespawnToWorkerPump()
    {
        Interlocked.Increment(ref _toWorkerGeneration);
        var old = _toWorker;
        _toWorker = CreateChannel();
        try { old.Writer.TryComplete(); } catch { }
        StartToWorkerPump(initial: false);
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
        // 引擎重启伴随换泵：新引擎从头开始，旧泵（若仍卡着）彻底退休
        Interlocked.Increment(ref _toWorkerGeneration);
        StartToWorkerPump(initial: true);   // 接管现有通道的全新世代（旧泵醒来即退）
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
