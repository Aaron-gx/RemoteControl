using System.Runtime.InteropServices;

namespace Agent.Common;

/// <summary>
/// 后台定向注入 / 命中测试所需的 P/Invoke。
/// 与 NativeApi.cs（输入注入）、NativeApi.Window.cs（窗口操作）分开，便于对照。
/// </summary>
public static partial class NativeApi
{
    // ---------------------------------------------------------------- 命中测试

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr WindowFromPoint(POINT pt);

    [DllImport("user32.dll")]
    public static extern IntPtr RealChildWindowFromPoint(IntPtr parent, POINT ptClient);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool ScreenToClient(IntPtr hwnd, ref POINT pt);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool ClientToScreen(IntPtr hwnd, ref POINT pt);

    [DllImport("user32.dll")]
    public static extern bool IsWindowEnabled(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetParent(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    public const uint GA_ROOT = 2;
    public const uint GA_ROOTOWNER = 3;

    // ---------------------------------------------------------------- 投递消息

    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    public static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    public const uint WM_NULL = 0x0000;
    public const uint WM_MOUSEACTIVATE = 0x0021;
    public const uint WM_KEYDOWN = 0x0100;
    public const uint WM_KEYUP = 0x0101;
    public const uint WM_CHAR = 0x0102;

    public const int MA_ACTIVATE = 1;

    // PostMessage 用的按键状态位（WM_*BUTTON* 的 wParam / WM_MOUSEMOVE 的 wParam）
    public const int MK_LBUTTON = 0x0001;
    public const int MK_RBUTTON = 0x0002;
    public const int MK_SHIFT = 0x0004;
    public const int MK_CONTROL = 0x0008;
    public const int MK_MBUTTON = 0x0010;

    // ---------------------------------------------------------------- 键盘字符翻译

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int ToUnicodeEx(uint wVirtKey, uint wScanCode, byte[] lpKeyState,
        [Out] char[] pwszBuff, int cchBuff, uint wFlags, IntPtr dwhkl);

    [DllImport("user32.dll")]
    public static extern uint MapVirtualKeyW(uint code, uint mapType);

    [DllImport("user32.dll")]
    public static extern IntPtr GetKeyboardLayout(uint threadId);

    public const uint MAPVK_VK_TO_VSC = 0;
}
