using System.Runtime.InteropServices;

namespace Agent.Common;

/// <summary>
/// 会话自愈（2026-10-11）：把跑在 SYSTEM 服务会话（session 0）里的被控端迁移到交互控制台会话。
///
/// 【为什么需要】看门狗计划任务（SYSTEM）和开机自启任务（SYSTEM）拉起的实例都在 session 0，
/// 看不到用户桌面 —— 虚拟外屏缺位 → 引擎暂停出帧 + 拒绝打开软件，远控等于废了，而且
/// 用户会话里明明有登录着的桌面（锁屏也算）。实测事故：热更新应用后实例永久卡在 session 0。
///
/// 【怎么迁移】本进程在 session 0、且活动控制台会话有已登录用户（锁屏也算）时：
/// 以该用户令牌派生一个"延迟 2 秒启动本程序"的子进程（隐藏窗口），然后本进程退出 ——
/// 2 秒后互斥体已释放，新实例在用户会话拿到互斥体，看门狗也不会再抢（进程活着）。
/// 报平安标记在退出前补写（否则热更新会因 45 秒无心跳而回滚）。
///
/// 【防循环】只在本进程会话 == 0 时触发（用户会话实例不会自迁）；每次迁移写时间戳标记，
/// 10 分钟内不重复（防"用户会话实例起来即死"的死循环）。
/// </summary>
public static class SessionHandover
{
    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUserW(
        IntPtr token, string? applicationName, string commandLine,
        IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles,
        uint creationFlags, IntPtr environment, string? currentDirectory,
        ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public uint cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public ushort wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public uint dwProcessId, dwThreadId;
    }

    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint CREATE_NO_WINDOW = 0x08000000;

    /// <summary>迁移节流标记（同目录上一次迁移的时间戳）</summary>
    private static string MarkerPath =>
        Path.Combine(Path.GetTempPath(), "RC-Update", "session-handover.txt");

    private static string MarkerDir => Path.GetDirectoryName(MarkerPath)!;

    /// <summary>
    /// 尝试把服务迁移到活动控制台会话。返回 true = 已派生迁移子进程（调用方应尽快退出）。
    /// 条件不满足（不在 session 0 / 控制台无登录用户 / 10 分钟内迁过）返回 false。
    /// </summary>
    public static bool TryHandoverToConsoleSession(string exePath, Logger log)
    {
        try
        {
            int currentSession = System.Diagnostics.Process.GetCurrentProcess().SessionId;
            if (currentSession != 0) return false;   // 不在服务会话：无事可做

            // 10 分钟节流（防迁移目标异常时的死循环）
            try
            {
                if (File.Exists(MarkerPath) &&
                    DateTime.UtcNow - File.GetLastWriteTimeUtc(MarkerPath) < TimeSpan.FromMinutes(10))
                {
                    log.Info("会话迁移：10 分钟内已迁移过，跳过（防循环）");
                    return false;
                }
            }
            catch { }

            uint console = WTSGetActiveConsoleSessionId();
            if (console == 0)
            {
                log.Info("会话迁移：无活动控制台会话（无人登录），留在服务会话继续服务");
                return false;
            }
            if (!WTSQueryUserToken(console, out var token))
            {
                log.Info($"会话迁移：控制台会话 {console} 没有已登录用户（停在登录界面），留在服务会话继续服务");
                return false;
            }

            if (!File.Exists(exePath))
            {
                CloseHandle(token);
                log.Warn($"会话迁移：exe 不存在（{exePath}），放弃迁移");
                return false;
            }

            if (!CreateEnvironmentBlock(out var env, token, false))
            {
                CloseHandle(token);
                log.Warn($"会话迁移：创建用户环境失败（win32={Marshal.GetLastWin32Error()}），放弃迁移");
                return false;
            }

            // 隐藏的 cmd：等 2 秒（本进程退出、互斥体释放）后启动 exe。
            // lpDesktop 必须显式指到 winsta0\default：NULL 会让子进程继承本进程（session 0 服务桌面），
            // 在目标会话里起不来（跨会话 CreateProcessAsUser 的标准要求）。
            var si = new STARTUPINFO { cb = (uint)Marshal.SizeOf<STARTUPINFO>(), lpDesktop = @"winsta0\default" };
            var cmdLine = $"cmd.exe /c timeout /t 2 /nobreak >nul & start \"\" \"{exePath}\"";
            var ok = CreateProcessAsUserW(token, null, cmdLine, IntPtr.Zero, IntPtr.Zero,
                false, CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW, env, null, ref si, out var pi);
            DestroyEnvironmentBlock(env);
            CloseHandle(token);

            if (!ok)
            {
                log.Warn($"会话迁移：CreateProcessAsUser 失败（win32={Marshal.GetLastWin32Error()}），留在服务会话继续服务");
                return false;
            }
            CloseHandle(pi.hProcess);
            CloseHandle(pi.hThread);

            try
            {
                Directory.CreateDirectory(MarkerDir);
                File.WriteAllText(MarkerPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} session {currentSession} -> {console}");
            }
            catch { }
            log.Warn($"会话迁移：已在控制台会话 {console} 派生新实例（pid={pi.dwProcessId}），本进程（会话 {currentSession}）退出让出互斥体");
            return true;
        }
        catch (Exception ex)
        {
            log.Warn($"会话迁移异常（留在当前会话继续服务）：{ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }
}
