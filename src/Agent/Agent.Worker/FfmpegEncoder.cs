using System.Diagnostics;
using System.Text;
using Agent.Common;

namespace Agent.Worker;

/// <summary>
/// H.264 编码器：把 BGRA 原始帧喂给 ffmpeg stdin，从 stdout 读出 Annex-B 码流块。
/// 编码器候选： h264_nvenc / h264_qsv / h264_amf / libx264（自动探测，失败逐级回退）
/// </summary>
public sealed class FfmpegEncoder : IDisposable
{
    private readonly Logger _log;
    private readonly string _ffmpeg;
    private readonly int _width, _height, _fps, _kbps;
    private readonly string _configured;
    private Process? _proc;
    private Thread? _reader;
    private Stream? _stdin;
    private volatile bool _alive;
    private readonly StringBuilder _stderrTail = new();
    private long _chunkCount;
    private long _byteCount;

    /// <summary>读到一个码流块（buffer 仅在回调内有效）</summary>
    public event Action<byte[], int>? Chunk;

    public string EncoderName { get; private set; } = "";
    public bool IsAlive => _alive;
    /// <summary>ffmpeg 标准输入（采集器直接往这里写原始帧）</summary>
    public Stream? Stdin => _stdin;
    public string LastError { get; private set; } = "";
    public long ChunkCount => Interlocked.Read(ref _chunkCount);
    public long ByteCount => Interlocked.Read(ref _byteCount);
    /// <summary>当前编码器认的输入尺寸（必须是构造时传进来的那个，改不了）</summary>
    public int Width => _width;
    public int Height => _height;

    public FfmpegEncoder(string ffmpegPath, string configuredEncoder, int width, int height, int fps, int kbps, Logger log)
    {
        _ffmpeg = ffmpegPath;
        _configured = configuredEncoder;
        _width = width;
        _height = height;
        _fps = fps;
        _kbps = kbps;
        _log = log;
    }

    // ---------------------------------------------------------------- 编码器探测

    private static readonly string[] AutoOrder = { "h264_nvenc", "h264_qsv", "h264_amf", "libx264" };

    // 探测结果缓存（探测一次约 2.5s，重建码流时不应重复付这个代价）
    private static readonly Dictionary<string, string> EncoderCache = new(StringComparer.OrdinalIgnoreCase);

    // 运行期发现硬件编码器反复失败时置位：此后一律优先软件编码
    private static volatile bool _forceSoftware;

    /// <summary>
    /// 运行期降级到软件编码 libx264。
    /// 硬件编码器（nvenc/qsv/amf）会被"打开的应用"抢走 —— 模拟器 / 游戏 / 播放器占满或复位
    /// GPU 会话之后，nvenc 会一直启动失败，而探测结果是**缓存住**的，于是每次重建都拿同一个
    /// 坏编码器再失败一次，画面永远回不来（用户看到的"打开应用就黑屏卡掉"）。
    /// </summary>
    public static void ForceSoftware(string reason, Logger log)
    {
        if (_forceSoftware) return;
        _forceSoftware = true;
        log.Warn($"编码器降级为软件编码 libx264（原因：{reason}）");
    }

    public static string ResolveEncoder(string ffmpeg, string configured, Logger log)
    {
        if (_forceSoftware)
        {
            var swKey = $"{ffmpeg}|__software";
            lock (EncoderCache)
            {
                if (EncoderCache.TryGetValue(swKey, out var cachedSw)) return cachedSw;
            }
            if (Probe(ffmpeg, "libx264"))
            {
                lock (EncoderCache) EncoderCache[swKey] = "libx264";
                return "libx264";
            }
        }

        var cacheKey = $"{ffmpeg}|{configured}";
        lock (EncoderCache)
        {
            if (EncoderCache.TryGetValue(cacheKey, out var cached)) return cached;
        }

        string resolved;
        if (!string.IsNullOrWhiteSpace(configured) && !configured.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            if (Probe(ffmpeg, configured)) resolved = configured;
            else
            {
                log.Warn($"配置的编码器 {configured} 不可用，回退自动选择");
                resolved = ProbeInOrder(ffmpeg, log);
            }
        }
        else
        {
            resolved = ProbeInOrder(ffmpeg, log);
        }
        lock (EncoderCache) EncoderCache[cacheKey] = resolved;
        return resolved;
    }

    private static string ProbeInOrder(string ffmpeg, Logger log)
    {
        foreach (var cand in AutoOrder)
        {
            if (Probe(ffmpeg, cand))
            {
                log.Info($"自动选择编码器：{cand}");
                return cand;
            }
            log.Debug($"编码器 {cand} 不可用");
        }
        throw new InvalidOperationException("找不到可用的 H.264 编码器（nvenc/qsv/amf/libx264 都失败）");
    }

    private static bool Probe(string ffmpeg, string encoder)
    {
        try
        {
            var psi = new ProcessStartInfo(ffmpeg,
                $"-hide_banner -v error -f lavfi -i color=c=black:s=64x64:r=1 -frames:v 1 -c:v {encoder} -f null -")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            p.StandardError.ReadToEnd();
            p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(15000))
            {
                try { p.Kill(true); } catch { }
                return false;
            }
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    // ---------------------------------------------------------------- 生命周期

    public bool Start()
    {
        Stop();
        EncoderName = ResolveEncoder(_ffmpeg, _configured, _log);
        var args = BuildArgs(EncoderName);
        _log.Info($"启动 ffmpeg：{EncoderName} {_width}x{_height}@{_fps} {_kbps}kbps");
        _stderrTail.Clear();
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
            _reader = new Thread(ReadLoop) { IsBackground = true, Name = "ffmpeg-stdout" };
            _reader.Start();
            _ = Task.Run(() => ReadStderr(p));
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"启动 ffmpeg 失败：{ex.Message}";
            _log.Error(LastError);
            _alive = false;
            return false;
        }
    }

    private string BuildArgs(string encoder)
    {
        var sb = new StringBuilder();
        sb.Append("-hide_banner -loglevel error -nostdin ");
        sb.Append("-fflags nobuffer -flags low_delay ");
        sb.Append($"-f rawvideo -pixel_format bgra -video_size {_width}x{_height} -framerate {_fps} -i pipe:0 ");
        sb.Append("-an -sn -dn ");
        sb.Append($"-c:v {encoder} ");
        switch (encoder)
        {
            case "libx264":
                sb.Append("-preset ultrafast -tune zerolatency -profile:v baseline -level 4.0 ");
                sb.Append("-x264-params \"scenecut=0:rc-lookahead=0:sync-lookahead=0:repeat-headers=1\" ");
                break;
            case "h264_nvenc":
                sb.Append("-preset p1 -tune ull -rc cbr -zerolatency 1 -profile:v baseline -delay 0 ");
                break;
            case "h264_qsv":
                sb.Append("-preset veryfast -profile:v baseline -low_power 1 ");
                break;
            case "h264_amf":
                sb.Append("-quality speed -usage lowlatency -rc cbr -profile:v baseline ");
                break;
            default:
                sb.Append("-profile:v baseline ");
                break;
        }
        int gop = Math.Max(1, _fps * 2);
        sb.Append($"-b:v {_kbps}k -maxrate {_kbps}k -bufsize {_kbps * 2}k ");
        sb.Append($"-g {gop} -keyint_min {_fps} -sc_threshold 0 -pix_fmt yuv420p -bf 0 ");
        sb.Append("-flush_packets 1 -f h264 pipe:1");
        return sb.ToString();
    }

    private void ReadLoop()
    {
        var buf = new byte[16 * 1024];
        try
        {
            var stdout = _proc!.StandardOutput.BaseStream;
            while (_alive)
            {
                int n = stdout.Read(buf, 0, buf.Length);
                if (n <= 0) break;
                Interlocked.Increment(ref _chunkCount);
                Interlocked.Add(ref _byteCount, n);
                try { Chunk?.Invoke(buf, n); }
                catch (Exception ex) { _log.Warn($"处理码流块失败：{ex.Message}"); }
            }
        }
        catch (Exception ex)
        {
            if (_alive) _log.Warn($"ffmpeg 读取循环结束：{ex.Message}");
        }
        finally
        {
            _alive = false;
            var tail = _stderrTail.ToString().Trim();
            if (tail.Length > 0) LastError = tail;
            _log.Info($"ffmpeg 输出循环结束（编码器 {EncoderName}，共 {_chunkCount} 块 {_byteCount / 1024}KB）" +
                      (tail.Length > 0 ? $" 最后错误：{Truncate(tail, 300)}" : ""));
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
                    if (_stderrTail.Length > 4000) _stderrTail.Remove(0, _stderrTail.Length - 4000);
                }
                _log.Debug($"ffmpeg: {line}");
            }
        }
        catch { }
    }

    /// <summary>喂一帧（BGRA）。返回 false 表示编码器已死</summary>
    public bool WriteFrame(ReadOnlySpan<byte> bgra)
    {
        var stdin = _stdin;
        if (!_alive || stdin == null) return false;
        try
        {
            stdin.Write(bgra);
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"写入编码器失败：{ex.Message}";
            _alive = false;
            return false;
        }
    }

    public void Stop()
    {
        _alive = false;
        try { _stdin?.Flush(); } catch { }
        try { _stdin?.Close(); } catch { }
        _stdin = null;
        try
        {
            if (_proc is { HasExited: false })
            {
                if (!_proc.WaitForExit(1500)) _proc.Kill(entireProcessTree: true);
            }
        }
        catch { }
        try { _proc?.Dispose(); } catch { }
        _proc = null;
        try { _reader?.Join(500); } catch { }
        _reader = null;
    }

    public void Dispose() => Stop();

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];
}
