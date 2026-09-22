using Agent.Common;

namespace Agent.Worker;

/// <summary>鼠标输入的走法：真实光标（原生，但要占用那个唯一的系统光标）或后台定向注入（不碰光标）</summary>
public enum InputPath
{
    RealCursor,
    Background,
}

/// <summary>
/// 鼠标输入的"双路径"自适应路由器 —— 这是"两个鼠标共用"这个难题的落点。
///
/// 【为什么这么设计】
/// Windows 一个会话只有一个系统光标（硬限制，商用方案 MouseMux 也绕不开，它默认模式同样要
/// 点击串行化）。但我们不是必须让两边去抢那一个光标：
///
///   · 本机用户空闲时 → 远端走 <see cref="InputPath.RealCursor"/>：把真实光标摆到虚拟外屏的
///     坐标上再 SendInput。悬停、拖拽、原生右键菜单、滚轮、拖放全都是 100% 原生行为，
///     而那块屏没有物理输出，本机用户看不见这只光标。
///   · 本机用户正在操作时 → 远端自动切到 <see cref="InputPath.Background"/>：算准"那个位置下面是
///     哪个窗口"，把 WM_MOUSEMOVE/WM_*BUTTON 直接 Post 给窗口，**系统光标一根毫毛都不动**。
///     本机用户于是始终独占真实光标，永远不会被打断、也永远不会看见远端光标。
///
/// 于是效果是：**本机用户永远 100% 原生、永不被打断；远端默认也是 100% 原生，只有在本机用户
/// 正在用机器的那段时间里降级为"后台定向注入"继续干活**。两个光标各自独立、互不可见。
///
/// 【已知代价】后台定向注入拿不下"必须真实输入"的东西（原生 TrackPopupMenu 右键菜单、
/// 依赖 SetCapture 的拖拽、DirectInput 游戏）。所以：
///   · 按住左键期间的整段拖拽会**锁死路径**，不会中途切换把拖拽弄断；
///   · 本机用户一停手（默认 1.2s），远端立刻拿回真实光标，这些能力当即可用；
///   · 远端点过的窗口会被"认领"，之后远端打字时把该窗口带前台（本机用户在忙则不打字，见 Program）。
/// 这就是没有内核驱动时能做到的上限；要"任意程序任意时刻全并发"，只能上过滤器驱动
/// （Interception/vmulti）+ 自绘光标，代价见 docs/架构-单会话共享与双光标.md §2.2。
/// </summary>
public sealed class MouseRouter
{
    /// <summary>本机用户停手多久之后，远端可以拿回真实光标</summary>
    private const long LocalIdleBeforeRealCursorMs = 1200;
    /// <summary>本机用户忙碌时，打字前把认领窗口带前台这件事要不要做（防抢前台）</summary>
    private const long LocalIdleBeforeStealForegroundMs = 1200;

    private readonly Logger _log;
    private readonly InputInjector _injector;
    private readonly CursorArbiter? _arbiter;

    private int _captureLeft, _captureTop;

    private int _held;                  // MK_* 位，我们自己维护（后台注入的 wParam 要用）
    private bool _pathLocked;           // 按住期间锁路径
    private InputPath _lockedPath;
    private IntPtr _captured;           // 后台注入下"按住"时锁定的目标窗口（模拟 SetCapture）
    private long _lastPathLogTick;
    private long _lastFallbackLogTick;

    public InputPath CurrentPath { get; private set; } = InputPath.RealCursor;
    /// <summary>远端最近点过的窗口（后台注入模式下用来做键盘的"认领"）</summary>
    public IntPtr ClaimedWindow { get; private set; } = IntPtr.Zero;

    /// <summary>把窗口置前台的实现（由 WindowManager 提供，避免这里再依赖它）</summary>
    public Func<IntPtr, bool>? ForegroundSetter { get; set; }

    public MouseRouter(Logger log, InputInjector injector, CursorArbiter? arbiter)
    {
        _log = log;
        _injector = injector;
        _arbiter = arbiter;
    }

    public void SetCaptureTarget(DisplayInfo target)
    {
        _captureLeft = target.Left;
        _captureTop = target.Top;
    }

    private int ScreenX(int x) => _captureLeft + x;
    private int ScreenY(int y) => _captureTop + y;
    private bool LocalBusy => _arbiter != null && _arbiter.LocalActiveWithin(LocalIdleBeforeRealCursorMs);

    private InputPath Decide() => LocalBusy ? InputPath.Background : InputPath.RealCursor;

    private void SwitchTo(InputPath path)
    {
        if (path == CurrentPath) return;
        CurrentPath = path;
        long now = Environment.TickCount64;
        if (now - _lastPathLogTick < 3000) return;
        _lastPathLogTick = now;
        _log.Info(path == InputPath.RealCursor
            ? "鼠标路径 → 真实光标（本机用户已停手，远端恢复原生体验）"
            : "鼠标路径 → 后台定向注入（本机用户正在操作，远端改为把事件直接投给窗口，不碰他的光标）");
    }

    // ---------------------------------------------------------------- 事件入口

    public void Move(int x, int y)
    {
        var path = _pathLocked ? _lockedPath : Decide();
        SwitchTo(path);

        if (path == InputPath.RealCursor)
        {
            _injector.MouseMove(x, y);
            return;
        }

        // 按住期间的移动必须发给"按下的那个窗口"，否则应用看不到完整的拖拽（模拟 SetCapture）
        var hwnd = _held != 0 && _captured != IntPtr.Zero
            ? _captured
            : BackgroundInput.HitTest(ScreenX(x), ScreenY(y));
        if (!BackgroundInput.IsPostable(hwnd)) return;
        BackgroundInput.PostMove(hwnd, ScreenX(x), ScreenY(y), _held);
    }

    public void Button(int x, int y, byte button, bool up)
    {
        // 按下这一刻决定这一段交互走哪条路，并在按住期间锁死
        var path = _pathLocked ? _lockedPath : Decide();
        if (_held == 0 && !up)
        {
            _pathLocked = true;
            _lockedPath = path;
        }
        SwitchTo(path);

        if (path == InputPath.RealCursor)
        {
            _injector.MouseButton(x, y, button, up);
            if (_held == 0 && up) { _pathLocked = false; }
            return;
        }

        int flag = button switch
        {
            0 => NativeApi.MK_LBUTTON,
            1 => NativeApi.MK_RBUTTON,
            2 => NativeApi.MK_MBUTTON,
            _ => 0,
        };
        if (flag == 0) return;

        IntPtr hwnd;
        if (!up)
        {
            hwnd = BackgroundInput.HitTest(ScreenX(x), ScreenY(y));
            if (!BackgroundInput.IsPostable(hwnd))
            {
                // 没有可投递的窗口（极端情况）：退回真实光标，宁可碰一下光标也别把这一击丢了
                FallbackToRealCursor("按下时没有可投递的窗口", x, y, button, up);
                return;
            }
            _captured = hwnd;
            ClaimedWindow = hwnd;
            _held |= flag;
        }
        else
        {
            hwnd = _captured != IntPtr.Zero ? _captured : BackgroundInput.HitTest(ScreenX(x), ScreenY(y));
            if (!BackgroundInput.IsPostable(hwnd))
            {
                _held &= ~flag;
                if (_held == 0) { _captured = IntPtr.Zero; _pathLocked = false; }
                return;
            }
            _held &= ~flag;
        }

        BackgroundInput.PostButton(hwnd, ScreenX(x), ScreenY(y), button, up, _held);

        if (_held == 0)
        {
            _captured = IntPtr.Zero;
            _pathLocked = false;
        }
    }

    public void Wheel(int x, int y, short delta)
    {
        var path = _pathLocked ? _lockedPath : Decide();
        SwitchTo(path);

        if (path == InputPath.RealCursor)
        {
            _injector.MouseWheel(x, y, delta);
            return;
        }

        var hwnd = BackgroundInput.HitTest(ScreenX(x), ScreenY(y));
        if (!BackgroundInput.IsPostable(hwnd)) return;
        BackgroundInput.PostWheel(hwnd, ScreenX(x), ScreenY(y), delta);
    }

    private void FallbackToRealCursor(string why, int x, int y, byte button, bool up)
    {
        long now = Environment.TickCount64;
        if (now - _lastFallbackLogTick > 3000)
        {
            _lastFallbackLogTick = now;
            _log.Warn($"后台注入不可用（{why}），本次交互改用真实光标");
        }
        // 整段交互（按下→移动→抬起）都走真实光标，否则拖拽会在中途换路而断掉
        _pathLocked = true;
        _lockedPath = InputPath.RealCursor;
        SwitchTo(InputPath.RealCursor);
        _injector.MouseButton(x, y, button, up);
    }

    // ---------------------------------------------------------------- 键盘的"认领"

    /// <summary>
    /// 远端打字前，确保"他点过的那个窗口"在前台 —— 否则键会打进本机用户的前台窗口。
    /// 本机用户正在操作时返回 false（调用方应放弃注入这次按键）。
    /// </summary>
    public bool EnsureClaimedForeground()
    {
        var h = ClaimedWindow;
        if (h == IntPtr.Zero) return true;                 // 没认领过 → 沿用原有逻辑

        var root = NativeApi.GetAncestor(h, NativeApi.GA_ROOTOWNER);
        if (root == IntPtr.Zero) root = h;
        if (NativeApi.GetForegroundWindow() == root) return true;

        if (_arbiter != null && _arbiter.LocalActiveWithin(LocalIdleBeforeStealForegroundMs))
        {
            long now = Environment.TickCount64;
            if (now - _lastFallbackLogTick > 3000)
            {
                _lastFallbackLogTick = now;
                _log.Info("本机用户正在操作，暂不把远端认领的窗口带前台（远端键盘本轮跳过）");
            }
            return false;
        }

        var ok = ForegroundSetter?.Invoke(root) ?? false;
        _log.Info($"远端打字：把认领窗口 {root} 带前台，{(ok ? "成功" : "未生效")}");
        return true;
    }
}
