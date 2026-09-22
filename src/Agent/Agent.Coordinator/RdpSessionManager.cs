using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using Agent.Common;
using Microsoft.Win32;

namespace Agent.Coordinator;

/// <summary>
/// RDP 回环会话管理（策划 §5）：
/// - RDPWrap 安装/工作检测
/// - 确保 RDP 服务与防火墙就绪、RemoteWorker 用户存在
/// - cmdkey 存凭据 → mstsc /v:127.0.0.2 建立并发会话 → 删除凭据
/// - 会话就绪后由 SessionLauncher 在其中启动 Worker
/// </summary>
public sealed class RdpSessionManager
{
    private readonly AgentConfig _cfg;
    private readonly Logger _log;
    private Process? _mstsc;

    public RdpSessionManager(AgentConfig cfg, Logger log)
    {
        _cfg = cfg;
        _log = log;
    }

    public string LoopbackHost => _cfg.RdpLoopbackHost;
    public string RdpFilePath => Path.Combine(AppContext.BaseDirectory, "config", "session1.rdp");

    /// <summary>当前 Worker 会话 id（-1 = 尚未确定）。看护重建会话后由协调器据此启动 Worker。</summary>
    public int WorkerSessionId { get; private set; } = -1;

    // ---------------------------------------------------------------- 检测

    public bool IsRdpWrapInstalled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\TermService\Parameters");
            var dll = key?.GetValue("ServiceDll")?.ToString() ?? "";
            return dll.Contains("rdpwrap", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _log.Warn($"读取 TermService 注册表失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>端口在监听 = RDPWrap 生效（策划 §5.2）</summary>
    public bool IsRdpWrapWorking()
    {
        try
        {
            using var tcp = new TcpClient();
            var task = tcp.ConnectAsync(LoopbackHost, 3389);
            return task.Wait(2000) && tcp.Connected;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 当前系统是否被 rdpwrap.ini 支持。
    /// 关键：RDPWrap 用的段名是 termsrv.dll 的 VS_FIXEDFILEINFO 版本（如 10.0.26100.8972），
    /// **不是** 系统 UBR（如 26100.9168）——两者经常不同，必须读 DLL 版本。
    /// </summary>
    public (bool Supported, string Detail) GetRdpWrapIniSupport()
    {
        try
        {
            var ini = FindIni();
            string ver = TermsrvVersion() ?? Environment.OSVersion.Version.ToString();
            if (ini == null) return (false, $"未找到 rdpwrap.ini（termsrv 版本 {ver}）");

            var lines = File.ReadAllLines(ini);
            bool exact = lines.Any(l => l.Trim() == $"[{ver}]");
            if (exact) return (true, $"ini 精确支持 termsrv {ver}");

            var siblings = lines
                .Where(l => l.TrimStart().StartsWith("[10.0."))
                .Select(l => l.Trim().Trim('[', ']'))
                .Where(v => !v.Contains('-'))
                .Distinct()
                .Take(2000)
                .ToList();
            var prefix = string.Join('.', ver.Split('.').Take(3)) + ".";
            var same = siblings.Where(v => v.StartsWith(prefix))
                .OrderBy(v => v.Length).ThenBy(v => v).ToList();
            return (false, same.Count > 0
                ? $"ini 无 [{ver}]（系统 UBR {Environment.OSVersion.Version}），同主版本条目：{string.Join(", ", same.Take(10))}"
                : $"ini 无 [{ver}]，且无同主版本条目");
        }
        catch (Exception ex)
        {
            return (false, $"解析 ini 失败：{ex.Message}");
        }
    }

    /// <summary>读取 termsrv.dll 的定长文件版本（RDPWrap 实际使用的段名）</summary>
    public static string? TermsrvVersion()
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "termsrv.dll");
            if (!File.Exists(path)) return null;
            var vi = FileVersionInfo.GetVersionInfo(path);
            if (vi.FileMajorPart == 0) return null;
            return $"{vi.FileMajorPart}.{vi.FileMinorPart}.{vi.FileBuildPart}.{vi.FilePrivatePart}";
        }
        catch { return null; }
    }

    private string? FindIni()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "drivers", "RDPWrap", "rdpwrap.ini"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "drivers", "RDPWrap", "rdpwrap.ini"),
            @"C:\Program Files\RDP Wrapper\rdpwrap.ini",
            @"C:\Program Files\RDP Wrapper\rdpwrap.dll",
        };
        return candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
    }

    // ---------------------------------------------------------------- 前置条件

    /// <summary>开启 RDP（fDenyTSConnections=0）、启动 TermService、放行防火墙</summary>
    public bool EnsureRdpEnabled()
    {
        bool changed = false;
        try
        {
            using (var key = Registry.LocalMachine.OpenSubKey(
                       @"SYSTEM\CurrentControlSet\Control\Terminal Server", writable: true))
            {
                if (key != null)
                {
                    var v = key.GetValue("fDenyTSConnections");
                    if (v == null || Convert.ToInt32(v) != 0)
                    {
                        key.SetValue("fDenyTSConnections", 0, RegistryValueKind.DWord);
                        _log.Info("已开启远程桌面（fDenyTSConnections=0）");
                        changed = true;
                    }
                }
            }

            using (var key = Registry.LocalMachine.OpenSubKey(
                       @"SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp", writable: true))
            {
                if (key != null)
                {
                    var v = key.GetValue("UserAuthentication");
                    if (v != null && Convert.ToInt32(v) != 1)
                    {
                        // NLA：本机回环 + cmdkey 凭据可正常工作
                        key.SetValue("UserAuthentication", 1, RegistryValueKind.DWord);
                        changed = true;
                    }
                }
            }

            StartService("TermService");
            if (changed) Thread.Sleep(1500);
            EnsureFirewallRule();
            return true;
        }
        catch (Exception ex)
        {
            _log.Error("开启远程桌面失败", ex);
            return false;
        }
    }

    private void EnsureFirewallRule()
    {
        try
        {
            var psi = new ProcessStartInfo("netsh.exe",
                "advfirewall firewall show rule name=\"RemoteControl-RDP\"")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using (var p = Process.Start(psi)!)
            {
                p.StandardOutput.ReadToEnd();
                p.WaitForExit(10000);
                if (p.ExitCode == 0) return; // 已存在
            }
            var add = new ProcessStartInfo("netsh.exe",
                "advfirewall firewall add rule name=\"RemoteControl-RDP\" dir=in action=allow protocol=TCP localport=3389")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p2 = Process.Start(add)!;
            p2.WaitForExit(15000);
            _log.Info($"已添加 RDP 防火墙规则（exit={p2.ExitCode}）");
        }
        catch (Exception ex)
        {
            _log.Warn($"添加防火墙规则失败（不影响本机回环）：{ex.Message}");
        }
    }

    private static void StartService(string name)
    {
        try
        {
            var psi = new ProcessStartInfo("sc.exe", $"start {name}")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            p.StandardOutput.ReadToEnd();
            p.WaitForExit(15000);
        }
        catch { }
    }

    /// <summary>确保 RemoteWorker 用户存在且在 Remote Desktop Users 组（策划 §5.4）</summary>
    public bool EnsureWorkerUser(out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(_cfg.WorkerPassword))
        {
            error = "配置里 WorkerPassword 为空，无法创建/使用远程会话用户";
            return false;
        }
        try
        {
            if (!LocalUserExists(_cfg.WorkerUser))
            {
                _log.Info($"创建本地用户 {_cfg.WorkerUser}");
                var (code, outp) = Run("net.exe",
                    $"user \"{_cfg.WorkerUser}\" \"{_cfg.WorkerPassword}\" /add /passwordchg:no /expires:never");
                if (code != 0)
                {
                    error = $"创建用户失败（{code}）：{outp.Trim()}";
                    return false;
                }
            }
            else
            {
                // 已存在：确保密码与配置一致（避免残留旧密码导致 RDP 登录失败）
                Run("net.exe", $"user \"{_cfg.WorkerUser}\" \"{_cfg.WorkerPassword}\"");
            }
            Run("net.exe", $"localgroup \"Remote Desktop Users\" \"{_cfg.WorkerUser}\" /add");
            // 同时加入 Users 组（net user /add 已默认加入，双保险）
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool LocalUserExists(string name)
    {
        try
        {
            var psi = new ProcessStartInfo("net.exe", $"user \"{name}\"")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            p.StandardOutput.ReadToEnd();
            p.WaitForExit(10000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    // ---------------------------------------------------------------- 会话

    /// <summary>找到 RemoteWorker 的活动会话 id；没有返回 null</summary>
    public int? FindWorkerSession()
    {
        var s = SessionLauncher.FindSessionByUser(_cfg.WorkerUser, requireActive: true);
        if (s != null) return s.Id;
        // 有些情况下 WTS 报的状态不是 Active，但会话其实在
        var any = SessionLauncher.FindSessionByUser(_cfg.WorkerUser, requireActive: false);
        return any?.Id;
    }

    /// <summary>会话是否处于活动（Active）状态</summary>
    public bool IsSessionActive(int sessionId)
        => SessionLauncher.EnumerateSessions().Any(s => s.Id == sessionId && s.IsActive);

    /// <summary>mstsc 客户端是否还活着（它一退出，RDP 会话就会变成"已断开"）</summary>
    private bool IsMstscAlive()
    {
        try
        {
            if (_mstsc is { HasExited: false }) return true;
            using var any = Process.GetProcessesByName("mstsc").FirstOrDefault();
            return any != null;
        }
        catch { return false; }
    }

    private static void KillAllMstsc(Logger log)
    {
        foreach (var p in Process.GetProcessesByName("mstsc"))
        {
            try
            {
                p.Kill(true);
                log.Info($"已结束 mstsc pid={p.Id}");
            }
            catch { }
            p.Dispose();
        }
    }

    private static readonly SemaphoreSlim EstablishLock = new(1, 1);

    /// <summary>
    /// 是否正在建立/重建会话（建会话锁被占用，或处于观察窗口内）。
    /// 看护据此区分"正在建"与"真的坏了"，避免把正常的首次登录当成故障上报。
    /// </summary>
    public bool IsEstablishing => EstablishLock.CurrentCount == 0;
    private DateTime _observeSinceUtc = DateTime.MinValue;

    /// <summary>
    /// 确保存在一个**活动的** RemoteWorker 会话。
    ///
    /// 实测经验（很关键，决定了这里的"观察窗口"设计）：
    /// 1) 首次登录要建用户配置文件，40~60s 属于正常，过早重启 mstsc 会导致永远建不成；
    /// 2) 对"已断开"的会话做重连会卡在会话内登录界面（LogonUI）拿不到密码，必须注销后重建；
    /// 3) 会话处于"已断开"时**没有显示表面**，会话内 GDI/WGC 采集全部返回拒绝访问。
    /// </summary>
    public bool EnsureWorkerSession(TimeSpan timeout, out int sessionId, out string error)
    {
        if (!EstablishLock.Wait(TimeSpan.FromSeconds(1)))
        {
            sessionId = -1;
            error = "已有建会话流程在进行中";
            _log.Debug("跳过并发的建会话请求");
            return false;
        }
        try
        {
            return EnsureWorkerSessionCore(timeout, out sessionId, out error);
        }
        finally
        {
            EstablishLock.Release();
        }
    }

    private bool EnsureWorkerSessionCore(TimeSpan timeout, out int sessionId, out string error)
    {
        sessionId = -1;
        error = "";
        int current = SessionLauncher.CurrentSessionId;

        // ---- 情况 A：已有活动会话（且不是当前会话） ----
        var existing = FindWorkerSession();
        if (existing.HasValue && existing.Value != current && IsSessionActive(existing.Value))
        {
            sessionId = existing.Value;
            _observeSinceUtc = DateTime.MinValue;
            if (!IsMstscAlive())
            {
                _log.Warn("已有活动会话但 mstsc 不在运行，补一个客户端以保持会话");
                WriteRdpFile();
                AddCredential();
                StartMstsc();
            }
            _log.Info($"已存在活动的 RemoteWorker 会话：{sessionId}");
            EnsureMstscHidden();
            WorkerSessionId = sessionId;
            return WaitSessionReady(sessionId, TimeSpan.FromSeconds(5));
        }

        if (!IsRdpWrapWorking())
        {
            error = "RDPWrap 未生效：127.0.0.2:3389 未监听（请先跑 install-agent.ps1 安装 RDPWrap 并更新 rdpwrap.ini）";
            _log.Error(error);
            return false;
        }

        if (!EnsureWorkerUser(out var userErr))
        {
            error = userErr;
            _log.Error(error);
            return false;
        }

        // ---- 观察窗口：给它时间自己变好 ----
        if (_observeSinceUtc == DateTime.MinValue) _observeSinceUtc = DateTime.UtcNow;
        double observed = (DateTime.UtcNow - _observeSinceUtc).TotalSeconds;
        bool deadClient = existing.HasValue == false && !IsMstscAlive();
        double grace = existing.HasValue && IsSessionActive(existing.Value) ? 5 : 150;

        if (observed < grace && !deadClient)
        {
            if (existing.HasValue)
                _log.Info($"RemoteWorker 会话 {existing.Value} 正在建立/重连中（已观察 {observed:F0}s，宽限 {grace:F0}s）");
            else
                _log.Info($"正在等待 RDP 会话出现（已观察 {observed:F0}s，宽限 {grace:F0}s）");
            error = "会话建立中";
            return false;
        }

        // ---- 情况 B：会话存在但不活动 → 注销后重建（重连会卡在登录界面） ----
        if (existing.HasValue && existing.Value != current)
        {
            _log.Warn($"会话 {existing.Value} 已断开且超过观察窗口：注销后重建");
            try
            {
                if (!NativeApi.WTSLogoffSession(IntPtr.Zero, existing.Value, true))
                    Run("logoff.exe", existing.Value.ToString());
            }
            catch (Exception ex) { _log.Warn($"注销会话失败：{ex.Message}"); }
            Thread.Sleep(3000);
        }

        // ---- 重新拉起客户端 ----
        KillAllMstsc(_log);
        Thread.Sleep(1500);
        _observeSinceUtc = DateTime.UtcNow;
        WriteRdpFile();
        AddCredential();
        if (!StartMstsc())
        {
            error = "启动 mstsc 失败";
            return false;
        }

        // ---- 等待会话出现并进入活动状态（首次登录 40~60s 很正常） ----
        var sw = Stopwatch.StartNew();
        var effective = timeout < TimeSpan.FromSeconds(150) ? TimeSpan.FromSeconds(150) : timeout;
        while (sw.Elapsed < effective)
        {
            Thread.Sleep(2000);
            // 关键：Windows 11 24H2 起 mstsc 在读完 .rdp 后会弹"远程桌面连接安全警告"
            // （内嵌密码 → 未知发布者），无人值守时它会把连接一直卡在对话框上（实测 180s 超时）。
            // 每 2 秒扫一次，发现 mstsc 的对话框就自动确认。
            DismissBlockingDialogs();
            var s = SessionLauncher.FindSessionByUser(_cfg.WorkerUser, requireActive: true);
            if (s != null && s.Id != current && IsSessionActive(s.Id))
            {
                sessionId = s.Id;
                WorkerSessionId = s.Id;
                _log.Info($"RemoteWorker 会话已就绪且活动：id={s.Id}（用时 {sw.Elapsed.TotalSeconds:F1}s）");
                Run("cmdkey.exe", $"/delete:TERMSRV/{LoopbackHost}");
                _observeSinceUtc = DateTime.MinValue;
                Thread.Sleep(3000);      // 等会话桌面完全就绪
                EnsureMstscHidden();
                return WaitSessionReady(sessionId, TimeSpan.FromSeconds(10));
            }
            if (_mstsc is { HasExited: true })
                _log.Warn("mstsc 已退出，继续等待会话…");
        }
        error = $"等待 RDP 会话超时（{effective.TotalSeconds:F0}s）";
        _log.Error(error);
        return false;
    }

    /// <summary>等待会话的工作站名就绪（避免拿到的会话还在初始化）</summary>
    private bool WaitSessionReady(int sessionId, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            string station = SessionLauncher.QueryString(sessionId, NativeApi.WTSWinStationName);
            if (!string.IsNullOrEmpty(station) && !station.StartsWith("Services"))
                return true;
            Thread.Sleep(500);
        }
        return true;
    }

    /// <summary>
    /// 保证 mstsc 窗口处于"隐藏但未最小化"状态：
    /// - 最小化 → 会话无显示表面，采集与键鼠注入都会失效
    /// - 隐藏   → 会话保持活动，且本地用户看不到这个窗口（符合策划"连上后自动隐藏"）
    /// </summary>
    public void EnsureMstscHidden()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("mstsc"))
            {
                try
                {
                    p.Refresh();
                    var h = p.MainWindowHandle;
                    if (h == IntPtr.Zero) continue;
                    if (NativeApi.IsIconic(h))
                    {
                        // 必须用 SW_SHOWNOACTIVATE 取消最小化：SW_RESTORE 会把窗口激活，
                        // 相当于抢走本地用户当前窗口的焦点（会打断本地用户打字）
                        _log.Warn("mstsc 窗口被最小化（会让远程会话进入无显示状态），取消最小化后隐藏（不抢焦点）");
                        NativeApi.ShowWindow(h, NativeApi.SW_SHOWNOACTIVATE);
                        Thread.Sleep(600);
                    }
                    if (NativeApi.IsWindowVisible(h))
                    {
                        NativeApi.ShowWindow(h, NativeApi.SW_HIDE);
                        _log.Info($"已隐藏 mstsc 窗口 {h}（保持会话活动）");
                    }
                }
                finally { p.Dispose(); }
            }
        }
        catch (Exception ex) { _log.Debug($"隐藏 mstsc 窗口失败：{ex.Message}"); }
    }

    private void AddCredential()
        => Run("cmdkey.exe", $"/generic:TERMSRV/{LoopbackHost} /user:{_cfg.WorkerUser} /pass:{_cfg.WorkerPassword}");

    /// <summary>
    /// 自动确认 mstsc 的阻塞式对话框（仅限 mstsc 进程自己的窗口，绝不碰用户其他程序的对话框）。
    ///
    /// 背景：Windows 11 24H2 起，.rdp 内嵌密码会让 mstsc 弹「远程桌面连接安全警告 —
    /// 无法验证此远程连接的发布者 / 使用以下凭据连接: xxx 的密码」，按钮是「连接(N)/取消(C)」；
    /// 老版本则弹证书警告「无法验证此远程计算机的身份」，按钮是「是(Y)/否(N)」。
    /// 无人值守场景下没人点它，连接就会一直挂着（实测 180s 超时 → 没有会话 → 黑屏）。
    /// 处理：先勾上"不再询问"类复选框，再点确认按钮。
    /// </summary>
    public void DismissBlockingDialogs()
    {
        try
        {
            var pids = new HashSet<int>();
            foreach (var p in Process.GetProcessesByName("mstsc"))
            {
                using (p) pids.Add(p.Id);
            }
            if (pids.Count == 0) return;

            NativeApi.EnumWindows((h, _) =>
            {
                try
                {
                    NativeApi.GetWindowThreadProcessId(h, out int pid);
                    if (!pids.Contains(pid)) return true;
                    if (!NativeApi.IsWindowVisible(h)) return true;

                    string cls = NativeApi.GetClassName(h);
                    if (cls == "Credential Dialog Xaml Host")
                    {
                        _log.Warn("mstsc 弹出凭据输入框（保存的凭据已失效），将重试建立会话");
                        return true;
                    }
                    if (cls != "#32770") return true;      // 只处理对话框

                    var checkBoxes = new List<IntPtr>();
                    IntPtr accept = IntPtr.Zero;
                    NativeApi.EnumChildWindows(h, (c, _2) =>
                    {
                        string ccls = NativeApi.GetClassName(c);
                        if (ccls != "Button" && ccls != "SysLink") return true;
                        int style = NativeApi.GetWindowStyle(c);
                        if (NativeApi.IsCheckBoxStyle(style)) { checkBoxes.Add(c); return true; }
                        string text = NativeApi.GetWindowText(c).Replace("&", "").Trim();
                        if (IsAcceptButtonText(text)) accept = c;
                        return true;
                    }, IntPtr.Zero);

                    if (accept == IntPtr.Zero) return true;   // 认不出来就别乱点
                    foreach (var cb in checkBoxes)
                        NativeApi.SendMessageW(cb, NativeApi.BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                    NativeApi.SendMessageW(accept, NativeApi.BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                    _log.Warn($"已自动确认 mstsc 对话框：「{NativeApi.GetWindowText(h)}」→ 继续连接");
                }
                catch (Exception ex) { _log.Debug($"处理 mstsc 对话框异常：{ex.Message}"); }
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex) { _log.Debug($"扫描 mstsc 对话框异常：{ex.Message}"); }
    }

    /// <summary>确认类按钮文案（连接/是/确定/继续/Yes/Connect…）。绝不匹配"取消/否"。</summary>
    private static bool IsAcceptButtonText(string text)
    {
        if (text.Length == 0) return false;
        if (text.StartsWith("取消") || text.StartsWith("否") || text.StartsWith("关闭")) return false;
        return text.StartsWith("连接") || text.StartsWith("是") || text.StartsWith("确定")
               || text.StartsWith("继续") || text.StartsWith("重试")
               || text.StartsWith("Connect") || text.StartsWith("Yes") || text.StartsWith("Continue")
               || text.StartsWith("Retry") || text == "OK";
    }

    /// <summary>启动 mstsc 连接回环地址（最小化窗口；mstsc 一旦退出，会话就会变成"已断开"）</summary>
    private bool StartMstsc()
    {
        try
        {
            // 注意：**不能最小化启动**。实测 RDP 客户端窗口最小化时，服务端会话会进入
            // "无显示"状态（光标恒为 0,0、无前台窗口），远程键鼠注入全部失效。
            // 正确做法：先正常显示连上，再 SW_HIDE 隐藏（隐藏状态下会话仍然活动，已实测）。
            var psi = new ProcessStartInfo("mstsc.exe", $"\"{RdpFilePath}\"")
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Normal,
            };
            _mstsc = Process.Start(psi);
            _log.Info($"已启动 mstsc（pid={_mstsc?.Id}）连接 {LoopbackHost}");
            return true;
        }
        catch (Exception ex)
        {
            _log.Error($"启动 mstsc 失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 会话看护（由协调器每 20s 调用）：只在会话真的不可用时才动手，
    /// 且通过观察窗口避免与正在进行的建立流程互相打架。
    /// </summary>
    public bool EnsureSessionAlive()
    {
        var s = FindWorkerSession();
        if (s.HasValue && s.Value != SessionLauncher.CurrentSessionId && IsSessionActive(s.Value))
        {
            _observeSinceUtc = DateTime.MinValue;
            if (!IsMstscAlive())
            {
                _log.Warn("会话活动但 mstsc 已退出，重新拉起客户端");
                WriteRdpFile();
                AddCredential();
                StartMstsc();
            }
            else
            {
                // 用户/系统把 mstsc 最小化会让会话失去显示表面（光标恒 0,0、无前台窗口、
                // 键鼠注入失效）→ 每个看护周期都纠正为"隐藏但未最小化"
                EnsureMstscHidden();
            }
            return true;
        }

        if (!EstablishLock.Wait(0))
        {
            _log.Debug("建会话流程正在进行，看护暂不介入");
            return false;
        }
        EstablishLock.Release();

        _log.Warn("检测到 RDP 会话不可用（不存在/已断开/mstsc 已退出），执行自动重建");
        if (!EnsureWorkerSession(TimeSpan.FromSeconds(180), out int sid, out string err))
        {
            if (err != "会话建立中") _log.Error($"RDP 会话自动重建失败：{err}");
            return false;
        }
        _log.Info($"RDP 会话自动重建成功：id={sid}");
        return true;
    }

    private void WriteRdpFile()
    {
        var path = RdpFilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var sb = new StringBuilder();
        sb.AppendLine("screen mode id:i:1");
        sb.AppendLine("use multimon:i:0");
        sb.AppendLine($"desktopwidth:i:{_cfg.VirtualDisplayWidth}");
        sb.AppendLine($"desktopheight:i:{_cfg.VirtualDisplayHeight}");
        sb.AppendLine("session bpp:i:32");
        sb.AppendLine("winposstr:s:0,3,0,0,800,600");
        sb.AppendLine("compression:i:1");
        sb.AppendLine("keyboardhook:i:0");
        sb.AppendLine("audiocapturemode:i:0");
        sb.AppendLine("videoplaybackmode:i:1");
        sb.AppendLine("connection type:i:6");
        sb.AppendLine("networkautodetect:i:0");
        sb.AppendLine("bandwidthautodetect:i:0");
        sb.AppendLine("displayconnectionbar:i:1");
        sb.AppendLine("disable wallpaper:i:0");
        sb.AppendLine("allow font smoothing:i:1");
        sb.AppendLine("allow desktop composition:i:1");
        sb.AppendLine("disable full window drag:i:1");
        sb.AppendLine("disable menu anims:i:1");
        sb.AppendLine("disable themes:i:0");
        sb.AppendLine("disable cursor setting:i:0");
        sb.AppendLine("bitmapcachepersistenable:i:1");
        sb.AppendLine($"full address:s:{LoopbackHost}");
        // 用户名 + DPAPI 加密后的密码（CryptProtectData，当前用户可解）：
        // 这样 mstsc 直接完成登录，不会再弹凭据框（cmdkey 方式在实测中会弹框卡住）
        sb.AppendLine($"username:s:{_cfg.WorkerUser}");
        var blob = ProtectPassword(_cfg.WorkerPassword);
        if (blob != null) sb.AppendLine($"password 51:b:{blob}");
        sb.AppendLine("prompt for credentials:i:0");
        sb.AppendLine("promptcredentialonce:i:1");
        sb.AppendLine("authentication level:i:0");
        sb.AppendLine("negotiate security layer:i:1");
        sb.AppendLine("enablecredsspsupport:i:1");
        // 关键：关闭剪贴板/驱动器重定向，否则远程会话剪贴板会串到本地用户（破坏隔离）
        sb.AppendLine("redirectclipboard:i:0");
        sb.AppendLine("redirectdrives:i:0");
        sb.AppendLine("redirectprinters:i:0");
        sb.AppendLine("redirectcomports:i:0");
        sb.AppendLine("redirectsmartcards:i:0");
        sb.AppendLine("redirectwebauthn:i:0");
        sb.AppendLine("redirectlocation:i:0");
        sb.AppendLine("redirectposdevices:i:0");
        sb.AppendLine("audiomode:i:2");
        sb.AppendLine("devicestoredirect:s:");
        sb.AppendLine("drivestoredirect:s:");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        _log.Debug($"已写入 {path}");
    }

    /// <summary>注销 RemoteWorker 会话并结束 mstsc（托盘退出时调用）</summary>
    public void CloseLoopbackSession()
    {
        var id = FindWorkerSession();
        if (id.HasValue && id.Value != SessionLauncher.CurrentSessionId)
        {
            try
            {
                if (NativeApi.WTSLogoffSession(IntPtr.Zero, id.Value, false))
                    _log.Info($"已注销会话 {id.Value}");
                else
                {
                    _log.Warn($"WTSLogoffSession({id.Value}) 失败：{NativeApi.LastWin32Error()}");
                    Run("logoff.exe", id.Value.ToString());
                }
            }
            catch (Exception ex) { _log.Warn($"注销会话失败：{ex.Message}"); }
        }
        try
        {
            if (_mstsc is { HasExited: false })
            {
                _mstsc.Kill(entireProcessTree: true);
                _log.Info("已结束 mstsc");
            }
        }
        catch { }
    }

    /// <summary>用 CryptProtectData 生成 mstsc 的 "password 51:b:" 字段（十六进制）</summary>
    private static string? ProtectPassword(string password)
    {
        if (string.IsNullOrEmpty(password)) return null;
        IntPtr inPtr = IntPtr.Zero, outPtr = IntPtr.Zero;
        try
        {
            var bytes = System.Text.Encoding.Unicode.GetBytes(password);
            var inBlob = new NativeApi.DATA_BLOB { cbData = bytes.Length, pbData = Marshal.AllocHGlobal(bytes.Length) };
            inPtr = inBlob.pbData;
            Marshal.Copy(bytes, 0, inPtr, bytes.Length);

            if (!NativeApi.CryptProtectData(ref inBlob, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out var outBlob))
                return null;
            outPtr = outBlob.pbData;
            var result = new byte[outBlob.cbData];
            Marshal.Copy(outPtr, result, 0, result.Length);
            return Convert.ToHexString(result);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (inPtr != IntPtr.Zero) Marshal.FreeHGlobal(inPtr);
            if (outPtr != IntPtr.Zero) NativeApi.LocalFree(outPtr);
        }
    }

    private static (int Code, string Output) Run(string exe, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(30000);
            return (p.ExitCode, o);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }
}
