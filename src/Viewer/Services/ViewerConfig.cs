using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Viewer.Services;

/// <summary>主控端配置： config\viewer.json</summary>
public sealed class ViewerConfig
{
    public string ServerUrl { get; set; } = "ws://127.0.0.1:8080/ws";
    /// <summary>
    /// 备用中继端点（逗号分隔，可留空）：主地址连不上时按顺序重试这些。
    /// 留空时会自动尝试同主机的 443 / 8443（部分网络只放行 443）。
    /// 也可以直接在界面的「服务器」输入框里填多个地址（逗号/分号分隔）。
    /// </summary>
    public string ServerUrlFallbacks { get; set; } = "";
    /// <summary>留空 = 由 ServerUrl 推导（http(s)://host:port）</summary>
    public string FileServerUrl { get; set; } = "";
    public string TargetAgentId { get; set; } = "";
    /// <summary>中继服务器要求的共享令牌（服务器未启用鉴权则留空）</summary>
    public string AgentToken { get; set; } = "";
    public string FfmpegPath { get; set; } = "";
    /// <summary>远程剪贴板文件的保存目录</summary>
    public string DownloadDir { get; set; } = "";
    public bool AutoConnect { get; set; } = false;
    public bool ClipboardSyncEnabled { get; set; } = true;

    /// <summary>
    /// 视频链路偏好：auto = 优先直连、失败回落中继；relay = 只用中继；direct = 只走直连。
    /// 注意：控制/剪贴板/文件**始终走中继**（它是信令与兜底通道），这里选的是视频走哪条路。
    /// </summary>
    public string LinkMode { get; set; } = "auto";

    /// <summary>
    /// 远端鼠标走哪条路：real=始终真实光标（默认，兼容性最好）/ auto=自动 / background=始终后台注入。
    /// 被控端在"本机用户在忙"时默认会自动改用后台定向注入（不抢他的光标），但微信这类不吃
    /// 合成消息的应用会点不动（实测踩到），所以默认用真实光标，把选择权留给主控端。
    /// </summary>
    public string InputMode { get; set; } = "real";

    /// <summary>
    /// 主控端显示被控端的哪些屏幕：virtual = 只显示虚拟外屏（默认，省带宽）；
    /// all = 全部屏幕合成一幅画面（主屏 + 副屏；物理屏那块只读）。
    /// 被控端按这个下发 CaptureModeHint，连上时会重新告知一次。
    /// </summary>
    public string CaptureMode { get; set; } = "virtual";

    /// <summary>
    /// "主屏 / 全部屏幕"这两档画面里，物理屏那块是不是**只读**。
    /// false（默认）= 直接可操作：点、拖、滚都送过去（产品负责人要求"把主屏的控制也打开"）；
    /// true = 只读：点它只是"把那个窗口搬到副屏"（本机用户完全不受打扰，早期默认值）。
    /// 注意：真机上如果还有人坐着用这台机器，可操作会让他看见远端光标在动 —— 所以做成开关。
    /// </summary>
    public bool PhysicalScreenReadOnly { get; set; } = false;

    [JsonPropertyName("LogLevel")]
    public string LogLevelName { get; set; } = "Info";
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 800;

    public static string ConfigPath => Path.Combine(AppContext.BaseDirectory, "config", "viewer.json");

    /// <summary>
    /// 配置写不进程序目录时的兜底位置（%APPDATA%\远程控制主控端\configiewer.json）。
    /// 为什么需要：装在 C:\Program Files 时普通用户对程序目录没有写权限，配置只能写到用户目录，
    /// 否则「服务器地址/令牌/链路偏好」这些用户改过的东西下次启动就丢了。
    /// </summary>
    public static string FallbackConfigPath()
    {
        try
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "远程控制主控端", "config", "viewer.json");
        }
        catch
        {
            return Path.Combine(Path.GetTempPath(), "远程控制主控端", "config", "viewer.json");
        }
    }

    /// <summary>
    /// 打包时预置的默认值（服务器地址 / 令牌），放在程序同目录。分发出去的主控端是
    /// "地址与令牌都填好"的，客户双击即可用 —— 令牌不该让用户自己去找、去填。
    /// </summary>
    public static string DefaultsPath => Path.Combine(AppContext.BaseDirectory, "viewer-defaults.txt");

    /// <summary>
    /// 把预置值补进配置。
    /// freshConfig = true（本机还没有 viewer.json，即"客户第一次运行"）：预置值直接生效，
    ///   否则会被代码里的占位默认值（127.0.0.1）挡住 —— 客户就得自己去改地址了。
    /// freshConfig = false（本机已有配置）：只补**为空**的项，厂商/客户自己填过的值不动。
    /// 返回是否真的补过（补过才落盘，避免每次启动都写文件）。
    /// </summary>
    private bool ApplyBakedDefaults(bool freshConfig)
    {
        try
        {
            if (!File.Exists(DefaultsPath)) return false;
            var changed = false;
            foreach (var raw in File.ReadAllLines(DefaultsPath))
            {
                var line = raw.Trim().TrimStart('\uFEFF');
                if (line.Length == 0 || line.StartsWith("#")) continue;
                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var k = line[..eq].Trim().ToLowerInvariant();
                var v = line[(eq + 1)..].Trim();
                if (v.Length == 0) continue;
                if (k == "serverurl" && (freshConfig || string.IsNullOrWhiteSpace(ServerUrl))) { ServerUrl = v; changed = true; }
                else if (k == "serverurlfallbacks" && (freshConfig || string.IsNullOrWhiteSpace(ServerUrlFallbacks))) { ServerUrlFallbacks = v; changed = true; }
                else if (k == "token" && (freshConfig || string.IsNullOrWhiteSpace(AgentToken))) { AgentToken = v; changed = true; }
            }
            return changed;
        }
        catch { return false; }
    }

    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static ViewerConfig Load()
    {
        ViewerConfig cfg;
        // 程序目录里的配置优先；没有就看兜底位置（Program Files 安装时配置写在用户目录）
        var effective = File.Exists(ConfigPath) ? ConfigPath
            : (File.Exists(FallbackConfigPath()) ? FallbackConfigPath() : ConfigPath);
        var fresh = !File.Exists(effective);
        if (!fresh)
        {
            try
            {
                cfg = JsonSerializer.Deserialize<ViewerConfig>(File.ReadAllText(effective), JsonOpts) ?? new ViewerConfig();
            }
            catch { cfg = new ViewerConfig(); fresh = true; }
        }
        else
        {
            cfg = new ViewerConfig();
        }
        if (string.IsNullOrWhiteSpace(cfg.DownloadDir))
            cfg.DownloadDir = DefaultDownloadDir();
        if (cfg.ApplyBakedDefaults(fresh))
            cfg.Save();      // 把预置值落进 viewer.json，界面上/其它调用处都能看到
        cfg.EnsureDirs();
        return cfg;
    }

    /// <summary>配置实际落盘的位置（正常=程序目录；Program Files 安装时=用户目录）</summary>
    public static string LastSavedPath { get; private set; } = "";

    public void Save()
    {
        var json = JsonSerializer.Serialize(this, JsonOpts);
        foreach (var path in new[] { ConfigPath, FallbackConfigPath() })
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, json);
                LastSavedPath = path;
                return;
            }
            catch { }
        }
    }

    /// <summary>默认下载目录：优先非系统盘（用户要求不往 C 盘放东西），否则用户下载目录</summary>
    public static string DefaultDownloadDir()
    {
        try
        {
            foreach (var drive in new[] { "E:", "D:" })
            {
                if (Directory.Exists(drive + "\\"))
                    return Path.Combine(drive + "\\", "RemoteControl", "Downloads");
            }
        }
        catch { }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "RemoteControl");
    }

    public void EnsureDirs()
    {
        try { Directory.CreateDirectory(DownloadDir); } catch { }
    }

    public string ResolveFileServerUrl()
    {
        if (!string.IsNullOrWhiteSpace(FileServerUrl)) return FileServerUrl.TrimEnd('/');
        var u = new Uri(ServerUrl.Replace("ws://", "http://").Replace("wss://", "https://"));
        return $"{u.Scheme}://{u.Authority}";
    }

    public Agent.Common.LogLevel MinLogLevel =>
        Enum.TryParse<Agent.Common.LogLevel>(LogLevelName, true, out var l) ? l : Agent.Common.LogLevel.Info;
}
