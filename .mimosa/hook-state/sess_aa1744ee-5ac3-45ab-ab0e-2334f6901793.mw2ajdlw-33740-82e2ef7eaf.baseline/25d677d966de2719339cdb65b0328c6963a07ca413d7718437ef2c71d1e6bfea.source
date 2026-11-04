using System.IO;

namespace AgentSetup;

/// <summary>
/// 记住上次填写的内容（安装目录/服务器/令牌/选项），下次安装不用重复输入。
/// 用最简单的 key=value 文本存，避免 JSON 大小写/BOM 之类的坑。
/// </summary>
public static class SetupSettings
{
    private static string FilePath => Path.Combine(AppContext.BaseDirectory, "setup-last.txt");

    /// <summary>
    /// 默认安装目录：优先 E:（厂商约定的位置），没有就退回存在的盘 ——
    /// 客户的机器上不一定有 E: 盘，写死 E: 会让默认值直接不可用。
    /// </summary>
    public static string DefaultInstallDir()
    {
        foreach (var root in new[] { @"E:\", @"D:\", @"C:\" })
        {
            try { if (Directory.Exists(root)) return Path.Combine(root, "RemoteControl", "agent"); }
            catch { }
        }
        return @"C:\RemoteControl\agent";
    }

    public sealed class Data
    {
        public string InstallDir { get; set; } = DefaultInstallDir();
        public string ServerUrl { get; set; } = "ws://202.60.232.209:8080/ws";
        public string Token { get; set; } = "";
        public bool MultiSession { get; set; } = false;     // 默认共享模式：不新建用户，远程操作本机已登录桌面
        public bool VirtualDisplay { get; set; } = true;    // 虚拟外屏：共享模式下就是"远程那块屏"
        public bool AutoStart { get; set; } = true;
        public bool DefenderExclusion { get; set; } = true;
    }

    public static Data Load()
    {
        var d = new Data();
        try
        {
            if (!File.Exists(FilePath)) return d;
            foreach (var raw in File.ReadAllLines(FilePath))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var k = line[..eq].Trim().ToLowerInvariant();
                var v = line[(eq + 1)..].Trim().TrimStart('\uFEFF');
                switch (k)
                {
                    case "installdir": if (v.Length > 0) d.InstallDir = v; break;
                    case "serverurl": if (v.Length > 0) d.ServerUrl = v; break;
                    case "token": d.Token = v; break;
                    case "multisession": d.MultiSession = v != "0"; break;
                    case "virtualdisplay": d.VirtualDisplay = v != "0"; break;
                    case "autostart": d.AutoStart = v != "0"; break;
                    case "defenderexclusion": d.DefenderExclusion = v != "0"; break;
                }
            }
        }
        catch { }
        return d;
    }

    public static void Save(Data d)
    {
        try
        {
            File.WriteAllLines(FilePath, new[]
            {
                "# 被控端安装程序上次填写的内容（删掉本文件即恢复默认）",
                "InstallDir=" + d.InstallDir,
                "ServerUrl=" + d.ServerUrl,
                "Token=" + d.Token,
                "MultiSession=" + (d.MultiSession ? "1" : "0"),
                "VirtualDisplay=" + (d.VirtualDisplay ? "1" : "0"),
                "AutoStart=" + (d.AutoStart ? "1" : "0"),
                "DefenderExclusion=" + (d.DefenderExclusion ? "1" : "0"),
            });
        }
        catch { }
    }
}
