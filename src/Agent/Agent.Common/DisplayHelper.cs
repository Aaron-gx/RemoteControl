using System.Runtime.InteropServices;

namespace Agent.Common;

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
