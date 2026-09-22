using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Agent.Common;

namespace Agent.Coordinator;

/// <summary>
/// 跨会话启动进程（Coordinator 在 Session 0 启动 Session 1 的 Worker）。
///
/// 三级降级（策划 §2.1 的 CreateProcessAsUser，加上实用兜底）：
///   1. WTSQueryUserToken + CreateProcessAsUser   （需要 SeTcbPrivilege，SYSTEM 下可用）
///   2. 复制目标会话内已有进程的令牌 + CreateProcessAsUser（管理员启用 SeDebugPrivilege 即可）
///   3. schtasks /RU &lt;user&gt; /IT 计划任务触发（无需特殊权限）
/// </summary>
public static class SessionLauncher
{
    public sealed record SessionEntry(int Id, string UserName, string StationName, int State)
    {
        public bool IsActive => State == NativeApi.WTSActive;
    }

    /// <summary>枚举登录会话</summary>
    public static List<SessionEntry> EnumerateSessions()
    {
        var list = new List<SessionEntry>();
        if (!NativeApi.WTSEnumerateSessionsW(IntPtr.Zero, 0, 1, out var p, out int count))
            return list;
        try
        {
            int sz = Marshal.SizeOf<NativeApi.WTS_SESSION_INFO>();
            for (int i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<NativeApi.WTS_SESSION_INFO>(p + i * sz);
                var user = QueryString(info.SessionID, NativeApi.WTSUserName);
                var station = QueryString(info.SessionID, NativeApi.WTSWinStationName);
                list.Add(new SessionEntry(info.SessionID, user, station, info.State));
            }
        }
        finally { NativeApi.WTSFreeMemory(p); }
        return list;
    }

    public static string QueryString(int sessionId, int infoClass)
    {
        if (!NativeApi.WTSQuerySessionInformationW(IntPtr.Zero, sessionId, infoClass, out var buf, out _))
            return "";
        try { return Marshal.PtrToStringUni(buf) ?? ""; }
        finally { NativeApi.WTSFreeMemory(buf); }
    }

    public static SessionEntry? FindSessionByUser(string userName, bool requireActive = true)
        => EnumerateSessions().FirstOrDefault(s =>
            string.Equals(s.UserName, userName, StringComparison.OrdinalIgnoreCase)
            && (!requireActive || s.IsActive));

    public static int CurrentSessionId
    {
        get
        {
            NativeApi.ProcessIdToSessionId(Environment.ProcessId, out int sid);
            return sid;
        }
    }

    /// <summary>在指定会话中启动进程；返回 pid，失败返回 -1</summary>
    public static int Launch(int sessionId, string exePath, string arguments, Logger log, out string method)
    {
        if (!File.Exists(exePath))
        {
            method = "none";
            log.Error($"Worker 可执行文件不存在：{exePath}");
            return -1;
        }

        EnablePrivilege("SeDebugPrivilege", log);
        EnablePrivilege("SeTcbPrivilege", log);
        EnablePrivilege("SeIncreaseQuotaPrivilege", log);
        EnablePrivilege("SeAssignPrimaryTokenPrivilege", log);

        string workDir = Path.GetDirectoryName(exePath)!;

        // ---- 方法 1：WTSQueryUserToken ----
        if (NativeApi.WTSQueryUserToken(sessionId, out var hToken) && hToken != IntPtr.Zero)
        {
            try
            {
                int pid = CreateAsUser(hToken, exePath, arguments, workDir, log, "WTSQueryUserToken");
                if (pid > 0) { method = "WTSQueryUserToken"; return pid; }
            }
            finally { NativeApi.CloseHandle(hToken); }
        }
        else
        {
            log.Info($"WTSQueryUserToken({sessionId}) 失败：{NativeApi.LastWin32Error()}");
        }

        // ---- 方法 2：复制目标会话内进程令牌 ----
        var victim = FindProcessInSession(sessionId);
        log.Info($"复制令牌方式：目标会话 {sessionId} 中{(victim != IntPtr.Zero ? "找到" : "未找到")}可用进程");
        if (victim != IntPtr.Zero)
        {
            try
            {
                if (NativeApi.OpenProcessToken(victim, NativeApi.TOKEN_DUPLICATE | NativeApi.TOKEN_QUERY, out var hSrc))
                {
                    try
                    {
                        if (NativeApi.DuplicateTokenEx(hSrc, NativeApi.TOKEN_ALL_ACCESS_LOCAL, IntPtr.Zero,
                                NativeApi.SecurityImpersonation, NativeApi.TokenPrimary, out var hDup))
                        {
                            try
                            {
                                int pid = CreateAsUser(hDup, exePath, arguments, workDir, log, "DuplicateToken");
                                if (pid > 0) { method = "DuplicateTokenEx"; return pid; }
                            }
                            finally { NativeApi.CloseHandle(hDup); }
                        }
                        else
                        {
                            log.Warn($"DuplicateTokenEx 失败：{NativeApi.LastWin32Error()}");
                        }
                    }
                    finally { NativeApi.CloseHandle(hSrc); }
                }
                else
                {
                    log.Warn($"复制会话进程令牌：OpenProcessToken 失败 {NativeApi.LastWin32Error()}");
                }
            }
            finally { NativeApi.CloseHandle(victim); }
        }

        // ---- 方法 3：计划任务 ----
        int taskPid = LaunchViaScheduledTask(sessionId, exePath, log);
        if (taskPid > 0) { method = "schtasks"; return taskPid; }

        method = "failed";
        return -1;
    }

    private static int CreateAsUser(IntPtr token, string exePath, string arguments, string workDir,
        Logger log, string via)
    {
        IntPtr env = IntPtr.Zero;
        NativeApi.CreateEnvironmentBlock(out env, token, false);
        try
        {
            var si = new NativeApi.STARTUPINFO
            {
                cb = Marshal.SizeOf<NativeApi.STARTUPINFO>(),
                lpDesktop = @"winsta0\default",
            };
            string cmd = $"\"{exePath}\" {arguments}".TrimEnd();
            bool ok = NativeApi.CreateProcessAsUserW(token, exePath, cmd, IntPtr.Zero, IntPtr.Zero, false,
                NativeApi.CREATE_UNICODE_ENVIRONMENT | NativeApi.CREATE_NEW_CONSOLE, env, workDir, ref si, out var pi);
            if (!ok)
            {
                int err = Marshal.GetLastWin32Error();
                log.Warn($"[{via}] CreateProcessAsUser 失败：{NativeApi.DescribeWin32Error(err)}");
                // 有些场景（令牌不可分配为 primary）用 CreateProcessWithTokenW 可以绕过
                ok = NativeApi.CreateProcessWithTokenW(token, 0, exePath, cmd,
                    NativeApi.CREATE_UNICODE_ENVIRONMENT | NativeApi.CREATE_NEW_CONSOLE, env, workDir, ref si, out pi);
                if (!ok)
                {
                    log.Warn($"[{via}] CreateProcessWithTokenW 也失败：{NativeApi.LastWin32Error()}");
                    return -1;
                }
            }
            try
            {
                log.Info($"[{via}] 已在会话中启动 Worker，pid={pi.dwProcessId}");
                return pi.dwProcessId;
            }
            finally
            {
                NativeApi.CloseHandle(pi.hProcess);
                NativeApi.CloseHandle(pi.hThread);
            }
        }
        finally
        {
            if (env != IntPtr.Zero) NativeApi.DestroyEnvironmentBlock(env);
        }
    }

    private static IntPtr FindProcessInSession(int sessionId)
    {
        // 优先 explorer（一定有桌面令牌），否则任意进程
        foreach (var name in new[] { "explorer", "rdpclip", "dwm", "winlogon", "csrss" })
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                try
                {
                    if (NativeApi.ProcessIdToSessionId(p.Id, out int sid) && sid == sessionId)
                    {
                        // 关键：OpenProcessToken(TOKEN_DUPLICATE) 要求进程句柄具备 PROCESS_QUERY_INFORMATION，
                        // 只给 QUERY_LIMITED 会被拒（实测 ACCESS_DENIED），于是永远退化成计划任务方式
                        const uint PROCESS_QUERY_INFORMATION = 0x0400;
                        const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
                        var h = NativeApi.OpenProcess(
                            PROCESS_QUERY_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION, false, p.Id);
                        if (h != IntPtr.Zero) return h;
                    }
                }
                catch { }
                finally { p.Dispose(); }
            }
        }
        return IntPtr.Zero;
    }

    private static int LaunchViaScheduledTask(int sessionId, string exePath, Logger log)
    {
        string user = QueryString(sessionId, NativeApi.WTSUserName);
        if (string.IsNullOrEmpty(user)) return -1;
        const string taskName = @"RemoteControl\AgentWorker";
        try
        {
            // 删掉旧的（忽略失败）
            RunCmd("schtasks.exe", $"/Delete /F /TN \"{taskName}\"", log);
            string cmd = $"/Create /F /TN \"{taskName}\" /TR \"\\\"{exePath}\\\"\" /SC ONCE /ST 00:00 /RU {user} /IT /RL LIMITED";
            var (code, outp) = RunCmd("schtasks.exe", cmd, log);
            if (code != 0)
            {
                log.Warn($"创建计划任务失败（{code}）：{outp.Trim()}");
                return -1;
            }
            var (code2, outp2) = RunCmd("schtasks.exe", $"/Run /TN \"{taskName}\"", log);
            if (code2 != 0)
            {
                log.Warn($"运行计划任务失败（{code2}）：{outp2.Trim()}");
                return -1;
            }
            // schtasks 不返回 pid，稍后按进程名+会话匹配
            Thread.Sleep(1500);
            var pid = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exePath))
                .FirstOrDefault(p =>
                {
                    try { return NativeApi.ProcessIdToSessionId(p.Id, out int s) && s == sessionId; }
                    catch { return false; }
                });
            if (pid != null)
            {
                log.Info($"通过计划任务在会话 {sessionId} 启动 Worker，pid={pid.Id}");
                return pid.Id;
            }
            log.Warn("计划任务已触发，但未找到 Worker 进程");
            return -1;
        }
        catch (Exception ex)
        {
            log.Warn($"计划任务方式启动失败：{ex.Message}");
            return -1;
        }
    }

    private static (int Code, string Output) RunCmd(string exe, string args, Logger log)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        using var p = Process.Start(psi)!;
        string outp = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(30000);
        log.Debug($"{exe} {args} → exit={p.ExitCode}");
        return (p.ExitCode, outp);
    }

    public static bool EnablePrivilege(string name, Logger? log = null)
    {
        if (!NativeApi.OpenProcessToken(System.Diagnostics.Process.GetCurrentProcess().Handle,
                NativeApi.TOKEN_QUERY | 0x0020 /*TOKEN_ADJUST_PRIVILEGES*/, out var token))
            return false;
        try
        {
            if (!NativeApi.LookupPrivilegeValueW(null, name, out var luid)) return false;
            var tp = new NativeApi.TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Privileges = new NativeApi.LUID_AND_ATTRIBUTES
                {
                    Luid = luid,
                    Attributes = 0x00000002, // SE_PRIVILEGE_ENABLED
                },
            };
            if (!NativeApi.AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero))
            {
                log?.Warn($"启用特权 {name} 调用失败：{NativeApi.LastWin32Error()}");
                return false;
            }
            // 官方语义：AdjustTokenPrivileges 返回 TRUE 时还要看 last error 是否 ERROR_NOT_ALL_ASSIGNED(1300)，
            // 成功时该值是"陈旧值"，不能当成失败（这是常见坑）
            int err = Marshal.GetLastWin32Error();
            bool ok = err != 1300;
            log?.Info($"启用特权 {name}：{(ok ? "成功" : "令牌中不存在")}");
            return ok;
        }
        finally { NativeApi.CloseHandle(token); }
    }

    /// <summary>是否以管理员身份运行</summary>
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
