using System.Text.Json;

namespace Agent.Common;

/// <summary>
/// 编码器能力键（2026-10-14 重构，对标 UU codec capability cache 设计）。
/// 【铁律 23：缓存键必须覆盖"会改变结果"的全部维度】维度清单：
///   · ffmpeg 标识（路径+大小+mtime —— 换 ffmpeg 构建，编码器可用性可能变化）
///   · 显卡标识（VendorId:DeviceId:描述 —— 换卡/换驱动，nvenc/qsv/amf 可用性变化）
///   · 编码族 h264/h265（2026-10-11 H.265 黑屏事故的根因维度）
///   · 编码器名
///   · 是否 dda 自采集（dda 只允许直接消费 d3d11 帧的编码器，候选集不同）
/// 分辨率**不进键**：硬件编码器的能力与分辨率无关，去掉它之后换分辨率不再重付探测代价
/// （~2.5s/候选）；分辨率相关的运行期失败由既有 3-strike→ForceSoftware 机制兜底。
/// </summary>
public readonly record struct EncoderCapabilityId(string Ffmpeg, string Adapter, string Family, string Encoder, bool SelfCapture)
{
    /// <summary>持久化用的短键（ffmpeg/显卡在文件级校验，不重复进每个条目）</summary>
    public string ShortKey => $"{Family}|{Encoder}|{(SelfCapture ? "dda" : "pipe")}";
}

/// <summary>
/// 编码器能力缓存（准入规则对标 UU 的 encoder/decoder_codec_capability_cache）：
///  · 硬件编码器探测成功 → 写内存 + 落盘持久化（引擎被看门狗/0x58 重启后不再重复探测）；
///  · 软件编码（libx264/libx265）命中 → 仅内存：探到软编只说明"此刻"硬编起不来
///    （GPU 会话被游戏/模拟器占用是常态），重启后应重新试硬编；
///  · 探测失败（负结果）→ 仅内存 + TTL（默认 120s）：驱动装好/GPU 空出来后能自动重试，
///    又防止码流重建循环里反复烧探测时间。
/// 持久化文件只含"硬件 OK"条目 + 记录它所属的 ffmpeg/显卡环境；环境一变（换卡、换 ffmpeg）
/// 旧条目整体作废——这是惰性校验：首次 Lookup/Record 带着当前环境来时才比对。
/// </summary>
public sealed class EncoderCapabilityCache
{
    /// <summary>负结果（探测失败）的内存有效期</summary>
    public const int NegativeTtlMs = 120_000;

    private sealed class Entry
    {
        public bool Ok;
        public long Tick;
    }

    private sealed class PersistModel
    {
        public int Schema { get; set; } = 1;
        public string? Ffmpeg { get; set; }
        public string? Adapter { get; set; }
        /// <summary>ShortKey → 探测成功的 unix 毫秒时间戳（只放硬件 OK 条目）</summary>
        public Dictionary<string, long> Ok { get; set; } = new();
        /// <summary>编码族 → 运行期降级时间（ForceSoftware 运维可见记录，不参与判定）</summary>
        public Dictionary<string, string> Forced { get; set; } = new();
    }

    private readonly object _gate = new();
    private readonly string? _persistPath;
    private readonly Func<long> _clock;
    private readonly Dictionary<string, Entry> _mem = new();   // 全键（ffmpeg|adapter|short）→ 条目
    private PersistModel _persist = new();

    /// <param name="persistPath">持久化文件路径；null = 纯内存（单测用）</param>
    /// <param name="clock">时钟注入（单测 TTL 用），默认 Environment.TickCount64</param>
    public EncoderCapabilityCache(string? persistPath = null, Func<long>? clock = null)
    {
        _persistPath = persistPath;
        _clock = clock ?? (static () => Environment.TickCount64);
        if (persistPath != null) LoadFromDisk();
    }

    /// <summary>进程级共享实例（与旧版静态 EncoderCache 同生命周期语义）</summary>
    public static EncoderCapabilityCache Default { get; } = new(DefaultPersistPath());

    private static string? DefaultPersistPath()
    {
        try
        {
            var dir = Path.GetDirectoryName(AgentConfig.ConfigPath());
            return dir == null ? null : Path.Combine(dir, "encoder-cache.json");
        }
        catch { return null; }
    }

    // ---------------------------------------------------------------- 查询 / 记录

    /// <summary>
    /// 查缓存。true=探测过且可用；false=探测过且失败（TTL 内）；null=未缓存或负结果已过期（应重探）。
    /// </summary>
    public bool? Lookup(EncoderCapabilityId id)
    {
        var full = FullKey(id);
        lock (_gate)
        {
            if (_mem.TryGetValue(full, out var e))
            {
                if (e.Ok) return true;
                if (_clock() - e.Tick < NegativeTtlMs) return false;
                _mem.Remove(full);   // 负结果过期：允许重探
                return null;
            }
            // 内存未命中 → 查磁盘持久化的"硬件 OK"（环境必须吻合，否则先作废）
            if (_persistPath != null && EnvironmentMatches(id) && _persist.Ok.ContainsKey(id.ShortKey))
            {
                _mem[full] = new Entry { Ok = true, Tick = _clock() };
                return true;
            }
            return null;
        }
    }

    /// <summary>记录一次探测结果。硬件成功会顺带落盘。</summary>
    public void Record(EncoderCapabilityId id, bool probedOk)
    {
        var full = FullKey(id);
        lock (_gate)
        {
            _mem[full] = new Entry { Ok = probedOk, Tick = _clock() };
            if (!probedOk || IsSoftwareEncoder(id.Encoder) || _persistPath == null) return;
            // 环境变了（换卡/换 ffmpeg）→ 旧环境的硬件结论全部作废再写新环境
            if (!EnvironmentMatches(id))
            {
                _persist.Ok.Clear();
                _persist.Ffmpeg = id.Ffmpeg;
                _persist.Adapter = id.Adapter;
            }
            _persist.Ok[id.ShortKey] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            SaveToDisk();
        }
    }

    /// <summary>记录运行期 ForceSoftware 降级（运维可见，不参与任何判定）</summary>
    public void RecordForcedSoftware(string family)
    {
        lock (_gate)
        {
            if (_persistPath == null) return;
            _persist.Forced[family] = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss");
            SaveToDisk();
        }
    }

    public static bool IsSoftwareEncoder(string encoder) =>
        encoder.StartsWith("libx", StringComparison.OrdinalIgnoreCase);

    private bool EnvironmentMatches(in EncoderCapabilityId id) =>
        string.Equals(_persist.Ffmpeg, id.Ffmpeg, StringComparison.OrdinalIgnoreCase)
        && string.Equals(_persist.Adapter, id.Adapter, StringComparison.OrdinalIgnoreCase);

    private string FullKey(in EncoderCapabilityId id) => $"{id.Ffmpeg}|{id.Adapter}|{id.ShortKey}";

    // ---------------------------------------------------------------- ffmpeg / 显卡标识

    /// <summary>ffmpeg 身份：路径+大小+mtime（换构建/更新后旧缓存失效）</summary>
    public static string FfmpegIdentity(string ffmpegPath)
    {
        try
        {
            var fi = new FileInfo(ffmpegPath);
            return fi.Exists ? $"{fi.FullName}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}" : ffmpegPath;
        }
        catch { return ffmpegPath; }
    }

    /// <summary>默认显卡身份（DXGI 枚举；进程内缓存一次）</summary>
    public static string AdapterIdentity() => NativeDxgi.DefaultAdapterIdentity();

    // ---------------------------------------------------------------- 持久化

    private void LoadFromDisk()
    {
        try
        {
            if (!File.Exists(_persistPath)) return;
            var model = JsonSerializer.Deserialize<PersistModel>(File.ReadAllText(_persistPath), AgentConfig.JsonOpts);
            if (model == null || model.Schema != 1) return;
            _persist = model;   // 环境是否吻合由首次 Lookup/Record 惰性比对
        }
        catch
        {
            _persist = new PersistModel();   // 文件损坏：当没缓存过，下次成功探测时重写
        }
    }

    private void SaveToDisk()
    {
        if (_persistPath == null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_persistPath)!);
            var tmp = _persistPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_persist, AgentConfig.JsonOpts));
            File.Move(tmp, _persistPath, overwrite: true);   // 原子替换
        }
        catch { /* 写不进去就退化为纯内存缓存（安装目录只读等场景） */ }
    }
}
