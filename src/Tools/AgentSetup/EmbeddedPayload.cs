using System.IO;
using System.IO.Compression;
using System.Reflection;

namespace AgentSetup;

/// <summary>
/// 内嵌安装包：整个被控端（程序 + ffmpeg + 驱动 + 安装脚本）打成 zip 内嵌在 exe 里，
/// 用户拿到的就是**一个 exe**，运行时解到临时目录再用（装完可删）。
/// 开发调试时如果没内嵌，就回退到 exe 同目录的 payload 文件夹。
/// </summary>
public static class EmbeddedPayload
{
    private static string? _extractedRoot;

    /// <summary>解出来的安装包根目录（含 build\agent、drivers、scripts）</summary>
    public static string Root
    {
        get
        {
            if (_extractedRoot != null) return _extractedRoot;

            // 1) 优先用内嵌 zip（正式分发的形态）
            var asm = Assembly.GetExecutingAssembly();
            using var stream = asm.GetManifestResourceStream("payload.zip");
            if (stream != null)
            {
                var dir = Path.Combine(Path.GetTempPath(), "rc-agent-setup-" + Guid.NewGuid().ToString("N")[..8]);
                Directory.CreateDirectory(dir);
                var zipPath = Path.Combine(dir, "payload.zip");
                using (var fs = File.Create(zipPath)) stream.CopyTo(fs);
                ZipFile.ExtractToDirectory(zipPath, dir, overwriteFiles: true);
                try { File.Delete(zipPath); } catch { }
                _extractedRoot = dir;
                return dir;
            }

            // 2) 回退：同目录 payload 文件夹（开发/调试）
            var fallback = Path.Combine(AppContext.BaseDirectory, "payload");
            _extractedRoot = fallback;
            return fallback;
        }
    }

    /// <summary>打包时写进来的默认值（服务器地址 / 口令）：被控端安装时**什么都不用填**</summary>
    public sealed class Defaults
    {
        public string ServerUrl { get; set; } = "";
        public string Token { get; set; } = "";
    }

    public static Defaults LoadDefaults()
    {
        var d = new Defaults();
        try
        {
            var f = Path.Combine(Root, "setup-defaults.txt");
            if (!File.Exists(f)) return d;
            foreach (var raw in File.ReadAllLines(f))
            {
                var line = raw.Trim().TrimStart('\uFEFF');
                if (line.Length == 0 || line.StartsWith("#")) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var k = line[..eq].Trim().ToLowerInvariant();
                var v = line[(eq + 1)..].Trim();
                if (k == "serverurl") d.ServerUrl = v;
                else if (k == "token") d.Token = v;
            }
        }
        catch { }
        return d;
    }

    /// <summary>装完清理临时解包目录</summary>
    public static void Cleanup()
    {
        try
        {
            if (_extractedRoot == null) return;
            if (_extractedRoot.Contains("rc-agent-setup-", StringComparison.OrdinalIgnoreCase))
                Directory.Delete(_extractedRoot, recursive: true);
        }
        catch { }
    }
}
