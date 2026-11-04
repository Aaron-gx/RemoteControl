using Agent.Common;

// CrashDumper 本地验收工具（不是被控端，可安全在本机跑）：
//   CrashDumpProbe throw   → 模拟后台线程未处理异常 → 期望生成 crash-*.dmp 且进程以非零码退出
//   CrashDumpProbe report  → 模拟下次启动 → 期望报告历史 dump 列表
// 验收标准：dump 文件以 MDMP 魔数开头、体积非平凡（>50KB）、保留上限生效。

var dir = Path.Combine(AppContext.BaseDirectory, "dumps");
var log = new Logger("probe", Path.Combine(AppContext.BaseDirectory, "logs"));

if (args.Length > 0 && args[0] == "report")
{
    // 只装目录状态（不重复挂钩），验证 ReportPreviousCrashes
    CrashDumper.Install(dir, log);
    CrashDumper.ReportPreviousCrashes(log);
    Console.WriteLine("report done");
    return 0;
}

CrashDumper.Install(dir, log);

if (args.Length > 0 && args[0] == "throw")
{
    Console.WriteLine("probing: throwing unhandled exception on background thread...");
    var t = new Thread(_ => throw new InvalidOperationException("CRASH-PROBE: deliberate"));
    t.Start();
    t.Join();
    Thread.Sleep(500);   // 给处理器一点时间（UnhandledException 在收尾阶段触发）
    Console.WriteLine("still alive (unexpected for a fatal one — but dump may still have been written)");
    return 0;
}

Console.WriteLine("usage: CrashDumpProbe [throw|report]");
return 1;
