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

    /// <summary>远端开始操作之前**就已经存在**的窗口（这些一律不动）</summary>
    private readonly HashSet<IntPtr> _baseline = new();
    /// <summary>已经搬过的窗口（同一个窗口只搬一次）</summary>
    private readonly HashSet<IntPtr> _swept = new();
    private bool _baselineBuilt;

    /// <summary>
    /// 把"新出现的窗口"从物理屏搬到虚拟外屏。
    ///
    /// 【为什么需要它】远端点 Win+R、开始菜单、或任何系统对话框时，Windows 把这些窗口开在
    /// **主屏**上（它按主屏/活动窗口所在屏决定位置，跟光标在不在外屏无关）——
    /// 现场就是"我按了 Win+R，运行框跑到主屏去了，我在外屏上根本点不到它"。
    /// 所以：**远端刚操作过**（remoteActive）而**本机用户没在动手**（localActive=false）时，
    /// 才把新窗口搬过来；反过来（本机用户自己在开程序）一个都不碰。
    /// 只搬"落在物理屏上"的窗口，本来就在外屏的不动。
    /// </summary>
    public int SweepNewWindowsFromPhysical(bool remoteActive, bool localActive)
    {
        var target = _captureTarget;
        if (target == null) return 0;
        if (localActive) return 0;                       // 本机用户在操作：绝不碰他的窗口

        if (!remoteActive)
        {
            // 远端空闲：**把此刻屏幕上所有窗口记成基线**。
            // 这一步是关键：只有"基线之后才出现的窗口"才算远端带出来的，
            // 之前那版把"没见过的窗口"当"新窗口"，导致远端一动鼠标就把屏幕上所有老窗口
            // （微信、浏览器…）全当成新出现的搬走 —— 现场反馈"我动一下鼠标微信就自己传过来了"。
            RebuildBaseline();
            return 0;
        }
        if (!_baselineBuilt) RebuildBaseline();
        if (_swept.Count > 400) _swept.Clear();

        int moved = 0;
        NativeApi.EnumWindows((h, _) =>
        {
            try
            {
                if (h == IntPtr.Zero) return true;
                if (_baseline.Contains(h) || _swept.Contains(h)) return true;   // 老窗口 / 已处理过
                if (!NativeApi.IsWindowVisible(h)) return true;
                // 控制台窗口（cmd/powershell）的窗口属于 conhost，IsSaneWindow 会把它过滤掉 ——
                // 但 Win+R 输入 cmd 正是最常见的用法，所以这里单独放行（现场日志：cmd 窗口没被搬）
                bool console = NativeApi.GetClassName(h).Contains("ConsoleWindow");
                if (!console && !IsSaneWindow(h)) return true;
                // 桌面/任务栏这类"外壳"窗口不搬（现场日志里出现过把 4480x1440 的桌面窗口搬走）
                var cls = NativeApi.GetClassName(h);
                if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return true;
                NativeApi.GetWindowThreadProcessId(h, out int pid);
                if (pid == 0 || pid == Environment.ProcessId) return true;
                if (!NativeApi.GetWindowRect(h, out var r)) return true;
                if (r.Right - r.Left < 80 || r.Bottom - r.Top < 40) return true;   // 忽略小提示条

                bool onVirtual = r.Left >= target.Left && r.Left < target.Right &&
                                 r.Top >= target.Top && r.Top < target.Bottom;
                _swept.Add(h);
                if (onVirtual) return true;                 // 已经在外屏上，不用动

                string name = ProcessNameOf(pid);
                _log.Info($"远端操作后新出现的窗口（{name}）落在物理屏 ({r.Left},{r.Top}) → 搬到外屏");
                MoveToTargetCore(h, name, force: true);
                moved++;
            }
            catch { }
            return true;
        }, IntPtr.Zero);
        return moved;
    }

    /// <summary>现在屏幕上有哪些窗口 = 基线。远端按 Win+R 之前拍一张，之后新出现的才算"它带出来的"</summary>
    public void SnapshotBaseline()
    {
        try
        {
            _baseline.Clear();
            NativeApi.EnumWindows((h, _) => { if (h != IntPtr.Zero) _baseline.Add(h); return true; }, IntPtr.Zero);
            _baselineBuilt = true;
        }
        catch { }
    }

    /// <summary>把当前所有可见顶层窗口记为基线（远端空闲时刷新）</summary>
    private void RebuildBaseline()
    {
        try
        {
            NativeApi.EnumWindows((h, _) => { if (h != IntPtr.Zero) _baseline.Add(h); return true; }, IntPtr.Zero);
            _baselineBuilt = true;
        }
        catch { }
        return;
    }

    /// <summary>
    /// 把"屏幕坐标 (x,y) 处的那个窗口"搬到虚拟外屏（供主控端在物理屏画面上点一下就把窗口拉过来）。
    /// 只搬顶层窗口，且跳过隐藏/不可用窗口。
    /// </summary>
    public bool MoveWindowAt(int screenX, int screenY)
    {
        var target = _captureTarget;
        if (target == null) return false;
        try
        {
            var pt = new NativeApi.POINT { X = screenX, Y = screenY };
            var hwnd = NativeApi.WindowFromPoint(pt);
            if (hwnd == IntPtr.Zero) return false;
            var root = NativeApi.GetAncestor(hwnd, NativeApi.GA_ROOT);
            if (root != IntPtr.Zero) hwnd = root;
            if (!NativeApi.IsWindowVisible(hwnd) || !IsSaneWindow(hwnd)) return false;
            NativeApi.GetWindowThreadProcessId(hwnd, out int pid);
            if (pid == 0 || pid == Environment.ProcessId) return false;

            string name = ProcessNameOf(pid);
            _log.Info($"主控端请求把 ({screenX},{screenY}) 处的窗口（{name}）搬到外屏");
            MoveToTargetCore(hwnd, name, force: true);
            return true;
        }
        catch (Exception ex)
        {
            _log.Warn($"搬窗口失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 把某个进程的窗口搬到虚拟外屏（主控端软件列表里的"⇄ 搬到副屏"）。
    /// 用进程名找窗口而不是路径：同一个程序可能从版本子目录启动，路径对不上但进程名对得上。
    /// </summary>
    public bool MoveAppToVirtualScreen(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            string name = ProcessNameOf(pid);
            // 关键：**只要"真正的应用窗口"**。按 pid 找出来的第一个往往不是它 ——
            // explorer 的"桌面窗口"铺满整个虚拟桌面、标题为空，先被撞上的话就会出现
            // "搬是搬了，但用户的资源管理器窗口还在主屏"（现场反馈：右键打开资源管理器，窗口在主屏）。
            var list = FindWindowsByPid(pid).Where(IsMovableAppWindow).ToList();
            if (list.Count == 0) list = FindWindowsByName(name).Where(IsMovableAppWindow).ToList();
            if (list.Count == 0)
            {
                // 控制台程序（cmd / powershell）的窗口属于 conhost，按 pid 找不到 ——
                // 退一步按"窗口标题里含进程名"找（cmd 的标题就是 "命令提示符 - cmd"）。
                list = ListConsoleWindows()
                    .Where(h => NativeApi.GetWindowText(h).Contains(name, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }
            if (list.Count == 0)
            {
                _log.Warn($"没找到 pid={pid}（{name}）的窗口，无法搬到外屏");
                return false;
            }
            int moved = 0;
            foreach (var h in list)
            {
                if (!NativeApi.IsWindowVisible(h)) continue;
                MoveToTargetCore(h, name, force: true);
                moved++;
            }
            _log.Info($"已把 {name}（pid={pid}）的 {moved} 个窗口搬到外屏");
            return moved > 0;
        }
        catch (Exception ex)
        {
            _log.Warn($"搬程序窗口失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>把某个窗口挪到指定屏幕（只挪位置、不改大小）：Win+R 的运行框去外屏、兜底归位都用它</summary>
    public bool MoveWindowTo(IntPtr hwnd, DisplayInfo screen)
    {
        if (hwnd == IntPtr.Zero || screen == null) return false;
        try
        {
            if (NativeApi.IsZoomed(hwnd)) NativeApi.ShowWindow(hwnd, NativeApi.SW_RESTORE);
            int x = screen.Left + 24, y = screen.Top + 24;
            return NativeApi.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0,
                NativeApi.SWP_NOSIZE | NativeApi.SWP_NOZORDER | NativeApi.SWP_NOACTIVATE);
        }
        catch (Exception ex)
        {
            _log.Warn($"挪窗口失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 是不是"用户可以看见的应用窗口"：桌面/任务栏/图标视图这类外壳窗口一律排除，且要求有标题。
    /// 控制台窗口（cmd/powershell）标题正常，照常放行。
    /// </summary>
    private static bool IsMovableAppWindow(IntPtr h)
    {
        try
        {
            if (h == IntPtr.Zero || !NativeApi.IsWindowVisible(h)) return false;
            var cls = NativeApi.GetClassName(h);
            if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"
                or "SysListView32" or "SHELLDLL_DefView" or "Windows.UI.Core.CoreWindow") return false;
            if (cls.Contains("ConsoleWindow")) return true;    // 控制台：标题有时为空也要放行
            return NativeApi.GetWindowText(h).Trim().Length > 0;
        }
        catch { return false; }
    }

    private static string ProcessNameOf(int pid)
    {
        try { using var p = System.Diagnostics.Process.GetProcessById(pid); return p.ProcessName; }
        catch { return $"pid{pid}"; }
    }

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
            IntPtr moved = IntPtr.Zero;
            int movedAt = -1;
            // 最多 24 秒：Edge/Chromium 冷启动经常超过 8 秒（实测"打开 Edge 一直在主屏"就是这个原因），
            // 而远程打开的东西不该久久留在本地用户的屏幕上。
            for (int i = 0; i < 120; i++)
            {
                Thread.Sleep(200);

                // 0) 已经摆过去了的窗口：过几秒再检查一次，防止应用自己"恢复上次位置"又跑回主屏
                if (moved != IntPtr.Zero)
                {
                    int since = i - movedAt;
                    if (since == 5 || since == 15 || since == 30)
                    {
                        if (!NativeApi.IsWindow(moved)) return;
                        if (IsOffTarget(moved)) MoveToTargetCore(moved, p.ProcessName, force: true);
                    }
                    continue;
                }

                // 1) "启动后新增的可见控制台窗口" —— 控制台程序最可靠（含 Windows Terminal 宿主）
                var added = ListConsoleWindows().Where(h => !consoleWindowsBefore.Contains(h)).ToList();
                if (added.Count > 0)
                {
                    var sane = added.Where(IsSaneWindow).ToList();
                    if (sane.Count > 0)
                    {
                        var h = sane.OrderByDescending(WindowWidth).First();
                        _log.Info($"找到新控制台窗口 {h}（{added.Count} 个新增，类名 {NativeApi.GetClassName(h)}）");
                        MoveToTargetCore(h, p.ProcessName, force: true);
                        moved = h; movedAt = i;
                        continue;
                    }
                    if (smallFallback == IntPtr.Zero) smallFallback = added[0];   // 还没长成，先留着
                }

                // 2) 按 pid 找它自己的顶层可见窗口 —— 比 MainWindowHandle 可靠：
                //    Chromium/Electron 的 MainWindowHandle 常常先给一个隐藏的辅助窗口，或者干脆是 0。
                var byPid = FindWindowsByPid(p.Id);
                if (byPid.Count > 0)
                {
                    var h = byPid.OrderByDescending(WindowWidth).First();
                    _log.Info($"找到 {p.ProcessName} 的窗口 {h}（pid 匹配，{byPid.Count} 个）");
                    MoveToTargetCore(h, p.ProcessName, force: true);
                    moved = h; movedAt = i;
                    continue;
                }

                // 3) 单实例应用（微信/Edge 二次点击）常常是"已有进程接管"，启动的 pid 立刻退出 →
                //    按进程名找窗口，这样也照样能把它摆到副屏。
                if (p.HasExited || i % 5 == 0)
                {
                    var byName = FindWindowsByName(p.ProcessName);
                    if (byName.Count > 0)
                    {
                        var h = byName.OrderByDescending(WindowWidth).First();
                        _log.Info($"找到 {p.ProcessName} 的窗口 {h}（按进程名匹配）");
                        MoveToTargetCore(h, p.ProcessName, force: true);
                        moved = h; movedAt = i;
                        continue;
                    }
                }

                p.Refresh();
                if (p.HasExited && i > 10 && moved == IntPtr.Zero)
                {
                    _log.Warn($"{p.ProcessName} 的启动进程已退出且没找到窗口（可能被已有实例接管）");
                    return;
                }

                // 4) 中途也用 AttachConsole 探一次（有些机器上新窗口不会以"新增可见窗口"的身份出现）
                if (i == 10 || i == 20 || i == 30)
                {
                    var ch = TryGetConsoleWindow(p.Id, strict: true);
                    if (ch != IntPtr.Zero)
                    {
                        _log.Info($"通过 conhost 找到控制台窗口 {ch}（{p.ProcessName}）");
                        MoveToTargetCore(ch, p.ProcessName, force: true);
                        moved = ch; movedAt = i;
                        continue;
                    }
                }
            }

            if (moved != IntPtr.Zero) return;

            // 5) 兜底：即使窗口尺寸退化/还没显示，也**必须**给它一个前台窗口
            //    —— 否则按键注入会因为"当前没有前台窗口"全部被丢掉（实测缺陷）。
            var relaxed = TryGetConsoleWindow(p.Id, strict: false);
            if (relaxed != IntPtr.Zero)
            {
                _log.Warn($"只找到尚未成形的控制台窗口 {relaxed}（{p.ProcessName}），仍置为前台以保证按键有去处");
                NativeApi.ShowWindow(relaxed, NativeApi.SW_SHOW);
                MoveToTargetCore(relaxed, p.ProcessName, force: true);
                return;
            }
            if (smallFallback != IntPtr.Zero)
            {
                _log.Warn($"只找到尺寸退化的新控制台窗口 {smallFallback}，仍置为前台");
                NativeApi.ShowWindow(smallFallback, NativeApi.SW_SHOW);
                MoveToTargetCore(smallFallback, p.ProcessName, force: true);
                return;
            }
            _log.Warn($"未找到 {p.ProcessName} 的窗口，键鼠注入可能落到别的窗口上");
        }
        catch (Exception ex)
        {
            _log.Debug($"窗口定位失败（可忽略）：{ex.Message}");
        }
    }

    /// <summary>枚举某个进程的可见顶层窗口（排除退化小窗）</summary>
    private static List<IntPtr> FindWindowsByPid(int pid)
    {
        var list = new List<IntPtr>();
        if (pid <= 0) return list;
        try
        {
            NativeApi.EnumWindows((h, _) =>
            {
                NativeApi.GetWindowThreadProcessId(h, out int wpid);
                if (wpid == pid && NativeApi.IsWindowVisible(h) && IsSaneWindow(h)) list.Add(h);
                return true;
            }, IntPtr.Zero);
        }
        catch { }
        return list;
    }

    /// <summary>按进程名枚举可见顶层窗口（单实例应用：启动进程立刻退出时用）</summary>
    private static List<IntPtr> FindWindowsByName(string procName)
    {
        var list = new List<IntPtr>();
        try
        {
            NativeApi.EnumWindows((h, _) =>
            {
                if (!NativeApi.IsWindowVisible(h) || !IsSaneWindow(h)) return true;
                NativeApi.GetWindowThreadProcessId(h, out int wpid);
                if (wpid <= 0) return true;
                try
                {
                    using var pr = System.Diagnostics.Process.GetProcessById(wpid);
                    if (string.Equals(pr.ProcessName, procName, StringComparison.OrdinalIgnoreCase)) list.Add(h);
                }
                catch { }
                return true;
            }, IntPtr.Zero);
        }
        catch { }
        return list;
    }

    /// <summary>窗口是否已经不在采集屏里</summary>
    private bool IsOffTarget(IntPtr hwnd)
    {
        var target = _captureTarget;
        if (target == null) return false;
        if (!NativeApi.GetWindowRect(hwnd, out var r)) return false;
        return !(r.Right > target.Left && r.Left < target.Right && r.Bottom > target.Top && r.Top < target.Bottom);
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
    /// 把窗口摆到采集屏（副屏）。
    ///
    /// force=true：**不管窗口现在在哪，一律挪过去**。被控端自己启动的（也就是远程打开的）窗口走这条 ——
    ///   客户要求"远程打开的必须显示在副屏，不能打扰主屏"。
    /// force=false：只在窗口与目标屏**完全没有交集**时才动它，避免打扰本机用户自己摆好的窗口（老行为）。
    ///
    /// 另外：最大化状态下 SetWindowPos 的位置会被忽略，所以挪之前先还原；
    /// 全屏独占的应用（模拟器/游戏/播放器）历史上挪动会渲染异常甚至卡死，
    /// 因此**只挪位置、绝不改尺寸**（只有窗口比目标屏还大时才会落到两块屏上，这种情况另说）。
    /// </summary>
    private void MoveToTargetCore(IntPtr hwnd, string name, bool force = false)
    {
        var target = _captureTarget;
        if (target == null) return;
        if (!NativeApi.GetWindowRect(hwnd, out var r)) return;

        bool overlaps = r.Right > target.Left && r.Left < target.Right
                     && r.Bottom > target.Top && r.Top < target.Bottom;
        if (force || !overlaps)
        {
            int w = r.Right - r.Left;
            int h = r.Bottom - r.Top;
            // 让窗口中心落在目标屏内即可，尺寸原样保留
            int x = target.Left + Math.Max(0, (target.Width - Math.Min(w, target.Width)) / 2);
            int y = target.Top + Math.Max(0, (target.Height - Math.Min(h, target.Height)) / 2);
            // 只在窗口确实是最小化的时候才 SW_RESTORE：对最大化的/全屏独占的窗口调 SW_RESTORE
            // 会把它的显示状态改掉（用户报的"打开应用后黑屏卡掉"就有这条路径的份）。
            if (NativeApi.IsIconic(hwnd)) NativeApi.ShowWindow(hwnd, NativeApi.SW_RESTORE);
            // 最大化状态下位置会被忽略 → 先还原成普通窗口，才能挪到副屏去
            if (NativeApi.IsZoomed(hwnd)) NativeApi.ShowWindow(hwnd, NativeApi.SW_RESTORE);
            if (NativeApi.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0,
                    NativeApi.SWP_NOZORDER | NativeApi.SWP_NOACTIVATE | NativeApi.SWP_NOSIZE))
                _log.Info($"把 {name} 窗口移到外屏 {x},{y}（尺寸保持 {w}x{h}）{(force ? "［强制］" : "")}");
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
