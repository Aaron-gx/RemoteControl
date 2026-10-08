using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agent.Common;

/// <summary>
/// Agent 配置： config\agent.json（不存在则生成默认值）
///
/// 历史包袱的清理记录（2026-10-04，进程合并）：早期的"RDP 多会话独立方案"字段
/// （UseRdpSession / WorkerUser / WorkerPassword / RdpLoopbackHost / RdpConnectTimeoutSec /
/// AllowLegacyRdpSession / WorkerExe）已随多会话代码一起删除 —— 产品定稿为共享模式，
/// 且 Coordinator 与 Worker 已合并进同一个进程。旧配置文件里残留这些键会被直接忽略；
/// 检测到旧多会话配置时按共享模式收尾（见 Load）。
/// </summary>
public sealed class AgentConfig
{
    // ---- 服务器 ----
    public string ServerUrl { get; set; } = "ws://127.0.0.1:8080/ws";
    /// <summary>
    /// 备用中继端点（逗号分隔，可留空）。主地址连不上时按顺序重试这些，
    /// 用来对付"某些网络封了 8080 端口"：留空时会自动尝试同主机的 443 / 8443。
    /// </summary>
    public string ServerUrlFallbacks { get; set; } = "";
    public string FileServerUrl { get; set; } = "http://127.0.0.1:8080";
    public string AgentId { get; set; } = "";
    /// <summary>与服务器约定的可选令牌（服务器未启用则留空）</summary>
    public string AgentToken { get; set; } = "";

    /// <summary>
    /// 离线宽限：连续多少小时**联系不上服务器**就停机（联网后自动恢复，不需要人工处理）。
    /// 目的是让"撤销 / 续期"在一个有界时间内生效 —— 否则把网线一拔（或屏蔽中继走 P2P），
    /// 已撤销或已缩短的授权可以一直用到旧到期时间。
    /// 0 = 不限制（旧行为）；默认 48 小时：覆盖正常网络抖动与周末断开，又能在两天内收敛。
    /// </summary>
    public int MaxOfflineHours { get; set; } = 48;

    // ---- 热更新（2026-10 新增）----
    /// <summary>
    /// 是否启用自动更新。**默认开**：清单不存在/验签失败时什么都不发生，不会把机器更新坏。
    /// 更新包必须过 ECDSA 验签 + sha256 比对才会应用；新版本起来后 45 秒内没"报平安"会自动回滚。
    /// </summary>
    public bool AutoUpdateEnabled { get; set; } = true;
    /// <summary>检查更新的间隔（小时）。启动后 30 秒会先查一次。</summary>
    public int UpdateCheckIntervalHours { get; set; } = 0;
    /// <summary>更新检查周期（分钟），优先于 Hours。默认 15：真机踩坑（2026-10-08）——
    /// 被控端停在旧版 6 小时，键盘五连修复全没生效，用户以为产品坏了。</summary>
    public int UpdateCheckIntervalMinutes { get; set; } = 15;

    /// <summary>
    /// 光标仲裁（只在采集目标是虚拟外屏时生效）：
    /// 一个 Windows 会话只有一个系统光标，远端把它用到外屏上（外屏没有物理输出，
    /// 所以坐着的人看不见它）。但本机用户的物理鼠标一动就会把同一个光标拽回物理屏。
    /// 打开这个开关后，一旦物理输入到达而光标还在外屏上，就先把光标还回物理屏、
    /// 按键事件按归还后的坐标重放 —— 本机用户看不见远端光标，点击也不会误落到远端窗口上。
    /// </summary>
    public bool CursorArbitrationEnabled { get; set; } = true;

    // ---- 虚拟外屏（共享模式下的"远程那块屏"）----
    /// <summary>
    /// 挂在本机已登录会话上的一块扩展显示器：采集目标优先选它，右键打开的软件也会被摆到它上面，
    /// 窗口能从物理屏拖过来。默认开启（关掉就退化成"远程看物理主屏"）。
    /// </summary>
    public bool EnableVirtualDisplay { get; set; } = true;

    /// <summary>
    /// 默认给主控端看多少屏：false = 只看虚拟外屏（默认，1080p 一块屏、省带宽）；
    /// true = 被控端所有屏幕合成一块画布（主屏 + 副屏）。主控端界面上可以随时切，
    /// 这里只是"连上时先按哪档来"。
    /// </summary>
    public bool CaptureAllScreens { get; set; } = false;

    /// <summary>
    /// 本机用户在副屏上点/激活某个窗口时，是否自动把它搬回物理主屏。
    /// 默认 **false**：现场反馈"窗口被搬到副屏后，本机一点它就自己跑回主屏了"——
    /// 那会把远端正在操作的窗口抢走。需要旧行为时改成 true。
    /// </summary>
    public bool ReturnWindowOnLocalClick { get; set; } = false;
    public int VirtualDisplayCount { get; set; } = 1;
    public int VirtualDisplayWidth { get; set; } = 1920;
    public int VirtualDisplayHeight { get; set; } = 1080;
    public int VirtualDisplayRefreshRate { get; set; } = 60;
    public string VirtualDisplayConfigPath { get; set; } = @"C:\VirtualDisplayDriver\vdd_settings.xml";
    public string VirtualDisplayDriverId { get; set; } = @"Root\MttVDD";

    // ---- 视频 ----
    public int FrameRate { get; set; } = 30;
    public int BitrateKbps { get; set; } = 4000;
    /// <summary>auto | libx264 | h264_nvenc | h264_qsv | h264_amf | h264_mf</summary>
    public string Encoder { get; set; } = "auto";
    /// <summary>视频采集后端：auto | wgc | gdi</summary>
    public string CaptureBackend { get; set; } = "auto";
    public string FfmpegPath { get; set; } = "";
    /// <summary>码流帧通道容量（字节）。旧架构里是跨进程共享内存的大小，现在只是进程内队列的限容。</summary>
    public int FrameBufferSizeBytes { get; set; } = 4 * 1024 * 1024;

    // ---- P2P 直连（视频走直连，控制仍走中继；连不上自动回落）----
    /// <summary>是否启用直连</summary>
    public bool DirectLinkEnabled { get; set; } = true;
    /// <summary>直连监听端口（被控端）</summary>
    public int DirectLinkPort { get; set; } = 47890;
    /// <summary>是否尝试 UPnP 自动端口映射（家庭路由器后面跨网直连的关键）</summary>
    public bool UpnpEnabled { get; set; } = true;
    /// <summary>手动指定公网地址（已做端口映射时填，如 1.2.3.4:47890）</summary>
    public string DirectLinkPublicAddress { get; set; } = "";

    // ---- 行为 ----
    public bool AutoStartOnBoot { get; set; } = true;
    /// <summary>
    /// 是否在系统托盘（右下角）显示图标。**默认 false**（2026-10-04 客户要求：装完被控端后
    /// 右下角不要再出现任何显示）。隐藏后没有托盘菜单（直连开关/退出）：
    /// 退出用任务管理器结束 Agent.Coordinator，或改回 true 重启被控端。
    /// </summary>
    public bool ShowTrayIcon { get; set; } = false;
    [JsonPropertyName("LogLevel")]
    public string LogLevelName { get; set; } = "Info";
    /// <summary>是否上报软件列表（含图标）</summary>
    public bool ReportSoftwareIcons { get; set; } = true;
    /// <summary>采集引擎（原 Worker）意外退出后自动重启</summary>
    public bool RestartWorkerOnExit { get; set; } = true;

    /// <summary>启动时发现的旧配置问题（只在日志里说明，不写回配置文件）</summary>
    [JsonIgnore]
    public string ConfigNotice { get; private set; } = "";

    // ---------------------------------------------------------------- 载入

    public static string ConfigPath(string? baseDir = null)
        => Path.Combine(baseDir ?? AppContext.BaseDirectory, "config", "agent.json");

    public static AgentConfig Load(string? baseDir = null)
    {
        var path = ConfigPath(baseDir);
        AgentConfig cfg;
        string json = "";
        if (File.Exists(path))
        {
            try
            {
                json = File.ReadAllText(path);
                cfg = JsonSerializer.Deserialize<AgentConfig>(json, JsonOpts) ?? new AgentConfig();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"解析 {path} 失败：{ex.Message}", ex);
            }
        }
        else
        {
            cfg = new AgentConfig();
        }
        if (string.IsNullOrWhiteSpace(cfg.AgentId))
        {
            cfg.AgentId = GenerateAgentId();
            cfg.Save(baseDir);
        }
        MigrateLegacyConfig(cfg, json, baseDir);
        return cfg;
    }

    /// <summary>
    /// 旧多会话配置的收尾（等价于已删除的 EnforceProductMode）：配置文件里还留着
    /// UseRdpSession=true 的机器，说明它当年跑的是"新建 RemoteWorker 用户 + RDP 回环会话"，
    /// 那份配置里虚拟外屏通常是关的。共享模式下没有外屏，远端就只能操作物理主屏、
    /// 坐在机器前的人会看见远端光标 —— 所以这里把它打开并写回，让存量机器升级后自动回到正确的形态。
    /// </summary>
    private static void MigrateLegacyConfig(AgentConfig cfg, string json, string? baseDir)
    {
        if (string.IsNullOrEmpty(json) || !json.Contains("\"UseRdpSession\"")) return;
        cfg.ConfigNotice = "配置里残留旧多会话字段（UseRdpSession 等，已随 2026-10 进程合并删除）：" +
                           "按共享模式（本机已登录账户 + 虚拟外屏）处理，未知字段将被忽略。";
        if (!cfg.EnableVirtualDisplay)
        {
            cfg.EnableVirtualDisplay = true;
            cfg.ConfigNotice += " 同时打开了 EnableVirtualDisplay（虚拟外屏）：共享模式下没有它，远端只能操作物理主屏。";
            try { cfg.Save(baseDir); } catch { /* 写不进去也要继续跑，只是下次还要迁移一遍 */ }
        }
    }

    public void Save(string? baseDir = null)
    {
        var path = ConfigPath(baseDir);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpts));
    }

    /// <summary>
    /// 生成 AgentId。
    /// 后缀必须是**可复现**的！以前用随机 GUID：一旦配置写不进去（安装目录只读、被从临时目录
    /// 启动、机器上有多份被控端各写各的），每次重启都会换一个新 AgentId —— 主控端下拉里选中的
    /// 那台就永远"不在线"，现场表现就是"连不上服务器"（实测：两小时内同一台机器换了三个 ID）。
    /// 现在用"机器名 + 系统 MachineGuid"哈希：同一台机器永远得到同一个 ID，重装也稳定。
    /// </summary>
    public static string GenerateAgentId()
    {
        var name = Environment.MachineName;
        var safe = new string(name.Where(char.IsLetterOrDigit).ToArray());
        if (safe.Length == 0) safe = "AGENT";
        try
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(name + "|" + MachineGuid()));
            return $"{safe.ToUpperInvariant()}-{Convert.ToHexString(hash, 0, 4)}";
        }
        catch
        {
            return $"{safe.ToUpperInvariant()}-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}";
        }
    }

    /// <summary>系统 MachineGuid（同一台机器稳定不变，读取不需要管理员权限）</summary>
    private static string MachineGuid()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            return k?.GetValue("MachineGuid")?.ToString() ?? "";
        }
        catch { return ""; }
    }

    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public LogLevel MinLogLevel => Enum.TryParse<LogLevel>(LogLevelName, true, out var l) ? l : Common.LogLevel.Info;

    /// <summary>离线宽限（TimeSpan）；MaxOfflineHours &lt;= 0 时为零 = 不启用</summary>
    public TimeSpan MaxOffline => MaxOfflineHours > 0 ? TimeSpan.FromHours(MaxOfflineHours) : TimeSpan.Zero;

    /// <summary>把 ws:// 地址转成 HTTP 基地址（文件服务用）</summary>
    public string ResolveFileServerUrl()
    {
        if (!string.IsNullOrWhiteSpace(FileServerUrl)) return FileServerUrl.TrimEnd('/');
        var u = new Uri(ServerUrl.Replace("ws://", "http://").Replace("wss://", "https://"));
        return $"{u.Scheme}://{u.Authority}";
    }
}

/// <summary>定位 ffmpeg.exe（配置 → 应用目录 → PATH）</summary>
public static class FfmpegLocator
{
    public static string? Resolve(string configured, string? baseDir = null)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configured))
        {
            candidates.Add(Path.IsPathRooted(configured)
                ? configured
                : Path.Combine(baseDir ?? AppContext.BaseDirectory, configured));
        }
        var root = baseDir ?? AppContext.BaseDirectory;
        candidates.Add(Path.Combine(root, "third_party", "ffmpeg", "ffmpeg.exe"));
        candidates.Add(Path.Combine(root, "ffmpeg.exe"));
        candidates.Add(Path.Combine(root, "..", "..", "..", "..", "..", "third_party", "ffmpeg", "ffmpeg.exe"));
        foreach (var c in candidates)
        {
            try
            {
                var full = Path.GetFullPath(c);
                if (File.Exists(full)) return full;
            }
            catch { }
        }
        // PATH
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator))
        {
            try
            {
                var p = Path.Combine(dir.Trim(), "ffmpeg.exe");
                if (File.Exists(p)) return p;
            }
            catch { }
        }
        return null;
    }
}
