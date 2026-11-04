using System.Runtime.InteropServices;
using System.Text;
using Agent.Common;
using Agent.Worker;

namespace InputProbe;

/// <summary>
/// 输入注入探针 —— 回答一个问题：**远端在画面里点的位置，最终落在哪块屏、哪个窗口上。**
///
/// 【为什么需要它】
/// 现场反馈一直是"主控端看着副屏（虚拟外屏），但点击落在主屏上"：画面是对的，输入是错的。
/// 光靠读代码分不清是下面哪一条：
///   A. 采集目标/输入基准算错（本应 (1920,0)，实际按 (0,0) 算 → 全部落到主屏）；
///   B. 注入的事件没被自己的光标仲裁认出来（dwExtraInfo 标记丢了）→ 仲裁把远端输入当成
///      本机用户输入，把光标"归位"回主屏、并把事件重放到主屏；
///   C. 别的进程（另一份被控端 / 别的远控软件）也在管光标，把我们摆到外屏的光标抢回去；
///   D. 系统光标本身就不听注入（被锁定/托管）。
/// 本探针走**线上那条真实代码路径**（InputInjector / MouseRouter / CursorArbiter），把每种情况
/// 分别判定出来，并给出"这次点击到底送到哪了"的结论。
///
/// 【安全性】只点自己创建的窗口：每次点击前用 WindowFromPoint 校验命中窗口属于本进程，
/// 不是自己窗口就把窗口挪到空位重试，仍不行就跳过。运行前记下光标位置，退出前还原。
/// </summary>
internal static class Program
{
    private static readonly List<string> Lines = new();
    private static Form? _win;
    private static Button? _btn;
    private static int _clicks;
    private static Point _lastDown = new(-1, -1);

    // 诊断钩子看到的注入事件
    private static readonly List<string> HookSeen = new();
    private static NativeApi.LowLevelMouseProc? _diagProc;
    private static IntPtr _diagHook;

    [STAThread]
    private static int Main()
    {
        ApplicationConfiguration.Initialize();
        Console.OutputEncoding = Encoding.UTF8;

        Console.WriteLine("=== 输入注入探针（只点自己创建的窗口）===");
        Console.WriteLine($"pid={Environment.ProcessId}  DPI={Application.HighDpiMode}");

        // 探针的日志别写进真正的被控端日志目录
        var probeLogDir = Path.Combine(Path.GetTempPath(), "inputprobe");
        Directory.CreateDirectory(probeLogDir);
        var log = new Logger("inputprobe", probeLogDir, LogLevel.Info, echoConsole: false);

        NativeApi.GetCursorPos(out var saved);

        // ---------------------------------------------------------- 1. 显示器拓扑
        var monitors = DisplayHelper.GetMonitors();
        Console.WriteLine($"\n--- 显示器拓扑（{monitors.Count} 块）---");
        foreach (var m in monitors) Console.WriteLine($"  {m}");

        var target = DisplayHelper.PickCaptureTarget();
        if (target == null) { Console.WriteLine("没有可用显示器，退出"); return 2; }
        var (vl, vt, vw, vh) = DisplayHelper.GetVirtualDesktopBounds();
        Console.WriteLine($"\n采集目标（PickCaptureTarget）= {target}");
        Console.WriteLine($"输入换算基准 = ({target.Left},{target.Top}){(target.IsVirtual ? "  ✓ 虚拟外屏" : "  ✗ 不是虚拟屏")}");
        Console.WriteLine($"虚拟桌面 = ({vl},{vt}) {vw}x{vh}，物理屏 {monitors.Count(m => !m.IsVirtual)} 块");
        Result(target.IsVirtual, $"[A] 采集目标选择：{target.DeviceName} @({target.Left},{target.Top})" +
                                (target.IsVirtual ? "（虚拟外屏，正确）" : "（不是虚拟外屏：远端只能操作物理屏）"));

        var injector = new InputInjector(log);
        injector.SetCaptureTarget(target);

        // ---------------------------------------------------------- 2. 开一个自己的窗口（尽量放在采集屏中央）
        var primary = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
        var testW = Math.Min(560, Math.Max(320, target.Width - 80));
        var testH = Math.Min(260, Math.Max(200, target.Height - 80));
        _win = new Form
        {
            Text = "InputProbe 目标窗口（探针自建）",
            StartPosition = FormStartPosition.Manual,
            Location = new Point(target.Left + Math.Max(40, (target.Width - testW) / 2),
                                 target.Top + Math.Max(40, (target.Height - testH) / 2)),
            ClientSize = new Size(testW, testH),
            // 不抢前台、不置顶：探针只是临时开个窗口量一下坐标，别干扰用户
            ShowInTaskbar = false,
        };
        _btn = new Button { Text = "目标按钮", Left = 40, Top = 40, Width = 180, Height = 48 };
        _btn.Click += (_, _) => Interlocked.Increment(ref _clicks);
        _btn.MouseDown += (_, e) => _lastDown = new Point(e.X, e.Y);
        _win.Controls.Add(_btn);
        _win.Show();
        Application.DoEvents();

        if (!EnsureOwnWindowAtButton(out var why0))
        {
            Console.WriteLine($"\n⚠ 无法在采集屏上找到可用的自有窗口位置（{why0}）——本机还有别的窗口占着，结论可能不准");
        }
        var buttonCenter = ButtonCenter();
        Console.WriteLine($"目标窗口：屏幕位置 ({_win.Left},{_win.Top})，按钮中心 {buttonCenter}，窗口在采集屏内=" +
                          $"{(buttonCenter.X >= target.Left && buttonCenter.X < target.Right ? "是" : "否")}");

        // ---------------------------------------------------------- 3. 诊断钩子：看注入事件带不带我们的标记
        Console.WriteLine("\n--- 诊断钩子：注入事件能不能被认出来（光标仲裁靠这个标记区分远端注入与本机用户）---");
        InstallDiagHook();
        HookSeen.Clear();
        injector.MouseMove(200, 200);
        Thread.Sleep(80);
        injector.Click(220, 220, 0, false);
        injector.Click(220, 220, 0, true);
        Thread.Sleep(80);
        UninstallDiagHook();
        foreach (var s in HookSeen.Take(4)) Console.WriteLine("    " + s);
        bool tagged = HookSeen.Any(s => s.Contains("标记=是"));
        bool injected = HookSeen.Any(s => s.Contains("INJECTED"));
        Result(tagged, $"[B] 注入事件标记：钩子看到 {HookSeen.Count} 个事件，" +
                       $"带 LLMHF_INJECTED={injected}，带我们的标记={tagged}" +
                       (tagged ? "" : " ✗ 标记丢失 → 光标仲裁会把远端输入当成本机用户输入，把光标抢回主屏"));

        // ---------------------------------------------------------- 4. 真实光标路径点击（线上默认模式）
        var router = new MouseRouter(log, injector, null) { Mode = RemoteInputMode.AlwaysRealCursor };
        router.SetCaptureTarget(target);
        Console.WriteLine($"\n--- 点击前：光标在 {Where()}（采集屏是 ({target.Left},{target.Top})-({target.Right},{target.Bottom})）---");
        _clicks = 0;
        ClickViaRouter(router, target, buttonCenter);
        var afterReal = Where();
        Result(_clicks > 0,
            $"[C] 真实光标路径点击：目标窗口收到 {_clicks} 次点击（期望 1）；点击后光标 {afterReal}；" +
            $"实际路径={router.CurrentPath}，光标被判定为不受控={router.CursorContested}，" +
            $"探针看到的实际光标={router.LastCursorSeen}");

        // ---------------------------------------------------------- 5. 装入光标仲裁（与线上一致）
        Console.WriteLine("\n--- 装入 CursorArbiter（现场同款）---");
        CursorArbiter? arbiter = null;
        try { arbiter = new CursorArbiter(target, log); }
        catch (Exception ex) { Console.WriteLine($"    仲裁器创建失败：{ex.Message}"); }
        var routerA = new MouseRouter(log, injector, arbiter) { Mode = RemoteInputMode.AlwaysRealCursor };
        routerA.SetCaptureTarget(target);
        long snapsBefore = arbiter?.SnapBackCount ?? 0;
        _clicks = 0;
        ClickViaRouter(routerA, target, buttonCenter);
        long snapsAfter = arbiter?.SnapBackCount ?? 0;
        Result(_clicks > 0,
            $"[D] 仲裁器在管时点击：目标窗口收到 {_clicks} 次点击（期望 1）；" +
            $"仲裁归还光标次数 {snapsBefore}→{snapsAfter}" +
            (snapsAfter > snapsBefore ? " ✗ 仲裁把自己注入的输入当成了本机用户输入" : "（没有误判）"));

        // ---------------------------------------------------------- 6. 模拟"别的程序抢光标"
        Console.WriteLine("\n--- 模拟第二份实例/别的远控软件的全局钩子（会把光标抢回主屏）---");
        var hostile = new HostileHook(primary, target);
        hostile.Install();
        var routerH = new MouseRouter(log, injector, arbiter) { Mode = RemoteInputMode.AlwaysRealCursor };
        routerH.SetCaptureTarget(target);
        _clicks = 0;
        ClickViaRouter(routerH, target, buttonCenter);
        Result(_clicks > 0,
            $"[E] 有人抢光标时点击：目标窗口收到 {_clicks} 次点击（期望 1）；" +
            $"实际路径={routerH.CurrentPath}，被判定不受控={routerH.CursorContested}，被抢走 {hostile.Stolen} 次" +
            (_clicks > 0 ? " —— 已自动改走后台定向注入，点击没有落到主屏" : " ✗ 点击丢了"));

        // ---------------------------------------------------------- 7. 后台定向注入本身
        routerH.Mode = RemoteInputMode.AlwaysBackground;
        _clicks = 0;
        ClickViaRouter(routerH, target, buttonCenter);
        Result(_clicks > 0, $"[F] 后台定向注入点击：目标窗口收到 {_clicks} 次点击（期望 1）—— 这条不依赖系统光标");
        hostile.Uninstall();

        // ---------------------------------------------------------- 收尾
        arbiter?.Dispose();
        NativeApi.SetCursorPos(saved.X, saved.Y);
        _win.Close();

        Console.WriteLine("\n=== 结论 ===");
        foreach (var l in Lines) Console.WriteLine(l);
        Console.WriteLine($"\n光标已尝试还原到 ({saved.X},{saved.Y})，当前 {Where()}");
        Console.WriteLine("[A] 失败 = 采集目标选错（点击必落到主屏）；[B] 失败 = 光标仲裁会误判远端输入；");
        Console.WriteLine("[C]/[D]/[E] 失败 = 点击没送达目标窗口；[F] 成功 = 后台定向注入可用（抢光标时的兜底）。");
        Environment.Exit(0);   // 收干净：别留窗口/进程（留着的窗口会挡着后续排查）
        return 0;
    }

    // ---------------------------------------------------------------- 步骤辅助

    private static void ClickViaRouter(MouseRouter router, DisplayInfo target, Point screenPoint)
    {
        // MouseRouter 收的是「画面坐标」（相对被采集屏左上角），这里换算回去
        int rx = screenPoint.X - target.Left;
        int ry = screenPoint.Y - target.Top;
        if (!SafeToClick(screenPoint, out var why))
        {
            Console.WriteLine($"    （跳过点击：{why}）");
            return;
        }
        router.Button(rx, ry, 0, false);
        router.Button(rx, ry, 0, true);
        Application.DoEvents();
        Thread.Sleep(120);
        Application.DoEvents();
    }

    private static Point ButtonCenter() =>
        new(_win!.Left + _btn!.Left + _btn.Width / 2, _win.Top + _btn.Top + _btn.Height / 2);

    private static Point Where() { NativeApi.GetCursorPos(out var p); return new Point(p.X, p.Y); }

    /// <summary>按钮中心必须在本进程的窗口上：不是就把窗口挪一挪（避免误点别人的程序）</summary>
    private static bool EnsureOwnWindowAtButton(out string why)
    {
        why = "";
        for (int i = 0; i < 8; i++)
        {
            var p = ButtonCenter();
            var hwnd = BackgroundInput.HitTest(p.X, p.Y);
            if (hwnd != IntPtr.Zero)
            {
                NativeApi.GetWindowThreadProcessId(hwnd, out int pid);
                if (pid == Environment.ProcessId) return true;
                why = $"按钮位置被别的进程窗口占用 (pid={pid})";
            }
            else why = "按钮位置命中的窗口为空";
            _win!.Left += 60;     // 挪一段再试
            Application.DoEvents();
            Thread.Sleep(60);
        }
        return false;
    }

    private static bool SafeToClick(Point p, out string why)
    {
        why = "";
        var hwnd = BackgroundInput.HitTest(p.X, p.Y);
        if (hwnd == IntPtr.Zero) { why = "命中窗口为空"; return false; }
        NativeApi.GetWindowThreadProcessId(hwnd, out int pid);
        if (pid != Environment.ProcessId) { why = $"该位置是别的进程的窗口 (pid={pid})"; return false; }
        return true;
    }

    private static void Result(bool ok, string text)
    {
        Console.WriteLine((ok ? "  [PASS] " : "  [FAIL] ") + text);
        Lines.Add((ok ? "[PASS] " : "[FAIL] ") + text);
    }

    // ---------------------------------------------------------------- 诊断钩子

    private static void InstallDiagHook()
    {
        _diagProc = DiagProc;
        _diagHook = NativeApi.SetWindowsHookExW(NativeApi.WH_MOUSE_LL, _diagProc, NativeApi.GetModuleHandleW(null), 0);
        if (_diagHook == IntPtr.Zero) Console.WriteLine("    （诊断钩子安装失败，[B] 无法判定）");
    }

    private static void UninstallDiagHook()
    {
        if (_diagHook != IntPtr.Zero) NativeApi.UnhookWindowsHookEx(_diagHook);
        _diagHook = IntPtr.Zero;
    }

    private static IntPtr DiagProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var ms = Marshal.PtrToStructure<NativeApi.MSLLHOOKSTRUCT>(lParam);
            uint msg = (uint)wParam.ToInt64();
            string name = msg switch
            {
                NativeApi.WM_MOUSEMOVE => "MOVE",
                NativeApi.WM_LBUTTONDOWN => "LDOWN",
                NativeApi.WM_LBUTTONUP => "LUP",
                NativeApi.WM_RBUTTONDOWN => "RDOWN",
                NativeApi.WM_RBUTTONUP => "RUP",
                NativeApi.WM_MOUSEWHEEL => "WHEEL",
                _ => "0x" + msg.ToString("X4"),
            };
            if (HookSeen.Count < 40)
                HookSeen.Add($"{name} pt=({ms.pt.X},{ms.pt.Y}) INJECTED={((ms.flags & NativeApi.LLMHF_INJECTED) != 0)} " +
                             $"extraInfo=0x{ms.dwExtraInfo.ToInt64():X} 标记={(NativeApi.IsOurInjection(ms.flags, ms.dwExtraInfo) ? "是" : "否")}");
        }
        return NativeApi.CallNextHookEx(_diagHook, nCode, wParam, lParam);
    }

    /// <summary>
    /// 模拟"另一份进程的全局鼠标钩子"：把光标在虚拟屏上的鼠标事件吃掉，并把光标"归位"回物理屏。
    /// 这正是老版本被控端（或别的远控软件）在同一台机器上跑时干的事，也是现场
    /// "远端在副屏点、结果点到主屏"的机制。
    /// </summary>
    private sealed class HostileHook
    {
        private readonly DisplayInfo _physical;
        private readonly DisplayInfo _virtual;
        private NativeApi.LowLevelMouseProc? _proc;
        private IntPtr _hook;
        public int Stolen;

        public HostileHook(DisplayInfo physical, DisplayInfo @virtual) { _physical = physical; _virtual = @virtual; }

        public void Install()
        {
            _proc = Proc;
            _hook = NativeApi.SetWindowsHookExW(NativeApi.WH_MOUSE_LL, _proc, NativeApi.GetModuleHandleW(null), 0);
            Console.WriteLine(_hook == IntPtr.Zero
                ? "    钩子安装失败（不影响其它步骤）"
                : "    钩子已安装：光标在虚拟屏上的鼠标事件都被吃掉并把光标归位回主屏");
        }

        public void Uninstall()
        {
            if (_hook != IntPtr.Zero) NativeApi.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }

        private IntPtr Proc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode < 0) return NativeApi.CallNextHookEx(_hook, nCode, wParam, lParam);
            var ms = Marshal.PtrToStructure<NativeApi.MSLLHOOKSTRUCT>(lParam);
            bool onVirtual = ms.pt.X >= _virtual.Left && ms.pt.X < _virtual.Right &&
                             ms.pt.Y >= _virtual.Top && ms.pt.Y < _virtual.Bottom;
            if (!onVirtual) return NativeApi.CallNextHookEx(_hook, nCode, wParam, lParam);
            Interlocked.Increment(ref Stolen);
            NativeApi.SetCursorPos(_physical.Left + _physical.Width / 2, _physical.Top + _physical.Height / 2);
            return new IntPtr(1);
        }
    }
}
