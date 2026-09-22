using System.Diagnostics;
using Agent.Common;

namespace Agent.Worker;

/// <summary>
/// Session 1 内的窗口/进程管理：打开、关闭、把窗口拉到虚拟副屏上
/// </summary>
public sealed class WindowManager
{
    private readonly Logger _log;
    private DisplayInfo? _captureTarget;

    public WindowManager(Logger log) => _log = log;

    public void SetCaptureTarget(DisplayInfo target) => _captureTarget = target;

    /// <summary>
    /// 枚举当前会话里可见的"控制台类"窗口（用于"启动前后对比"定位新窗口）。
    /// 两种宿主都要认：
    /// - 传统 conhost：`ConsoleWindowClass`
    /// - Windows 11 默认终端 Windows Terminal：`CASCADIA_HOSTING_WINDOW_CLASS`
    ///   （此时新开的 cmd 没有自己的窗口，窗口属于 WindowsTerminal.exe）
    /// </summary>
    private static bool IsConsoleWindowClass(string cls)
        => cls.Equals("ConsoleWindowClass", StringComparison.OrdinalIgnoreCase)
           || cls.Equals("CASCADIA_HOSTING_WINDOW_CLASS", StringComparison.OrdinalIgnoreCase);

    private static HashSet<IntPtr> ListConsoleWindows()
    {
        var set = new HashSet<IntPtr>();
        try
        {
            NativeApi.EnumWindows((h, _) =>
            {
                if (IsConsoleWindowClass(NativeApi.GetClassName(h)) && NativeApi.IsWindowVisible(h))
                    set.Add(h);
                return true;
            }, IntPtr.Zero);
        }
        catch { }
        return set;
    }

    /// <summary>在远程会话中启动软件，返回 pid</summary>
    public int Open(string exePath, string? arguments = null)
    {
        if (!File.Exists(exePath))
        {
            _log.Error($"打开失败：文件不存在 {exePath}");
            return -1;
        }
        try
        {
            // 控制台程序（cmd/powershell）的窗口属于 conhost，MainWindowHandle 常常为 0，
            // 而 AttachConsole+GetConsoleWindow 会拿到退化的小窗口（实测 16x16）。
            // 最稳的办法：记录启动前的控制台窗口集合，启动后取差集就是新窗口。
            var before = ListConsoleWindows();
            var psi = new ProcessStartInfo(exePath)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? "",
                Arguments = arguments ?? "",
            };
            var p = Process.Start(psi);
            int pid = p?.Id ?? -1;
            _log.Info($"已启动 {Path.GetFileName(exePath)} pid={pid}");
            if (p != null) _ = Task.Run(() => EnsureWindowOnTarget(p, before));
            return pid;
        }
        catch (Exception ex)
        {
            _log.Error($"启动 {exePath} 失败：{ex.Message}");
            return -1;
        }
    }

    /// <summary>关闭进程（直接杀，策划 §3.1 右键菜单「关闭(退出)」）</summary>
    public bool Close(int pid)
    {
        try
        {
            var p = Process.GetProcessById(pid);
            try
            {
                string name = p.ProcessName;
                p.Kill(entireProcessTree: false);
                _log.Info($"已结束进程 {name}({pid})");
                return true;
            }
            finally { p.Dispose(); }
        }
        catch (ArgumentException)
        {
            _log.Warn($"进程 {pid} 不存在（可能已退出）");
            return true;
        }
        catch (Exception ex)
        {
            _log.Error($"结束进程 {pid} 失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>等一下窗口出现，若不在采集屏幕上就移过去，并置为前台（键鼠注入要落到它上面）</summary>
    private void EnsureWindowOnTarget(Process p, HashSet<IntPtr> consoleWindowsBefore)
    {
        try
        {
            IntPtr smallFallback = IntPtr.Zero;
            // 最多 8 秒：控制台窗口有时要好几秒才成形（实测出现过"窗口还是 16x16/未显示"的阶段）
            for (int i = 0; i < 40; i++)
            {
                Thread.Sleep(200);

                // 1) "启动后新增的可见控制台窗口" —— 控制台程序最可靠（含 Windows Terminal 宿主）
                var added = ListConsoleWindows().Where(h => !consoleWindowsBefore.Contains(h)).ToList();
                if (added.Count > 0)
                {
                    var sane = added.Where(IsSaneWindow).ToList();
                    if (sane.Count > 0)
                    {
                        var h = sane.OrderByDescending(WindowWidth).First();
                        _log.Info($"找到新控制台窗口 {h}（{added.Count} 个新增，类名 {NativeApi.GetClassName(h)}）");
                        MoveToTargetIfNeeded(h, p.ProcessName);
                        return;
                    }
                    if (smallFallback == IntPtr.Zero) smallFallback = added[0];   // 还没长成，先留着
                }

                p.Refresh();
                if (p.HasExited) return;

                // 2) GUI 程序：主窗口句柄
                var mh = p.MainWindowHandle;
                if (mh != IntPtr.Zero)
                {
                    MoveToTargetIfNeeded(mh, p.ProcessName);
                    return;
                }

                // 3) 中途也用 AttachConsole 探一次（有些机器上新窗口不会以"新增可见窗口"的身份出现）
                if (i == 10 || i == 20 || i == 30)
                {
                    var ch = TryGetConsoleWindow(p.Id, strict: true);
                    if (ch != IntPtr.Zero)
                    {
                        _log.Info($"通过 conhost 找到控制台窗口 {ch}（{p.ProcessName}）");
                        MoveToTargetIfNeeded(ch, p.ProcessName);
                        return;
                    }
                }
            }

            // 4) 兜底：即使窗口尺寸退化/还没显示，也**必须**给它一个前台窗口
            //    —— 否则按键注入会因为"当前没有前台窗口"全部被丢掉（实测缺陷）。
            //    窗口小不影响按键进入该控制台的输入缓冲。
            var relaxed = TryGetConsoleWindow(p.Id, strict: false);
            if (relaxed != IntPtr.Zero)
            {
                _log.Warn($"只找到尚未成形的控制台窗口 {relaxed}（{p.ProcessName}），仍置为前台以保证按键有去处");
                NativeApi.ShowWindow(relaxed, NativeApi.SW_SHOW);
                MoveToTargetIfNeeded(relaxed, p.ProcessName);
                return;
            }
            if (smallFallback != IntPtr.Zero)
            {
                _log.Warn($"只找到尺寸退化的新控制台窗口 {smallFallback}，仍置为前台");
                NativeApi.ShowWindow(smallFallback, NativeApi.SW_SHOW);
                MoveToTargetIfNeeded(smallFallback, p.ProcessName);
                return;
            }
            _log.Warn($"未找到 {p.ProcessName} 的窗口，键鼠注入可能落到别的窗口上");
        }
        catch (Exception ex)
        {
            _log.Debug($"窗口定位失败（可忽略）：{ex.Message}");
        }
    }

    /// <summary>窗口尺寸是否正常（排除 16x16 之类的退化窗口）</summary>
    private static bool IsSaneWindow(IntPtr hwnd)
    {
        try
        {
            if (!NativeApi.GetWindowRect(hwnd, out var r)) return false;
            return r.Right - r.Left >= 100 && r.Bottom - r.Top >= 60;
        }
        catch { return false; }
    }

    private static int WindowWidth(IntPtr hwnd)
        => NativeApi.GetWindowRect(hwnd, out var r) ? r.Right - r.Left : 0;

    /// <summary>用 AttachConsole(pid) + GetConsoleWindow() 取控制台窗口句柄</summary>
    private IntPtr TryGetConsoleWindow(int pid, bool strict = true)
    {
        try
        {
            if (!NativeApi.AttachConsole((uint)pid)) return IntPtr.Zero;
            try
            {
                var h = NativeApi.GetConsoleWindow();
                // 校验尺寸：AttachConsole 有时会给出 16x16 的退化窗口，拿它当焦点等于没聚焦
                if (h != IntPtr.Zero && NativeApi.GetWindowRect(h, out var r))
                {
                    int w = r.Right - r.Left, hh = r.Bottom - r.Top;
                    if (w < 100 || hh < 60)
                    {
                        _log.Warn($"conhost 控制台窗口 {h} 尺寸退化（{w}x{hh}），忽略");
                        return IntPtr.Zero;
                    }
                }
                return h;
            }
            finally { NativeApi.FreeConsole(); }
        }
        catch { return IntPtr.Zero; }
    }

    /// <summary>光标仲裁器：用来判断"本机用户是不是正在操作"，决定能不能抢前台</summary>
    public CursorArbiter? Arbiter { get; set; }

    /// <summary>
    /// 把窗口挪到采集目标（外屏）上：**只挪位置、不改尺寸**。
    /// 原来这里按目标屏大小 SetWindowPos 缩放窗口，实测对模拟器/游戏/播放器一类会渲染异常
    /// 甚至卡死（用户反馈的"打开应用后黑屏卡掉"）；另外只在窗口与目标屏**完全没有交集**时
    /// 才动它，别去打扰本机用户自己摆好的窗口。
    /// </summary>
    private void MoveToTargetIfNeeded(IntPtr hwnd, string name)
    {
        var target = _captureTarget;
        if (target == null) return;
        if (!NativeApi.GetWindowRect(hwnd, out var r)) return;

        bool overlaps = r.Right > target.Left && r.Left < target.Right
                     && r.Bottom > target.Top && r.Top < target.Bottom;
        if (!overlaps)
        {
            int w = r.Right - r.Left;
            int h = r.Bottom - r.Top;
            // 让窗口中心落在目标屏内即可，尺寸原样保留
            int x = target.Left + Math.Max(0, (target.Width - Math.Min(w, target.Width)) / 2);
            int y = target.Top + Math.Max(0, (target.Height - Math.Min(h, target.Height)) / 2);
            // 只在窗口确实是最小化的时候才 SW_RESTORE。对最大化的窗口、或全屏独占的
            // 模拟器/游戏/播放器调 SW_RESTORE，会把它的显示状态整个改掉（用户报的
            // "打开应用后黑屏卡掉"就有这条路径的份）。
            if (NativeApi.IsIconic(hwnd)) NativeApi.ShowWindow(hwnd, NativeApi.SW_RESTORE);
            if (NativeApi.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0,
                    NativeApi.SWP_NOZORDER | NativeApi.SWP_NOACTIVATE | NativeApi.SWP_NOSIZE))
                _log.Info($"把 {name} 窗口移到外屏 {x},{y}（尺寸保持 {w}x{h}）");
        }
        RobustSetForeground(hwnd);
    }

    /// <summary>
    /// 把窗口置为前台。Windows 有"前台锁"：非前台进程直接 SetForegroundWindow 可能被忽略，
    /// 标准绕过办法是先把本线程的输入队列附加到当前前台窗口的线程上。
    /// 键鼠注入要落到目标窗口，这一步是关键。
    /// </summary>
    public void RobustSetForeground(IntPtr hwnd)
    {
        try
        {
            // 本机用户正在用键鼠时**不抢前台**：抢了之后他接下来的按键就会打进远端的窗口
            // （丢字、误操作），这是共享模式下"主屏受影响"最直接的一种。
            // 远端并不依赖这一步 —— 他点一下那个窗口，系统自己会把它激活。
            var arb = Arbiter;
            if (arb != null && arb.LocalActiveRecently)
            {
                _log.Info($"本机用户正在操作，跳过置前台（远端点一下该窗口即可激活）：{hwnd}");
                return;
            }

            if (NativeApi.IsIconic(hwnd)) NativeApi.ShowWindow(hwnd, NativeApi.SW_RESTORE);

            IntPtr fg = NativeApi.GetForegroundWindow();
            if (fg == hwnd)
            {
                _log.Debug("目标窗口已在前台");
                return;
            }
            uint fgThread = fg == IntPtr.Zero ? 0 : NativeApi.GetWindowThreadProcessId(fg, out _);
            uint myThread = NativeApi.GetCurrentThreadId();
            bool attached = false;
            if (fgThread != 0 && fgThread != myThread)
                attached = NativeApi.AttachThreadInput(myThread, fgThread, true);
            try
            {
                NativeApi.BringWindowToTop(hwnd);
                NativeApi.SetForegroundWindow(hwnd);
                NativeApi.SetActiveWindow(hwnd);
            }
            finally
            {
                if (attached) NativeApi.AttachThreadInput(myThread, fgThread, false);
            }
            var now = NativeApi.GetForegroundWindow();
            _log.Info($"已尝试把窗口 {hwnd} 置为前台（当前前台={now}，{(now == hwnd ? "成功" : "未生效")}）");
        }
        catch (Exception ex)
        {
            _log.Debug($"置前台失败：{ex.Message}");
        }
    }
}
