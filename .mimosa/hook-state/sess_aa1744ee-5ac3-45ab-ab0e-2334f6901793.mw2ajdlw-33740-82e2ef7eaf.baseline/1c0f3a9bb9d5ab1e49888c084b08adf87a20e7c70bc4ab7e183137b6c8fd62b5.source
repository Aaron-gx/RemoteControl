using Agent.Common;

namespace UpnpProbe;

/// <summary>
/// 用本项目的 UpnpPortMapper 测"这台机器的路由器能不能自动开端口"。
/// 用法：UpnpProbe.exe [端口] [keep|delete]
/// 默认端口 47890；默认测完就删掉映射（keep = 保留，供外部探测用）。
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        int port = args.Length > 0 && int.TryParse(args[0], out var p) ? p : 47890;
        bool keep = args.Length > 1 && args[1].Equals("keep", StringComparison.OrdinalIgnoreCase);

        var log = new Logger("upnp-probe");
        Console.WriteLine($"=== UPnP 自动映射测试：目标端口 {port} ===");
        Console.WriteLine($"本机内网地址：{string.Join(", ", NetHelper.LocalIPv4())}");

        var upnp = new UpnpPortMapper(log);
        bool ok;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            ok = upnp.TryMap(port, log);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FAIL] 抛异常：{ex.GetType().Name}: {ex.Message}");
            return 2;
        }
        sw.Stop();

        Console.WriteLine($"Mapped       = {upnp.Mapped}");
        Console.WriteLine($"ExternalIP   = {upnp.ExternalIP ?? "(null)"}");
        Console.WriteLine($"ExternalPort = {upnp.ExternalPort}");
        Console.WriteLine($"耗时         = {sw.ElapsedMilliseconds} ms");

        if (ok && !string.IsNullOrEmpty(upnp.ExternalIP))
        {
            Console.WriteLine($"[PASS] 路由器可自动映射：外网地址 = {upnp.ExternalIP}:{upnp.ExternalPort}");
            Console.WriteLine("       外部可用下面这条验证（在另一台机器上跑）：");
            Console.WriteLine($"         curl -s -m 5 -o NUL -w \"%{{http_code}}\" http://{upnp.ExternalIP}:{upnp.ExternalPort}/");
            // 映射保持不动：这个端口本来就是 P2P 直连要用的（租约 0 = 直到路由器重启）
            Console.WriteLine("（映射保留：这个端口正是直连要用的，租约会持续到路由器重启）");
            return 0;
        }

        Console.WriteLine("[FAIL] 路由器不支持/未开启 UPnP —— 这条路走不通，需要换方案");
        Console.WriteLine("       常见原因：光猫与路由器双重 NAT、路由器里 UPnP 被关掉、或 IGD 服务类型不被支持");
        return 1;
    }
}
