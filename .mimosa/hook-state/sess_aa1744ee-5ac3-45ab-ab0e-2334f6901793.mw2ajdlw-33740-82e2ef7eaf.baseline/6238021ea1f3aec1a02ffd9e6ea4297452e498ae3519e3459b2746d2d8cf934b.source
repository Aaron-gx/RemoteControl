using System.IO;

namespace AgentSetup;

/// <summary>静默安装（无人值守）：不弹界面，按参数装完，退出码 0 表示成功。</summary>
public static class SilentRunner
{
    /// <summary>日志文件路径：安装程序同目录优先（最好找），写不了退到 %TEMP%。</summary>
    private static string LogPath()
    {
        var name = "安装日志-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt";
        foreach (var dir in new[] { AppContext.BaseDirectory, Path.GetTempPath() })
        {
            try
            {
                var path = Path.Combine(dir, name);
                File.AppendAllText(path, "");    // 先探一次可写性
                return path;
            }
            catch { }
        }
        return Path.Combine(Path.GetTempPath(), name);
    }

    public static int Run(string[] args)
    {
        string Arg(string name, string fallback = "")
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return fallback;
        }
        bool Flag(string name) => args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));

        // 日志优先写在**安装程序同目录**（用户一眼能找到，桌面/下载目录都在那儿），
        // 写不了才退到 %TEMP%。名字带时间戳，多次安装不会互相覆盖。
        var log = LogPath();
        void Log(string line)
        {
            try { File.AppendAllText(log, line + Environment.NewLine); } catch { }
            try { Console.WriteLine(line); } catch { }
        }

        Log($"[{DateTime.Now:HH:mm:ss}] 被控端静默安装开始（日志：{log}）");

        if (!Installer.IsElevated)
        {
            Log("需要管理员权限：正在申请提权…");
            if (!Installer.RelaunchElevated(args, out var err))
            {
                Log("提权失败：" + err);
                return 2;
            }
            return 0;   // 提权后的进程继续安装
        }

        var payloadError = Installer.ValidatePayload();
        if (payloadError != null) { Log(payloadError); return 3; }

        var o = new Installer.Options
        {
            // 静默安装也应当是"零填写"：地址/令牌/目录都用安装包预置值兜底
            InstallDir = Arg("--dir", SetupSettings.DefaultInstallDir()),
            ServerUrl = Arg("--server", EmbeddedPayload.LoadDefaults().ServerUrl is { Length: > 0 } s ? s : "ws://202.60.232.209:8080/ws"),
            Token = Arg("--token", EmbeddedPayload.LoadDefaults().Token),
            WorkerPassword = Arg("--worker-password", ""),
            // 默认共享模式（--multi-session 才切到旧的独立会话模式）；--no-rdpwrap 保留为兼容写法
            MultiSession = Flag("--multi-session") && !Flag("--no-rdpwrap"),
            VirtualDisplay = !Flag("--no-vdd"),
            AutoStart = !Flag("--no-autostart"),
            DefenderExclusion = !Flag("--no-defender"),
        };
        Log($"安装目录：{o.InstallDir}");
        Log($"服务器：{o.ServerUrl}");
        Log($"会话模型：{(o.MultiSession ? "独立会话（旧）" : "共享（默认，本机已登录桌面 + 外屏）")}  虚拟外屏：{o.VirtualDisplay}  自动启动：{o.AutoStart}");

        var code = Installer.RunInstall(o, Log);
        Log($"[{DateTime.Now:HH:mm:ss}] 安装脚本退出码：{code}");
        if (code == 0) Installer.StartAgent(o.InstallDir, out _);
        Log(code == 0 ? "安装完成。" : "安装失败，请查看上面的日志。");
        EmbeddedPayload.Cleanup();     // 删掉临时解包目录（单文件分发时才有）
        return code;
    }
}
