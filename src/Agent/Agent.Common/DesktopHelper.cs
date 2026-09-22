using System.Runtime.InteropServices;

namespace Agent.Common;

/// <summary>
/// 桌面 / 窗口站诊断：
/// 计划任务等方式启动的进程可能不在**会话的输入桌面**上，此时 GetForegroundWindow 返回 0、
/// SetForegroundWindow 无效、SendInput 也进不到用户正在看的桌面（实测踩到）。
/// </summary>
public static class DesktopHelper
{
    public static string GetWindowStationName() => GetObjectName(NativeApi.GetProcessWindowStation());

    public static string GetThreadDesktopName() =>
        GetObjectName(NativeApi.GetThreadDesktop(NativeApi.GetCurrentThreadId()));

    public static string GetInputDesktopName()
    {
        var h = NativeApi.OpenInputDesktop(0, false, 0x0001 /*DESKTOP_READOBJECTS*/);
        if (h == IntPtr.Zero) return $"(打不开输入桌面 err={Marshal.GetLastWin32Error()})";
        try { return GetObjectName(h); }
        finally { NativeApi.CloseDesktop(h); }
    }

    /// <summary>当前进程是否在会话的输入桌面上</summary>
    public static bool IsOnInputDesktop()
    {
        var mine = GetThreadDesktopName();
        var input = GetInputDesktopName();
        return mine.Length > 0 && mine == input;
    }

    private static string GetObjectName(IntPtr h)
    {
        if (h == IntPtr.Zero) return "";
        var buf = new char[256];
        if (!NativeApi.GetUserObjectInformationW(h, NativeApi.UOI_NAME, buf, buf.Length * 2, out _))
            return $"(查询失败 err={Marshal.GetLastWin32Error()})";
        var s = new string(buf);
        int z = s.IndexOf('\0');
        return (z >= 0 ? s[..z] : s).Trim();
    }
}
