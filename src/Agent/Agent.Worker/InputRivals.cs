using System.Diagnostics;

namespace Agent.Worker;

/// <summary>
/// "同类软件"探测：机器上有没有别的远控 / 远程协助类程序在跑。
///
/// 【为什么需要它，以及为什么这么重要】
/// 一个 Windows 会话只有一只系统光标。别的远控软件（UU远程 / ToDesk / 向日葵 / GameViewer …）
/// 同样会装全局鼠标钩子、同样往这只光标上写位置。两家一起跑时会发生**互相抢**：
///   · 它把光标挪到外屏 → 我们的光标仲裁判定成"本机用户在动鼠标"→ 把光标抢回物理屏、
///     并把事件重放到物理屏 → **它在外屏点不动**；
///   · 反过来它的动作也把我们的注入打断 → 我们也点不动，只能退到后台定向注入。
/// 现场表现就是"能滑动、点不了、点击跑到主屏"，而两边单独跑都正常（实测踩到）。
///
/// 所以策略是：**发现同类软件就把我们自己的光标仲裁停掉**，退出争夺、与它共用同一套系统光标
/// （和单机用鼠标一样）；它退出后再把仲裁装回来。这里只负责"发现"，处置在调用方。
/// </summary>
internal static class InputRivals
{
    /// <summary>已知会自己管鼠标的同类软件（进程名片段，大小写不敏感）</summary>
    private static readonly string[] Keys =
    {
        "todesk", "sunlogin", "anydesk", "rustdesk", "teamviewer", "splashtop", "gotomypc",
        "gameviewer", "awesun", "uuremote", "quickassist", "mstsc",
        "uu远程", "向日葵", "远程协助", "远程桌面连接",
    };

    /// <summary>返回命中的进程名（去重）。空数组 = 没有发现同类软件。</summary>
    public static string[] Detect()
    {
        var hits = new List<string>();
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    var name = p.ProcessName;
                    var lower = name.ToLowerInvariant();
                    if (Keys.Any(k => lower.Contains(k))) hits.Add(name);
                }
                catch { }
                finally { try { p.Dispose(); } catch { } }
            }
        }
        catch { }
        return hits.Distinct().ToArray();
    }

    /// <summary>本机被控端自己占用的进程数（装了第二份被控端时 &gt; 2，同样会互相抢光标）</summary>
    public static int SelfProcessCount()
    {
        int n = 0;
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    var lower = p.ProcessName.ToLowerInvariant();
                    if (lower.StartsWith("agent.worker") || lower.StartsWith("agent.coordinator")) n++;
                }
                catch { }
                finally { try { p.Dispose(); } catch { } }
            }
        }
        catch { }
        return n;
    }
}
