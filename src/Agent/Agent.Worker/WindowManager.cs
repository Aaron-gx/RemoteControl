using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
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
    /// 主控端软件列表里「此电脑」专用项的标记（打开指令的 exePath 字段用它，被控端认出后开资源管理器）。
    /// 用 ASCII 哨兵值而不是真实 exe 路径：被控端自己解析系统目录下的 explorer 路径，主控端不用知道。
    /// </summary>
    public const string ThisPcMarker = SoftwareListMarkers.ThisPc;

    /// <summary>「此电脑」的 shell 目录 CLSID（explorer.exe 加它作参数 = 直接打开"此电脑"）</summary>
    private const string ThisPcClsid = "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}";

    /// <summary>
    /// 打开「此电脑」（主控端软件列表置顶项，2026-10 新增）。窗口必须出现在**外屏**上：
    /// ① `/separate`：Win11 的资源管理器默认把新窗口**并成已有窗口里的标签页** —— 桌面上已有
    ///    资源管理器窗口时，"此电脑"会塞进主屏那个旧窗口里，根本没有新窗口出现，
    ///    钩子/快扫/轮询全都无处下手（客户反馈"有时候到不了副屏"）。/separate 强制独立进程开
    ///    独立窗口，这套搬窗机制才有东西可搬；新窗口属于新起的 explorer 进程，认领也更干净。
    /// ② 打开前先拍基线快照 —— 只认"快照之后新出现"的窗口，绝不碰本机用户已开着的资源管理器窗口；
    /// ③ 桌面/任务栏窗口由 IsShellWindow 一票否决（详见其注释）。
    /// </summary>
    public int OpenThisPC()
    {
        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        if (!File.Exists(exe))
        {
            _log.Error($"找不到 explorer.exe：{exe}");
            return -1;
        }
        SnapshotBaseline();
        _log.Info("主控端请求打开「此电脑」（/separate 独立窗口，将摆到外屏）");
        return Open(exe, "/separate " + ThisPcClsid);
    }

    /// <summary>
    /// 是不是"外壳"窗口（桌面 Progman/WorkerW、任务栏、桌面图标视图）。
    /// 这些窗口属于 explorer 进程、尺寸又"正常"（桌面就是全屏大小），按 pid/进程名找窗口时
    /// 一定会撞上 —— 把它们挪走等于把用户的整个桌面/任务栏搬走，必须一票否决。
    /// </summary>
    private static bool IsShellWindow(IntPtr h)
    {
        try
        {
            var cls = NativeApi.GetClassName(h);
            return cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"
                or "SHELLDLL_DefView" or "SysListView32";
        }
        catch { return true; }   // 拿不到类名时按"是外壳"处理（宁可不搬，不可搬错）
    }

    /// <summary>远端开始操作之前**就已经存在**的窗口（这些一律不动）。
    /// 永远通过 TakeBaselineSnapshot() 拿快照副本读 —— 集合在写入时整体换新实例，副本读是线程安全的。</summary>
    private HashSet<IntPtr> _baseline = new();
    /// <summary>快照时**已经在运行**的进程：Win+R 会话期只搬"新起来的进程"的窗口，
    /// 这样本机用户自己开着的程序（浏览器/微信…）不会被搬到外屏（现场反馈"打开任何东西都闪到副屏"）</summary>
    private HashSet<int> _baselinePids = new();
    private readonly object _baselineWriteLock = new();
    /// <summary>已经搬过的窗口（同一个窗口只搬一次）</summary>
    private readonly HashSet<IntPtr> _swept = new();
    /// <summary>上次打印"按名兜底被闸门拦下"日志的时刻（限流：5 秒一条，别刷屏）</summary>
    private long _lastByNameSkipLog;
    /// <summary>最近一次"打开指令"的时刻（ArmShowCatcher 时更新）：头几秒内出现的同名窗口与指令强相关，视为确定认领</summary>
    private long _armTick;
    /// <summary>本轮武装期预定位的窗口计数（explorer 连开十几个辅助窗，日志限流用）</summary>
    private long _prePosLogCount;
    private bool _baselineBuilt;

    /// <summary>拿一份基线窗口快照（副本）。写入方整体换新集合，读副本永远安全。</summary>
    private HashSet<IntPtr> TakeBaselineSnapshot()
    {
        lock (_baselineWriteLock) return _baseline;
    }

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

        var baseline = TakeBaselineSnapshot();
        var baselinePids = TakeBaselinePidsSnapshot();
        int moved = 0;
        NativeApi.EnumWindows((h, _) =>
        {
            try
            {
                if (h == IntPtr.Zero) return true;
                if (baseline.Contains(h) || _swept.Contains(h)) return true;   // 老窗口 / 已处理过
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

                // 【2026-10-10 右键菜单跑主屏·另一半】菜单/下拉弹窗：跳过所有"进程基线"判据
                // （菜单属于早已在跑的进程，baselinePids 必然包含它，以前因此永远没人搬）。
                // 远端在操作、本机没动手的时段里出现在**物理屏**上的菜单，就是远端点出来却被
                // 系统摆错位置的 —— 夹进外屏让它回到远端看得见的地方（位置就近平移，不改大小）。
                if (TrySweepPopup(h, target)) return true;

                // 只搬"快照之后新起来的进程"的窗口：本机用户原来开着的程序一个都不动
                if (baselinePids.Count > 0 && baselinePids.Contains(pid)) return true;
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

    /// <summary>
    /// 副屏孤儿窗口回收（2026-10-05，客户反馈"Telegram 第一次打开不回主屏"）：
    /// 主控端离线（10 秒无远端输入）时，把所有**整块落在虚拟外屏上**的可见应用窗口搬回主屏。
    /// Telegram 从托盘恢复不触发 EVENT_OBJECT_SHOW（窗口一直是"可见"的），SHOW 路径抓不到。
    /// </summary>
    public void ReclaimOrphanWindowsFromVirtual()
    {
        try
        {
            var virt = VirtualScreenProvider?.Invoke();
            if (virt is not { IsVirtual: true }) return;
            var primary = DisplayHelper.GetMonitors().FirstOrDefault(m => m.IsPrimary);
            if (primary == null) return;

            NativeApi.EnumWindows((h, _) =>
            {
                try
                {
                    if (!NativeApi.IsWindow(h) || !NativeApi.IsWindowVisible(h)) return true;
                    if (NativeApi.GetAncestor(h, NativeApi.GA_ROOT) != h) return true;
                    if (IsShellWindow(h)) return true;
                    if (!IsSaneWindow(h)) return true;
                    NativeApi.GetWindowThreadProcessId(h, out int pid);
                    if (pid == 0 || pid == Environment.ProcessId) return true;
                    if (!NativeApi.GetWindowRect(h, out var r)) return true;
                    int w = r.Right - r.Left, hgt = r.Bottom - r.Top;
                    if (w < 300 || hgt < 200) return true;
                    if (!(r.Left >= virt.Left && r.Right <= virt.Right && r.Top >= virt.Top && r.Bottom <= virt.Bottom)) return true;

                    int x = primary.Left + Math.Max(0, (primary.Width - Math.Min(w, primary.Width)) / 2);
                    int y = primary.Top + Math.Max(0, (primary.Height - Math.Min(hgt, primary.Height)) / 2);
                    if (NativeApi.IsZoomed(h)) NativeApi.ShowWindow(h, NativeApi.SW_RESTORE);
                    if (NativeApi.SetWindowPos(h, IntPtr.Zero, x, y, 0, 0,
                            NativeApi.SWP_NOSIZE | NativeApi.SWP_NOZORDER | NativeApi.SWP_NOACTIVATE))
                    {
                        _log.Info($"副屏孤儿窗口回收（{ProcessNameOf(pid)} {w}x{hgt}）→ 搬回主屏 ({x},{y})");
                    }
                }
                catch { }
                return true;
            }, IntPtr.Zero);
        }
        catch { }
    }

    /// <summary>
    /// 虚拟外屏上当前有多少个可见的应用窗口（-1 = 没有虚拟屏）。
    /// 0 = 空屏：主控端据此提示"窗口可能都已被搬回主屏"，而不是让人对着黑壁纸猜软件坏了
    /// （2026-10-10 现场：客户重连后看到空副屏，以为"点击失灵"）。
    /// </summary>
    public int CountVisibleWindowsOnVirtual()
    {
        var virt = VirtualScreenProvider?.Invoke();
        if (virt is not { IsVirtual: true }) return -1;
        int count = 0;
        try
        {
            NativeApi.EnumWindows((h, _) =>
            {
                try
                {
                    if (!NativeApi.IsWindow(h) || !NativeApi.IsWindowVisible(h)) return true;
                    if (NativeApi.GetAncestor(h, NativeApi.GA_ROOT) != h) return true;
                    if (IsShellWindow(h)) return true;
                    if (!IsSaneWindow(h)) return true;
                    NativeApi.GetWindowThreadProcessId(h, out int pid);
                    if (pid == 0 || pid == Environment.ProcessId) return true;
                    if (!NativeApi.GetWindowRect(h, out var r)) return true;
                    if (r.Right <= r.Left || r.Bottom <= r.Top) return true;
                    // 与孤儿回收同判据：整块落在虚拟外屏上才算"在这块屏上"
                    if (r.Left >= virt.Left && r.Right <= virt.Right &&
                        r.Top >= virt.Top && r.Bottom <= virt.Bottom)
                        count++;
                }
                catch { }
                return true;
            }, IntPtr.Zero);
        }
        catch { }
        return count;
    }

    /// <summary>
    /// 虚拟外屏整体挪位后，把中心点还留在旧矩形里的可见顶层窗口按相同位移平移过去。
    /// 系统挪屏不保证带着普通窗口走：外屏从 (1920,0) 挪到 (2560,0) 后，
    /// 原来停在外屏上的窗口会孤零零留在物理屏区域里（最大化窗口系统会自己跟着屏走，跳过）。
    /// </summary>
    public void ShiftWindowsWithVirtualScreen(DisplayInfo oldRect, DisplayInfo newRect)
    {
        int dx = newRect.Left - oldRect.Left, dy = newRect.Top - oldRect.Top;
        if (dx == 0 && dy == 0) return;
        try
        {
            NativeApi.EnumWindows((h, _) =>
            {
                try
                {
                    if (!NativeApi.IsWindow(h) || !NativeApi.IsWindowVisible(h)) return true;
                    if (NativeApi.GetAncestor(h, NativeApi.GA_ROOT) != h) return true;
                    if (IsShellWindow(h)) return true;
                    // 控制台窗口 IsSaneWindow 会过滤掉，但 cmd/PowerShell 是远端最常用的，单独放行
                    bool console = NativeApi.GetClassName(h).Contains("ConsoleWindow");
                    if (!console && !IsSaneWindow(h)) return true;
                    if (NativeApi.IsZoomed(h)) return true;   // 最大化窗口由系统随屏迁移
                    NativeApi.GetWindowThreadProcessId(h, out int pid);
                    if (pid == 0 || pid == Environment.ProcessId) return true;
                    if (!NativeApi.GetWindowRect(h, out var r)) return true;
                    int cx = (r.Left + r.Right) / 2, cy = (r.Top + r.Bottom) / 2;
                    if (cx < oldRect.Left || cx >= oldRect.Right || cy < oldRect.Top || cy >= oldRect.Bottom)
                        return true;   // 中心不在旧外屏里，不归我们管
                    if (NativeApi.SetWindowPos(h, IntPtr.Zero, r.Left + dx, r.Top + dy, 0, 0,
                            NativeApi.SWP_NOSIZE | NativeApi.SWP_NOZORDER | NativeApi.SWP_NOACTIVATE))
                    {
                        _log.Info($"外屏挪位：窗口（{ProcessNameOf(pid)}）随屏平移 → ({r.Left + dx},{r.Top + dy})");
                    }
                }
                catch { }
                return true;
            }, IntPtr.Zero);
        }
        catch { }
    }

    /// <summary>现在屏幕上有哪些窗口 = 基线。远端按 Win+R 之前拍一张，之后新出现的才算"它带出来的"</summary>
    public void SnapshotBaseline()
    {
        try
        {
            var windows = new HashSet<IntPtr>();
            var pids = new HashSet<int>();
            NativeApi.EnumWindows((h, _) =>
            {
                if (h == IntPtr.Zero) return true;
                windows.Add(h);
                NativeApi.GetWindowThreadProcessId(h, out int pid);
                if (pid > 0) pids.Add(pid);
                return true;
            }, IntPtr.Zero);
            lock (_baselineWriteLock)
            {
                _baseline = windows;      // 整体换新实例：正在用旧快照的读者不受影响（见 TakeBaselineSnapshot）
                _baselinePids = pids;
                _baselineBuilt = true;
            }
        }
        catch { }
    }

    /// <summary>拿一份基线进程快照（副本）</summary>
    private HashSet<int> TakeBaselinePidsSnapshot()
    {
        lock (_baselineWriteLock) return _baselinePids;
    }

    /// <summary>把当前所有可见顶层窗口记为基线（远端空闲时刷新）</summary>
    private void RebuildBaseline()
    {
        try
        {
            var windows = new HashSet<IntPtr>();
            NativeApi.EnumWindows((h, _) => { if (h != IntPtr.Zero) windows.Add(h); return true; }, IntPtr.Zero);
            lock (_baselineWriteLock)
            {
                _baseline = windows;      // 写入方整体换新集合（线程安全模型见 _baseline 注释）
                _baselineBuilt = true;
            }
        }
        catch { }
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

    /// <summary>
    /// 是不是"菜单/下拉"这类**瞬态弹窗**：#32768（经典右键菜单/系统菜单）、Win11 新版右键菜单
    /// （XamlExplorerHostIslandWindow）、或有属主的无标题栏 WS_POPUP（下拉框/组合框列表）。
    /// 【2026-10-10 右键菜单跑主屏】它们出现在哪由"点击发生在哪"决定，不由进程归属决定：
    /// 菜单属于早已在跑的进程（explorer/应用本体），按 pid 基线一刀切会把远端点出来的菜单漏掉
    /// （落到物理屏就没人搬）；反过来把出现在外屏的菜单当"本机用户开的窗口"搬回主屏也不对
    /// —— 本机用户的物理点击永远落在物理屏（仲裁器保证），出现在外屏的菜单必是远端点出来的。
    /// </summary>
    private static bool IsTransientPopup(IntPtr h)
    {
        try
        {
            var cls = NativeApi.GetClassName(h);
            if (cls is "#32768" or "XamlExplorerHostIslandWindow") return true;
            int style = NativeApi.GetWindowStyle(h);
            const int WS_POPUP = unchecked((int)0x80000000);
            const int WS_CAPTION = 0x00C00000;
            return (style & WS_POPUP) != 0 && (style & WS_CAPTION) == 0
                   && NativeApi.GetWindow(h, NativeApi.GW_OWNER) != IntPtr.Zero;
        }
        catch { return false; }
    }

    /// <summary>
    /// 弹窗搬移的公共实现（SweepNewWindowsFromPhysical 的弹窗分支与 SweepPopupsFromPhysical 共用）：
    /// h 是瞬态弹窗且落在物理屏上 → 就近平移夹进外屏。返回"是否已处理"（是弹窗就算处理完，
    /// 调用方不必再走普通窗口的判据）。菜单生命周期极短，按句柄记账防反复搬。
    /// </summary>
    private bool TrySweepPopup(IntPtr h, DisplayInfo target)
    {
        if (!IsTransientPopup(h)) return false;
        if (!NativeApi.IsWindow(h) || !NativeApi.IsWindowVisible(h)) return true;
        if (_swept.Contains(h)) return true;
        if (NativeApi.GetWindowRect(h, out var mr))
        {
            bool menuOnVirtual = mr.Left >= target.Left && mr.Left < target.Right &&
                                 mr.Top >= target.Top && mr.Top < target.Bottom;
            if (!menuOnVirtual)
            {
                int mw = mr.Right - mr.Left, mh = mr.Bottom - mr.Top;
                int mx = Math.Clamp(mr.Left, target.Left, Math.Max(target.Left, target.Right - mw));
                int my = Math.Clamp(mr.Top, target.Top, Math.Max(target.Top, target.Bottom - mh));
                if (NativeApi.SetWindowPos(h, IntPtr.Zero, mx, my, 0, 0,
                        NativeApi.SWP_NOSIZE | NativeApi.SWP_NOZORDER | NativeApi.SWP_NOACTIVATE))
                {
                    long n = Interlocked.Increment(ref _menuSweepLog);
                    if (n <= 2 || n % 20 == 0)
                        _log.Info($"远端的菜单/下拉落在了物理屏 ({mr.Left},{mr.Top}) → 已搬回外屏 ({mx},{my})，累计 {n} 次");
                }
            }
        }
        _swept.Add(h);
        return true;
    }

    /// <summary>
    /// 菜单/下拉弹窗专扫（2026-10-10 右键菜单跑主屏）：**通用** Sweep 只在 Win+R 会话期跑，
    /// 而右键随时发生。远端在操作、本机没动键鼠的时段里，每秒扫一遍顶层窗口，
    /// 把落在物理屏上的瞬态弹窗（#32768/XAML 菜单/有属主的下拉）搬回外屏。
    /// 只碰弹窗、不碰普通窗口，开销极小（EnumWindows + 类名/样式判断）。
    /// </summary>
    public int SweepPopupsFromPhysical()
    {
        var target = _captureTarget;
        if (target == null) return 0;
        if (_swept.Count > 400) _swept.Clear();
        int moved = 0;
        NativeApi.EnumWindows((h, _) =>
        {
            try
            {
                if (h == IntPtr.Zero) return true;
                if (TrySweepPopup(h, target)) moved++;
            }
            catch { }
            return true;
        }, IntPtr.Zero);
        return moved;
    }

    /// <summary>在远程会话中启动软件，返回 pid</summary>
    public int Open(string exePath, string? arguments = null)    {
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

            // 第一优先：**出生即外屏** —— CreateProcess 的 STARTUPINFO 把初始窗口位置直接定在虚拟外屏。
            // Windows 的规则：程序 CreateWindow 时 x/y 给 CW_USEDEFAULT（绝大多数程序就是这么开主窗口的），
            // 系统会采用 STARTUPINFO 的 dwX/dwY —— 窗口生下来就在外屏，主屏上**根本不出现**（零闪烁）。
            int pid = StartOnVirtualScreen(exePath, arguments, out bool positioned);
            Process? p = null;
            if (pid > 0)
            {
                try { p = Process.GetProcessById(pid); } catch { /* 单实例程序：启动进程可能瞬间退出 */ }
            }
            if (pid <= 0)
            {
                // 回落老路径（ShellExecute）：要求提权的程序（ERROR_ELEVATION_REQUIRED）、
                // lnk/协议等非可执行文件、以及 CreateProcess 因任何原因没成的情况。
                var psi = new ProcessStartInfo(exePath)
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(exePath) ?? "",
                    Arguments = arguments ?? "",
                };
                p = Process.Start(psi);
                pid = p?.Id ?? -1;
            }
            _log.Info($"已启动 {Path.GetFileName(exePath)} pid={pid}{(positioned ? "（初始位置=外屏）" : "")}");

            // 第二优先：**显示即捕获** —— 自己定位/记住上次位置的程序（Chromium 系、微信…）不理会
            // STARTUPINFO，仍会先在主屏出现。用 SetWinEventHook(EVENT_OBJECT_SHOW) 在它们**第一帧
            // 画出来之前**（毫秒级）搬走。老的 200ms 轮询是兜底，仍保留在 EnsureWindowOnTarget 里。
            ArmShowCatcher(pid, Path.GetFileNameWithoutExtension(exePath));

            if (p != null) _ = Task.Run(() => EnsureWindowOnTarget(p, before));
            return pid;
        }
        catch (Exception ex)
        {
            _log.Error($"启动 {exePath} 失败：{ex.Message}");
            return -1;
        }
    }

    /// <summary>窗口出生位置的层叠序号：连开几个程序不至于完全叠死</summary>
    private int _spawnCascade;

    /// <summary>
    /// 用 CreateProcess + STARTF_USEPOSITION 把新进程的**初始窗口位置**直接定在虚拟外屏。
    /// 失败（提权程序 ERROR_ELEVATION_REQUIRED、非可执行文件…）返回 -1，调用方回落 ShellExecute 老路径。
    /// </summary>
    private int StartOnVirtualScreen(string exePath, string? arguments, out bool positioned)
    {
        positioned = false;
        var target = _captureTarget;
        if (target == null) return -1;
        try
        {
            int x = target.Left + 24 + (_spawnCascade++ % 8) * 32;
            int y = target.Top + 24 + (_spawnCascade % 8) * 24;
            var si = new NativeApi.STARTUPINFO
            {
                dwFlags = (int)(NativeApi.STARTF_USEPOSITION | NativeApi.STARTF_USESHOWWINDOW),
                wShowWindow = NativeApi.SW_SHOWNORMAL,
                dwX = x,
                dwY = y,
            };
            si.cb = Marshal.SizeOf<NativeApi.STARTUPINFO>();
            var cmd = new StringBuilder("\"" + exePath + "\""
                + (string.IsNullOrWhiteSpace(arguments) ? "" : " " + arguments));
            string? dir = Path.GetDirectoryName(exePath);
            if (NativeApi.CreateProcessW(exePath, cmd, IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero,
                    string.IsNullOrEmpty(dir) ? null : dir, ref si, out var pi))
            {
                NativeApi.CloseHandle(pi.hProcess);
                NativeApi.CloseHandle(pi.hThread);
                positioned = true;
                return (int)pi.dwProcessId;
            }
            _log.Info($"带初始位置启动未成（Win32Error={Marshal.GetLastWin32Error()}），回落标准启动：{Path.GetFileName(exePath)}");
            return -1;
        }
        catch (Exception ex)
        {
            _log.Debug($"带初始位置启动异常（回落标准启动）：{ex.Message}");
            return -1;
        }
    }

    // ---------------------------------------------------------------- 显示即捕获（EVENT_OBJECT_SHOW）

    /// <summary>WinEvent 回调委托。必须是字段保活的强引用：委托被 GC 回收后本机回调会崩掉</summary>
    private NativeApi.WinEventProc? _showCatchProc;
    /// <summary>钩子/旋转线程已处理过的窗口。锁保护：钩子线程和热旋转线程并发调用 TryCatchWindow</summary>
    private readonly HashSet<IntPtr> _caught = new();
    private readonly object _caughtLock = new();
    private readonly object _catchGate = new();
    private long _catchUntil;
    private int _catchPid = -1;
    private string _catchName = "";
    private HashSet<int> _catchPidsAtArm = new();
    private Thread? _catchThread;

    /// <summary>
    /// 武装"一显示就搬走"的捕获器：SetWinEventHook(EVENT_OBJECT_SHOW)。
    /// 客户反馈"主控端右键打开软件，窗口先在主屏闪一下才到副屏，很打扰主屏工作"——
    /// 老做法是 200ms 轮询，主屏会看见新窗口闪 0.2~1 秒；钩子在窗口显示的**那一刻**（第一帧
    /// 被画出来之前）就把它挪到外屏，闪烁收敛到肉眼基本不可见。
    /// 监听**全部**进程（不只启动的那个 pid）：单实例程序（微信/Edge 二次启动）的新窗口
    /// 属于**早已在跑**的进程，按 pid 过滤会漏。
    /// 只武装一段时间（远程每次"打开"续 25 秒），平时不挂钩子。
    /// </summary>
    private void ArmShowCatcher(int pid, string processName)
    {
        if (_captureTarget == null) return;
        // 先拍基线（此刻已存在的窗口一律不属于"这次打开带出来的"—— 快扫和按名找窗都靠它防误抢），
        // 再进锁续期。钩子是常驻的（见 CatchLoop），续期即刻生效；进程快照慢一点没关系。
        SnapshotBaseline();
        lock (_catchGate)
        {
            // 60 秒（原 25 秒）：启动慢的程序（游戏/IDE 30 秒后才建主窗口）出来时钩子还在岗，
            // 否则主窗口落在主屏没人搬（2026-10-09 客户反馈"打开软件闪/跑到主屏"的一部分）
            _catchUntil = Environment.TickCount64 + 60_000;
            _armTick = Environment.TickCount64;   // "打开指令刚发生"的锚点：头几秒内的同名窗口视为确定认领
            Interlocked.Exchange(ref _prePosLogCount, 0);
            _catchPid = pid;
            _catchName = processName ?? "";
            try { _catchPidsAtArm = Process.GetProcesses().Select(pr => pr.Id).ToHashSet(); }
            catch { _catchPidsAtArm = new HashSet<int>(); }
            if (_catchThread == null || !_catchThread.IsAlive)
            {
                _showCatchProc = OnWindowShown;
                _catchThread = new Thread(CatchLoop)
                {
                    IsBackground = true,
                    Name = "show-catcher",
                    Priority = ThreadPriority.Highest,   // 带外 WinEvent 走消息队列，忙机器上排队延迟直接决定主屏闪几帧
                };
                _catchThread.Start();
            }
        }
        // 热旋转：**专用线程 + 1ms 间隔**，跑 2 秒 —— 独立于 WinEvent 钩子（钩子靠异步消息，
        // 投递延迟 10-30ms 偶尔跑不赢 DWM 的 16.7ms 帧边界，主屏就会看到 1 帧）。
        //
        // 【为什么不能用 Task.Delay(3)】Windows 计时器默认精度 15.6ms —— Task.Delay(3) 实际等 ~15ms，
        // 窗口在这个空档里已经在主屏画了 3~4 帧（v1.2.5 真机帧级实测踩到）。
        // 正确做法：专用线程 + timeBeginPeriod(1) 提高系统计时器精度 + Thread.Sleep(1)。
        // 1ms 旋转 + DWM cloak 保证在合成器**下一帧之前**把窗口隐身 → 主屏零帧。
        _ = Task.Run(() =>
        {
            try
            {
                NativeApi.timeBeginPeriod(1);   // 系统计时器精度提到 1ms（离开时恢复）
                long spinUntil = Environment.TickCount64 + 2000;
                var baseline = TakeBaselineSnapshot();
                while (Environment.TickCount64 < spinUntil &&
                       Environment.TickCount64 < Interlocked.Read(ref _catchUntil))
                {
                    try { FastCatchSweepWithBaseline(baseline); }
                    catch { }
                    Thread.Sleep(1);            // timeBeginPeriod(1) 下 ≈1ms（不用 Task.Delay，那个是 ~15ms）
                }
            }
            finally
            {
                try { NativeApi.timeEndPeriod(1); } catch { }
            }
        });
    }

    /// <summary>热旋转用的快扫（带**打开时的基线**而不是实时基线——热旋转期间基线不该被别处刷新）。
    /// 调 PrePositionNewWindow 而不是 TryCatchWindow：它同时处理**隐藏窗口**（CreateWindow→ShowWindow
    /// 间隙的窗口，IsWindowVisible=false——预定位到外屏，SHOW 时直接在外屏出现）和**已显示窗口**
    /// （fallthrough 到 cloak+hide+搬走）。v1.2.6 只查可见窗口，主窗口在创建→显示的间隙被漏掉。 </summary>
    private void FastCatchSweepWithBaseline(HashSet<IntPtr> baseline)
    {
        try
        {
            if (baseline.Count == 0) return;
            NativeApi.EnumWindows((h, _) =>
            {
                if (!baseline.Contains(h)) PrePositionNewWindow(h);
                return true;
            }, IntPtr.Zero);
        }
        catch { }
    }

    /// <summary>
    /// 钩子线程：**常驻**安装 EVENT_OBJECT_SHOW 钩子并泵消息（带外回调靠消息循环触发）；
    /// 武装期内每 60ms 额外做一轮全窗口快扫（FastCatchSweep）。
    ///
    /// 为什么改成常驻、不再"到期卸钩 → 下次再装"：explorer 这类由**常驻进程代开窗口**的程序，
    /// 从启动到窗口 SHOW 只有 ~100ms，而重新装钩要经过 500ms 空闲轮询 + 进程快照（几十 ms）的延迟，
    /// 钩子还没装好窗口已经在主屏闪过了（客户反馈"『此电脑』在主屏闪一下才到副屏"就是这个竞态）。
    /// 常驻钩平时在回调里按捕获期（_catchUntil）判定，未武装时零动作，开销可忽略。
    /// </summary>
    private void CatchLoop()
    {
        // Peek 一下强制创建本线程的消息队列（SetWinEventHook 的注册线程必须有队列）
        NativeApi.PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
        IntPtr hook = IntPtr.Zero;
        try
        {
            // 钩 CREATE + SHOW 两个事件（0x8001~0x8002 恰好相邻，作为区间一次挂上）：
            //  · CREATE：窗口刚创建、**还没显示** —— 趁它看不见先挪到外屏（隐藏窗口的挪动不可见，
            //    显示时直接就在外屏，主屏零动作 —— 这是"零闪烁"的关键一步，见 PrePositionNewWindow）；
            //  · SHOW：显示那一刻的兜底（预定位被应用自己覆盖/漏掉时，第一帧前后搬走）。
            hook = NativeApi.SetWinEventHook(NativeApi.EVENT_OBJECT_CREATE, NativeApi.EVENT_OBJECT_SHOW,
                IntPtr.Zero, _showCatchProc!, 0, 0, NativeApi.WINEVENT_OUTOFCONTEXT);
            if (hook == IntPtr.Zero)
                _log.Warn("SetWinEventHook 失败：退回快扫 + 轮询搬窗（主屏可能闪一下）");
            while (true)
            {
                bool armed = Environment.TickCount64 < Interlocked.Read(ref _catchUntil);
                // 消息泵的等待超时只做兜底——消息到达时 MsgWait **立即**唤醒，不等超时
                NativeApi.MsgWaitForMultipleObjects(0, IntPtr.Zero, false, (uint)(armed ? 60 : 500), NativeApi.QS_ALLINPUT);
                while (NativeApi.PeekMessage(out _, IntPtr.Zero, 0, 0, NativeApi.PM_REMOVE)) { }
                if (armed) FastCatchSweep();
            }
        }
        finally
        {
            if (hook != IntPtr.Zero) NativeApi.UnhookWinEvent(hook);
        }
    }

    /// <summary>武装期快扫：枚举顶层窗口，与钩子回调同一套判据（TryCatchWindow）。
    /// 与钩子的关键差别：快扫看到的是**全部**窗口（含本机用户早已开着的），
    /// 所以必须再用**基线快照**滤一道 —— 只碰"这次打开指令之后才出现的窗口"，
    /// 否则按"同名进程"认领会把用户开着的资源管理器/微信整窝端走。</summary>
    private void FastCatchSweep()
    {
        try
        {
            var baseline = TakeBaselineSnapshot();
            if (baseline.Count == 0) return;   // 还没有基线（理论到不了这，武装时刚拍过）：宁可不抓也不误抢
            NativeApi.EnumWindows((h, _) =>
            {
                if (!baseline.Contains(h)) TryCatchWindow(h);
                return true;
            }, IntPtr.Zero);
        }
        catch { }
    }

    /// <summary>WinEvent 回调（只在钩子线程上触发）：CREATE → 预定位（趁窗口还没显示挪到外屏）；SHOW → 常规抓取 + 反向抓取；其它（DESTROY 等）忽略</summary>
    private void OnWindowShown(IntPtr hook, uint eventCode, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            if (hwnd == IntPtr.Zero || idObject != NativeApi.OBJID_WINDOW) return;   // 只要窗口本身，不要子控件
            if (eventCode == NativeApi.EVENT_OBJECT_CREATE)
                PrePositionNewWindow(hwnd);
            else if (eventCode == NativeApi.EVENT_OBJECT_SHOW)
            {
                TryCatchWindow(hwnd);
                ReversePullToPrimaryIfLocalUser(hwnd);
            }
        }
        catch { }
    }

    /// <summary>
    /// 反向抓取（2026-10-05，客户反馈"副屏关过软件后，主屏开的软件跑到副屏不回来"）：
    /// Windows 会记住每个程序**上次关闭时所在的屏**——软件在副屏被关过，本机用户下次从主屏
    /// （开始菜单/任务栏/双击）打开它时，窗口直接出生在副屏。而本机用户根本看不见副屏，
    /// 表现就是"打开没反应/窗口失踪"。
    /// 处置：常驻 SHOW 监听（不依赖武装期）——新窗口显示时若**整块落在虚拟外屏**、且最近 15 秒
    /// 只有本机用户动过键鼠（远端注入不计入物理时间戳）、且不在远端"打开"的武装期内（那多半是
    /// 远端自己开的，别去抢），就把它搬回**物理主屏**。
    /// </summary>
    private void ReversePullToPrimaryIfLocalUser(IntPtr hwnd)
    {
        try
        {
            var virt = VirtualScreenProvider?.Invoke();
            if (virt is not { IsVirtual: true }) return;                            // 没开虚拟外屏：无"副屏"可言
            if (Environment.TickCount64 < Interlocked.Read(ref _catchUntil)) return;  // 远端刚发起过打开：那窗口多半是远端的
            if (!NativeApi.IsWindow(hwnd) || !NativeApi.IsWindowVisible(hwnd)) return;
            if (NativeApi.GetAncestor(hwnd, NativeApi.GA_ROOT) != hwnd) return;
            if (IsShellWindow(hwnd)) return;
            // 【2026-10-10 右键菜单跑主屏】菜单/下拉永远不反向搬：出现在外屏的菜单必是远端
            // 在外屏点出来的（本机用户的物理点击被仲裁器留在物理屏）。以前误用"最近 15 秒本机
            // 动过键鼠"判断 —— 机器上装有向日葵/GameViewer 等会剥掉注入位的钩子链时，远端的
            // 右键被记成"本机输入"，菜单刚在外屏弹出就被搬去主屏（现场：副屏右键菜单跑主屏）。
            if (IsTransientPopup(hwnd)) return;
            var cls = NativeApi.GetClassName(hwnd);
            bool console = cls.Contains("ConsoleWindow") || cls.Contains("CASCADIA_HOSTING");
            if (!console && !IsSaneWindow(hwnd)) return;
            if (!NativeApi.GetWindowRect(hwnd, out var r)) return;
            int w = r.Right - r.Left, h = r.Bottom - r.Top;
            if (w < 300 || h < 200) return;                                         // 浮条/小控件不算
            // 必须整块落在虚拟外屏里（跨屏的不动——可能是远端正在拖拽）
            if (!(r.Left >= virt.Left && r.Right <= virt.Right && r.Top >= virt.Top && r.Bottom <= virt.Bottom)) return;

            // 最近 15 秒内本机用户动过键鼠（而他看不见副屏）→ 这个窗口多半是他开的、却去了他看不见的地方
            if (!LocalUserTouchedRecently(15_000)) return;

            NativeApi.GetWindowThreadProcessId(hwnd, out int pid);
            if (pid == 0 || pid == Environment.ProcessId) return;

            var primary = DisplayHelper.GetMonitors().FirstOrDefault(m => m.IsPrimary);
            if (primary == null) return;
            if (NativeApi.IsZoomed(hwnd)) NativeApi.ShowWindow(hwnd, NativeApi.SW_RESTORE);
            int x = primary.Left + Math.Max(0, (primary.Width - Math.Min(w, primary.Width)) / 2);
            int y = primary.Top + Math.Max(0, (primary.Height - Math.Min(h, primary.Height)) / 2);
            if (NativeApi.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0,
                    NativeApi.SWP_NOSIZE | NativeApi.SWP_NOZORDER | NativeApi.SWP_NOACTIVATE))
            {
                long now = Environment.TickCount64;
                if (_reversePullLog == 0 || now - _reversePullLog > 3000)
                {
                    _reversePullLog = now;
                    _log.Info($"本机用户刚开的窗口落在了外屏（{ProcessNameOf(pid)} {w}x{h}）→ 已搬回主屏 ({x},{y})");
                }
            }
        }
        catch { }
    }

    /// <summary>虚拟外屏信息提供者（由 WorkerContext 注入：副屏判定跟随采集拓扑，而非仅捕获目标）</summary>
    public Func<DisplayInfo?>? VirtualScreenProvider;
    private long _reversePullLog;
    /// <summary>菜单搬回外屏的日志计数（限流用）</summary>
    private long _menuSweepLog;

    /// <summary>
    /// CREATE 事件的**预定位**（零闪烁的关键）：窗口刚创建、还没显示 —— explorer 这类程序创建窗口时
    /// 带着它记住的上次位置（在主屏），等 SHOW 再搬主屏已经闪过一帧了（客户视频里"还是要闪一下"）。
    /// 在它显示之前就挪到外屏：隐藏窗口的挪动在屏幕上没有任何表现，显示出来就直接在副屏。
    /// 程序若在显示时又自己摆位置，SHOW 钩子/快扫会再抓一次（退化为 1 帧，不会更糟）。
    /// 只碰"像主窗口"的（有标题栏、非工具窗），隐藏的辅助小窗一个不动。
    /// </summary>
    private void PrePositionNewWindow(IntPtr hwnd)
    {
        try
        {
            var target = _captureTarget;
            if (target == null) return;
            // 注意：不做武装期检查 —— 热旋转线程以**打开时刻的基线**过滤，基线里没有的窗口就是
            // 这次打开带出来的，不需要再验证武装期（避免武装期刚过期时漏掉最后几个窗口）
            if (!NativeApi.IsWindow(hwnd)) return;
            if (NativeApi.IsWindowVisible(hwnd))
            {
                // CREATE 事件迟到（投递是异步的）：窗口其实已经显示了 —— 不放弃，直接按 SHOW 路径
                // cloak 捕获（否则要再等 SHOW 事件，多闪几帧）
                TryCatchWindow(hwnd);
                return;
            }
            if (NativeApi.GetAncestor(hwnd, NativeApi.GA_ROOT) != hwnd) return;         // 只看顶层窗口
            if (IsShellWindow(hwnd)) return;

            // 只预定位"像主窗口"的：带标题栏且不是工具窗/无边框弹泡 —— 隐藏的辅助窗口
            // （托盘窗/消息中转窗等）位置可能被程序依赖，不动它们
            int style = NativeApi.GetWindowStyle(hwnd);
            const int WS_CAPTION = 0x00C00000;
            const int WS_TOOLWINDOW = 0x00000080;
            if ((style & WS_CAPTION) == 0 || (style & WS_TOOLWINDOW) != 0) return;

            NativeApi.GetWindowThreadProcessId(hwnd, out int pid);
            if (pid == 0 || pid == Environment.ProcessId) return;
            bool oursByPid, oursByName, oursByNewPid;
            lock (_catchGate)
            {
                oursByPid = pid == _catchPid;
                oursByName = !string.IsNullOrEmpty(_catchName)
                             && string.Equals(ProcessNameOf(pid), _catchName, StringComparison.OrdinalIgnoreCase);
                oursByNewPid = _catchPidsAtArm.Count == 0 || !_catchPidsAtArm.Contains(pid);
            }
            if (!(oursByPid || oursByName || oursByNewPid)) return;

            // 分级闸门（与 TryCatchWindow 同一套：①精确认领不设防 / ②同名只防正在动 / ③猜测全套）。
            // 注意预定位是**隐藏窗口**的搬动，本机毫无视觉影响，但仍要防"把他将要看的东西悄悄挪走"。
            var arb = Arbiter;
            bool justOpened = Environment.TickCount64 - _armTick < 4000;   // 打开指令后头 4 秒：同名窗口与指令强相关，按确定认领处理
            if (!oursByPid && !justOpened && arb != null && arb.LocalActiveRecently) return;
            // 【修复 2026-10-07】justOpened 也要豁免第③级（新进程）：cmd.exe 的窗口属于 conhost（不同进程不同名），
            // 之前 4 秒豁免只覆盖同名（第②级），新进程（第③级）仍被拦 → cmd 打开仍闪（客户反馈）
            if (!oursByPid && !oursByName && !justOpened && LocalUserTouchedRecently()) return;

            if (!NativeApi.GetWindowRect(hwnd, out var r)) return;
            bool onVirtual = r.Left >= target.Left && r.Left < target.Right &&
                             r.Top >= target.Top && r.Top < target.Bottom;
            if (onVirtual) return;   // 创建位置本来就在外屏：不用管

            if (NativeApi.SetWindowPos(hwnd, IntPtr.Zero, target.Left + 24, target.Top + 24, 0, 0,
                    NativeApi.SWP_NOSIZE | NativeApi.SWP_NOZORDER | NativeApi.SWP_NOACTIVATE))
            {
                // explorer 一次打开会连开十来个隐藏辅助窗，全都预定位——日志只记第一条防刷屏
                long n = Interlocked.Increment(ref _prePosLogCount);
                if (n <= 2 || n % 20 == 0)
                    _log.Info($"新窗口显示前已预定位到外屏（{ProcessNameOf(pid)}，创建于主屏 ({r.Left},{r.Top})，本次第 {n} 个）");
            }
        }
        catch { }
    }

    /// <summary>
    /// 共用的"抓走它"判据（钩子回调与武装期快扫同走这一条路，保证行为一致）：
    /// 是这次远程打开带出来的、正落在物理屏 → 立刻搬往外屏。
    /// </summary>
    private void TryCatchWindow(IntPtr hwnd)
    {
        try
        {
            var target = _captureTarget;
            if (target == null) return;
            if (Environment.TickCount64 >= Interlocked.Read(ref _catchUntil)) return; // 未武装/已过期就不再动手
            if (!NativeApi.IsWindow(hwnd) || !NativeApi.IsWindowVisible(hwnd)) return;
            if (NativeApi.GetAncestor(hwnd, NativeApi.GA_ROOT) != hwnd) return;      // 只看顶层窗口
            lock (_caughtLock) { if (_caught.Contains(hwnd)) return; }

            // 桌面/任务栏等外壳窗口一票否决（explorer 的桌面窗口尺寸"正常"，混在它的窗口里）
            if (IsShellWindow(hwnd)) return;
            var cls = NativeApi.GetClassName(hwnd);
            // 控制台窗口（cmd/powershell，含 Windows Terminal 宿主）尺寸判据放行，与 Sweep 一致
            bool console = cls.Contains("ConsoleWindow") || cls.Contains("CASCADIA_HOSTING");
            if (!console && !IsSaneWindow(hwnd)) return;
            if (!NativeApi.GetWindowRect(hwnd, out var r)) return;
            if (r.Right - r.Left < 80 || r.Bottom - r.Top < 40) return;
            bool onVirtual = r.Left >= target.Left && r.Left < target.Right &&
                             r.Top >= target.Top && r.Top < target.Bottom;
            if (onVirtual)
            {
                // 已经在外屏（预定位/STARTUPINFO 生效的多数情况）：记账，之后不再反复处理它
                lock (_caughtLock) { if (_caught.Count > 400) _caught.Clear(); _caught.Add(hwnd); }
                return;
            }

            NativeApi.GetWindowThreadProcessId(hwnd, out int pid);
            if (pid == 0 || pid == Environment.ProcessId) return;

            // 三级认领（认得越准，闸门越松）：
            //  ① 就是刚启动的那个 pid —— 远程**明确**打开的，毫无疑问；
            //  ② 同名老进程接管的窗口（单实例程序 / "此电脑"→explorer）—— 很可能是远程的；
            //  ③ 武装之后才新出现的进程 —— 纯猜测，最可能是本机用户自己开的。
            bool oursByPid, oursByName, oursByNewPid;
            lock (_catchGate)
            {
                oursByPid = pid == _catchPid;
                oursByName = !string.IsNullOrEmpty(_catchName)
                             && string.Equals(ProcessNameOf(pid), _catchName, StringComparison.OrdinalIgnoreCase);
                oursByNewPid = _catchPidsAtArm.Count == 0 || !_catchPidsAtArm.Contains(pid);
            }
            if (!(oursByPid || oursByName || oursByNewPid)) return;

            // 分级闸门（现场教训两头都踩过：不设防→主屏开 Telegram 被搬走；一刀切→远程开的
            // 「此电脑」因为旁边有人动过鼠标也被扣在主屏）：
            //  ① 精确认领 → 不设防：远程打开的东西必须去外屏；
            //  ② 同名认领 → 只防"正在动键鼠"（本机用户双击开同名程序，窗口一般 1.2s 内出来）；
            //  ③ 猜测认领 → 全套闸门（1.2s + 12 秒内动过即拒）。
            var arb = Arbiter;
            bool justOpened = Environment.TickCount64 - _armTick < 4000;   // 打开指令后头 4 秒：同名窗口与指令强相关，按确定认领处理
            if (!oursByPid && !justOpened && arb != null && arb.LocalActiveRecently) return;
            // 【修复 2026-10-07】justOpened 也要豁免第③级（新进程）：cmd.exe 的窗口属于 conhost（不同进程不同名），
            // 之前 4 秒豁免只覆盖同名（第②级），新进程（第③级）仍被拦 → cmd 打开仍闪（客户反馈）
            if (!oursByPid && !oursByName && !justOpened && LocalUserTouchedRecently()) return;

            // 同名/猜测认领的尺寸护栏：只搬"像正常应用窗口"的（排除浮条和覆盖层）。
            // 【修复 2026-10-07】去掉"不超过主屏九成"的宽度限制——那个护栏把宽窗口（如浏览器）
            // 拦住了不搬 → 窗口跨在两屏中间（客户反馈"一半在主控端一半在被控端"）。
            // 桌面/任务栏已由 IsShellWindow 过滤，不需要宽度限制。
            if (!oursByPid)
            {
                int w = r.Right - r.Left, h = r.Bottom - r.Top;
                if (w < 300 || h < 200) return;   // 只排除小浮条
            }

            lock (_caughtLock) { if (_caught.Count > 400) _caught.Clear(); _caught.Add(hwnd); }
            string name = ProcessNameOf(pid);
            _log.Info($"新窗口刚显示就捕获（{name}，类名 {cls}，主屏 ({r.Left},{r.Top}) {r.Right - r.Left}x{r.Bottom - r.Top}，认领={(oursByPid ? "pid" : oursByName ? "同名" : "新进程")}）→ 即刻搬往外屏");
            // 双保险隐身：SW_HIDE 从屏幕上彻底移除（不依赖 DWMWA_CLOAK 是否生效）+ DWM cloak。
            // 搬完在副屏恢复显示。v1.2.5 真机帧级实测：仅靠 cloak 仍有 2 帧可见（cloak 可能被系统忽略）。
            NativeApi.ShowWindow(hwnd, NativeApi.SW_HIDE);
            NativeApi.CloakWindow(hwnd);
            try { MoveToTargetCore(hwnd, name, force: true); }
            finally
            {
                NativeApi.UncloakWindow(hwnd);
                NativeApi.ShowWindow(hwnd, NativeApi.SW_SHOWNOACTIVATE);  // 在副屏恢复显示（不抢焦点）
                // 【修复 2026-10-08】恢复显示后必须**强制激活**：SHOWNOACTIVATE 出来的窗口
                // 前台归它、键盘焦点却没人认领 —— WinUI 应用（新版记事本等）不走激活流程就不给
                // 编辑框设焦点，SendInput 的键全落空（现场：远端打字/复制粘贴全部无效）。
                ForceActivateWindow(hwnd);
            }
        }
        catch { }
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
                //    按进程名找窗口，这样也照样能把它摆到副屏（同名认领：只防"正在动键鼠"，
                //    与捕获链的分级闸门一致；基线过滤仍生效 —— 只认新出现的窗口）。
                if (p.HasExited || i % 5 == 0)
                {
                    var arb = Arbiter;
                    bool justOpened = Environment.TickCount64 - _armTick < 4000;
                    if (!justOpened && arb != null && arb.LocalActiveRecently)
                    {
                        if (_lastByNameSkipLog == 0 || Environment.TickCount64 - _lastByNameSkipLog > 5000)
                        {
                            _lastByNameSkipLog = Environment.TickCount64;
                            _log.Info("本机用户正在操作：按进程名找窗的兜底本轮跳过（不抢他刚开的窗口）");
                        }
                    }
                    else
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

    /// <summary>枚举某个进程的可见顶层窗口（排除退化小窗与桌面/任务栏等外壳窗口 —— explorer 的桌面窗口就混在它的窗口里）</summary>
    private static List<IntPtr> FindWindowsByPid(int pid)
    {
        var list = new List<IntPtr>();
        if (pid <= 0) return list;
        try
        {
            NativeApi.EnumWindows((h, _) =>
            {
                NativeApi.GetWindowThreadProcessId(h, out int wpid);
                if (wpid == pid && NativeApi.IsWindowVisible(h) && !IsShellWindow(h) && IsSaneWindow(h)) list.Add(h);
                return true;
            }, IntPtr.Zero);
        }
        catch { }
        return list;
    }

    /// <summary>按进程名枚举可见顶层应用窗口（单实例应用：启动进程立刻退出时用）。
    /// 只认"基线快照之后才出现的"窗口（基线没拍过就不过滤）—— explorer 这类常驻程序
    /// 按名字能找到本机用户早已开着的十几个窗口，不过滤就会把别人的窗口搬走。
    /// 外壳窗口（桌面/任务栏，同属 explorer 进程）一律排除。</summary>
    private List<IntPtr> FindWindowsByName(string procName)
    {
        var list = new List<IntPtr>();
        var baseline = TakeBaselineSnapshot();
        try
        {
            NativeApi.EnumWindows((h, _) =>
            {
                if (!NativeApi.IsWindowVisible(h) || !IsSaneWindow(h) || IsShellWindow(h)) return true;
                if (baseline.Count > 0 && baseline.Contains(h)) return true;   // 老窗口：不是这次打开带出来的
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
    /// 本机用户最近 windowMs 毫秒内是否有过**任何物理输入**（鼠标/键盘，远端注入不算）。
    /// 用于所有"把新窗口搬到外屏"的路径：本机用户刚动过（哪怕已停手几秒 —— 双击开程序到窗口
    /// 真正显示就是这种"动一下、等几秒"的形态）→ 新窗口多半是他开的，一个不碰。
    ///
    /// 【为什么不比较"最近输入是本机还是远端"】上一版试过（v1.1.2）：远端用户看画面时鼠标
    /// 一直在动，远端输入时间戳被持续刷新、几乎永远比本机新，判据形同虚设 —— 客户视频里
    /// "主屏开任何软件都被传到副屏"就是这么漏的。改成只看本机：本机 12 秒内动过就拒绝，
    /// 远端在不在动无关紧要（远端要搬的东西本来就只在副屏内部，不需要跟本机比先后）。
    /// </summary>
    private bool LocalUserTouchedRecently(int windowMs = 12_000)
    {
        var arb = Arbiter;
        if (arb == null) return false;
        return Environment.TickCount64 - arb.LastPhysicalInputTick < windowMs;
    }

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
        // 外壳窗口（桌面/任务栏）无条件拒绝：它们跟着 explorer 进程混进各种"找窗口"的路径，
        // 被挪走 = 用户的整个桌面/任务栏消失。这是所有搬移动作的最后一道闸。
        if (IsShellWindow(hwnd))
        {
            _log.Warn($"拒绝搬动外壳窗口 {hwnd}（桌面/任务栏）：{name}");
            return;
        }
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

    private IntPtr _imeCheckedWindow;
    private long _imeCheckedTick;

    /// <summary>
    /// 【修复 2026-10-08】打字前确保**当前前台窗口**输入法是英文直通（同一窗口 3 秒节流）。
    /// 场景：真实光标模式下远端点进一个已在前的窗口（浏览器/早就开着的应用）——
    /// ClaimedWindow 只在后台注入路径设置，这类窗口不会走 ForceActivateWindow，
    /// 输入法停留在中文模式，字母全部进拼音组字框（现场：打一个字后就不动了）。
    /// </summary>
    public void EnsureImeEnglishForForeground()
    {
        try
        {
            var fg = NativeApi.GetForegroundWindow();
            if (fg == IntPtr.Zero) return;
            long now = Environment.TickCount64;
            if (fg == _imeCheckedWindow && now - _imeCheckedTick < 3000) return;
            _imeCheckedWindow = fg;
            _imeCheckedTick = now;
            NormalizeImeToEnglish(fg);
        }
        catch { }
    }

    /// <summary>
    /// 把窗口的输入法切到"英文直通"（微软拼音的"英"状态）。
    /// 中文模式下输入法会把合成注入的字母全部吃进拼音组字框（远端打字发滞、出现莫名汉字），
    /// 并拦截 Ctrl 组合键（Ctrl+C/V/A 全失效）。远端认领的窗口一律英文直通；
    /// 要输中文：主控端本地打好字 → 剪贴板同步 → Ctrl+V 粘贴。
    /// 输入法模式是按窗口输入上下文存的，只影响这个窗口，不动本机用户自己的窗口。
    /// </summary>
    public void NormalizeImeToEnglish(IntPtr hwnd)
    {
        try
        {
            var ime = NativeApi.ImmGetDefaultIMEWnd(hwnd);
            if (ime == IntPtr.Zero) return;
            // 【加固 2026-10-08】裸 SendMessage 是跨进程同步调用：目标 IME 窗口忙/挂时会把
            // 按键热路径整个拖住（现场"打字一个字一个字往外蹦"的嫌疑路径）。改 60ms 超时版。
            var mode = NativeApi.SendMessageTimeoutW(ime, NativeApi.WM_IME_CONTROL,
                (IntPtr)NativeApi.IMC_GETCONVERSIONMODE, IntPtr.Zero,
                NativeApi.SMTO_ABORTIFHUNG, 60, out _);
            if (((int)mode.ToInt64() & NativeApi.IME_CMODE_NATIVE) != 0)
            {
                NativeApi.SendMessageTimeoutW(ime, NativeApi.WM_IME_CONTROL,
                    (IntPtr)NativeApi.IMC_SETCONVERSIONMODE, IntPtr.Zero,
                    NativeApi.SMTO_ABORTIFHUNG, 60, out _);   // 清掉 NATIVE 位 → 英文直通
                _log.Info($"已把窗口 {hwnd} 的输入法切到英文直通（原模式 {mode}）：远端字母不再进组字框，Ctrl 组合键恢复");
            }
        }
        catch (Exception ex)
        {
            _log.Debug($"切换输入法模式失败（不影响其余功能）：{ex.Message}");
        }
    }

    /// <summary>
    /// 强制激活窗口并把**键盘焦点**真正交给它 —— 即使 GetForegroundWindow 已经是它。
    /// RobustSetForeground 在"已是前台"时会提前返回，但 SHOWNOACTIVATE 出来的窗口
    /// 可能前台归它、焦点却没人认领（WinUI 应用不走激活流程就不给编辑框 SetFocus，
    /// SendInput 的键全部落空）。这里把本线程附加到目标窗口线程的输入队列上，
    /// SetActiveWindow + SetFocus 走完整的激活链。
    /// </summary>
    public void ForceActivateWindow(IntPtr hwnd)
    {
        try
        {
            var arb = Arbiter;
            if (arb != null && arb.LocalActiveRecently) return;   // 本机用户在操作时不抢焦点
            if (NativeApi.IsIconic(hwnd)) NativeApi.ShowWindow(hwnd, NativeApi.SW_RESTORE);
            uint targetThread = NativeApi.GetWindowThreadProcessId(hwnd, out _);
            uint myThread = NativeApi.GetCurrentThreadId();
            bool attached = targetThread != 0 && targetThread != myThread
                            && NativeApi.AttachThreadInput(myThread, targetThread, true);
            try
            {
                NativeApi.BringWindowToTop(hwnd);
                NativeApi.SetForegroundWindow(hwnd);
                NativeApi.SetActiveWindow(hwnd);
                NativeApi.SetFocus(hwnd);
                NormalizeImeToEnglish(hwnd);
                _log.Info($"已强制激活搬到外屏的窗口 {hwnd}（前台={NativeApi.GetForegroundWindow()} 焦点给到它）");
            }
            finally
            {
                if (attached) NativeApi.AttachThreadInput(myThread, targetThread, false);
            }
        }
        catch (Exception ex)
        {
            _log.Debug($"强制激活失败：{ex.Message}");
        }
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
            if (now == hwnd) NormalizeImeToEnglish(hwnd);
            _log.Info($"已尝试把窗口 {hwnd} 置为前台（当前前台={now}，{(now == hwnd ? "成功" : "未生效")}）");
        }
        catch (Exception ex)
        {
            _log.Debug($"置前台失败：{ex.Message}");
        }
    }
}
