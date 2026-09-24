using System.Text;
using System.Runtime.InteropServices;

namespace Agent.Common;

/// <summary>窗口操作相关 P/Invoke</summary>
public static partial class NativeApi
{
    public const int SW_HIDE = 0;
    public const int SW_SHOWNORMAL = 1;
    public const int SW_SHOWMINIMIZED = 2;
    public const int SW_SHOWMAXIMIZED = 3;
    public const int SW_SHOWNOACTIVATE = 4;   // 显示但不激活（用于取消最小化且不抢焦点）
    public const int SW_SHOW = 5;             // 按当前大小显示并激活
    public const int SW_RESTORE = 9;

    public const uint SWP_NOSIZE = 0x0001;     // 只挪位置，不改窗口尺寸
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool ShowWindow(IntPtr hwnd, int cmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern IntPtr FindWindowW(string? className, string? windowName);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool OpenClipboard(IntPtr hwndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    public static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int pid);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    public static extern bool AttachThreadInput(uint attachTo, uint attachFrom, bool attach);

    [DllImport("user32.dll")]
    public static extern IntPtr SetActiveWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern bool BringWindowToTop(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hwnd);

    /// <summary>窗口是否最大化（最大化状态下 SetWindowPos 的位置会被忽略，挪屏前得先还原）</summary>
    [DllImport("user32.dll")]
    public static extern bool IsZoomed(IntPtr hwnd);

    // ---------------------------------------------------------------- 桌面 / 窗口站

    public const int UOI_NAME = 2;

    [DllImport("user32.dll")]
    public static extern IntPtr GetProcessWindowStation();

    [DllImport("user32.dll")]
    public static extern IntPtr GetThreadDesktop(uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool CloseDesktop(IntPtr h);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool GetUserObjectInformationW(IntPtr hObj, int index,
        [Out] char[] info, int length, out int lengthNeeded);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetThreadDesktop(IntPtr hDesktop);

    // ---------------------------------------------------------------- 控制台窗口

    public const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool AttachConsole(uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool FreeConsole();

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetConsoleWindow();

    // ---------------------------------------------------------------- 窗口枚举

    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassNameW(IntPtr hwnd, StringBuilder name, int maxCount);

    /// <summary>取窗口类名（"ConsoleWindowClass" / "Notepad" / …）</summary>
    public static string GetClassName(IntPtr hwnd)
    {
        try
        {
            var sb = new StringBuilder(256);
            int n = GetClassNameW(hwnd, sb, sb.Capacity);
            return n > 0 ? sb.ToString() : "";
        }
        catch { return ""; }
    }

    // ---------------------------------------------------------------- 子窗口 / 对话框自动化

    public delegate bool EnumChildWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool EnumChildWindows(IntPtr parent, EnumChildWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowLongW(IntPtr hwnd, int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>取窗口标题/控件文本</summary>
    public static string GetWindowText(IntPtr hwnd)
    {
        try
        {
            var sb = new StringBuilder(512);
            int n = GetWindowTextW(hwnd, sb, sb.Capacity);
            return n > 0 ? sb.ToString() : "";
        }
        catch { return ""; }
    }

    /// <summary>取窗口样式（GWL_STYLE），用于识别按钮/复选框类型</summary>
    public static int GetWindowStyle(IntPtr hwnd)
    {
        try { return GetWindowLongW(hwnd, GWL_STYLE); } catch { return 0; }
    }

    public const int GWL_STYLE = -16;
    public const uint BM_CLICK = 0x00F5;
    public const uint BM_SETCHECK = 0x00F1;

    /// <summary>是否为复选框/三态框（BS_CHECKBOX / BS_AUTOCHECKBOX / BS_3STATE / BS_AUTO3STATE）</summary>
    public static bool IsCheckBoxStyle(int style)
    {
        int type = style & 0xF;
        return type == 2 || type == 3 || type == 5 || type == 6;
    }
}
