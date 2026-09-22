using System.IO;
using System.Windows;
using Agent.Common;

namespace LicenseKeygen;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // 命令行模式（便于脚本化签发）：
        //   LicenseKeygen.exe --cli --lic ORD-1 --sub 客户甲 --srv DEP-XXXX --days 90 [--exp-seconds 90]
        //                      [--key <私钥路径>] [--ip 1.2.3.4] [--verify <激活码>]
        if (e.Args.Length > 0 && (e.Args.Contains("--cli") || e.Args.Contains("--verify")))
        {
            Environment.Exit(RunCli(e.Args));
            return;
        }
        base.OnStartup(e);
        new MainWindow().Show();
    }

    private static int RunCli(string[] args)
    {
        string? Arg(string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return null;
        }

        var verify = Arg("--verify");
        if (verify != null)
        {
            var r = LicenseCodec.Verify(verify, DateTime.UtcNow);
            Console.WriteLine($"状态: {r.State}");
            if (r.Payload != null)
                Console.WriteLine($"编号={r.Payload.Lic} 客户={r.Payload.Sub} 绑定部署={r.Payload.Srv} " +
                                  $"有效期={r.Payload.NotBefore():yyyy-MM-dd}~{r.Payload.ExpiresAt():yyyy-MM-dd} 剩余={r.Payload.DaysLeft():F1}天");
            if (!r.Ok) Console.WriteLine("原因: " + r.Error);
            return r.Ok ? 0 : 2;
        }

        var keyPath = Arg("--key") ?? @"E:\RemoteControl\license\private.pem";
        if (!File.Exists(keyPath))
        {
            Console.Error.WriteLine($"找不到私钥：{keyPath}");
            return 1;
        }
        var lic = Arg("--lic") ?? throw new ArgumentException("缺少 --lic");
        var days = int.TryParse(Arg("--days") ?? "90", out var d) ? d : 90;
        var now = DateTime.UtcNow;
        // --exp-seconds 用于验收/测试：签一个 N 秒后到期的码（验证"到期即停"用）
        var expSeconds = int.TryParse(Arg("--exp-seconds") ?? "", out var es) ? es : 0;
        var payload = new LicensePayload
        {
            Lic = lic,
            Sub = Arg("--sub") ?? "",
            Srv = Arg("--srv") ?? "",
            IP = Arg("--ip") ?? "",
            MaxViewers = int.TryParse(Arg("--maxview") ?? "0", out var mv) ? mv : 0,
            Nbf = LicenseTime.DayStart(now),
            Exp = expSeconds > 0
                ? LicenseTime.Unix(now.AddSeconds(expSeconds))
                : LicenseTime.DayEnd(now.Date.AddDays(days)),
        };
        var code = LicenseCodec.Sign(payload, File.ReadAllText(keyPath));
        var check = LicenseCodec.Verify(code, now, payload.Srv, payload.IP);
        if (!check.Ok) { Console.Error.WriteLine("自检失败：" + check.Error); return 3; }
        Console.WriteLine(code);
        return 0;
    }
}
