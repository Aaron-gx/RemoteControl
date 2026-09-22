using System.Runtime.InteropServices;

namespace Agent.Common;

/// <summary>
/// 后台定向注入：把鼠标事件**直接投递给"某个屏幕坐标下的那个窗口"**，全程不移动系统光标。
///
/// 【为什么需要它】
/// 一个 Windows 会话只有一个系统光标，这是操作系统的硬限制（连商用的 MouseMux 也绕不开，
/// 它靠内核过滤驱动 + 自绘光标，且默认模式仍然要"点击串行化"）。本机用户在用自己的物理鼠标时，
/// 我们如果还去挪那个唯一的系统光标，就是两边抢同一个光标。
/// 这条路的思路是：**不碰系统光标** —— 远端要点的位置，我们算出"那个位置下面是哪个窗口"，
/// 把 WM_MOUSEMOVE / WM_LBUTTONDOWN … 直接 Post 给那个窗口。于是系统光标可以老老实实
/// 留在本机用户那边，一根毫毛都不动。
///
/// 【能做到什么 / 做不到什么】—— 这是实测过的边界，别当它是万能药：
///   ✅ 标准控件（按钮/编辑框/列表/菜单条）、DuiLib 这类"自己在 WM_MOUSEMOVE 里做命中测试 +
///      自绘悬停"的应用（微信就是这一类）、Chromium/Electron 的单 HWND 窗口：点击、移动、
///      滚轮、悬停高亮基本可用。
///   ❌ 依赖**真实输入**才能起来的东西：原生 TrackPopupMenu 右键菜单（要求窗口是前台窗口）、
///      依赖 SetCapture 的拖拽（我们只能模拟：按住期间持续往同一个窗口投递带 MK_LBUTTON 的
///      WM_MOUSEMOVE）、DirectInput/原始输入的游戏、以及"合成消息 vs 可信输入"敏感的程序。
///   因此本类不是唯一路径，它和"真实光标注入"由 <c>MouseRouter</c> 自适应切换。
///
/// 本类只做两件事：命中测试 + 投递。策略在 MouseRouter。
/// </summary>
public static class BackgroundInput
{
    private const int MaxDescend = 16;

    /// <summary>命中最深的那层可交互子窗口（跳过隐藏/禁用的），供投递使用</summary>
    public static IntPtr HitTest(int screenX, int screenY)
    {
        var pt = new NativeApi.POINT { X = screenX, Y = screenY };
        var hwnd = NativeApi.WindowFromPoint(pt);
        if (hwnd == IntPtr.Zero) return IntPtr.Zero;

        for (int i = 0; i < MaxDescend; i++)
        {
            var client = new NativeApi.POINT { X = screenX, Y = screenY };
            if (!NativeApi.ScreenToClient(hwnd, ref client)) break;
            var child = NativeApi.RealChildWindowFromPoint(hwnd, client);
            if (child == IntPtr.Zero || child == hwnd) break;
            hwnd = child;
        }

        // 命中最深的那个如果不可用（隐藏/禁用），往上找到第一个可用的祖先
        var probe = hwnd;
        for (int i = 0; i <= MaxDescend && probe != IntPtr.Zero; i++)
        {
            if (NativeApi.IsWindowVisible(probe) && NativeApi.IsWindowEnabled(probe)) return probe;
            probe = NativeApi.GetParent(probe);
        }
        return hwnd;
    }

    /// <summary>这个窗口能不能被我们投递（排除自己进程的窗口，避免自我注入）</summary>
    public static bool IsPostable(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !NativeApi.IsWindowVisible(hwnd)) return false;
        NativeApi.GetWindowThreadProcessId(hwnd, out int pid);
        return pid != 0 && pid != Environment.ProcessId;
    }

    /// <summary>投递 WM_MOUSEMOVE（带按键状态，拖拽时要带 MK_LBUTTON）</summary>
    public static bool PostMove(IntPtr hwnd, int x, int y, int buttonState)
        => PostClient(hwnd, NativeApi.WM_MOUSEMOVE, x, y, buttonState);

    /// <summary>投递按键（button：0 左 1 右 2 中），按当前按住状态自动补上 wParam</summary>
    public static bool PostButton(IntPtr hwnd, int x, int y, byte button, bool up, int heldState)
    {
        int flag = button switch
        {
            0 => NativeApi.MK_LBUTTON,
            1 => NativeApi.MK_RBUTTON,
            2 => NativeApi.MK_MBUTTON,
            _ => 0,
        };
        if (flag == 0) return false;
        uint msg = (button, up) switch
        {
            (0, false) => NativeApi.WM_LBUTTONDOWN,
            (0, true) => NativeApi.WM_LBUTTONUP,
            (1, false) => NativeApi.WM_RBUTTONDOWN,
            (1, true) => NativeApi.WM_RBUTTONUP,
            (2, false) => NativeApi.WM_MBUTTONDOWN,
            _ => NativeApi.WM_MBUTTONUP,
        };
        int wParam = up ? (heldState & ~flag) : (heldState | flag);
        return PostClient(hwnd, msg, x, y, wParam);
    }

    /// <summary>
    /// 投递滚轮。**注意 WM_MOUSEWHEEL 是唯一用屏幕坐标的鼠标消息**，
    /// 而且增量在有符号高 16 位，不能走 ScreenToClient。
    /// </summary>
    public static bool PostWheel(IntPtr hwnd, int screenX, int screenY, short delta, bool horizontal = false)
    {
        int lp = MakeLParam(screenX, screenY);
        int wp = unchecked((int)((uint)(ushort)delta << 16));
        uint msg = (uint)(horizontal ? NativeApi.WM_MOUSEHWHEEL : NativeApi.WM_MOUSEWHEEL);
        return NativeApi.PostMessage(hwnd, msg, new IntPtr(wp), new IntPtr(lp));
    }

    /// <summary>投递键盘按下/抬起（不经过输入队列，进不了"当前前台窗口"之外的地方）</summary>
    public static bool PostKey(IntPtr hwnd, uint vk, bool up, bool extended)
    {
        uint scan = NativeApi.MapVirtualKeyW(vk, NativeApi.MAPVK_VK_TO_VSC) & 0xFF;
        uint lp = 1u | (scan << 16);
        if (extended) lp |= 1u << 24;
        if (up) lp |= 1u << 30 | 1u << 31;
        return NativeApi.PostMessage(hwnd, up ? NativeApi.WM_KEYUP : NativeApi.WM_KEYDOWN,
            new IntPtr(vk), new IntPtr(lp));
    }

    /// <summary>投递字符（文本框真正吃的是 WM_CHAR，不是 WM_KEYDOWN）</summary>
    public static bool PostChar(IntPtr hwnd, char ch)
        => NativeApi.PostMessage(hwnd, NativeApi.WM_CHAR, new IntPtr(ch), new IntPtr(1));

    private static bool PostClient(IntPtr hwnd, uint msg, int screenX, int screenY, int wParam)
    {
        var pt = new NativeApi.POINT { X = screenX, Y = screenY };
        if (!NativeApi.ScreenToClient(hwnd, ref pt)) return false;
        int lp = MakeLParam(pt.X, pt.Y);
        return NativeApi.PostMessage(hwnd, msg, new IntPtr(wParam), new IntPtr(lp));
    }

    /// <summary>MAKELPARAM：坐标按 16 位截断是 Win32 的规定（拖到窗口外时为负数）</summary>
    private static int MakeLParam(int lo, int hi) => (hi << 16) | (lo & 0xFFFF);
}
