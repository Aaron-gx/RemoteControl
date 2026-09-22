using System.Runtime.InteropServices;

namespace Agent.Common;

/// <summary>
/// 低层鼠标/键盘钩子（WH_MOUSE_LL / WH_KEYBOARD_LL）与注入事件标记。
/// 单独一个 partial 文件，方便和 NativeApi.cs / NativeApi.Window.cs 对照阅读。
/// </summary>
public static partial class NativeApi
{
    // ---------------------------------------------------------------- 常量

    public const int WH_KEYBOARD_LL = 13;
    public const int WH_MOUSE_LL = 14;

    public const int WM_MOUSEMOVE = 0x0200;
    public const int WM_LBUTTONDOWN = 0x0201;
    public const int WM_LBUTTONUP = 0x0202;
    public const int WM_RBUTTONDOWN = 0x0204;
    public const int WM_RBUTTONUP = 0x0205;
    public const int WM_MBUTTONDOWN = 0x0207;
    public const int WM_MBUTTONUP = 0x0208;
    public const int WM_MOUSEWHEEL = 0x020A;
    public const int WM_MOUSEHWHEEL = 0x020E;

    /// <summary>MSLLHOOKSTRUCT.flags：事件由软件注入（SendInput / mouse_event）</summary>
    public const uint LLMHF_INJECTED = 0x00000001;
    /// <summary>MSLLHOOKSTRUCT.flags：由更低完整性级别的进程注入</summary>
    public const uint LLMHF_LOWER_IL_INJECTED = 0x00000002;

    /// <summary>
    /// 我们自己的注入事件标记，写进 MOUSEINPUT / KEYBDINPUT 的 dwExtraInfo。
    /// 只靠 LLMHF_INJECTED 区分不了"远端注入"和"本机触摸板/厂商驱动用 SendInput 送的输入"，
    /// 而两者的待遇正好相反（前者要归还光标、后者必须原样放行），所以必须打这个记号。
    /// </summary>
    public const long InjectTag = 0x5257_4354_524C_3031; // 'RWCTRL01'

    public static IntPtr InjectTagPtr => new(InjectTag);

    /// <summary>这个事件是不是我们自己注入的</summary>
    public static bool IsOurInjection(uint flags, IntPtr extraInfo)
        => (flags & LLMHF_INJECTED) != 0 && extraInfo.ToInt64() == InjectTag;

    // ---------------------------------------------------------------- 结构

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    // ---------------------------------------------------------------- 钩子

    public delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
    public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookExW(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    public static extern IntPtr SetWindowsHookExKb(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandleW(string? lpModuleName);
}
