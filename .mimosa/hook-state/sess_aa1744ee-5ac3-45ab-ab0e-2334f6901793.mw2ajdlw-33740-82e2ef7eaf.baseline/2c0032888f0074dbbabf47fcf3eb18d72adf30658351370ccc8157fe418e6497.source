namespace Agent.Common;

/// <summary>
/// 码率/帧率自适应：链路拥塞就逐级降，通畅一段时间再**探测式**逐级升（2026-10-10 升级，
/// 对标 UU 远程的 auto quality loop：升档 → 观察窗验证 → 失败回滚 + 退避）。
///
/// 【为什么需要】
/// 链路（尤其"被控端 → 中继 → 主控端"这条）的可用带宽是变化的，而 ffmpeg 的码率是**启动参数**。
/// 当实际只跑得动 300kbps 却按 4Mbps 编码时，码流会一路堆到共享帧环里写不进去。老代码的反应是
/// "丢掉这一块并重建码流" —— 而重建码流会让主控端**重置解码器、重新等关键帧**，在慢链路上这等于
/// 永远出不来画面（实测：被控端采集 23fps、累计已发 316MB，主控端却一帧都解不出来，
/// 界面上只有黑屏和 FPS/码率 `--`）。
///
/// 这里的做法：拥塞（帧环写不进去 / 出口队列积压）就降一档，还不够就再降，最后连帧率一起降；
/// 让链路能承载住，画面先动起来。
///
/// 【升档探测（本次新增）】
/// 旧逻辑"通畅 30 秒就升一档"没有验证环节：带宽实际只多了一点点时，升上去立刻又拥塞，
/// 再等 30 秒又升 —— 画质在档位间来回跳。现在升档是一次数值探测：
///   升一档 → 观察 10 秒 → 窗口内又拥塞就**立即回滚**并指数退避（1→2→5 分钟封顶），
///   连续 5 分钟干净才清空退避。UUDA 的做法是对带宽做主动测量（probe_bps 超过下一档均值才升），
/// 我们没有 BWE，"试 10 秒看拥塞信号"就是同一件事的轻量实现。
///
/// 档位（以配置值为基准；帧率也从第 2 档起一起降——降帧率同时减 CPU 与码率压力；
/// 最后几档是绝对码率，专门对付"上行只有一两百 kbps"的弱网）：
///   0 = 配置值（默认 4000k / 30fps）
///   1 = 60%
///   2 = 35% 且帧率 3/4
///   3 = 20% 且帧率 2/3
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

    // ---- 升档探测（2026-10-10）----
    /// <summary>升档后的验证观察窗：窗口内再拥塞 = 探测失败，立即回滚</summary>
    private const long VerifyWindowMs = 10_000;
    /// <summary>升档失败后的起步退避（随后指数翻倍）</summary>
    private const long StepUpBackoffInitialMs = 60_000;
    /// <summary>退避上限</summary>
    private const long StepUpBackoffMaxMs = 300_000;
    /// <summary>连续干净这么久 → 清空退避（链路真的好了，恢复正常升档节奏）</summary>
    private const long BackoffResetCleanMs = 300_000;

    /// <summary>
    /// 前几档降得快一点：接入那一刻是按配置码率（常见 4000k）硬推的，而实测上行可能只有它的一半 ——
    /// 这段时间视频队列是满的、画面必然是花的（丢帧丢到关键帧才自愈）。2.5 秒一档，
    /// 5~8 秒就能降到链路承载得住的位置，把"开局那段糊"压到最短。
    /// </summary>
    private long StepDownInterval => _level <= 2 ? StepDownFastMs : StepDownDedupMs;

    private readonly Logger _log;
    private readonly int _configuredKbps;
    private readonly int _configuredFps;
    /// <summary>时钟注入（单测用；默认 Environment.TickCount64）</summary>
    private readonly Func<long> _now;
    private int _level;
    private long _lastStepTick;
    private long _cleanSince;
    private long _lastSeenDrops;
    /// <summary>升档验证期截止时刻（0 = 不在验证期）</summary>
    private long _verifyUntil;
    /// <summary>当前升档退避时长（0 = 无退避）</summary>
    private long _stepUpBackoffMs;
    /// <summary>升档退避截止时刻</summary>
    private long _stepUpBlockedUntil;

    public BitrateGovernor(Logger log, int kbps, int fps, Func<long>? clock = null)
    {
        _log = log;
        _configuredKbps = Math.Max(FloorKbps, kbps);
        _configuredFps = Math.Max(5, fps);
        _now = clock ?? (static () => Environment.TickCount64);
        _cleanSince = _now();
    }

    public int Level => _level;
    public int BitrateKbps => LevelKbps(_level);
    public int FrameRate => LevelFps(_level);
    /// <summary>是否已经降到最低档（仍然丢块就说明链路比 250kbps 还差，只能靠丢帧维持）</summary>
    public bool AtFloor => _level >= MaxLevel;
    /// <summary>是否处于升档验证期（窗口内再拥塞会立即回滚）</summary>
    public bool Verifying => _verifyUntil != 0 && _now() < _verifyUntil;

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
        long now = _now();
        bool droppedThisTick = totalDropped > _lastSeenDrops;
        _lastSeenDrops = totalDropped;
        bool congested = droppedThisTick || outboundCongested;
        if (congested) _cleanSince = 0;
        else if (_cleanSince == 0) _cleanSince = now;

        if (congested)
        {
            // 升档探测失败：观察窗内链路又拥塞 → 立即回滚到原来的档位，并拉长下次升档的间隔。
            // 不走下面的常规降档（回滚本身就降了一档），也不受降档节流限制（探测失败要立刻退）。
            if (_verifyUntil != 0 && now < _verifyUntil)
            {
                _verifyUntil = 0;
                _level = Math.Min(MaxLevel, _level + 1);
                _lastStepTick = now;
                _stepUpBackoffMs = _stepUpBackoffMs <= 0
                    ? StepUpBackoffInitialMs
                    : Math.Min(StepUpBackoffMaxMs, _stepUpBackoffMs * 2);
                _stepUpBlockedUntil = now + _stepUpBackoffMs;
                _log.Warn($"升档探测失败（观察窗内链路又拥塞）：回滚到第 {_level} 档 " +
                          $"{BitrateKbps}kbps/{FrameRate}fps，{_stepUpBackoffMs / 1000}s 后再试");
                return true;
            }
            _verifyUntil = 0;

            if (_level < MaxLevel && now - _lastStepTick >= StepDownInterval)
            {
                _level++;
                _lastStepTick = now;
                _log.Warn($"链路拥塞（{(droppedThisTick ? "帧环写不进" : "中继队列积压")}），" +
                          $"码率自适应降到第 {_level} 档：{BitrateKbps}kbps / {FrameRate}fps");
                return true;
            }
        }
        else
        {
            // 连续长时间干净 → 清空升档退避（链路整体好转，别再拿旧惩罚挡着）
            if (_stepUpBackoffMs > 0 && _cleanSince != 0 && now - _cleanSince >= BackoffResetCleanMs)
            {
                _stepUpBackoffMs = 0;
                _stepUpBlockedUntil = 0;
            }

            if (_cleanSince != 0 && _level > 0
                && now - _cleanSince >= CleanBeforeStepUpMs && now - _lastStepTick >= StepUpDedupMs
                && now >= _stepUpBlockedUntil)
            {
                _level--;
                _lastStepTick = now;
                _verifyUntil = now + VerifyWindowMs;
                _log.Info($"链路已通畅 {CleanBeforeStepUpMs / 1000}s，尝试升到第 {_level} 档：" +
                          $"{BitrateKbps}kbps/{FrameRate}fps（{VerifyWindowMs / 1000}s 内再拥塞会立即回滚）");
                return true;
            }
        }
        return false;
    }

    public string Describe() =>
        $"档位 {_level}/{MaxLevel} {BitrateKbps}kbps/{FrameRate}fps" +
        (_configuredKbps != BitrateKbps || _configuredFps != FrameRate
            ? $"（配置 {_configuredKbps}kbps/{_configuredFps}fps）"
            : "") +
        (_stepUpBackoffMs > 0 ? $"，升档退避中（{_stepUpBackoffMs / 1000}s）" : "");

    private int LevelKbps(int level)
    {
        int kbps = level switch
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
        // 弱配置兜底（2026-10-10）：小码率配置（如 500k）下绝对值档位（200/150）会反超
        // 百分比档位 —— "降档反而升码率"会把弱链路直接压死。档位必须单调不升。
        if (level > 0)
            kbps = Math.Min(kbps, LevelKbps(level - 1));
        return Math.Max(FloorKbps, kbps);
    }

    private int LevelFps(int level) => level switch
    {
        >= 7 => 5,
        6 => 8,
        5 => Math.Max(6, _configuredFps / 2),
        4 => Math.Max(8, _configuredFps * 2 / 3),
        3 => Math.Max(9, _configuredFps * 2 / 3),
        2 => Math.Max(12, _configuredFps * 3 / 4),
        _ => _configuredFps,
    };

    private int Pct(double p) => Math.Max(FloorKbps, (int)Math.Round(_configuredKbps * p));
}
