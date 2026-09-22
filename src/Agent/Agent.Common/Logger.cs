using System.Text;

namespace Agent.Common;

public enum LogLevel { Debug, Info, Warn, Error }

/// <summary>
/// 极简线程安全文件日志： &lt;BaseDir&gt;\logs\&lt;name&gt;-yyyyMMdd.log
/// </summary>
public sealed class Logger : IDisposable
{
    /// <summary>
    /// 同一个日志文件只留一个写者：多个 Logger 实例（各自的 FileStream 有各自的写入偏移）
    /// 同时写一个文件会互相覆盖 —— 表现为丢行、半行、错行，排查问题时最要命。
    /// 引用计数保证任何一个实例 Dispose 都不会把别人正在用的 writer 关掉。
    /// </summary>
    private sealed class Sink
    {
        /// <summary>同一个 writer 的所有写者共用（StreamWriter 本身不是线程安全的）</summary>
        public readonly object Gate = new();
        public StreamWriter Writer = null!;
        public int Refs;
    }

    private static readonly Dictionary<string, Sink> Sinks = new();

    private readonly string _path;
    private readonly string _name;
    private Sink? _sink;
    private readonly LogLevel _minLevel;
    private readonly bool _echoConsole;

    public static Logger? Current { get; private set; }

    public string Path => _path;

    public Logger(string name, string? baseDir = null, LogLevel minLevel = LogLevel.Debug, bool echoConsole = true)
    {
        _name = name;
        _minLevel = minLevel;
        _echoConsole = echoConsole;
        baseDir ??= AppContext.BaseDirectory;
        var dir = System.IO.Path.Combine(baseDir, "logs");
        Directory.CreateDirectory(dir);
        _path = System.IO.Path.Combine(dir, $"{name}-{DateTime.Now:yyyyMMdd}.log");
        try
        {
            lock (Sinks)
            {
                if (!Sinks.TryGetValue(_path, out var sink))
                {
                    var fs = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    sink = new Sink { Writer = new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = true } };
                    Sinks[_path] = sink;
                }
                sink.Refs++;
                _sink = sink;
            }
        }
        catch
        {
            _sink = null;
        }
        Current = this;
    }

    public void Debug(string msg) => Write(LogLevel.Debug, msg);
    public void Info(string msg) => Write(LogLevel.Info, msg);
    public void Warn(string msg) => Write(LogLevel.Warn, msg);
    public void Error(string msg) => Write(LogLevel.Error, msg);

    public void Error(string msg, Exception ex) => Write(LogLevel.Error, $"{msg} :: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");

    public void Write(LogLevel level, string msg)
    {
        if (level < _minLevel) return;
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{level.ToString().ToUpperInvariant(),-5}] [{_name}] {msg}";
        var sink = _sink;
        if (sink != null)
        {
            try { lock (sink.Gate) { sink.Writer.WriteLine(line); } } catch { /* ignore */ }
        }
        if (_echoConsole)
        {
            try
            {
                Console.WriteLine(line);
            }
            catch { /* no console attached */ }
        }
    }

    public void Dispose()
    {
        var sink = _sink;
        _sink = null;
        if (sink != null)
        {
            lock (Sinks)
            {
                // 还有别的实例在用同一个文件就只减计数，最后一个走的负责关
                if (--sink.Refs <= 0)
                {
                    lock (sink.Gate) { try { sink.Writer.Dispose(); } catch { } }
                    Sinks.Remove(_path);
                }
            }
        }
        if (ReferenceEquals(Current, this)) Current = null;
    }
}
