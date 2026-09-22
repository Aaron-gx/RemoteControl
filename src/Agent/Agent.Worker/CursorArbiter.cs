using System.Runtime.InteropServices;
using Agent.Common;

namespace Agent.Worker;

/// <summary>
/// 光标仲裁 —— 让"远端那只鼠标"和"坐在机器前的人那只鼠标"不打架。
///
/// 【为什么需要】
/// 一个 Windows 会话**只有一个系统光标**，这是操作系统的硬限制。本产品的做法是：
/// 远端的一切操作都落在**一块虚拟外屏**上（外屏没有物理输出，所以远端光标天然
/// 不会被坐着的人看见）。剩下唯一的破绽是：本机用户的物理鼠标一动，就会把同一个
/// 系统光标从外屏拽回物理屏 ——
///   1) 本机用户会看到光标"跳"回来（远端光标的位置也就丢了）；
///   2) 更危险：本机用户"点"的那一下会落在光标**当时所在**的位置。如果光标还停在外屏上，
///      这一击就直接打到了远端的窗口上（并且是在他看不见的地方生效）。
///
/// 【做法】
/// 装一个 WH_MOUSE_LL 钩子。只认"我们自己注入的事件"（dwExtraInfo 打了
/// <see cref="NativeApi.InjectTag"/> 这个记号），其余一律当作本机用户的物理输入：
///   - 物理输入到达、而光标正停在外屏上 → 先 SetCursorPos 把光标还回他上次在物理屏上的
///     位置（并按物理位移补上这一下移动），再把本次按键/滚轮**按归还后的坐标重放**，
///     原事件吃掉。于是本机用户永远看不见远端光标，点击也永远落在自己的屏上。
///   - 光标本来就在物理屏上 → 什么都不做，原样放行（零开销、零行为变化）。
///
/// 【边界】
///   - 只在采集目标是虚拟屏时启用。若外屏没开（远程只能看物理主屏），仲裁会打断远端，
///     这时由调用方决定不创建本对象。
///   - 只注入/归还光标，不吞键盘。键盘焦点是另一个"单例"问题（谁在前台谁收键），
///     靠 WindowManager 的"本机用户忙碌时不抢前台"来收敛，见 <see cref="LocalActiveRecently"/>。
///   - 钩子内只做 SetCursorPos + SendInput，都是微秒级，不会触发 LowLevelHooksTimeout。
///   - 失败安全：钩子装不上就记一条日志并完全不介入；钩子内抛异常一律放行。
/// </summary>
public sealed class CursorArbiter : IDisposable
{
    /// <summary>当前进程里的仲裁器（一个会话只有一个 Worker，所以是单例）</summary>
    public static CursorArbiter? Current { get; private set; }

    /// <summary>本机用户多久没动过鼠标，就认为他不在用（用来决定"能不能安全地把窗口抢到前台"）</summary>
    private const long IdleBeforeStolenFocusMs = 2500;

    private readonly Logger _log;
    private NativeApi.LowLevelMouseProc? _mouseProc;      // 必须持有引用：被 GC 回收后钩子回调就是野指针
    private NativeApi.LowLevelKeyboardProc? _kbProc;
    private IntPtr _mouseHook, _kbHook;

    // 外屏矩形（本机用户"不该被拖进去"的区域）
    private int _virtLeft, _virtTop, _virtRight, _virtBottom;
    // 物理屏并集（归还位置要夹在这里面）
    private int _physLeft, _physTop, _physRight, _physBottom;
    private int _fallbackX, _fallbackY;                   // 物理屏中心（光标初始就在外屏时用）

    private int _physX, _physY;                           // 本机用户最后在物理屏上的位置
    private int _seenX, _seenY;                           // 最近一次看到的光标位置（含远端注入）
    private long _lastPhysicalTick;
    private long _snapBacks;
    private int _errors;
    private bool _disposed;

    public long LastPhysicalInputTick => Interlocked.Read(ref _lastPhysicalTick);
    public long SnapBackCount => Interlocked.Read(ref _snapBacks);

    /// <summary>本机用户最近是否在动手（鼠标或键盘）</summary>
    public bool LocalActiveRecently => LocalActiveWithin(IdleBeforeStolenFocusMs);

    /// <summary>本机用户在最近 ms 毫秒内动过鼠标或键盘吗</summary>
    public bool LocalActiveWithin(long ms) => Environment.TickCount64 - LastPhysicalInputTick < ms;

    public CursorArbiter(DisplayInfo virtualMonitor, Logger log)
    {
        _log = log;
        RefreshDisplays(virtualMonitor);

        if (NativeApi.GetCursorPos(out var p)) { _seenX = _physX = p.X; _seenY = _physY = p.Y; }
        if (IsOnVirtual(_physX, _physY))
        {
            // 起始位置就在外屏（上一次远端操作留下的），本机用户还没有过物理位置 → 给一个合理的默认
            _physX = _seenX = _fallbackX;
            _physY = _seenY = _fallbackY;
        }

        var hmod = NativeApi.GetModuleHandleW(null);
        _mouseProc = MouseProc;
        _mouseHook = NativeApi.SetWindowsHookExW(NativeApi.WH_MOUSE_LL, _mouseProc, hmod, 0);
        if (_mouseHook == IntPtr.Zero)
        {
            _log.Warn($"光标仲裁未启用：安装 WH_MOUSE_LL 钩子失败（Win32={NativeApi.LastWin32Error()}）");
            return;
        }
        _kbProc = KeyboardProc;
        _kbHook = NativeApi.SetWindowsHookExKb(NativeApi.WH_KEYBOARD_LL, _kbProc, hmod, 0);
        Current = this;
        _log.Info($"光标仲裁已启用：外屏=({_virtLeft},{_virtTop})-({_virtRight},{_virtBottom})，" +
                  $"本机物理屏并集=({_physLeft},{_physTop})-({_physRight},{_physBottom})，" +
                  $"回车起点=({_physX},{_physY})");
    }

    /// <summary>显示器拓扑变化（外屏重建/换分辨率）后重新取矩形</summary>
    public void RefreshDisplays(DisplayInfo? virtualMonitor = null)
    {
        var monitors = DisplayHelper.GetMonitors();
        var virt = virtualMonitor ?? monitors.FirstOrDefault(m => m.IsVirtual);
        if (virt != null)
        {
            _virtLeft = virt.Left; _virtTop = virt.Top; _virtRight = virt.Right; _virtBottom = virt.Bottom;
        }

        var physical = monitors.Where(m => !m.IsVirtual).ToList();
        if (physical.Count == 0)
        {
            // 一块物理屏都没有（纯虚拟环境）：那就以主屏/首屏为"本机用户的屏"，等价于不干预
            physical = monitors.Where(m => m != virt).ToList();
        }
        if (physical.Count > 0)
        {
            _physLeft = physical.Min(m => m.Left);
            _physTop = physical.Min(m => m.Top);
            _physRight = physical.Max(m => m.Right);
            _physBottom = physical.Max(m => m.Bottom);
            var main = physical.FirstOrDefault(m => m.IsPrimary) ?? physical[0];
            _fallbackX = main.Left + main.Width / 2;
            _fallbackY = main.Top + main.Height / 2;
        }
        else
        {
            _physLeft = _virtLeft; _physTop = _virtTop; _physRight = _virtRight; _physBottom = _virtBottom;
            _fallbackX = _virtLeft; _fallbackY = _virtTop;
        }
    }

    private bool IsOnVirtual(int x, int y)
        => x >= _virtLeft && x < _virtRight && y >= _virtTop && y < _virtBottom;

    private int ClampX(int x) => Math.Clamp(x, _physLeft, Math.Max(_physLeft, _physRight - 1));
    private int ClampY(int y) => Math.Clamp(y, _physTop, Math.Max(_physTop, _physBottom - 1));

    // ---------------------------------------------------------------- 钩子回调

    private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0 || _disposed) return NativeApi.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        try
        {
            var ms = Marshal.PtrToStructure<NativeApi.MSLLHOOKSTRUCT>(lParam);

            if (NativeApi.IsOurInjection(ms.flags, ms.dwExtraInfo))
            {
                // 远端注入：原样放行，只记位置（下一次算物理位移要用）
                _seenX = ms.pt.X; _seenY = ms.pt.Y;
                return NativeApi.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
            }

            // 到这里就是本机用户的输入（物理设备，或别的软件用 SendInput 送的）
            Interlocked.Exchange(ref _lastPhysicalTick, Environment.TickCount64);

            if (!IsOnVirtual(ms.pt.X, ms.pt.Y))
            {
                _seenX = _physX = ms.pt.X;
                _seenY = _physY = ms.pt.Y;
                return NativeApi.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
            }

            // 光标还停在外屏上，而本机用户动手了：把光标还回去，事件按归还后的坐标重放
            int dx = ms.pt.X - _seenX, dy = ms.pt.Y - _seenY;
            if (Math.Abs(dx) > 2000 || Math.Abs(dy) > 2000) { dx = 0; dy = 0; }  // 拓扑刚变过，只做归还不补位移
            int tx = ClampX(_physX + dx), ty = ClampY(_physY + dy);
            NativeApi.SetCursorPos(tx, ty);
            _seenX = _physX = tx;
            _seenY = _physY = ty;

            long n = Interlocked.Increment(ref _snapBacks);
            if (n == 1 || n % 100 == 0)
                _log.Info($"光标已从外屏归还给本机用户：({tx},{ty})，累计 {n} 次");

            return Replay(wParam.ToInt32(), ms.mouseData)
                ? new IntPtr(1)   // 原事件带着外屏坐标，必须吃掉；重放的那条才落在物理屏上
                : NativeApi.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }
        catch (Exception ex)
        {
            if (++_errors <= 3) _log.Warn($"光标仲裁异常（已放行）：{ex.GetType().Name}: {ex.Message}");
        }
        return NativeApi.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    /// <summary>键盘钩子只用来判断"本机用户是不是正在用键盘"，从不拦截任何按键</summary>
    private IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && !_disposed)
        {
            try
            {
                var kb = Marshal.PtrToStructure<NativeApi.KBDLLHOOKSTRUCT>(lParam);
                if (!NativeApi.IsOurInjection(kb.flags, kb.dwExtraInfo))
                    Interlocked.Exchange(ref _lastPhysicalTick, Environment.TickCount64);
            }
            catch { }
        }
        return NativeApi.CallNextHookEx(_kbHook, nCode, wParam, lParam);
    }

    /// <summary>
    /// 把本机用户的事件按归还后的位置重放。刻意**不带 MOUSEEVENTF_ABSOLUTE**：
    /// 光标位置刚刚由 SetCursorPos 精确设定，不带绝对坐标的事件就落在那个像素上，
    /// 既不用做 0..65535 归一化，也不会有取整误差。
    /// </summary>
    private bool Replay(int msg, uint mouseData)
    {
        uint flag = msg switch
        {
            NativeApi.WM_MOUSEMOVE => NativeApi.MOUSEEVENTF_MOVE,
            NativeApi.WM_LBUTTONDOWN => NativeApi.MOUSEEVENTF_LEFTDOWN,
            NativeApi.WM_LBUTTONUP => NativeApi.MOUSEEVENTF_LEFTUP,
            NativeApi.WM_RBUTTONDOWN => NativeApi.MOUSEEVENTF_RIGHTDOWN,
            NativeApi.WM_RBUTTONUP => NativeApi.MOUSEEVENTF_RIGHTUP,
            NativeApi.WM_MBUTTONDOWN => NativeApi.MOUSEEVENTF_MIDDLEDOWN,
            NativeApi.WM_MBUTTONUP => NativeApi.MOUSEEVENTF_MIDDLEUP,
            NativeApi.WM_MOUSEWHEEL => NativeApi.MOUSEEVENTF_WHEEL,
            NativeApi.WM_MOUSEHWHEEL => NativeApi.MOUSEEVENTF_HWHEEL,
            _ => 0,
        };
        if (flag == 0) return false;

        var inputs = new NativeApi.INPUT[1];
        inputs[0].type = NativeApi.INPUT_MOUSE;
        inputs[0].u.mi = new NativeApi.MOUSEINPUT
        {
            dx = 0,
            dy = 0,
            mouseData = flag is NativeApi.MOUSEEVENTF_WHEEL or NativeApi.MOUSEEVENTF_HWHEEL ? mouseData : 0,
            dwFlags = flag,
            time = 0,
            dwExtraInfo = NativeApi.InjectTagPtr,
        };
        return NativeApi.SendInput(1, inputs, Marshal.SizeOf<NativeApi.INPUT>()) == 1;
    }

    public void Dispose()
    {
        _disposed = true;
        if (Current == this) Current = null;
        try { if (_mouseHook != IntPtr.Zero) NativeApi.UnhookWindowsHookEx(_mouseHook); } catch { }
        try { if (_kbHook != IntPtr.Zero) NativeApi.UnhookWindowsHookEx(_kbHook); } catch { }
        _mouseHook = _kbHook = IntPtr.Zero;
    }
}
