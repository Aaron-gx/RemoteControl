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
/// 依赖 SetCapture 的拖拽、DirectInput 游戏、以及微信这类不吃合成消息的应用）。所以：
///   · 按住左键期间的整段拖拽会**锁死路径**，不会中途切换把拖拽弄断；
///   · 本机用户一停手（默认 1.2s），远端立刻拿回真实光标，这些能力当即可用；
///   · 远端点过的窗口会被"认领"，之后远端打字时把该窗口带前台（本机用户在忙则不打字，见 Program）。
/// 这就是没有内核驱动时能做到的上限；要"任意程序任意时刻全并发"，只能上过滤器驱动
/// （Interception/vmulti）+ 自绘光标，代价见 docs/架构-单会话共享与双光标.md §2.2。
///
/// 【光标被别人控制住时怎么办】真实光标这条路有个前提：**系统光标听我们的**。可它并不总是听 ——
/// 同一台机器上还装着别的远控软件（GameViewer/向日葵/ToDesk…）、或者上一份没卸干净的旧被控端
/// 还在跑时，它们会各自装一个全局鼠标钩子，把我们刚摆到外屏的光标"抢回去"；此时 SendInput
/// 照常返回成功、日志里一切正常，但按键落在光标当时所在的位置 —— 现场症状就是
/// "主控端看着副屏点，结果点到主屏上，怎么点都没反应"。
/// 所以每次交互前会先探测一次光标是否真的到位（<see cref="InputInjector.TryProbeCursor"/>）：
/// 不到位就当场改走后台定向注入（不依赖光标），并在日志里说明原因。宁可退到兼容性次一档的路，
/// 也不能把点击送到本机用户的屏幕上。
/// </summary>
public sealed class MouseRouter
{
    /// <summary>本机用户停手多久之后，远端可以拿回真实光标</summary>
    private const long LocalIdleBeforeRealCursorMs = 1200;
    /// <summary>本机用户忙碌时，打字前把认领窗口带前台这件事要不要做（防抢前台）</summary>
    private const long LocalIdleBeforeStealForegroundMs = 1200;
    /// <summary>确认"光标被抢"之后，多久重探一次（探头本身会动一下光标，别太频繁）</summary>
    private const long CursorReprobeIntervalMs = 2500;

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
    private long _lastContestLogTick;
    private long _lastProbeTick;
    private long _lastBlockerScanTick;

    public InputPath CurrentPath { get; private set; } = InputPath.RealCursor;
    /// <summary>远端鼠标走哪条路（主控端可选；默认真实光标：兼容性最好）</summary>
    public RemoteInputMode Mode { get; set; } = RemoteInputMode.AlwaysRealCursor;
    /// <summary>远端最近点过的窗口（后台注入模式下用来做键盘的"认领"）</summary>
    public IntPtr ClaimedWindow { get; private set; } = IntPtr.Zero;

    /// <summary>
    /// 本机光标"不听使唤"：注入的绝对移动没能把光标放到目标点上（被别人控制/归位）。
    /// 这时真实光标路径的点击会落到主屏，所以自动改走后台定向注入。
    /// </summary>
    public bool CursorContested { get; private set; }
    /// <summary>最近一次探测到的实际光标位置（诊断用）</summary>
    public (int X, int Y) LastCursorSeen { get; private set; }
    /// <summary>
    /// 可能正在抢光标的同类程序名（诊断用）——现场最需要的不是"点不动"这个结论，
    /// 而是"把哪个软件关掉就好了"：这里面通常是另一个远控软件或第二份被控端。
    /// </summary>
    public string BlockerHint { get; private set; } = "";
    /// <summary>
    /// 降级形态：移动事件被别的程序截走，但 SetCursorPos 还能用。
    /// 这时点击仍是"真实光标"（位置由 SetCursorPos 摆、按键只发事件不带坐标），
    /// 所以悬停/拖拽/原生右键菜单都还在，只是没法把移动事件交给系统去抢。
    /// </summary>
    private bool _assistedCursor;

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

    private InputPath Decide() => Mode switch
    {
        // 主控端明确选了后台注入：照办
        RemoteInputMode.AlwaysBackground => InputPath.Background,
        // 主控端明确选了真实光标：照办 —— 除非光标已经被别人控制住（那时点击会落到主屏上，
        // 比"兼容性差一点"严重得多），这种情况连日志带状态一起告诉主控端
        RemoteInputMode.AlwaysRealCursor => CursorContested ? InputPath.Background : InputPath.RealCursor,
        // 自动：本机用户在忙、或光标不听使唤，就退到后台注入
        _ => (LocalBusy || CursorContested) ? InputPath.Background : InputPath.RealCursor,
    };

    private void SwitchTo(InputPath path)
    {
        if (path == CurrentPath) return;
        CurrentPath = path;
        long now = Environment.TickCount64;
        if (now - _lastPathLogTick < 3000) return;
        _lastPathLogTick = now;
        _log.Info(path == InputPath.RealCursor
            ? "鼠标路径 → 真实光标（本机用户已停手，远端恢复原生体验）"
            : CursorContested
                ? "鼠标路径 → 后台定向注入（本机光标被别的程序控制住了，真实光标路径的点击会落到主屏）"
                : "鼠标路径 → 后台定向注入（本机用户正在操作，远端改为把事件直接投给窗口，不碰他的光标）");
    }

    // ---------------------------------------------------------------- 光标可达性

    /// <summary>
    /// 探一次"光标到底听不听我们的"：把光标摆到采集屏中央并要求它真的到那儿。
    /// 返回 true = 光标听使唤（真实光标路径可用）。
    /// </summary>
    public bool ProbeCursor(string why, bool force = false)
    {
        // 只有"远端那块屏是虚拟外屏"时才值得探：这块屏没有物理输出，探头动一下光标本机用户看不见。
        // 若采集的是物理屏（外屏没装上），探头就会在别人眼前晃一下鼠标，而且那种情况下
        // 后台定向注入同样会打扰他 —— 保持原行为，不探。
        if (!_injector.CaptureIsVirtual) return true;

        long now = Environment.TickCount64;
        if (!force && now - _lastProbeTick < CursorReprobeIntervalMs) return !CursorContested;
        _lastProbeTick = now;

        var (pw, ph) = _injector.CaptureSize;
        if (pw <= 0 || ph <= 0) return true;
        int px = Math.Max(1, pw / 2), py = Math.Max(1, ph / 2);
        // 已经降级成"SetCursorPos 摆位"的形态时，就别再用 SendInput 那套去探（它必然失败）
        bool ok = _assistedCursor
            ? _injector.TrySetCursorExact(px, py, out int ax2, out int ay2)
            : _injector.TryProbeCursor(px, py, out ax2, out ay2);
        LastCursorSeen = (ax2, ay2);

        if (!ok)
        {
            // 移动事件被抢 ≠ 完全没救：不少机器上 SetCursorPos（直接设位置，不走输入事件链）还是通的，
            // 那就用"直接摆光标 + 只发按键"的方式继续走真实光标 —— 点击位置仍然正确。
            if (!_assistedCursor && _injector.TrySetCursorExact(px, py, out int sx, out int sy))
            {
                _assistedCursor = true;
                _injector.AssistedCursor = true;      // 注入器内部切成"摆位 + 只发按键"
                CursorContested = false;
                SwitchTo(Decide());
                _log.Warn($"本机鼠标**移动事件**被别的程序截走（{why}），但直接设光标位置可用：" +
                          $"远端改用「摆好光标 + 只发按键」的方式操作（点击位置仍然正确）。" +
                          $"{BlockerSuffix()}");
                return true;
            }

            bool first = !CursorContested;
            CursorContested = true;
            ScanBlockers();
            SwitchTo(Decide());
            if (!first) return false;
            if (now - _lastContestLogTick > 5000)
            {
                _lastContestLogTick = now;
                int wantX = _captureLeft + px, wantY = _captureTop + py;
                _log.Warn($"本机光标不受控（{why}）：注入移动到 ({wantX},{wantY}) 后实际停在 ({ax2},{ay2})，" +
                          "说明有别的程序在控制/锁定光标。远端鼠标已改为后台定向注入（不依赖光标），" +
                          "点击不会再跑到主屏。注意：后台注入对滚动有效，但部分应用（微信、Chromium 系）" +
                          "不吃合成点击 —— 想恢复原生点击请先关掉占着光标的程序。" + BlockerSuffix());
            }
            return false;
        }

        if (CursorContested)
        {
            CursorContested = false;
            SwitchTo(Decide());
            _log.Info($"本机光标已恢复可控（{why}）：远端鼠标回到真实光标路径");
        }
        return true;
    }

    /// <summary>立刻重扫一遍"谁可能在管光标"（仲裁被同类软件顶掉时也要刷新，主控端状态栏据此显示）</summary>
    public void RefreshBlockerHint()
    {
        _lastBlockerScanTick = 0;
        ScanBlockers();
    }

    /// <summary>诊断尾巴：把"可能是谁在抢"直接写进日志，用户照着关掉即可</summary>
    private string BlockerSuffix()
        => string.IsNullOrEmpty(BlockerHint) ? "" : $"（检测到这些程序在运行，可能是它们：{BlockerHint}）";

    /// <summary>
    /// 扫一遍"可能在抢光标的同类程序"。只做诊断，不做任何处置 ——
    /// 关掉哪个是用户的决定（我们绝不去结束别人的进程）。
    /// </summary>
    private void ScanBlockers()
    {
        long now = Environment.TickCount64;
        if (now - _lastBlockerScanTick < 10000) return;
        _lastBlockerScanTick = now;

        var hits = InputRivals.Detect().ToList();
        // 一台机器上跑了两份被控端：两份全局钩子互相抢光标（历史上踩过，现在有单实例锁兜底）
        int self = InputRivals.SelfProcessCount();
        if (self > 2) hits.Add($"另一份被控端(共 {self} 个 Agent 进程)");
        BlockerHint = string.Join(" / ", hits);
    }

    // ---------------------------------------------------------------- 事件入口

    public void Move(int x, int y)
    {
        var path = _pathLocked ? _lockedPath : Decide();
        if (path == InputPath.RealCursor && !ProbeCursor("移动")) path = Decide();
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

        // 按下之前先确认光标听使唤：不听就这一整段交互都改走后台注入。
        // 这一步是"点击跑到主屏"这个现场问题的正解 —— 宁可这一击用兼容性差一点的方式送达，
        // 也不能让它落到本机用户屏幕上（那可能关掉他正在看的窗口）。
        if (path == InputPath.RealCursor && _held == 0 && !up && !ProbeCursor("点击前"))
            path = Decide();

        if (_held == 0 && !up)
        {
            _pathLocked = true;
            _lockedPath = path;
        }
        SwitchTo(path);

        if (path == InputPath.RealCursor)
        {
            // 移动+按键合成一次 SendInput：中间没有可被插队的空隙（详见 InputInjector.Click）
            _injector.Click(x, y, button, up);
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

        // 点击前把目标窗口带到前台。
        // 【为什么必须做】很多应用（Chromium 系、微信这类自绘 UI）**直接忽略非前台窗口收到的合成点击**，
        // 而滚轮不看前台、照样生效 —— 现场表现正是"能滑动，就是点不动"（实测反馈）。
        // RobustSetForeground 内部会在本机用户正在操作时自动跳过，不会打扰他。
        if (!up && ForegroundSetter != null)
        {
            var root = NativeApi.GetAncestor(hwnd, NativeApi.GA_ROOTOWNER);
            if (root == IntPtr.Zero) root = hwnd;
            if (NativeApi.GetForegroundWindow() != root)
            {
                var ok = false;
                try { ok = ForegroundSetter(root); } catch { }
                if (!ok)
                {
                    long nowTick = Environment.TickCount64;
                    if (nowTick - _lastFallbackLogTick > 3000)
                    {
                        _lastFallbackLogTick = nowTick;
                        _log.Info("目标窗口未能置前台（本机用户正在操作？）：部分应用可能不响应这次合成点击");
                    }
                }
            }
        }

        // 先补一条 WM_MOUSEMOVE：有些应用（自绘 UI、浏览器内核）要先收到悬停才认后面的点击
        BackgroundInput.PostMove(hwnd, ScreenX(x), ScreenY(y), _held);
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
        if (path == InputPath.RealCursor && !ProbeCursor("滚轮")) path = Decide();
        SwitchTo(path);

        if (path == InputPath.RealCursor)
        {
            _injector.MouseMove(x, y);
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
        _injector.Click(x, y, button, up);
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
