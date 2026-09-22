using System.Diagnostics;
using System.Text;

namespace Agent.Common;

/// <summary>
/// H.264 码流解码器（主控端用）：把收到的码流块喂给 ffmpeg，取回 BGRA 帧。
///
/// 关键设计（实时视频管线必须遵守）：
/// 1) <see cref="Feed"/> **绝不阻塞**：只有入队动作。网络接收循环一旦被解码器拖住，
///    中继服务器就会因为写超时把连接踢掉（实测坑）。
/// 2) 队列满即丢弃整段积压，并置 <see cref="NeedsStreamReset"/>：让上层重建解码器
///    并请求被控端重建码流（丢块会破坏 H.264 连续性，必须等新的 IDR）。
/// 3) 帧缓冲"最新帧胜出"：UI 按自身刷新率取用，网络抖动不会造成画面延迟堆积。
/// </summary>
public class H264StreamDecoder : IDisposable
{
    private readonly Logger _log;
    private readonly string _ffmpeg;
    private Process? _proc;
    private Stream? _stdin;
    private Thread? _reader;
    private Thread? _writer;
    private volatile bool _alive;
    private int _width, _height;

    private readonly object _queueLock = new();
    private readonly Queue<byte[]> _queue = new();
    private readonly SemaphoreSlim _queueSignal = new(0);
    private const int MaxQueuedChunks = 192;
    private const int MaxQueuedBytes = 6 * 1024 * 1024;
    private long _queuedBytes;

    private volatile bool _needsReset;
    private byte[]? _pending;
    private int _pendingLength;
    private long _decodedFrames, _droppedChunks;
    private readonly StringBuilder _stderrTail = new();
    private readonly object _poolLock = new();
    private readonly Stack<byte[]> _pool = new();

    /// <summary>需要重建解码器 + 请求对端重建码流</summary>
    public event Action<string>? StreamResetNeeded;

    public bool IsRunning => _alive;
    public long DecodedFrames => Interlocked.Read(ref _decodedFrames);
    public long DroppedChunks => Interlocked.Read(ref _droppedChunks);
    public bool NeedsStreamReset => _needsReset;
    public int Width => _width;
    public int Height => _height;

    public H264StreamDecoder(string ffmpegPath, Logger log)
    {
        _ffmpeg = ffmpegPath;
        _log = log;
    }

    public bool Configure(int width, int height)
    {
        if (width <= 0 || height <= 0) return false;
        if (_alive && width == _width && height == _height) return true;
        _width = width;
        _height = height;
        return Start();
    }

    private bool Start()
    {
        Stop();
        var args = "-hide_banner -loglevel error -fflags nobuffer -flags low_delay " +
                   "-probesize 32 -analyzeduration 0 -f h264 -i pipe:0 " +
                   $"-an -sn -vf format=bgra,scale={_width}:{_height} -f rawvideo pipe:1";
        try
        {
            var psi = new ProcessStartInfo(_ffmpeg, args)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardErrorEncoding = Encoding.UTF8,
            };
            var p = Process.Start(psi);
            if (p == null) throw new InvalidOperationException("ffmpeg 启动失败");
            _proc = p;
            _stdin = p.StandardInput.BaseStream;
            _alive = true;
            _needsReset = false;
            _reader = new Thread(ReadLoop) { IsBackground = true, Name = "decoder-out" };
            _reader.Start();
            _writer = new Thread(WriteLoop) { IsBackground = true, Name = "decoder-in" };
            _writer.Start();
            _ = Task.Run(() => ReadStderr(p));
            _log.Info($"解码器已启动：{_width}x{_height}（{Path.GetFileName(_ffmpeg)}）");
            return true;
        }
        catch (Exception ex)
        {
            _log.Error($"启动解码器失败：{ex.Message}");
            _alive = false;
            return false;
        }
    }

    // ---------------------------------------------------------------- 输入（非阻塞）

    /// <summary>喂入一段 H.264 码流；永不阻塞</summary>
    public void Feed(byte[] data, int offset, int count)
    {
        if (!_alive || count <= 0) return;
        var copy = new byte[count];
        Buffer.BlockCopy(data, offset, copy, 0, count);

        lock (_queueLock)
        {
            if (_queue.Count >= MaxQueuedChunks || _queuedBytes + count > MaxQueuedBytes)
            {
                // 积压过多：丢弃整段（H.264 已不连续，等新的 IDR 重建）
                Interlocked.Add(ref _droppedChunks, _queue.Count + 1);
                _queue.Clear();
                _queuedBytes = 0;
                if (!_needsReset)
                {
                    _needsReset = true;
                    _log.Warn($"解码器积压过多，丢弃积压并要求重建码流（累计丢弃 {_droppedChunks} 块）");
                    try { StreamResetNeeded?.Invoke("解码积压"); } catch { }
                }
                return;
            }
            _queue.Enqueue(copy);
            _queuedBytes += count;
        }
        try { _queueSignal.Release(); } catch { }
    }

    private void WriteLoop()
    {
        while (_alive)
        {
            try { _queueSignal.Wait(200); } catch { return; }
            while (true)
            {
                byte[]? chunk = null;
                lock (_queueLock)
                {
                    if (_queue.Count > 0)
                    {
                        chunk = _queue.Dequeue();
                        _queuedBytes -= chunk.Length;
                    }
                }
                if (chunk == null) break;
                var stdin = _stdin;
                if (!_alive || stdin == null) return;
                try
                {
                    stdin.Write(chunk, 0, chunk.Length);
                }
                catch (Exception ex)
                {
                    if (_alive) _log.Warn($"写入解码器失败：{ex.Message}");
                    _alive = false;
                    return;
                }
            }
        }
    }

    // ---------------------------------------------------------------- 输出

    private void ReadLoop()
    {
        int frameBytes = _width * _height * 4;
        try
        {
            var stdout = _proc!.StandardOutput.BaseStream;
            while (_alive)
            {
                var buf = Rent(frameBytes);
                int off = 0;
                while (off < frameBytes)
                {
                    int n = stdout.Read(buf, off, frameBytes - off);
                    if (n <= 0) throw new EndOfStreamException();
                    off += n;
                }
                Interlocked.Increment(ref _decodedFrames);
                var old = Interlocked.Exchange(ref _pending, buf);
                _pendingLength = frameBytes;
                if (old != null) Return(old);
            }
        }
        catch (Exception ex)
        {
            if (_alive) _log.Debug($"解码读取结束：{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _alive = false;
        }
    }

    private void ReadStderr(Process p)
    {
        try
        {
            string? line;
            while ((line = p.StandardError.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                lock (_stderrTail)
                {
                    _stderrTail.AppendLine(line);
                    if (_stderrTail.Length > 2000) _stderrTail.Remove(0, _stderrTail.Length - 2000);
                }
                _log.Debug($"解码器：{line}");
            }
        }
        catch { }
    }

    /// <summary>取最新一帧（无则 false）；取到后必须调用 <see cref="ReturnFrame"/></summary>
    public bool TryTakeFrame(out byte[] buffer, out int length)
    {
        var buf = Interlocked.Exchange(ref _pending, null);
        buffer = buf!;
        length = _pendingLength;
        return buf != null;
    }

    public void ReturnFrame(byte[] buffer) => Return(buffer);

    public void ClearNeedsReset() => _needsReset = false;

    /// <summary>重建解码器（必须在收到关键帧之前调用）</summary>
    public void Reset(string reason)
    {
        _log.Info($"重建解码器：{reason}");
        var w = _width; var h = _height;
        Stop();
        _width = w; _height = h;
        if (w > 0 && h > 0) Start();
    }

    public void Stop()
    {
        _alive = false;
        try { _queueSignal.Release(); } catch { }
        lock (_queueLock)
        {
            _queue.Clear();
            _queuedBytes = 0;
        }
        try { _stdin?.Close(); } catch { }
        _stdin = null;
        try
        {
            if (_proc is { HasExited: false } && !_proc.WaitForExit(800))
                _proc.Kill(entireProcessTree: true);
        }
        catch { }
        try { _proc?.Dispose(); } catch { }
        _proc = null;
        try { _reader?.Join(400); } catch { }
        try { _writer?.Join(400); } catch { }
        _reader = null;
        _writer = null;
        var old = Interlocked.Exchange(ref _pending, null);
        if (old != null) Return(old);
        if (_stderrTail.Length > 0)
        {
            _log.Debug($"解码器最后输出：{_stderrTail.ToString().Trim()}");
            _stderrTail.Clear();
        }
    }

    private byte[] Rent(int size)
    {
        lock (_poolLock)
        {
            while (_pool.Count > 0)
            {
                var b = _pool.Pop();
                if (b.Length >= size) return b;
            }
        }
        return new byte[size];
    }

    private void Return(byte[] buffer)
    {
        lock (_poolLock)
        {
            if (_pool.Count < 4) _pool.Push(buffer);
        }
    }

    public void Dispose() => Stop();
}
