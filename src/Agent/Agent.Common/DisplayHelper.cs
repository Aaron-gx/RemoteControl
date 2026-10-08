using System.Runtime.InteropServices;
using System.Threading;

namespace Agent.Common;

/// <summary>一次"健康布局"的快照（诊断与自愈参照）。对标 UU远程 的 save/restore_display_layout——
/// 我们的虚拟外屏常驻，不做整拓扑回滚（风险高：SetDisplayConfig 配错会黑屏），快照用于：
/// 启动时对照当前布局找异常（医生日志）+ 现场排障 + 人工恢复参照。</summary>
public sealed class DisplayLayoutSnapshot
{
    public long SavedAt { get; set; }
    public List<DisplayInfo> Monitors { get; set; } = new();

    public string Describe() => string.Join(" | ", Monitors);

    public static DisplayLayoutSnapshot Capture() => new()
    {
        SavedAt = DateTimeOffset.Now.ToUnixTimeSeconds(),
        Monitors = DisplayHelper.GetMonitors().ToList(),
    };

    /// <summary>落盘（内容有变化才写，避免 2 秒一拍刷盘）。只在布局"健康"（外屏在、不重叠）时存。</summary>
    /// <returns>true=这次真的写了新快照</returns>
    public static bool SaveIfChanged(string path, Logger? log = null)
    {
        try
        {
            var snap = Capture();
            var virt = snap.Monitors.FirstOrDefault(m => m.IsVirtual);
            if (virt == null) return false;
            foreach (var p in snap.Monitors.Where(m => !m.IsVirtual))
            {
                if (virt.Left < p.Right && virt.Right > p.Left && virt.Top < p.Bottom && virt.Bottom > p.Top)
                    return false;   // 还重叠着：不健康，不存
            }
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string fresh = System.Text.Json.JsonSerializer.Serialize(snap,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            string? old = File.Exists(path) ? File.ReadAllText(path) : null;
            if (old == fresh) return false;
            File.WriteAllText(path, fresh);
            log?.Info($"显示布局快照已更新：{snap.Describe()}");
            return true;
        }
        catch (Exception ex)
        {
            log?.Debug($"写显示布局快照失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>启动"布局医生"：读出快照并与当前拓扑对照，差异写日志
    ///（自愈仍走既有的相邻归位/分辨率切换路径，这里负责"说清楚"而不是"动手"）。</summary>
    public static void Doctor(string path, Logger log)
    {
        try
        {
            var cur = DisplayHelper.GetMonitors();
            if (!File.Exists(path))
            {
                log.Info($"布局医生：无历史快照（首次运行），当前拓扑：{string.Join(" | ", cur)}");
                return;
            }
            var snap = System.Text.Json.JsonSerializer.Deserialize<DisplayLayoutSnapshot>(File.ReadAllText(path));
            if (snap == null || snap.Monitors.Count == 0) return;
            var diffs = new List<string>();
            foreach (var s in snap.Monitors)
            {
                var c = cur.FirstOrDefault(m => m.DeviceName == s.DeviceName);
                if (c == null) { diffs.Add($"{s.DeviceName} 快照里有、现在没了"); continue; }
                if (c.Left != s.Left || c.Top != s.Top || c.Width != s.Width || c.Height != s.Height)
                    diffs.Add($"{s.DeviceName} 快照({s.Left},{s.Top} {s.Width}x{s.Height}) → 现在({c.Left},{c.Top} {c.Width}x{c.Height})");
            }
            foreach (var c in cur.Where(c => snap.Monitors.All(s => s.DeviceName != c.DeviceName)))
                diffs.Add($"{c.DeviceName} 快照里没有、现在新出现");
            log.Info(diffs.Count == 0
                ? $"布局医生：与最近一次健康快照一致（{snap.Describe()}）"
                : $"布局医生：与快照有差异 —— {string.Join("；", diffs)}");
        }
        catch (Exception ex)
        {
            log.Debug($"布局医生异常：{ex.Message}");
        }
    }
}

public sealed record DisplayInfo(string DeviceName, int Left, int Top, int Width, int Height, bool IsPrimary,
    string AdapterName = "")
{
    public int Right => Left + Width;
    public int Bottom => Top + Height;

    /// <summary>是否为虚拟显示器（适配器名含 VDD/Virtual）</summary>
    public bool IsVirtual =>
        AdapterName.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
        AdapterName.Contains("VDD", StringComparison.OrdinalIgnoreCase) ||
        DeviceName.Contains("VDD", StringComparison.OrdinalIgnoreCase) ||
        DeviceName.Contains("Virtual", StringComparison.OrdinalIgnoreCase);

    public override string ToString() =>
        $"{DeviceName} {Width}x{Height}@({Left},{Top}){(IsPrimary ? " [主]" : "")}" +
        (string.IsNullOrEmpty(AdapterName) ? "" : $" <{AdapterName}>");
}

/// <summary>显示器枚举（当前会话的虚拟桌面）</summary>
public static class DisplayHelper
{
    public static List<DisplayInfo> GetMonitors()
    {
        var list = new List<DisplayInfo>();
        NativeApi.MonitorEnumProc proc = (IntPtr hMonitor, IntPtr hdc, ref NativeApi.RECT rc, IntPtr data) =>
        {
            var mi = new NativeApi.MONITORINFOEX
            {
                cbSize = Marshal.SizeOf<NativeApi.MONITORINFOEX>(),
                szDevice = "",
            };
            if (NativeApi.GetMonitorInfoW(hMonitor, ref mi))
            {
                bool primary = (mi.dwFlags & 1) != 0;
                list.Add(new DisplayInfo(mi.szDevice,
                    mi.rcMonitor.Left, mi.rcMonitor.Top,
                    mi.rcMonitor.Right - mi.rcMonitor.Left,
                    mi.rcMonitor.Bottom - mi.rcMonitor.Top, primary,
                    NativeApi.GetAdapterName(mi.szDevice)));
            }
            return true;
        };
        NativeApi.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, proc, IntPtr.Zero);
        return list;
    }

    /// <summary>虚拟桌面几何（多显示器时是所有屏的并集）</summary>
    public static (int Left, int Top, int Width, int Height) GetVirtualDesktopBounds()
    {
        int l = NativeApi.GetSystemMetrics(NativeApi.SM_XVIRTUALSCREEN);
        int t = NativeApi.GetSystemMetrics(NativeApi.SM_YVIRTUALSCREEN);
        int w = NativeApi.GetSystemMetrics(NativeApi.SM_CXVIRTUALSCREEN);
        int h = NativeApi.GetSystemMetrics(NativeApi.SM_CYVIRTUALSCREEN);
        if (w <= 0 || h <= 0)
        {
            var m = GetMonitors();
            if (m.Count > 0)
            {
                l = m.Min(x => x.Left);
                t = m.Min(x => x.Top);
                w = m.Max(x => x.Right) - l;
                h = m.Max(x => x.Bottom) - t;
            }
        }
        return (l, t, w, h);
    }

    /// <summary>挑选要采集的显示器：优先虚拟副屏（按适配器名判定），否则主屏</summary>
    public static DisplayInfo? PickCaptureTarget(string? preferNameFragment = null)
    {
        var monitors = GetMonitors();
        if (monitors.Count == 0) return null;
        if (!string.IsNullOrEmpty(preferNameFragment))
        {
            var hit = monitors.FirstOrDefault(m =>
                m.DeviceName.Contains(preferNameFragment, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit;
        }
        var virtualMonitor = monitors.FirstOrDefault(m => m.IsVirtual);
        if (virtualMonitor != null) return virtualMonitor;
        return monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
    }

    /// <summary>
    /// 重启虚拟显示驱动设备节点（2026-10-08 真机事故：VDD 显示器自发消失 + NVIDIA 驱动重置把物理屏
    /// 也带走了，桌面塌缩成 800x600，远端键鼠全部错位）。现场验证 `pnputil /restart-device`
    /// 能把虚拟屏拉回来（物理屏需重启机器恢复）。巡检发现"该有的虚拟屏没了"时自动执行，10 分钟节流。
    /// </summary>
    public static bool TryRestartVirtualDisplayDriver(string driverId, Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(driverId)) return false;
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("pnputil", $"/restart-device \"{driverId}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return false;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(15000);
            var firstLine = output.Trim().Split('\n').FirstOrDefault() ?? "";
            log?.Invoke($"已执行 pnputil /restart-device {driverId}（exit={p.ExitCode}）：{firstLine}");
            return p.ExitCode == 0;
        }
        catch (Exception ex)
        {
            log?.Invoke($"重启虚拟屏驱动失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>虚拟外屏矩形是否与任一物理屏相交</summary>
    public static bool OverlapsAnyPhysical(DisplayInfo virt)
    {
        foreach (var p in GetMonitors())
        {
            if (p.DeviceName == virt.DeviceName || p.IsVirtual) continue;
            if (virt.Left < p.Right && virt.Right > p.Left && virt.Top < p.Bottom && virt.Bottom > p.Top)
                return true;
        }
        return false;
    }

    /// <summary>
    /// 确保虚拟外屏**紧贴所有物理屏并集的右侧、互不重叠**（2026-10-07，客户反馈
    /// "窗口一半在主屏一半在副屏"）。物理屏换分辨率（1920→2560）或 IDD 驱动重建后，
    /// 外屏可能仍停在旧坐标（例如 x=1920），把物理屏右半边盖住 —— 落在外屏上的窗口
    /// 左边 640px 会同时显示在物理屏上，本机用户看得一清二楚。
    /// 重叠时用 ChangeDisplaySettingsEx 挪到所有物理屏右侧紧贴处。
    /// 返回 true=**已核实挪动生效**（坐标已变，调用方必须重新枚举并重建采集目标）。
    /// 【2026-10-08 真机踩坑】部分 IDD 驱动/同机远控软件会把位置锁死：API 返回成功(0)
    /// 但位置纹丝不动 —— 必须重枚举核实，否则调用方会陷入"假成功→重建→再试"的死循环。
    /// </summary>
    public static bool EnsureVirtualAdjacent(DisplayInfo virt, Action<string>? log = null)
    {
        try
        {
            var monitors = GetMonitors();
            var me = monitors.FirstOrDefault(m => m.DeviceName == virt.DeviceName) ?? virt;
            var physical = monitors.Where(m => m.DeviceName != me.DeviceName && !m.IsVirtual).ToList();
            if (physical.Count == 0) return false;
            bool overlaps = physical.Any(p =>
                me.Left < p.Right && me.Right > p.Left && me.Top < p.Bottom && me.Bottom > p.Top);
            if (!overlaps) return false;

            // 挪到所有物理屏并集的右侧，顶边与最靠右那块物理屏对齐
            int newLeft = physical.Max(p => p.Right);
            int newTop = physical.First(p => p.Right == newLeft).Top;
            if (newLeft == me.Left && newTop == me.Top) return false;

            var dm = new NativeApi.DEVMODEW
            {
                dmDeviceName = "",
                dmFormName = "",
                dmSize = (ushort)Marshal.SizeOf<NativeApi.DEVMODEW>(),
            };
            if (!NativeApi.EnumDisplaySettingsW(me.DeviceName, -1, ref dm))
            {
                log?.Invoke($"读取 {me.DeviceName} 当前显示模式失败，无法挪位");
                return false;
            }
            log?.Invoke($"虚拟外屏 {me.DeviceName} 与物理屏重叠（{me.Left},{me.Top} {me.Width}x{me.Height}，" +
                        $"物理屏并集右边线 {newLeft}）→ 尝试挪到 ({newLeft},{newTop})");
            dm.dmPositionX = newLeft;
            dm.dmPositionY = newTop;
            dm.dmFields = NativeApi.DM_DISPLAYPOSITION;
            int r = NativeApi.ChangeDisplaySettingsExW(me.DeviceName, ref dm, IntPtr.Zero,
                NativeApi.CDS_UPDATEREGISTRY, IntPtr.Zero);
            if (r != NativeApi.DISP_CHANGE_SUCCESSFUL)
            {
                log?.Invoke($"挪动虚拟外屏失败：ChangeDisplaySettingsEx 返回 {r}");
                return false;
            }
            // API 报成功不算数，重枚举核实位置真的变了
            Thread.Sleep(600);
            var after = GetMonitors().FirstOrDefault(m => m.DeviceName == me.DeviceName);
            if (after == null || after.Left != newLeft || after.Top != newTop)
            {
                log?.Invoke($"挪动未生效（{me.DeviceName} 位置仍为 ({after?.Left.ToString() ?? "?"},{after?.Top.ToString() ?? "?"})，" +
                            "被虚拟屏驱动或同机远控软件锁定）。外屏与物理屏重叠期间，副屏窗口会有一截显示在物理屏上；" +
                            "重启被控端或重插物理屏可恢复。");
                return false;
            }
            log?.Invoke($"虚拟外屏已归位到 ({after.Left},{after.Top})");
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke($"挪动虚拟外屏异常：{ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 把某块显示器切到指定分辨率/刷新率（虚拟副屏默认可能是 800x600，需要显式切换）。
    /// 24H2 的 IDD 驱动存在"活动信号模式已变、桌面模式没变"的回归，这里用
    /// ChangeDisplaySettingsEx 显式切换桌面模式来规避。
    /// </summary>
    public static bool TrySetDisplayMode(string deviceName, int width, int height, int hz,
        Action<string>? log = null)
    {
        try
        {
            var dm = new NativeApi.DEVMODEW
            {
                dmDeviceName = "",
                dmFormName = "",
                dmSize = (ushort)Marshal.SizeOf<NativeApi.DEVMODEW>(),
            };
            if (!NativeApi.EnumDisplaySettingsW(deviceName, -1 /*ENUM_CURRENT_SETTINGS*/, ref dm))
            {
                log?.Invoke($"EnumDisplaySettings({deviceName}) 失败，无法读取当前模式");
                return false;
            }
            if (dm.dmPelsWidth == width && dm.dmPelsHeight == height)
            {
                // 只比宽高：RDP 虚拟显示器的刷新率是虚拟值（例如 32），按刷新率去切必然失败
                log?.Invoke($"{deviceName} 分辨率已匹配（{dm.dmPelsWidth}x{dm.dmPelsHeight}@{dm.dmDisplayFrequency}Hz），无需切换");
                return true;
            }
            log?.Invoke($"切换 {deviceName}：{dm.dmPelsWidth}x{dm.dmPelsHeight}@{dm.dmDisplayFrequency} → {width}x{height}@{hz}");
            dm.dmPelsWidth = (uint)width;
            dm.dmPelsHeight = (uint)height;
            if (hz > 0) dm.dmDisplayFrequency = (uint)hz;
            dm.dmFields = NativeApi.DM_PELSWIDTH | NativeApi.DM_PELSHEIGHT |
                          (hz > 0 ? NativeApi.DM_DISPLAYFREQUENCY : 0);
            int r = NativeApi.ChangeDisplaySettingsExW(deviceName, ref dm, IntPtr.Zero,
                NativeApi.CDS_UPDATEREGISTRY, IntPtr.Zero);
            if (r == NativeApi.DISP_CHANGE_SUCCESSFUL)
            {
                log?.Invoke($"{deviceName} 已切换到 {width}x{height}@{hz}");
                return true;
            }
            log?.Invoke($"ChangeDisplaySettingsEx({deviceName}) 返回 {r}（非 0 表示失败/需重启）");
            return false;
        }
        catch (Exception ex)
        {
            log?.Invoke($"切换显示模式异常：{ex.Message}");
            return false;
        }
    }

    /// <summary>设置进程 DPI 感知（避免坐标被系统缩放影响）</summary>
    public static void EnablePerMonitorDpiAwareness()
    {
        try
        {
            // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4
            if (NativeApi.SetProcessDpiAwarenessContext(new IntPtr(-4))) return;
        }
        catch { }
        try { NativeApi.SetProcessDpiAwareness(2); } catch { }
        try { NativeApi.SetProcessDPIAware(); } catch { }
    }
}
