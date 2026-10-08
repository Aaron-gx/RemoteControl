using System.Runtime.InteropServices;
using System.Text;

namespace Agent.Common;

/// <summary>
/// 崩溃 minidump 收集（2026-10-08，对标 UU远程 的 crashpad 方案的轻量实现）。
///
/// 未处理异常触发时用 MiniDumpWriteDump 把全进程现场写进 logs\crash\*.dmp
/// （含线程栈、模块表、异常记录），保留最近 5 份。现场排障不再只能靠日志猜：
/// grablog 拉不到栈，dmp 可以离线用 WinDbg/VS 打开。
///
/// 接线说明（Agent.Coordinator 启动最早期调用 <see cref="Install"/>）：
/// .NET 的未处理异常（后台线程/任务里抛出）由 AppDomain.UnhandledException 兜住；
/// native 访问冲突等 SEH 在 .NET 8 默认还是会先走托管异常路径，所以一处挂钩即够。
/// dump 写完必须让进程继续死亡（不吞异常）——热更新的"90 秒报平安"回滚机制
/// 依赖进程退出；本类绝不让进程活下去，只是死前留遗书。
/// </summary>
public static class CrashDumper
{
    // ---------------------------------------------------------------- P/Invoke

    [DllImport("dbghelp.dll", SetLastError = true)]
    private static extern bool MiniDumpWriteDump(IntPtr hProcess, uint processId, IntPtr hFile,
        uint dumpType, ref MINIDUMP_EXCEPTION_INFORMATION exceptionParam,
        IntPtr userStreamParam, IntPtr callbackParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct MINIDUMP_EXCEPTION_INFORMATION
    {
        public uint ThreadId;
        public IntPtr ExceptionPointers;
        public int ClientPointers;
    }

    /// <summary>抓异常指针（在异常上下文里必须先取，否则 GC/栈展开后拿不到）</summary>
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll")]
    private static extern bool IsDebuggerPresent();

    // MiniDumpNormal | WithProcessThreadData(0x4) | WithUnloadedModules(0x20) | WithThreadInfo(0x1000)
    // —— 栈/线程/模块/异常记录齐全，几 MB 级（本地实测 WithDataSegs 会把 .NET 数据段全拖进来，单份 100MB+，弃用）；
    // 托管栈反正会先写进日志，dump 主要留 native 现场与后续深挖。
    private const uint DumpType = 0x0000_1024;

    private static readonly object Gate = new();
    private static string _dumpDir = "";
    private static Logger? _log;
    private static bool _busy;   // 崩溃处理器里再崩的保险丝：只尝试一次

    /// <summary>挂上崩溃钩子。dumpDir 为空则不启用。</summary>
    public static void Install(string dumpDir, Logger log)
    {
        if (string.IsNullOrWhiteSpace(dumpDir)) return;
        _dumpDir = dumpDir;
        _log = log;
        try { Directory.CreateDirectory(_dumpDir); } catch { return; }
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try
            {
                var ex = e.ExceptionObject as Exception;
                Write(ex, e.IsTerminating ? "terminating" : "unhandled");
            }
            catch { }
        };
        // 任务里未观察的异常默认不杀进程，只记录（观察者模式），不上 dump
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            try { _log?.Warn($"任务未观察异常（已标记观察，不崩）：{e.Exception.InnerException?.Message ?? e.Exception.Message}"); } catch { }
            e.SetObserved();
        };
        log.Info($"崩溃转储已启用：未处理异常将写入 {_dumpDir}（保留最近 5 份）");
    }

    /// <summary>写当前进程的 minidump。ex 可为 null（现场只剩原因字符串时）。</summary>
    public static string? Write(Exception? ex, string reason)
    {
        lock (Gate)
        {
            if (_busy) return null;
            _busy = true;
        }
        try
        {
            if (IsDebuggerPresent()) return null;   // 调试器在场时不抢（让调试器处理）
            Directory.CreateDirectory(_dumpDir);
            // 异常记录先写日志（dump 之后进程可能直接死，日志是最稳的）
            _log?.Error($"未处理异常（{reason}），正在写崩溃转储：{ex}");
            var path = Path.Combine(_dumpDir,
                $"crash-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.dmp");
            using var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            var mei = new MINIDUMP_EXCEPTION_INFORMATION
            {
                ThreadId = GetCurrentThreadId(),
                ExceptionPointers = IntPtr.Zero,   // 托管异常没有现成 EXCEPTION_POINTERS；栈由 dump 内线程上下文还原
                ClientPointers = 0,
            };
            using var proc = System.Diagnostics.Process.GetCurrentProcess();
            bool ok = MiniDumpWriteDump(proc.Handle, (uint)Environment.ProcessId, fs.SafeFileHandle.DangerousGetHandle(),
                DumpType, ref mei, IntPtr.Zero, IntPtr.Zero);
            if (!ok)
            {
                _log?.Warn($"崩溃转储写入失败（GetLastError={Marshal.GetLastWin32Error()}）");
                try { File.Delete(path); } catch { }
                return null;
            }
            _log?.Error($"崩溃转储已落盘：{path}（{new FileInfo(path).Length / 1024} KB）");
            PruneOldDumps(5);
            return path;
        }
        catch (Exception self)
        {
            try { _log?.Warn($"写崩溃转储自身失败：{self.Message}"); } catch { }
            return null;
        }
    }

    /// <summary>启动时调用：把上次崩溃的 dump 报告出来（提示用户/开发去捞）。</summary>
    public static void ReportPreviousCrashes(Logger log)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_dumpDir) || !Directory.Exists(_dumpDir)) return;
            var files = new DirectoryInfo(_dumpDir)
                .EnumerateFiles("crash-*.dmp")
                .OrderByDescending(f => f.CreationTimeUtc)
                .Take(5)
                .ToList();
            foreach (var f in files)
                log.Warn($"检测到历史崩溃转储：{f.Name}（{f.Length / 1024} KB，{f.CreationTime:MM-dd HH:mm:ss}）— 请拷贝给开发分析");
        }
        catch { }
    }

    private static void PruneOldDumps(int keep)
    {
        try
        {
            var files = new DirectoryInfo(_dumpDir)
                .EnumerateFiles("crash-*.dmp")
                .OrderByDescending(f => f.CreationTimeUtc)
                .ToList();
            for (int i = keep; i < files.Count; i++)
            {
                try { files[i].Delete(); } catch { }
            }
        }
        catch { }
    }
}
