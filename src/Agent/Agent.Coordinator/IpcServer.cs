using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading.Channels;
using Agent.Common;

namespace Agent.Coordinator;

/// <summary>
/// 跨会话 IPC 服务端（运行在 Session 0 的 Coordinator）：
/// - 指令通道： Named Pipe \\.\pipe\AgentControl（服务端 = 本进程）
/// - 帧通道：   共享内存 Global\FrameBuffer（本进程 = 读者）
/// DACL 显式放开 Authenticated Users，否则 Session 1 的 Worker 连不上（策划 §2.3）
/// </summary>
public sealed class IpcServer : IDisposable
{
    private readonly Logger _log;
    private readonly int _frameBufferSize;
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<ProtocolMessage> _sendQueue =
        Channel.CreateBounded<ProtocolMessage>(new BoundedChannelOptions(2048)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

    private SharedFrameRing? _ring;
    private NamedPipeServerStream? _pipe;
    private Task? _listenTask;
    private Task? _sendTask;
    private Thread? _framePump;
    private volatile bool _workerConnected;
    private long _framesForwarded;

    /// <summary>共享内存创建完成，参数=是否带上了低完整性标签</summary>
    public event Action<bool>? RingCreated;

    public event Action<ProtocolMessage>? WorkerMessage;
    public event Action<int>? WorkerConnected;
    public event Action<string>? WorkerDisconnected;
    public event Action<byte[]>? FrameReceived;

    public SharedFrameRing? Ring => _ring;
    public bool IsWorkerConnected => _workerConnected;
    public long FramesForwarded => Interlocked.Read(ref _framesForwarded);

    public IpcServer(Logger log, int frameBufferSize = IpcProtocol.FrameBufferSize)
    {
        _log = log;
        _frameBufferSize = frameBufferSize;
    }

    public void Start()
    {
        _ring = SharedFrameRing.Create(capacity: _frameBufferSize);
        try { RingCreated?.Invoke(SharedFrameRing.LastCreateUsedLabel); } catch { }
        _log.Info($"共享内存就绪：{IpcProtocol.FrameBufferName}（{_frameBufferSize / 1024}KB）");
        _listenTask = Task.Run(() => ListenLoopAsync(_cts.Token));
        _sendTask = Task.Run(() => SendPumpAsync(_cts.Token));
        _framePump = new Thread(FramePump) { IsBackground = true, Name = "FramePump" };
        _framePump.Start();
    }

    // ---------------------------------------------------------------- 收帧

    private void FramePump()
    {
        var log = _log;
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var frame = _ring?.Read(200);
                if (frame == null) continue;
                Interlocked.Increment(ref _framesForwarded);
                FrameReceived?.Invoke(frame);
            }
            catch (Exception ex)
            {
                log.Warn($"帧泵异常：{ex.Message}");
                Thread.Sleep(200);
            }
        }
    }

    // ---------------------------------------------------------------- 管道

    private static PipeSecurity BuildPipeSecurity(bool withIntegrityLabel)
    {
        // 与共享内存同源：DACL 放开 Authenticated Users。
        // 必须附低完整性标签：管理员（High 完整性）创建的管道会被强制标签拦住，
        // 普通用户（Medium）进程即使 DACL 有权限也连不上。
        var ps = new PipeSecurity();
        ps.SetSecurityDescriptorSddlForm(
            withIntegrityLabel ? NativeApi.CrossSessionSddl : NativeApi.CrossSessionSddlNoLabel,
            AccessControlSections.All);
        return ps;
    }

    private NamedPipeServerStream CreatePipe(bool withIntegrityLabel)
        => NamedPipeServerStreamAcl.Create(
            IpcProtocol.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough,
            64 * 1024, 512 * 1024, BuildPipeSecurity(withIntegrityLabel));

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                // 先试带低完整性标签；若进程没有 SeSecurityPrivilege 就降级为纯 DACL
                try
                {
                    pipe = CreatePipe(true);
                }
                catch (Exception exPipe) when (exPipe is IOException or UnauthorizedAccessException)
                {
                    _log.Warn($"带完整性标签创建管道失败（{exPipe.Message}），降级为纯 DACL");
                    pipe = CreatePipe(false);
                }
                _pipe = pipe;
                _log.Info($"等待 Worker 连接：{IpcProtocol.FullPipeName}");
                await pipe.WaitForConnectionAsync(ct);
                _log.Info("Worker 已连接 Named Pipe");

                // 新 Worker 挂接：丢弃上一轮的残留帧
                _ring?.Reset();
                _workerConnected = true;
                WorkerConnected?.Invoke(Environment.ProcessId);

                await ReadLoopAsync(pipe, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.Warn($"管道异常：{ex.GetType().Name}: {ex.Message}");
                await Task.Delay(500, ct);
            }
            finally
            {
                if (_workerConnected)
                {
                    _workerConnected = false;
                    WorkerDisconnected?.Invoke("管道断开");
                }
                try { pipe?.Dispose(); } catch { }
                if (ReferenceEquals(_pipe, pipe)) _pipe = null;
            }
        }
    }

    private async Task ReadLoopAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        var acc = new FrameAccumulator(ipcFraming: true);
        var buf = new byte[64 * 1024];
        var pending = new List<ProtocolMessage>(32);
        while (!ct.IsCancellationRequested)
        {
            int n = await pipe.ReadAsync(buf.AsMemory(), ct);
            if (n <= 0) break;
            acc.Append(buf, 0, n);
            pending.Clear();
            acc.Drain(pending);
            foreach (var m in pending)
            {
                if (m.Type == MessageType.Heartbeat) continue;
                try { WorkerMessage?.Invoke(m); }
                catch (Exception ex) { _log.Error($"处理 Worker 消息 {m.Type} 失败", ex); }
            }
        }
    }

    // ---------------------------------------------------------------- 发送

    public void Send(ProtocolMessage msg)
    {
        if (!_sendQueue.Writer.TryWrite(msg))
            _log.Warn($"IPC 发送队列已满，丢弃 {msg.Type}");
    }

    private async Task SendPumpAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            ProtocolMessage msg;
            try { msg = await _sendQueue.Reader.ReadAsync(ct); }
            catch (OperationCanceledException) { return; }
            var pipe = _pipe;
            if (pipe == null || !pipe.IsConnected) continue;
            try
            {
                var buf = IpcProtocol.Wrap(msg);
                await pipe.WriteAsync(buf.AsMemory(), ct);
                await pipe.FlushAsync(ct);
            }
            catch (Exception ex)
            {
                _log.Warn($"向 Worker 发送 {msg.Type} 失败：{ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _pipe?.Dispose(); } catch { }
        var t = _framePump;
        if (t != null && t.IsAlive) t.Join(500);
        _ring?.Dispose();
        _cts.Dispose();
    }
}
