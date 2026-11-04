using System.IO;
using System.Text;

namespace Agent.Common;

public enum LogLevel { Debug, Info, Warn, Error }

/// <summary>
/// 极简线程安全文件日志： &lt;BaseDir&gt;\logs\&lt;name&gt;-yyyyMMdd.log
///
/// 【写不出去不能影响运行】日志目录**一定**要容错：程序装在 Program Files 时，普通用户对
/// 程序目录没有写权限（只有管理员有）；如果这里抛异常，程序就会一启动就崩、或者弹一个
/// "Access to the path ...\logs is denied" 的框（实测踩到，客户装到 C:\Program Files 就中招）。
/// 所以顺序是：程序目录 → %LOCALAPPDATA%\远程控制\logs → 只写控制台（不落盘，程序照常跑）。
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
    /// <summary>日志没能写进程序目录时的实际位置（供界面提示；正常时为空）</summary>
    public string? FallbackPath { get; private set; }

    public Logger(string name, string? baseDir = null, LogLevel minLevel = LogLevel.Debug, bool echoConsole = true)
    {
        _name = name;
        _minLevel = minLevel;
        _echoConsole = echoConsole;
        baseDir ??= AppContext.BaseDirectory;

        var preferred = System.IO.Path.Combine(baseDir, "logs", $"{name}-{DateTime.Now:yyyyMMdd}.log");
        _path = preferred;
        _path = TryOpen(baseDir, name, preferred, out var sink);
        _sink = sink;
        Current = this;

        if (sink != null && !string.Equals(_path, preferred, StringComparison.OrdinalIgnoreCase))
        {
            FallbackPath = _path;
            Write(LogLevel.Info, $"程序目录不可写（{preferred}），日志改写到：{_path}");
        }
    }

    /// <summary>
    /// 依次尝试"程序目录 → %LOCALAPPDATA%\远程控制"建日志文件，返回实际用到的路径；
    /// 全部失败返回首选项路径（此时不落盘、只写控制台）。
    /// </summary>
    private static string TryOpen(string baseDir, string name, string preferred, out Sink? sink)
    {
        sink = null;
        foreach (var dir in CandidateDirs(baseDir))
        {
            try
            {
                System.IO.Directory.CreateDirectory(dir);
                var path = System.IO.Path.Combine(dir, $"{name}-{DateTime.Now:yyyyMMdd}.log");
                lock (Sinks)
                {
                    if (!Sinks.TryGetValue(path, out var s))
                    {
                        var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                        s = new Sink { Writer = new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = true } };
                        Sinks[path] = s;
                    }
                    s.Refs++;
                    sink = s;
                }
                return path;
            }
            catch
            {
                sink = null;   // 换下一个候选目录
            }
        }
        return preferred;
    }

    /// <summary>日志目录候选：程序目录优先（绿色版/便携版就在自己旁边），不可写时退到用户目录</summary>
    private static IEnumerable<string> CandidateDirs(string baseDir)
    {
        var programLogs = System.IO.Path.Combine(baseDir, "logs");
        yield return programLogs;

        string user;
        try
        {
            user = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "远程控制", "logs");
        }
        catch
        {
            user = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "远程控制", "logs");
        }
        if (!string.Equals(user, programLogs, StringComparison.OrdinalIgnoreCase))
            yield return user;
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
