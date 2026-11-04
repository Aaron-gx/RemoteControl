using Agent.Common;

namespace LicenseCli;

/// <summary>
/// 命令行签发激活码（服务器部署用）。
///
/// 用法：
///   LicenseCli.exe --srv DEP-7CD522B1FB6D9C5C03D7 [--lic HK-REL-0007] [--sub 香港二]
///                  [--ip 1.2.3.4] [--maxview 2] [--days 365] [--key &lt;私钥路径&gt;]
///
/// 只打印激活码本身（方便脚本直接取用）；安装到中继：
///   /opt/rcserver/rcctl install "&lt;激活码&gt;"
/// </summary>
internal static class Program
{
    private const string DefaultKeyPath = @"E:\RemoteControl\license\private.pem";

    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var opt = Parse(args);
        if (opt == null || !opt.ContainsKey("srv"))
        {
            Console.Error.WriteLine("用法: LicenseCli.exe --srv DEP-xxxx [--lic 编号] [--sub 备注] [--ip 绑定IP] [--maxview 2] [--days 365] [--key 私钥路径]");
            return 2;
        }

        var keyPath = opt.GetValueOrDefault("key", DefaultKeyPath);
        if (!File.Exists(keyPath))
        {
            Console.Error.WriteLine($"找不到私钥：{keyPath}");
            return 3;
        }

        var now = DateTimeOffset.UtcNow;
        long nbf = now.ToUnixTimeSeconds();
        int days = int.TryParse(opt.GetValueOrDefault("days", "365"), out var d) ? d : 365;
        long exp = now.AddDays(days).ToUnixTimeSeconds();

        var payload = new LicensePayload
        {
            Lic = opt.GetValueOrDefault("lic", "HK-REL-" + now.ToString("yyMMddHHmm")),
            Sub = opt.GetValueOrDefault("sub", ""),
            Srv = opt["srv"],                       // 绑定部署ID：只能装在这台中继上
            IP = opt.GetValueOrDefault("ip", ""),
            MaxViewers = int.TryParse(opt.GetValueOrDefault("maxview", "2"), out var mv) ? mv : 2,
            Nbf = nbf,
            Exp = exp,
        };

        string code;
        try
        {
            var pem = File.ReadAllText(keyPath);
            code = LicenseCodec.Sign(payload, pem);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"签发失败：{ex.GetType().Name}: {ex.Message}");
            return 4;
        }

        // 自校验一遍：sign 出来的码必须能被 verify 认（防止私钥/公钥不配对）
        var vr = LicenseCodec.Verify(code, DateTimeOffset.UtcNow.UtcDateTime, payload.Srv, payload.IP);
        if (!vr.Ok)
        {
            Console.Error.WriteLine($"自校验未通过（{vr.State}: {vr.Error}）—— 私钥可能与该公钥不配对，别拿去装");
            return 5;
        }

        Console.Error.WriteLine($"授权编号 {payload.Lic} / 绑定 {payload.Srv} / 到期 {now.AddDays(days):yyyy-MM-dd} / 自校验通过");
        Console.WriteLine(code);      // 只把激活码本身打到 stdout，方便脚本捕获
        return 0;
    }

    private static Dictionary<string, string>? Parse(string[] args)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (!a.StartsWith("--")) return null;
            var k = a[2..];
            if (i + 1 >= args.Length) return null;
            d[k] = args[++i];
        }
        return d;
    }
}
