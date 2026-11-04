using System.Diagnostics;
using System.Text;
using Agent.Common;

namespace Agent.Worker;

/// <summary>
/// 视频编码器：把 BGRA 原始帧喂给 ffmpeg stdin，从 stdout 读出 Annex-B 码流块。
/// 支持 H.264 / H.265 两个编码族（2026-10-10，编码协商后可能用 H.265，同画质带宽约省一半）：
///   H.264： h264_nvenc / h264_qsv / h264_amf / libx264（自动探测，失败逐级回退）
///   H.265： hevc_nvenc / hevc_qsv / hevc_amf / libx265
/// </summary>
public sealed class FfmpegEncoder : IDisposable
{
    private readonly Logger _log;
    private readonly string _ffmpeg;
    private readonly int _width, _height, _fps, _kbps;
    private readonly string _configured;
    /// <summary>编码族（"h264" / "h265"）：决定候选顺序、专属参数与输出封装</summary>
    private readonly string _codec;
    /// <summary>
    /// 显卡直通自采集（2026-10-10，对标 UU 的零拷贝管线）：ffmpeg 用 ddagrab 滤镜在显卡上
    /// 直接采集（帧全程留在显存、直喂硬件编码器），取代"WGC→内存→管道→ffmpeg→回显卡"。
    /// 【硬限制】只支持直接消费 d3d11 帧的编码器：N 卡 nvenc / A 卡 amf；
    /// Intel QSV 在当前 ffmpeg 构建上需要 VPP 桥接且桥接失败（实测 scale_qsv VPP -15），
    /// qsv 机器配 dda 会在探测阶段失败、自动回退 WGC。
    /// </summary>
    private readonly bool _selfCapture;
    /// <summary>ddagrab 自采集用的 DXGI 输出索引（SelfCapture=true 时有效）</summary>
    private readonly int _ddaOutputIdx = -1;
    /// <summary>外部喂帧的像素格式（"bgra"/"nv12"；WGC 采集输出 NV12，GDI/画布输出 BGRA）</summary>
    private readonly string _inputFormat;
    private Process? _proc;
    private Thread? _reader;
    private Stream? _stdin;
    private volatile bool _alive;
    private readonly StringBuilder _stderrTail = new();
    private long _chunkCount;
    private long _byteCount;

    /// <summary>读到一个码流块（buffer 仅在回调内有效）</summary>
    public event Action<byte[], int>? Chunk;

    public string EncoderName { get; private set; } = "";
    public bool IsAlive => _alive;
    /// <summary>ffmpeg 标准输入（采集器直接往这里写原始帧）；自采集模式下为 null（ffmpeg 自己采集）</summary>
    public Stream? Stdin => _stdin;
    public string LastError { get; private set; } = "";
    public long ChunkCount => Interlocked.Read(ref _chunkCount);
    public long ByteCount => Interlocked.Read(ref _byteCount);
    /// <summary>当前编码器认的输入尺寸（必须是构造时传进来的那个，改不了）</summary>
    public int Width => _width;
    public int Height => _height;
    /// <summary>当前编码器用的码率/帧率（自适应会换档，换档时整个重建）</summary>
    public int BitrateKbps => _kbps;
    public int FrameRate => _fps;
    /// <summary>当前编码族（"h264" / "h265"）</summary>
    public string Codec => _codec;
    /// <summary>显卡直通自采集（true = ffmpeg 自己采集，外部不喂帧）</summary>
    public bool SelfCapture => _selfCapture;

    public FfmpegEncoder(string ffmpegPath, string configuredEncoder, int width, int height, int fps, int kbps, Logger log,
        string codec = CodecPolicy.H264, bool selfCapture = false, int ddaOutputIdx = -1, string inputFormat = "bgra")
    {
        _ffmpeg = ffmpegPath;
        _configured = configuredEncoder;
        _width = width;
        _height = height;
        _fps = fps;
        _kbps = kbps;
        _log = log;
        _codec = CodecPolicy.IsH265(codec) ? CodecPolicy.H265 : CodecPolicy.H264;
        _selfCapture = selfCapture;
        _ddaOutputIdx = ddaOutputIdx;
        _inputFormat = string.IsNullOrWhiteSpace(inputFormat) ? "bgra" : inputFormat.ToLowerInvariant();
    }

    // ---------------------------------------------------------------- 编码器探测

    private static readonly string[] AutoOrderH264 = { "h264_nvenc", "h264_qsv", "h264_amf", "libx264" };
    private static readonly string[] AutoOrderH265 = { "hevc_nvenc", "hevc_qsv", "hevc_amf", "libx265" };
    /// <summary>
    /// ddagrab 自采集模式可用的编码器（直接消费 d3d11 显存帧）：仅 N 卡 nvenc / A 卡 amf。
    /// qsv 需要 VPP 桥接，当前 ffmpeg 构建上桥接失败（实测），不列入。
    /// </summary>
    private static readonly string[] DdaOrderH264 = { "h264_nvenc", "h264_amf" };
    private static readonly string[] DdaOrderH265 = { "hevc_nvenc", "hevc_amf" };

    /// <summary>配置里的 Encoder 名是否属于这个编码族（配置 h264_nvenc 而 codec=h265 时忽略配置，族内自动选）</summary>
    private static bool FamilyMatches(string encoder, string codec) =>
        CodecPolicy.IsH265(codec)
            ? encoder.StartsWith("hevc_", StringComparison.OrdinalIgnoreCase) || encoder.Equals("libx265", StringComparison.OrdinalIgnoreCase)
            : encoder.StartsWith("h264_", StringComparison.OrdinalIgnoreCase) || encoder.Equals("libx264", StringComparison.OrdinalIgnoreCase);

    // 运行期发现某编码族的硬件编码器反复失败时置位：该族此后一律软件编码（按族区分，
    // h264 硬编被抢走不该连累 h265 的选择）。
    // 探测结果缓存已外移到 Agent.Common/EncoderCapabilityCache（2026-10-14 重构，对标 UU
    // codec capability cache）：键含 ffmpeg+显卡+编码族+编码器+是否dda、硬件成功落盘、
    // 软编仅内存、失败带 TTL —— 引擎重启不再重付探测代价，负结果 120s 后可自动重试。
    private static readonly object _forcedGate = new();
    private static readonly HashSet<string> _forcedSoftware = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>这个编码族是否已被运行期降级为软件编码</summary>
    public static bool SoftwareForced(string codec)
    {
        lock (_forcedGate) return _forcedSoftware.Contains(CodecPolicy.IsH265(codec) ? CodecPolicy.H265 : CodecPolicy.H264);
    }

    /// <summary>
    /// 运行期降级到软件编码（libx264 / libx265）。
    /// 硬件编码器（nvenc/qsv/amf）会被"打开的应用"抢走 —— 模拟器 / 游戏 / 播放器占满或复位
    /// GPU 会话之后，nvenc 会一直启动失败，而探测结果是**缓存住**的，于是每次重建都拿同一个
    /// 坏编码器再失败一次，画面永远回不来（用户看到的"打开应用就黑屏卡掉"）。
    /// </summary>
    public static void ForceSoftware(string reason, Logger log, string codec = CodecPolicy.H264)
    {
        var family = CodecPolicy.IsH265(codec) ? CodecPolicy.H265 : CodecPolicy.H264;
        lock (_forcedGate)
        {
            if (_forcedSoftware.Contains(family)) return;
            _forcedSoftware.Add(family);
        }
        EncoderCapabilityCache.Default.RecordForcedSoftware(family);   // 落盘运维记录（config/encoder-cache.json）
        log.Warn($"编码器降级为软件编码（{family} → {(CodecPolicy.IsH265(family) ? "libx265" : "libx264")}，原因：{reason}）");
    }

    /// <summary>
    /// H.265 家族里能否命中**硬件**编码器（结果按 ffmpeg+显卡缓存，与分辨率无关——
    /// 2026-10-14 起与 ResolveEncoder 共用同一批评测记录，同族探测一次全受益）。
    /// 能力协商的 auto 模式据此决定是否启用 H.265 —— libx265 软编比 libx264 重得多，
    /// 弱 CPU 上 1080p30 都跟不上，软编只在显式配置 VideoCodec=h265 时才会被用上。
    /// 探测失败的负结果 120s 内不重试（防反复烧探测），过期后自动重试（驱动/GPU 恢复可自愈）。
    /// </summary>
    public static bool H265HardwareAvailable(string ffmpeg, int width, int height, int fps, Logger log)
    {
        if (SoftwareForced(CodecPolicy.H265)) return false;
        var candidates = new[] { "hevc_nvenc", "hevc_qsv", "hevc_amf" };
        var verdicts = ProbeFamily(ffmpeg, CodecPolicy.H265, candidates, width, height, fps, selfCapture: false);
        var hit = candidates.FirstOrDefault(c => verdicts.TryGetValue(c, out var ok) && ok);
        if (hit != null) log.Info($"H.265 硬件编码可用：{hit}");
        return hit != null;
    }

    /// <summary>
    /// 探测 ddagrab 能用哪个 DXGI 输出采集目标分辨率（返回 -1 = 不启用）。
    /// 优先**按设备名精确匹配**（2026-10-14：DXGI 枚举 \\.\DISPLAYn 与 Windows 显示设备名一一对应，
    /// 两块同分辨率屏也能唯一锁定——旧的"分辨率唯一匹配"在这种场景会直接放弃）；
    /// 枚举失败回退旧启发式：逐 output_idx 跑 1 帧解析分辨率，唯一匹配才用。
    /// 注意：ddagrab 基于 DXGI Desktop Duplication，对 IddCx 虚拟屏的支持因机器/驱动而异，
    /// 抓不到的输出会在探测中失败并自然回退。
    /// </summary>
    public static int ResolveDdaOutput(string ffmpeg, int width, int height, Logger log, string? deviceName = null)
    {
        if (!string.IsNullOrWhiteSpace(deviceName))
        {
            var outputs = NativeDxgi.EnumOutputs();
            var hit = outputs.FirstOrDefault(o =>
                string.Equals(o.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));
            if (hit != null)
            {
                if (hit.Width == width && hit.Height == height)
                {
                    log.Info($"ddagrab：{hit.DeviceName} 精确匹配采集目标 {width}x{height}（output_idx={hit.OutputIndex}，{hit.AdapterName}）");
                    return hit.OutputIndex;
                }
                // 设备名对上但分辨率不符：以设备名为准（分辨率探测值可能滞后于刚发生的模式切换）
                log.Info($"ddagrab：{hit.DeviceName} 匹配采集目标（DXGI 报 {hit.Width}x{hit.Height}，目标 {width}x{height}，以设备名为准，output_idx={hit.OutputIndex}）");
                return hit.OutputIndex;
            }
            log.Debug($"ddagrab：DXGI 枚举里没有 {deviceName}（共 {outputs.Count} 个输出），回退分辨率匹配");
        }
        var hits = new List<int>();
        for (int idx = 0; idx < 4; idx++)
        {
            var (ok, w, h) = ProbeDdaOutput(ffmpeg, idx);
            if (!ok) continue;   // 该索引没有输出（枚举到头了继续试更大的也没意义，但循环便宜）
            if (w == width && h == height) hits.Add(idx);
            else log.Debug($"ddagrab output {idx} = {w}x{h}（目标 {width}x{height}，不匹配）");
        }
        if (hits.Count == 1)
        {
            log.Info($"ddagrab：output_idx={hits[0]} 与采集目标 {width}x{height} 唯一匹配，显卡直通可用");
            return hits[0];
        }
        if (hits.Count > 1)
            log.Info($"ddagrab：{hits.Count} 个输出都是 {width}x{height}，无法确定哪块是采集目标 → 不启用（改用 WGC）");
        else
            log.Info($"ddagrab：没有能采到 {width}x{height} 的输出 → 不启用（改用 WGC；虚拟屏可能不被 DXGI 采集支持）");
        return -1;
    }

    /// <summary>
    /// dda 显卡直通的整体可行性（2026-10-14，auto 后端默认先试 dda 的门槛）：
    /// 该编码族没被运行期降级为软件编码（软编 libx* 消费不了 d3d11 帧），
    /// 且 dda 候选（nvenc/amf）里至少一个探测通过。结果走能力缓存，重复调用近零开销。
    /// Intel 核显机器此处自然为 false（nvenc/amf 都起不来），维持 WGC+qsv 不变。
    /// </summary>
    public static bool DdaEncoderAvailable(string ffmpeg, string codec, int width, int height, int fps, Logger log)
    {
        if (SoftwareForced(codec)) return false;
        var h265 = CodecPolicy.IsH265(codec);
        var candidates = h265 ? DdaOrderH265 : DdaOrderH264;
        var verdicts = ProbeFamily(ffmpeg, h265 ? CodecPolicy.H265 : CodecPolicy.H264, candidates, width, height, fps, selfCapture: true);
        return candidates.Any(c => verdicts.TryGetValue(c, out var ok) && ok);
    }

    /// <summary>
    /// 跑 1 帧 ddagrab 并从 stderr 解析输出分辨率（成功 = 该输出此刻可采集）。
    /// 公开给停滞看护用：零输出时区分"静态桌面"（首帧总能取到 → 成功）与"采集链真死"。
    /// </summary>
    public static (bool ok, int w, int h) ProbeDdaOutput(string ffmpeg, int idx)
    {
        try
        {
            var psi = new ProcessStartInfo(ffmpeg,
                $"-hide_banner -v info -f lavfi -i \"ddagrab=output_idx={idx}:framerate=5\" -frames:v 1 -f null -")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return (false, 0, 0);
            var text = p.StandardError.ReadToEnd();
            p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(10000)) { try { p.Kill(true); } catch { } return (false, 0, 0); }
            if (p.ExitCode != 0) return (false, 0, 0);
            // 行样例：Stream #0:0: Video: wrapped_avframe, d3d11, 3200x2000 [SAR 1:1 ...]
            var m = System.Text.RegularExpressions.Regex.Match(text, @"d3d11[^,]*,\s*(\d+)x(\d+)");
            if (m.Success && int.TryParse(m.Groups[1].Value, out var w) && int.TryParse(m.Groups[2].Value, out var h))
                return (true, w, h);
            return (false, 0, 0);
        }
        catch { return (false, 0, 0); }
    }

    /// <summary>
    /// 解析出可用的编码器名（2026-10-14 重构：探测结果进 EncoderCapabilityCache，
    /// 键含 ffmpeg/显卡/编码族/编码器/是否dda，与分辨率无关）。
    /// 【2026-10-11 H.265 黑屏事故背景】旧键缺编码族维度：H.264 会话缓存的 h264_qsv 被
    /// H.265 协商命中 → ffmpeg 秒退无限循环；新键把编码族定死在条目里。
    /// </summary>
    public static string ResolveEncoder(string ffmpeg, string configured, Logger log, int width, int height, int fps,
        string codec = CodecPolicy.H264, bool selfCapture = false)
    {
        var h265 = CodecPolicy.IsH265(codec);
        var family = h265 ? CodecPolicy.H265 : CodecPolicy.H264;
        var order = selfCapture
            ? (h265 ? DdaOrderH265 : DdaOrderH264)
            : (h265 ? AutoOrderH265 : AutoOrderH264);
        var software = h265 ? "libx265" : "libx264";
        var cache = EncoderCapabilityCache.Default;
        var ffmpegId = EncoderCapabilityCache.FfmpegIdentity(ffmpeg);
        var adapterId = EncoderCapabilityCache.AdapterIdentity();
        EncoderCapabilityId Id(string enc) => new(ffmpegId, adapterId, family, enc, selfCapture);

        // 【2026-10-14 防 ddagrab→libx* 坑】运行期降级到软件编码后，ddagrab 的 d3d11 帧
        // 软编消费不了（命令行里没有 hwdownload）。这种情况上层（CreateSource）应直接跳过
        // dda 用 WGC+管道；这里兜底拒绝，绝不拼出一条必然失败的命令行。
        if (selfCapture && SoftwareForced(codec))
            throw new InvalidOperationException($"编码族 {family} 已降级软件编码，与 ddagrab 显卡直通不兼容（应回退 WGC 管道）");

        // 运行期降级路径（3-strike → ForceSoftware 后）：该族一律软件编码（结果仅内存缓存）
        if (SoftwareForced(codec))
        {
            if (cache.Lookup(Id(software)) == true) return software;
            if (Probe(ffmpeg, software, width, height, fps))
            {
                cache.Record(Id(software), true);
                return software;
            }
            // 软编都探测失败 = ffmpeg 本身有问题；此时回头去试刚被降级的硬编只会把事故放大
            throw new InvalidOperationException($"编码族 {family} 已降级软件编码，但 {software} 探测也失败（ffmpeg 可用？）");
        }

        // 配置的编码器只在该编码族内生效：协商切到 h265 后，配置里的 h264_nvenc 不能拿来编 hevc
        var useConfigured = !string.IsNullOrWhiteSpace(configured)
                            && !configured.Equals("auto", StringComparison.OrdinalIgnoreCase)
                            && FamilyMatches(configured, codec);
        if (useConfigured)
        {
            var cfgId = Id(configured!);
            var cached = cache.Lookup(cfgId);
            bool ok = cached ?? Probe(ffmpeg, configured!, width, height, fps);
            if (cached is null) cache.Record(cfgId, ok);
            if (ok) return configured!;
            log.Warn($"配置的编码器 {configured} 不可用，回退自动选择");
        }

        var verdicts = ProbeFamily(ffmpeg, family, order, width, height, fps, selfCapture);
        foreach (var cand in order)
        {
            if (verdicts.TryGetValue(cand, out var ok) && ok)
            {
                log.Info($"自动选择编码器：{cand}");
                return cand;
            }
        }
        throw new InvalidOperationException($"找不到可用的编码器（{string.Join("/", order)} 都失败）");
    }

    /// <summary>
    /// 探测一组候选：先查能力缓存，未命中的**并行**探测（错峰 300ms 启动，避免多个 ffmpeg
    /// 同时初始化 GPU 会话互相挤兑——对标 UU codec cache 的批量并发探测），结果全部回写缓存
    /// （同族一次探测，ResolveEncoder 与 H265HardwareAvailable 共享受益）。
    /// 探测成功与否都会进缓存：硬件成功落盘、失败带 120s TTL、软编仅内存（准入规则见 EncoderCapabilityCache）。
    /// </summary>
    private static Dictionary<string, bool> ProbeFamily(string ffmpeg, string family, string[] candidates,
        int width, int height, int fps, bool selfCapture)
    {
        var cache = EncoderCapabilityCache.Default;
        var ffmpegId = EncoderCapabilityCache.FfmpegIdentity(ffmpeg);
        var adapterId = EncoderCapabilityCache.AdapterIdentity();
        var results = new Dictionary<string, bool>();
        var probing = new List<(string Encoder, Task<bool> Task)>();
        foreach (var cand in candidates)
        {
            var verdict = cache.Lookup(new EncoderCapabilityId(ffmpegId, adapterId, family, cand, selfCapture));
            if (verdict is bool b)
            {
                results[cand] = b;
                continue;
            }
            int delayMs = 300 * probing.Count;   // 错峰：第 n 个未缓存候选晚 300ms*n 启动
            probing.Add((cand, Task.Run(async () =>
            {
                if (delayMs > 0) await Task.Delay(delayMs).ConfigureAwait(false);
                return Probe(ffmpeg, cand, width, height, fps);
            })));
        }
        foreach (var (enc, task) in probing)
        {
            bool ok;
            try { ok = task.GetAwaiter().GetResult(); }
            catch { ok = false; }
            results[enc] = ok;
            cache.Record(new EncoderCapabilityId(ffmpegId, adapterId, family, enc, selfCapture), ok);
        }
        return results;
    }

    /// <summary>
    /// 探测编码器是否真的能用。**必须按真实分辨率/帧率探测**（2026-10-09）：
    /// 旧版用 64x64 小图探测，半残的驱动（尤其 Intel QSV 的 -low_power 硬编码路径）
    /// 小图能过、1080p30 实跑就反复失败 → 码流无限重建 → "换一台电脑就特别卡"。
    /// 探测参数与实跑完全一致（同分辨率、同编码参数），探测过 = 实跑能用。
    /// </summary>
    private static bool Probe(string ffmpeg, string encoder, int width, int height, int fps)
    {
        try
        {
            var psi = new ProcessStartInfo(ffmpeg,
                $"-hide_banner -v error -f lavfi -i color=c=black:s={width}x{height}:r={fps} " +
                $"-frames:v {Math.Max(1, Math.Min(fps, 5))} -c:v {encoder} {EncoderExtraArgs(encoder)} -f null -")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            p.StandardError.ReadToEnd();
            p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(15000))
            {
                try { p.Kill(true); } catch { }
                return false;
            }
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// 每个编码器的专属参数（实跑与探测共用同一份，保证"探测过=实跑能用"）。
    /// 【2026-10-10 显卡调优】围绕"卡"的三处调整：
    ///  · nvenc：p1→p3。p1 是最差画质档（运动画面糊成块，观感就是"卡"）；
    ///    p3 仍属快档（NVENC 编 1080p30 余量巨大），画质明显改善，-tune ull 保住低延迟。
    ///  · amf：speed→balanced，同理；-usage lowlatency 保留。
    ///  · qsv（含 Arc）：去掉 -low_power 1。low_power（VDENC 直通路径）在 Arc 独显上
    ///    兼容性差、码控不稳；普通 VME 路径全平台稳定、画质更好；-async_depth 1 压低内部缓冲。
    /// 【2026-10-10 H.265 家族】同思路的等价参数；libx265 的 repeat-headers 必须开 ——
    /// 主控端中途重建解码器后要靠带内 VPS/SPS/PPS（每个关键帧前重发）才能接上流。
    /// </summary>
    private static string EncoderExtraArgs(string encoder) => encoder switch
    {
        "libx264"  => "-preset ultrafast -tune zerolatency -profile:v baseline -level 4.0 " +
                      "-x264-params \"scenecut=0:rc-lookahead=0:sync-lookahead=0:repeat-headers=1\" ",
        "h264_nvenc" => "-preset p3 -tune ull -rc cbr -zerolatency 1 -profile:v baseline -delay 0 ",
        "h264_qsv"  => "-preset veryfast -profile:v baseline -async_depth 1 ",
        // 【A 卡从未用上硬编码的根因】h264_amf 的 profile 只认数字（66=baseline），
        // 写 "baseline" 会解析失败 → 探测不过 → 一直静默退到 CPU 软编码（更慢更卡）。
        "h264_amf"  => "-quality balanced -usage lowlatency -rc cbr -profile:v 66 ",
        "libx265"  => "-preset ultrafast -tune zerolatency " +
                      "-x265-params \"scenecut=0:rc-lookahead=0:bframes=0:repeat-headers=1\" ",
        "hevc_nvenc" => "-preset p3 -tune ull -rc cbr -zerolatency 1 -delay 0 ",
        "hevc_qsv"  => "-preset veryfast -async_depth 1 ",
        "hevc_amf"  => "-quality balanced -usage lowlatency -rc cbr ",
        _ => "-profile:v baseline ",
    };

    // ---------------------------------------------------------------- 生命周期

    public bool Start()
    {
        Stop();
        EncoderName = ResolveEncoder(_ffmpeg, _configured, _log, _width, _height, _fps, _codec, _selfCapture);
        var args = BuildArgs(EncoderName);
        _log.Info($"启动 ffmpeg：{EncoderName}（{_codec.ToUpperInvariant()}{(_selfCapture ? "+ddagrab 显卡直采" : "")}） {_width}x{_height}@{_fps} {_kbps}kbps");
        _stderrTail.Clear();
        try
        {
            var psi = new ProcessStartInfo(_ffmpeg, args)
            {
                RedirectStandardInput = !_selfCapture,   // 自采集模式没有 stdin 喂帧
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardErrorEncoding = Encoding.UTF8,
            };
            var p = Process.Start(psi);
            if (p == null) throw new InvalidOperationException("ffmpeg 启动失败");
            _proc = p;
            _stdin = _selfCapture ? null : p.StandardInput.BaseStream;
            _alive = true;
            _reader = new Thread(ReadLoop) { IsBackground = true, Name = "ffmpeg-stdout" };
            _reader.Start();
            _ = Task.Run(() => ReadStderr(p));
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"启动 ffmpeg 失败：{ex.Message}";
            _log.Error(LastError);
            _alive = false;
            return false;
        }
    }

    private string BuildArgs(string encoder)
    {
        var sb = new StringBuilder();
        sb.Append("-hide_banner -loglevel error -nostdin ");
        sb.Append("-fflags nobuffer -flags low_delay ");
        if (_selfCapture)
        {
            // 显卡直通（ddagrab）：ffmpeg 自己在显卡上采集（帧全程留在显存直喂硬件编码器）。
            // 没有 stdin 喂帧、没有 BGRA 过 CPU/管道 —— 这就是对标 UU 零拷贝管线的全部意义。
            // draw_mouse=0：与 WGC（IsCursorCaptureEnabled=false）/GDI 的策略对齐——
            // 远端光标由主控端自绘，被控端画面里不烤任何光标（2026-10-14）。
            sb.Append($"-f lavfi -i \"ddagrab=output_idx={_ddaOutputIdx}:framerate={_fps}:draw_mouse=0\" ");
        }
        else
        {
            // NV12 是 NV12 管道（WGC 采集侧已转换，BT.601 与旧 BGRA 管线同色彩）；
            // BGRA 是 GDI/画布拼接路径
            sb.Append($"-f rawvideo -pixel_format {_inputFormat} -video_size {_width}x{_height} -framerate {_fps} -i pipe:0 ");
        }
        sb.Append("-an -sn -dn ");
        sb.Append($"-c:v {encoder} ");
        sb.Append(EncoderExtraArgs(encoder));
        // GOP 收到 1 秒：一是主控端刚接入/刚重置时能很快等到关键帧，二是丢块之后
        // 最多 1 秒就能靠下一个关键帧自愈（慢链路上 GOP 太长会让画面一直起不来）。
        int gop = Math.Max(1, _fps);
        // 【2026-10-10 显卡调优】bufsize 从 2 秒压到 0.5 秒：CBR 的平滑缓冲越大，编码器
        // 攒帧越久（最坏顶着 bufsize 的延迟出帧）——远程操作要的是"立刻出"，不是码率平滑。
        int bufK = Math.Max(200, _kbps / 2);
        sb.Append($"-b:v {_kbps}k -maxrate {_kbps}k -bufsize {bufK}k ");
        // 输出像素格式跟随输入：NV12 输入直接用（qsv/nvenc/amf 原生格式，省一次平面重排）；
        // BGRA 输入（GDI/画布）保持 yuv420p（旧管线行为）
        string outPix = _selfCapture ? "nv12" : (_inputFormat == "nv12" ? "nv12" : "yuv420p");
        sb.Append($"-g {gop} -keyint_min {gop} -sc_threshold 0 -pix_fmt {outPix} -bf 0 ");
        // Annex-B 裸流封装名：H.264 = h264，H.265 = hevc（主控端解码器用对应的 -f 接）
        sb.Append($"-flush_packets 1 -f {(CodecPolicy.IsH265(_codec) ? "hevc" : "h264")} pipe:1");
        return sb.ToString();
    }

    private void ReadLoop()
    {
        var buf = new byte[16 * 1024];
        try
        {
            var stdout = _proc!.StandardOutput.BaseStream;
            while (_alive)
            {
                int n = stdout.Read(buf, 0, buf.Length);
                if (n <= 0) break;
                Interlocked.Increment(ref _chunkCount);
                Interlocked.Add(ref _byteCount, n);
                try { Chunk?.Invoke(buf, n); }
                catch (Exception ex) { _log.Warn($"处理码流块失败：{ex.Message}"); }
            }
        }
        catch (Exception ex)
        {
            if (_alive) _log.Warn($"ffmpeg 读取循环结束：{ex.Message}");
        }
        finally
        {
            _alive = false;
            var tail = _stderrTail.ToString().Trim();
            if (tail.Length > 0) LastError = tail;
            _log.Info($"ffmpeg 输出循环结束（编码器 {EncoderName}，共 {_chunkCount} 块 {_byteCount / 1024}KB）" +
                      (tail.Length > 0 ? $" 最后错误：{Truncate(tail, 300)}" : ""));
        }
    }

    private void ReadStderr(Process p)
    {
        try
        {
            string? line;
            while ((line = p.StandardError.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                lock (_stderrTail)
                {
                    _stderrTail.AppendLine(line);
                    if (_stderrTail.Length > 4000) _stderrTail.Remove(0, _stderrTail.Length - 4000);
                }
                _log.Debug($"ffmpeg: {line}");
            }
        }
        catch { }
    }

    /// <summary>喂一帧（BGRA）。返回 false 表示编码器已死</summary>
    public bool WriteFrame(ReadOnlySpan<byte> bgra)
    {
        var stdin = _stdin;
        if (!_alive || stdin == null) return false;
        try
        {
            stdin.Write(bgra);
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"写入编码器失败：{ex.Message}";
            _alive = false;
            return false;
        }
    }

    public void Stop()
    {
        _alive = false;
        try { _stdin?.Flush(); } catch { }
        try { _stdin?.Close(); } catch { }
        _stdin = null;
        try
        {
            if (_proc is { HasExited: false })
            {
                if (!_proc.WaitForExit(1500)) _proc.Kill(entireProcessTree: true);
            }
        }
        catch { }
        try { _proc?.Dispose(); } catch { }
        _proc = null;
        try { _reader?.Join(500); } catch { }
        _reader = null;
    }

    public void Dispose() => Stop();

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];
}
