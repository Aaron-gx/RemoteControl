using System.Runtime.InteropServices;
using System.Text;

namespace Agent.Common;

/// <summary>
/// 「带初始窗口位置启动进程」+「窗口一显示就收到通知」相关 P/Invoke。
/// 用途：远程打开的软件直接生在虚拟外屏上，杜绝"先在主屏闪一下、再被搬走"（客户反馈）。
/// </summary>
public static partial class NativeApi
{
    // ---------------------------------------------------------------- CreateProcess + STARTUPINFO

    public const uint STARTF_USESHOWWINDOW = 0x00000001;
    public const uint STARTF_USEPOSITION = 0x00000004;   // 窗口初始位置采用 dwX/dwY

    // STARTUPINFO / PROCESS_INFORMATION / CloseHandle 用 NativeApi.cs 里已有的定义（多会话遗留代码在用）。
    // 注意：现有 STARTUPINFO 未标 CharSet.Unicode，string 字段按 ANSI 封送 —— 我们的调用里
    // lpReserved/lpDesktop/lpTitle 全传 null（封送成 NULL 指针，与字符集无关），是安全的。

    // lpCommandLine 必须是**可写**缓冲（CreateProcessW 解析期间会原地改写它），所以收 StringBuilder 而不是 string。
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool CreateProcessW(
        string? applicationName, StringBuilder commandLine,
        IntPtr processAttributes, IntPtr threadAttributes,
        bool inheritHandles, uint creationFlags, IntPtr environment, string? currentDirectory,
        ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInformation);

    /// <summary>目标程序要求管理员权限（manifest requireAdministrator）时 CreateProcess 的错误码 —— 回落 ShellExecute</summary>
    public const int ERROR_ELEVATION_REQUIRED = 740;

    // ---------------------------------------------------------------- WinEvent（窗口一显示就通知）

    // 注意：0x8000 才是 CREATE！0x8001 是 DESTROY（第一版就把预定位挂在了 DESTROY 上，
    // "窗口创建预定位"从来没触发过 —— 现场日志里从来没有预定位条目就是这么来的）。
    public const uint EVENT_OBJECT_CREATE = 0x8000;
    public const uint EVENT_OBJECT_DESTROY = 0x8001;
    public const uint EVENT_OBJECT_SHOW = 0x8002;
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    public const int OBJID_WINDOW = 0;

    public delegate void WinEventProc(IntPtr hook, uint eventCode, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventProc proc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    public static extern bool UnhookWinEvent(IntPtr hook);

    // ---------------------------------------------------------------- 钩子线程用的最小消息泵
    // 带外（out-of-context）WinEvent 回调是**发到注册线程的消息队列**里、由消息循环取的时候才触发的，
    // 所以注册钩子的线程必须自己泵消息。

    public const uint PM_REMOVE = 0x0001;
    public const uint QS_ALLINPUT = 0x04FF;

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [DllImport("user32.dll")]
    public static extern bool PeekMessage(out MSG msg, IntPtr hwnd, uint messageFilterMin, uint messageFilterMax, uint removeFlags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint MsgWaitForMultipleObjects(uint count, IntPtr handles, bool waitAll, uint milliseconds, uint wakeMask);

    // ---------------------------------------------------------------- 跨线程停掉消息循环

    public const uint WM_QUIT = 0x0012;

    /// <summary>
    /// 往指定线程的消息队列投递一条消息。进程合并后用它把 WM_QUIT 送进引擎 STA 线程：
    /// 停止指令来自别的线程（桥的分发线程），在那边调 ExitThread 退不了引擎线程的消息循环。
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);

    // ---------------------------------------------------------------- DWM 隐身（零闪烁的最后一击）

    public const int DWMWA_CLOAK = 13;

    /// <summary>
    /// DWM cloak：让窗口在**合成器层面**立即消失/恢复（比 ShowWindow(SW_HIDE) 快且不带隐藏动画），
    /// 用于"捕获到窗口显示在主屏的瞬间"——先 cloak（主屏上当帧消失）→ 搬到外屏 → uncloak。
    /// Win10 1803+ 可用；失败返回非 0（调用方忽略即可，退化为普通搬运）。
    /// </summary>
    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>cloak（窗口从屏幕上立即消失，内容仍在合成）</summary>
    public static void CloakWindow(IntPtr hwnd)
    {
        int v = 1;
        try { DwmSetWindowAttribute(hwnd, DWMWA_CLOAK, ref v, sizeof(int)); } catch { }
    }

    /// <summary>uncloak（恢复显示）</summary>
    public static void UncloakWindow(IntPtr hwnd)
    {
        int v = 0;
        try { DwmSetWindowAttribute(hwnd, DWMWA_CLOAK, ref v, sizeof(int)); } catch { }
    }

    // ---------------------------------------------------------------- 计时器精度（热旋转 1ms 轮询的前提）

    /// <summary>
    /// Windows 计时器默认精度 15.6ms —— Thread.Sleep(1) 实际等 ~15ms，窗口在这个空档已在主屏画了 3~4 帧。
    /// timeBeginPeriod(1) 把系统计时器精度提到 1ms（进程级全局；用完 timeEndPeriod(1) 恢复）。
    /// </summary>
    [DllImport("winmm.dll")]
    public static extern int timeBeginPeriod(int period);

    [DllImport("winmm.dll")]
    public static extern int timeEndPeriod(int period);
}
