using System.Runtime.InteropServices;
using Agent.Common;

namespace Viewer.Services;

/// <summary>
/// 主控端的"系统热键捕获"：把 Win、Win+X、Alt+Tab、Ctrl+Esc 这类**操作系统自己会先处理**的键
/// 截下来，转发给被控端。
///
/// 【为什么必须做】不装钩子时，Win+R 在本机就被 Windows 抢走了 —— 弹出来的是**本机**的运行框，
/// 而被控端什么都没收到（现场反馈："我按 Win+R 出来的是我这台的，说明键盘没传过去"）。
/// 原因：这些键由系统在更低层处理，WPF 的键盘事件（甚至我们发的消息）拦不住。
/// 只有 WH_KEYBOARD_LL（低级键盘钩子）能在系统之前看到并吃掉它们。
///
/// 【边界】
///   · **只在主控端窗口是前台窗口时**才生效，而且只拦上面那几个组合键；
///     窗口不在前台时一律放行 —— 绝不能影响本机用户自己的键盘（托盘/其它程序都要正常）。
///   · 只拦"组合了 Win/Ctrl+Esc/Alt+Tab 的那一下"，单独的字母数字键照旧走正常消息路径。
/// </summary>
public sealed class HotkeyCapture : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104;
    private const int VK_TAB = 0x09, VK_ESCAPE = 0x1B, VK_LWIN = 0x5B, VK_RWIN = 0x5C,
                      VK_LMENU = 0xA4, VK_RMENU = 0xA5, VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3;

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode, scanCode, flags, time;
        public IntPtr dwExtraInfo;
    }

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    private readonly Logger _log;
    private readonly Func<bool> _active;                 // 现在该不该接管（窗口在前台且已连接）
    private readonly Action<ushort, bool, byte> _forward; // 转发给被控端
    private HookProc? _proc;                              // 必须持有引用，否则回调变野指针
    private IntPtr _hook;
    /// <summary>
    /// Win 键是否按下 —— **必须自己记**：我们把 Win 的按下事件吃掉之后，
    /// 系统就不再把它标记为"按下"了（GetAsyncKeyState 会返回 false），
    /// 于是紧接着按 R 时判断"Win 在按着吗"得到 false，R 被放行给本机 ——
    /// 现场就是"Win+R 弹出的是本机运行框"（实测踩到）。
    /// </summary>
    private bool _winDown;

    public bool Installed => _hook != IntPtr.Zero;

    public HotkeyCapture(Logger log, Func<bool> active, Action<ushort, bool, byte> forward)
    {
        _log = log;
        _active = active;
        _forward = forward;
        try
        {
            _proc = Proc;
            _hook = SetWindowsHookExW(WH_KEYBOARD_LL, _proc, GetModuleHandleW(null), 0);
            _log.Info(_hook != IntPtr.Zero
                ? "系统热键捕获已启用：Win / Alt+Tab / Ctrl+Esc 会转发给被控端（仅主控端窗口在前台时）"
                : "系统热键捕获未启用（装钩子失败）：Win+R 这类键仍会在本机生效");
        }
        catch (Exception ex)
        {
            _log.Warn($"系统热键捕获初始化失败：{ex.Message}");
        }
    }

    private static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>这个键要不要截下来（只认那几个系统热键，其余一律放行）</summary>
    private static bool ShouldCapture(uint vk, bool winSelfTracked)
    {
        // Win 本身、以及"按着 Win 时按下的任何键"（Win 状态用我们自己记的，见 _winDown）
        if (vk is VK_LWIN or VK_RWIN) return true;
        if (winSelfTracked || Down(VK_LWIN) || Down(VK_RWIN)) return true;
        // Alt+Tab / Alt+Esc：切窗口/切任务，系统会先吃掉
        bool alt = Down(VK_LMENU) || Down(VK_RMENU);
        if (alt && (vk == VK_TAB || vk == VK_ESCAPE)) return true;
        // Ctrl+Esc：开始菜单
        bool ctrl = Down(VK_LCONTROL) || Down(VK_RCONTROL);
        if (ctrl && vk == VK_ESCAPE) return true;
        return false;
    }

    private IntPtr Proc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return CallNextHookEx(_hook, nCode, wParam, lParam);
        try
        {
            int msg = wParam.ToInt32();
            bool up = msg is 0x0101 or 0x0105;   // WM_KEYUP / WM_SYSKEYUP
            if (!up && msg is not (WM_KEYDOWN or WM_SYSKEYDOWN))
                return CallNextHookEx(_hook, nCode, wParam, lParam);

            var kb = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            uint vk = kb.vkCode;
            bool isWin = vk is VK_LWIN or VK_RWIN;
            if (isWin)
            {
                // Win 键本身：无论窗口在不在前台都要正确记录状态
                if (up) _winDown = false; else _winDown = true;
            }
            if (!_active())
            {
                // 窗口不在前台：一律放行给本机 —— **但 Win 的抬起必须转发给被控端**，
                // 否则一旦在"按住 Win 时切走焦点"，被控端的 Win 就永远按着，整机键盘都会乱弹。
                if (isWin && up) { _winDown = false; _forward((ushort)vk, true, 0); }
                return CallNextHookEx(_hook, nCode, wParam, lParam);
            }
            if (!ShouldCapture(vk, _winDown)) return CallNextHookEx(_hook, nCode, wParam, lParam);

            // 转发给被控端，并把本机这一下吃掉（否则本机也会弹运行框/切窗口）
            _forward((ushort)kb.vkCode, up, 0);
            return new IntPtr(1);
        }
        catch
        {
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }
    }

    public void Dispose()
    {
        try { if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook); } catch { }
        _hook = IntPtr.Zero;
    }
}
