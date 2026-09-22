using System.IO.Pipes;
using System.Threading.Channels;
using Agent.Common;

namespace Agent.Worker;

/// <summary>
/// Worker 侧的跨会话 IPC（Session 1）：
/// - 指令通道： Named Pipe 客户端 → Coordinator
/// - 帧通道：   共享内存 Global\FrameBuffer（本进程 = 写者）
/// </summary>
public sealed class IpcClient : IDisposable
{
    private readonly Logger _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<ProtocolMessage> _sendQueue =
        Channel.CreateBounded<ProtocolMessage>(new BoundedChannelOptions(2048)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

    private NamedPipeClientStream? _pipe;
    private Task? _readTask;
    private Task? _writeTask;

    public SharedFrameRing Ring { get; private set; } = null!;
    public bool IsConnected => _pipe?.IsConnected == true;

    public event Action<ProtocolMessage>? MessageReceived;
    public event Action? ConnectedEvent;

    public IpcClient(Logger log) => _log = log;

    /// <summary>连接管道 + 打开共享内存（带重试）</summary>
    public bool Connect(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                Ring = SharedFrameRing.Open();
                _log.Info($"共享内存已打开（容量 {Ring.Capacity / 1024}KB）");

                var pipe = new NamedPipeClientStream(".", IpcProtocol.PipeName, PipeDirection.InOut,
                    PipeOptions.Asynchronous | PipeOptions.WriteThrough);
                pipe.Connect(5000);
                pipe.ReadMode = PipeTransmissionMode.Byte;
                _pipe = pipe;
                _log.Info("Named Pipe 已连接 Coordinator");

                _readTask = Task.Run(() => ReadLoopAsync(_cts.Token));
                _writeTask = Task.Run(() => WriteLoopAsync(_cts.Token));
                ConnectedEvent?.Invoke();
                return true;
            }
            catch (Exception ex)
            {
                last = ex;
                try { Ring?.Dispose(); } catch { }
                try { _pipe?.Dispose(); } catch { }
                _pipe = null;
                Thread.Sleep(1000);
            }
        }
        _log.Error($"连接 Coordinator 失败：{last?.Message}" +
                   (last?.InnerException != null ? $" / inner: {last.InnerException.Message}" : ""));
        return false;
    }

    public void Send(ProtocolMessage msg)
    {
        if (!_sendQueue.Writer.TryWrite(msg))
            _log.Warn($"IPC 发送队列满，丢弃 {msg.Type}");
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var acc = new FrameAccumulator(ipcFraming: true);
        var buf = new byte[64 * 1024];
        var pending = new List<ProtocolMessage>(16);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                int n = await _pipe!.ReadAsync(buf.AsMemory(), ct);
                if (n <= 0) break;
                acc.Append(buf, 0, n);
                pending.Clear();
                acc.Drain(pending);
                foreach (var m in pending)
                {
                    try { MessageReceived?.Invoke(m); }
                    catch (Exception ex) { _log.Error($"处理 {m.Type} 失败", ex); }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.Warn($"IPC 读取中断：{ex.Message}");
                break;
            }
        }
        _log.Warn("IPC 读取循环结束");
    }

    private async Task WriteLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            ProtocolMessage msg;
            try { msg = await _sendQueue.Reader.ReadAsync(ct); }
            catch (OperationCanceledException) { return; }
            try
            {
                var pipe = _pipe;
                if (pipe == null || !pipe.IsConnected) continue;
                var buf = IpcProtocol.Wrap(msg);
                await pipe.WriteAsync(buf.AsMemory(), ct);
                await pipe.FlushAsync(ct);
            }
            catch (Exception ex)
            {
                _log.Warn($"IPC 发送 {msg.Type} 失败：{ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _pipe?.Dispose(); } catch { }
        try { Ring?.Dispose(); } catch { }
        _cts.Dispose();
    }
}
