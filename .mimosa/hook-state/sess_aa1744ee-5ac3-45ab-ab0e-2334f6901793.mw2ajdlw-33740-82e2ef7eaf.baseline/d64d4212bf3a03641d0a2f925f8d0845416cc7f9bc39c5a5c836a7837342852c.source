using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;

namespace AgentSetup;

/// <summary>安装流程的共用逻辑（界面与静默模式都用它）。</summary>
public static class Installer
{
    /// <summary>
    /// 安装包根目录。**必须走 EmbeddedPayload.Root**：单文件分发时 payload 是内嵌在 exe 里、
    /// 运行时才解到临时目录的；这里若写死 exe 同目录的 payload，单文件包会永远报"安装包不完整"。
    /// 开发调试（没有内嵌资源）时，EmbeddedPayload.Root 会自动回退到 exe 同目录的 payload 文件夹。
    /// </summary>
    public static string PayloadDir => EmbeddedPayload.Root;

    public static string PayloadAgentDir => Path.Combine(PayloadDir, "build", "agent");
    public static string PayloadScript => Path.Combine(PayloadDir, "scripts", "install-agent.ps1");
    public static string PayloadDriversDir => Path.Combine(PayloadDir, "drivers");

    public static bool IsElevated
    {
        get
        {
            try
            {
                using var id = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
    }

    /// <summary>以管理员身份重新启动自己（UAC 提示），成功则调用方应退出</summary>
    public static bool RelaunchElevated(string[] args, out string error)
    {
        error = "";
        try
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = string.Join(' ', args.Select(Quote)),
            };
            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string Quote(string a) => a.Contains(' ') ? "\"" + a + "\"" : a;

    /// <summary>安装包自检：缺哪个文件要说清楚，而不是装到一半失败</summary>
    public static string? ValidatePayload()
    {
        if (!Directory.Exists(PayloadAgentDir))
            return $"安装包不完整：缺少 {PayloadAgentDir}{Environment.NewLine}" +
                   "请把整个安装包目录（安装程序 + payload）一起拷贝过来。";
        if (!File.Exists(Path.Combine(PayloadAgentDir, "Agent.Coordinator.exe")))
            return $"安装包不完整：{PayloadAgentDir} 里没有 Agent.Coordinator.exe";
        if (!File.Exists(PayloadScript))
            return $"安装包不完整：缺少 {PayloadScript}";
        if (!Directory.Exists(PayloadDriversDir))
            return $"安装包不完整：缺少驱动目录 {PayloadDriversDir}（多会话/虚拟副屏需要）";
        return null;
    }

    public sealed class Options
    {
        public string InstallDir { get; set; } = @"E:\RemoteControl\agent";
        public string ServerUrl { get; set; } = "ws://202.60.232.209:8080/ws";
        public string Token { get; set; } = "";
        /// <summary>独立会话模式（旧方案）：新建 RemoteWorker 用户 + RDP 回环会话；默认 false = 共享本机桌面 + 外屏</summary>
        public bool MultiSession { get; set; } = false;
        /// <summary>虚拟外屏：共享模式下的"远程那块屏"（默认开；关掉就只采集物理主屏）</summary>
        public bool VirtualDisplay { get; set; } = true;
        public bool AutoStart { get; set; } = true;        // 自动启动（共享模式 = 登录时启动）
        public bool DefenderExclusion { get; set; } = true;
        /// <summary>仅在已有部署需要固定密码时使用（命令行 --worker-password）；留空则由安装脚本复用/生成</summary>
        public string WorkerPassword { get; set; } = "";
        public int FrameRate { get; set; } = 30;
        public int BitrateKbps { get; set; } = 4000;
    }


    /// <summary>
    /// 找出"这台机器上已有安装"里的 RemoteWorker 密码，安装时复用。
    /// 为什么需要：RemoteWorker 是**机器级共享账号**，若安装到新目录时重新生成密码，
    /// 会把别处那份安装的密码一起改掉（下次重建会话就登录不上了）。
    /// 查找顺序：目标目录本身 → 默认目录。
    /// </summary>
    public static string DiscoverWorkerPassword(string targetDir)
    {
        foreach (var dir in new[] { targetDir, @"E:\RemoteControl\agent" })
        {
            try
            {
                var cfg = Path.Combine(dir, "config", "agent.json");
                if (!File.Exists(cfg)) continue;
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(cfg));
                if (doc.RootElement.TryGetProperty("WorkerPassword", out var pw))
                {
                    var v = pw.GetString();
                    if (!string.IsNullOrWhiteSpace(v)) return v!;
                }
            }
            catch { }
        }
        return "";
    }

    public static string BuildArguments(Options o)
    {
        var sb = new StringBuilder();
        sb.Append("-NoProfile -ExecutionPolicy Bypass -File ").Append(Quote(PayloadScript));
        sb.Append(" -RepoRoot ").Append(Quote(PayloadDir));
        sb.Append(" -InstallDir ").Append(Quote(o.InstallDir));
        sb.Append(" -ServerUrl ").Append(Quote(o.ServerUrl));
        if (!string.IsNullOrWhiteSpace(o.Token)) sb.Append(" -Token ").Append(Quote(o.Token));
        var pw = string.IsNullOrWhiteSpace(o.WorkerPassword) ? DiscoverWorkerPassword(o.InstallDir) : o.WorkerPassword;
        if (!string.IsNullOrWhiteSpace(pw)) sb.Append(" -WorkerPassword ").Append(Quote(pw));
        sb.Append(" -FrameRate ").Append(o.FrameRate);
        sb.Append(" -BitrateKbps ").Append(o.BitrateKbps);
        // 旧的自制安装器上仍保留「独立会话模式」勾选项（这个安装器只在 -AlsoLegacyExe 时才出）。
        // 那条路会新建账户、数据不与管理员共享，install-agent.ps1 默认会拒绝，所以这里把
        // 逃生开关一起带上，保证勾了它的老分发包仍然装得上。
        if (o.MultiSession) sb.Append(" -MultiSession -IUnderstandLegacyMultiSession");
        if (!o.VirtualDisplay) sb.Append(" -SkipVdd");
        if (!o.AutoStart) sb.Append(" -NoAutoStart");
        if (!o.DefenderExclusion) sb.Append(" -SkipDefender");
        return sb.ToString();
    }

    /// <summary>
    /// PowerShell（.NET Framework 5.1）往重定向管道写的是**控制台 OEM 代码页**（中文系统是 GBK/936），
    /// 按 UTF-8 读会把中文读成乱码 —— 而日志里的中文报错正是排障的关键，所以这里按 OEM 代码页读。
    /// </summary>
    private static Encoding ConsoleOutputEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        }
        catch { return Encoding.UTF8; }
    }

    /// <summary>启动安装脚本并逐行回调输出（返回退出码）</summary>
    public static int RunInstall(Options o, Action<string> onLine)
    {
        var enc = ConsoleOutputEncoding();
        var psi = new ProcessStartInfo("powershell.exe", BuildArguments(o))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = enc,
            StandardErrorEncoding = enc,
        };
        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) => { if (e.Data != null) onLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) onLine("[stderr] " + e.Data); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        p.WaitForExit();
        return p.ExitCode;
    }


    /// <summary>读取安装目录里配置的 AgentId（装完要告诉用户"去列表里找哪一个"）</summary>
    public static string ReadAgentId(string installDir)
    {
        try
        {
            var cfg = Path.Combine(installDir, "config", "agent.json");
            if (!File.Exists(cfg)) return "";
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(cfg));
            return doc.RootElement.TryGetProperty("AgentId", out var id) ? (id.GetString() ?? "") : "";
        }
        catch { return ""; }
    }

    public sealed class SelfCheckResult
    {
        public bool Ok { get; set; }
        public List<string> Reasons { get; } = new();
    }

    /// <summary>
    /// 装完立刻检查"这台电脑能不能连上服务器"：调用安装包里的 连接自检.ps1，
    /// 解析它的输出，把失败原因摘出来（供界面直接显示结论）。
    /// </summary>
    public static SelfCheckResult RunSelfCheck(Options o, string agentId, Action<string> onLine)
    {
        var result = new SelfCheckResult();
        var script = Path.Combine(PayloadDir, "连接自检.ps1");
        if (!File.Exists(script))
        {
            onLine("（安装包里没有 连接自检.ps1，跳过自检）");
            result.Ok = true;      // 没有自检脚本不算失败
            return result;
        }
        var args = "-NoProfile -ExecutionPolicy Bypass -File " + Quote(script) +
                   " -Server " + Quote(o.ServerUrl) +
                   (string.IsNullOrWhiteSpace(o.Token) ? "" : " -Token " + Quote(o.Token)) +
                   (string.IsNullOrWhiteSpace(agentId) ? "" : " -AgentId " + Quote(agentId)) +
                   " -NoPause";
        try
        {
            var psi = new ProcessStartInfo("powershell.exe", args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = ConsoleOutputEncoding(),
                StandardErrorEncoding = ConsoleOutputEncoding(),
            };
            using var p = new Process { StartInfo = psi };
            p.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                onLine(e.Data);
                var t = e.Data.Trim();
                if (t.StartsWith("[失败]")) result.Reasons.Add(t[4..].Trim());
                else if (t.StartsWith("原因：")) result.Reasons.Add(t);
                else if (t.Contains("自检通过")) result.Ok = true;
            };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) onLine("[stderr] " + e.Data); };
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            p.WaitForExit();
        }
        catch (Exception ex)
        {
            onLine("自检执行异常：" + ex.Message);
            result.Ok = true;      // 自检本身失败不当作连接失败
        }
        if (result.Reasons.Count > 0) result.Ok = false;
        return result;
    }

    /// <summary>安装完成后启动被控端（当前会话内先跑起来；开机自启由计划任务负责）</summary>
    public static void StartAgent(string installDir, out string error)
    {
        error = "";
        try
        {
            var exe = Path.Combine(installDir, "Agent.Coordinator.exe");
            Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = installDir, UseShellExecute = true });
        }
        catch (Exception ex) { error = ex.Message; }
    }
}
