using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agent.Common;

/// <summary>
/// Agent 配置： config\agent.json（不存在则生成默认值）
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

    // ---- RDP 回环会话 ----
    /// <summary>远程会话专用本地用户（安装脚本创建）</summary>
    public string WorkerUser { get; set; } = "RemoteWorker";
    public string WorkerPassword { get; set; } = "";
    /// <summary>RDP 回环目标地址，必须是 127.0.0.2（策划 §5.3）</summary>
    public string RdpLoopbackHost { get; set; } = "127.0.0.2";
    /// <summary>
    /// 会话模型（产品级开关）：
    /// false（默认）= **共享模式**：Worker 直接跑在"本机已登录用户"的会话里，远程看到并操作的就是
    ///   这个桌面（外加一块虚拟外屏），软件与数据跟本机用户天然一致（同一个账户、同一个 profile），
    ///   窗口可以在物理屏和外屏之间拖；代价是本机与远程共用一套鼠标键盘、且机器必须有人登录着。
    /// true = 独立会话模式（旧方案）：新建 RemoteWorker 用户 + RDP 回环会话，两边各自桌面、互不干扰，
    ///   机器停在登录界面也能连；代价是数据不共享（不同 profile），需要 RDPWrap。
    /// </summary>
    public bool UseRdpSession { get; set; } = false;
    /// <summary>启动 mstsc 后等待会话建立的最长秒数（首次登录要建配置文件，实测 40~60s）</summary>
    public int RdpConnectTimeoutSec { get; set; } = 180;
    /// <summary>
    /// 旧多会话方案的逃生门。**产品本意是共享模式**：在管理员已登录的那个账户上多加一块虚拟外屏，
    /// 同一个账户、同一份 profile、同一个微信实例，窗口能在两块屏之间拖。
    /// 而"新建 RemoteWorker 用户 + RDP 回环会话"是另一个账户、另一份 profile，
    /// 微信/浏览器/授权的数据都跟本机用户不共享 —— 客户反馈的"被控端新建了一个用户"就是这条路。
    /// 因此默认 false：载入配置时 UseRdpSession 会被强制改回 false 并写回文件（留痕在日志里）。
    /// 只有显式把它打开，旧方案才会真的执行。
    /// </summary>
    public bool AllowLegacyRdpSession { get; set; } = false;

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
    [JsonPropertyName("LogLevel")]
    public string LogLevelName { get; set; } = "Info";
    /// <summary>是否上报软件列表（含图标）</summary>
    public bool ReportSoftwareIcons { get; set; } = true;
    /// <summary>Worker 进程名（SessionLauncher 使用）</summary>
    public string WorkerExe { get; set; } = "Agent.Worker.exe";
    /// <summary>Worker 退出后自动重启</summary>
    public bool RestartWorkerOnExit { get; set; } = true;

    // ---------------------------------------------------------------- 载入

    public static string ConfigPath(string? baseDir = null)
        => Path.Combine(baseDir ?? AppContext.BaseDirectory, "config", "agent.json");

    public static AgentConfig Load(string? baseDir = null)
    {
        var path = ConfigPath(baseDir);
        AgentConfig cfg;
        if (File.Exists(path))
        {
            try
            {
                cfg = JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(path), JsonOpts) ?? new AgentConfig();
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
        cfg.EnforceProductMode(baseDir);
        return cfg;
    }

    /// <summary>
    /// 产品模式强约束（共享模式是唯一的产品形态）：把"新建用户 + RDP 回环会话"挡在门外。
    /// 被控端要做的是"在已经登录的管理员账户上多加一块屏"，不是另开一个账户 ——
    /// 另开账户 = 另一份 profile = 微信/浏览器/文件全都不共享，正是客户不接受的做法。
    /// 检测到旧配置就地纠正并写回，方便存量机器升级后自动回到共享模式。
    /// </summary>
    public void EnforceProductMode(string? baseDir = null)
    {
        if (!UseRdpSession || AllowLegacyRdpSession) return;
        UseRdpSession = false;
        ModeCorrection =
            "配置里的 UseRdpSession=true 已强制关闭 → 共享模式（本机已登录账户 + 虚拟外屏）。" +
            "旧的多会话方案会新建 RemoteWorker 用户、数据与管理员账户不共享；" +
            "确实要跑旧方案，必须在 agent.json 里显式设置 AllowLegacyRdpSession=true。";
        if (!EnableVirtualDisplay)
        {
            // 旧的多会话配置里这块虚拟外屏是关着的（那时靠 RDP 会话自带显示）。
            // 共享模式下没有它，远端就只能操作物理主屏 —— 坐在机器前的人会看见远端光标，
            // 而且两边抢同一个光标，正是客户不接受的那种表现。
            EnableVirtualDisplay = true;
            ModeCorrection += " 同时打开了 EnableVirtualDisplay（虚拟外屏）：共享模式下没有它，远端只能操作物理主屏。";
        }
        try { Save(baseDir); } catch { /* 写不进去也要继续跑，只是下次还要纠正一遍 */ }
    }

    /// <summary>模式纠正说明（只在日志里用，不写回配置文件）</summary>
    [JsonIgnore]
    public string ModeCorrection { get; set; } = "";

    public void Save(string? baseDir = null)
    {
        var path = ConfigPath(baseDir);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpts));
    }

    public static string GenerateAgentId()
    {
        var name = Environment.MachineName;
        var safe = new string(name.Where(char.IsLetterOrDigit).ToArray());
        if (safe.Length == 0) safe = "AGENT";
        return $"{safe.ToUpperInvariant()}-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}";
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
