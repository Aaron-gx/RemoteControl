using Agent.Common;

namespace Agent.Worker;

/// <summary>
/// 码率/帧率自适应：链路拥塞就逐级降，通畅一段时间再逐级升回去。
///
/// 【为什么需要】
/// 链路（尤其"被控端 → 中继 → 主控端"这条）的可用带宽是变化的，而 ffmpeg 的码率是**启动参数**。
/// 当实际只跑得动 300kbps 却按 4Mbps 编码时，码流会一路堆到共享帧环里写不进去。老代码的反应是
/// "丢掉这一块并重建码流" —— 而重建码流会让主控端**重置解码器、重新等关键帧**，在慢链路上这等于
/// 永远出不来画面（实测：被控端采集 23fps、累计已发 316MB，主控端却一帧都解不出来，
/// 界面上只有黑屏和 FPS/码率 `--`）。
///
/// 这里的做法：拥塞（帧环写不进去）就降一档码率，还不够就再降，最后连帧率一起降；
/// 让链路能承载住，画面先动起来。通畅 30 秒后再逐级升回去，不会一直卡在低画质。
///
/// 档位（以配置值为基准；最后几档是绝对码率，专门对付"上行只有一两百 kbps"的弱网）：
///   0 = 配置值（默认 4000k / 30fps）
///   1 = 60%   2 = 35%   3 = 20%
///   4 = 12% 且帧率降到 2/3
///   5 = ≥150k 且帧率降到 1/2
///   6 = 150k / 8fps
///   7 = 100k / 5fps   ← 画面很糊但能动，先保住"能操作"
/// </summary>
public sealed class BitrateGovernor
{
    private const int FloorKbps = 100;
    private const int MaxLevel = 7;
    private const long StepDownDedupMs = 6000;    // 两次降档至少间隔这么久
    private const long StepDownFastMs = 2500;     // 刚接入还没降过档时降得快些
    private const long CleanBeforeStepUpMs = 30000; // 连续这么久没拥塞才考虑升档
    private const long StepUpDedupMs = 20000;

    /// <summary>
    /// 前几档降得快一点：接入那一刻是按配置码率（常见 4000k）硬推的，而实测上行可能只有它的一半 ——
    /// 这段时间视频队列是满的、画面必然是花的（丢帧丢到关键帧才自愈）。2.5 秒一档，
    /// 5~8 秒就能降到链路承载得住的位置，把"开局那段糊"压到最短。
    /// </summary>
    private long StepDownInterval => _level <= 2 ? StepDownFastMs : StepDownDedupMs;

    private readonly Logger _log;
    private readonly int _configuredKbps;
    private readonly int _configuredFps;
    private int _level;
    private long _lastStepTick;
    private long _cleanSince;
    private long _lastSeenDrops;

    public BitrateGovernor(Logger log, int kbps, int fps)
    {
        _log = log;
        _configuredKbps = Math.Max(FloorKbps, kbps);
        _configuredFps = Math.Max(5, fps);
        _cleanSince = Environment.TickCount64;
    }

    public int Level => _level;
    public int BitrateKbps => LevelKbps(_level);
    public int FrameRate => LevelFps(_level);
    /// <summary>是否已经降到最低档（仍然丢块就说明链路比 250kbps 还差，只能靠丢帧维持）</summary>
    public bool AtFloor => _level >= MaxLevel;

    /// <summary>
    /// 每秒调用一次。
    /// </summary>
    /// <param name="totalDropped">帧环写不进去而被丢弃的累计块数（本地信号）</param>
    /// <param name="outboundCongested">
    /// 协调器给的"中继那条腿堵了"信号（队列积压/刚挤掉帧）。
    /// 这个信号必需：Worker 只看得到帧环，而帧环会被协调器慢慢抽干、不一定写不进去 ——
    /// 实测就是这么漏掉的：帧环丢弃数一直是 0、自适应一动不动，而中继队列已经积压了几百条。
    /// </param>
    /// <returns>返回 true 表示参数变了，调用方需要按新参数重建编码器。</returns>
    public bool Report(long totalDropped, bool outboundCongested)
    {
        long now = Environment.TickCount64;
        bool droppedThisTick = totalDropped > _lastSeenDrops;
        _lastSeenDrops = totalDropped;
        bool congested = droppedThisTick || outboundCongested;
        if (congested) _cleanSince = 0;
        else if (_cleanSince == 0) _cleanSince = now;

        if (congested && _level < MaxLevel && now - _lastStepTick >= StepDownInterval)
        {
            _level++;
            _lastStepTick = now;
            _log.Warn($"链路拥塞（{(droppedThisTick ? "帧环写不进" : "中继队列积压")}），" +
                      $"码率自适应降到第 {_level} 档：{BitrateKbps}kbps / {FrameRate}fps");
            return true;
        }

        if (!congested && _cleanSince != 0 && _level > 0
            && now - _cleanSince >= CleanBeforeStepUpMs && now - _lastStepTick >= StepUpDedupMs)
        {
            _level--;
            _lastStepTick = now;
            _log.Info($"链路已通畅 {CleanBeforeStepUpMs / 1000}s，码率自适应升回第 {_level} 档：" +
                      $"{BitrateKbps}kbps / {FrameRate}fps");
            return true;
        }
        return false;
    }

    public string Describe() =>
        $"档位 {_level}/{MaxLevel} {BitrateKbps}kbps/{FrameRate}fps" +
        (_configuredKbps != BitrateKbps || _configuredFps != FrameRate
            ? $"（配置 {_configuredKbps}kbps/{_configuredFps}fps）" : "");

    private int LevelKbps(int level) => level switch
    {
        0 => _configuredKbps,
        1 => Pct(0.60),
        2 => Pct(0.35),
        3 => Pct(0.20),
        4 => Math.Max(200, Pct(0.12)),
        5 => Math.Max(150, Pct(0.06)),
        6 => 150,
        _ => FloorKbps,
    };

    private int LevelFps(int level) => level switch
    {
        >= 7 => 5,
        6 => 8,
        5 => Math.Max(6, _configuredFps / 2),
        4 => Math.Max(8, _configuredFps * 2 / 3),
        _ => _configuredFps,
    };

    private int Pct(double p) => Math.Max(FloorKbps, (int)Math.Round(_configuredKbps * p));
}
