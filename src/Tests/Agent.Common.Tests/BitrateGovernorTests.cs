using Xunit;

namespace Agent.Common.Tests;

/// <summary>
/// 码率自适应（BitrateGovernor，2026-10-10 升级版）单元测试：注入时钟推进虚拟时间，
/// 覆盖升档探测的核心闭环 —— 升一档 → 观察窗内又拥塞 → 立即回滚 + 指数退避 →
/// 退避期内不再升档 → 长时间干净后清空退避恢复正常升档。
/// </summary>
public class BitrateGovernorTests
{
    private static (BitrateGovernor gov, Action<long> tick) Make(int kbps = 4000, int fps = 30)
    {
        long now = 0;
        var log = new Logger("gov-test", minLevel: 0, echoConsole: false);
        var gov = new BitrateGovernor(log, kbps, fps, () => now);
        return (gov, t => now = t);
    }

    [Fact]
    public void Congestion_StepsDown_BitrateAndFps()
    {
        var (gov, set) = Make();
        set(1000);
        Assert.False(gov.Report(0, outboundCongested: false));

        // 拥塞降档（前 2 档快速通道 2.5s 一档）
        set(4000);
        Assert.True(gov.Report(0, outboundCongested: true));
        Assert.Equal(1, gov.Level);
        Assert.Equal(2400, gov.BitrateKbps);
        Assert.Equal(30, gov.FrameRate);          // 第 1 档不降帧率

        set(7000);
        Assert.True(gov.Report(0, outboundCongested: true));
        Assert.Equal(2, gov.Level);
        Assert.Equal(1400, gov.BitrateKbps);
        Assert.Equal(22, gov.FrameRate);          // 第 2 档起帧率一起降（30*3/4）

        set(11000);
        Assert.True(gov.Report(0, outboundCongested: true));
        Assert.Equal(3, gov.Level);
        Assert.Equal(800, gov.BitrateKbps);
        Assert.Equal(20, gov.FrameRate);          // 30*2/3
    }

    [Fact]
    public void StepUp_AfterCleanWindow()
    {
        var (gov, set) = Make();
        set(4000);
        Assert.True(gov.Report(0, outboundCongested: true));   // L1（首次降档要过 2.5s 节流）
        set(7000);
        Assert.True(gov.Report(0, outboundCongested: true));   // L2

        set(8000);
        Assert.False(gov.Report(0, outboundCongested: false)); // 干净从这里起算
        set(70000);                                     // 干净 62s ≥ 30s，距上次换档 63s ≥ 20s
        Assert.True(gov.Report(0, outboundCongested: false));  // 升回 L1（进入验证期）
        Assert.Equal(1, gov.Level);
        Assert.True(gov.Verifying);
    }

    [Fact]
    public void StepUpProbeFails_InWindow_RollsBackAndBacksOff()
    {
        var (gov, set) = Make();
        set(4000);
        Assert.True(gov.Report(0, outboundCongested: true));   // L1
        set(7000);
        Assert.True(gov.Report(0, outboundCongested: true));   // L2
        set(8000);
        Assert.False(gov.Report(0, outboundCongested: false));
        set(70000);
        Assert.True(gov.Report(0, outboundCongested: false));  // 升到 L1，验证期 10s

        // 观察窗内又拥塞 → 立即回滚 L2，并进入 60s 升档退避
        set(75000);
        Assert.True(gov.Report(0, outboundCongested: true));
        Assert.Equal(2, gov.Level);
        Assert.False(gov.Verifying);

        // 干净到 30s 以上、但仍在退避期内 → 不升档
        set(80000);
        Assert.False(gov.Report(0, outboundCongested: false));
        set(130000);                                    // 干净 50s，退避截止 75000+60000=135000
        Assert.False(gov.Report(0, outboundCongested: false));

        // 退避到期 → 恢复升档
        set(136000);
        Assert.True(gov.Report(0, outboundCongested: false));
        Assert.Equal(1, gov.Level);
    }

    [Fact]
    public void LongCleanStreak_ClearsBackoff()
    {
        var (gov, set) = Make();
        set(4000);
        Assert.True(gov.Report(0, outboundCongested: true));
        set(7000);
        Assert.True(gov.Report(0, outboundCongested: true));   // L2
        set(8000);
        Assert.False(gov.Report(0, outboundCongested: false));
        set(70000);
        Assert.True(gov.Report(0, outboundCongested: false));  // 升 L1（验证期）
        set(75000);
        Assert.True(gov.Report(0, outboundCongested: true));   // 探测失败回滚 L2，退避 60s

        // 连续干净 5 分钟（≥ 300s）→ 退避清零；随后的干净周期一到就直接升
        set(80000);
        Assert.False(gov.Report(0, outboundCongested: false));
        set(400000);                                    // cleanSince=70000，干净 330s
        Assert.True(gov.Report(0, outboundCongested: false));  // 退避已清 → 正常升档
        Assert.Equal(1, gov.Level);
    }

    [Fact]
    public void CongestionAfterVerifyWindow_IsRegularStepDown()
    {
        var (gov, set) = Make();
        set(4000);
        Assert.True(gov.Report(0, outboundCongested: true));   // L1
        set(7000);
        Assert.True(gov.Report(0, outboundCongested: true));   // L2
        set(8000);
        Assert.False(gov.Report(0, outboundCongested: false));
        set(70000);
        Assert.True(gov.Report(0, outboundCongested: false));  // 升 L1，验证期到 80000

        // 窗口**之后**才拥塞：这是常规降档（走节流），不是探测回滚
        set(90000);
        Assert.True(gov.Report(0, outboundCongested: true));
        Assert.Equal(2, gov.Level);
    }

    [Fact]
    public void WeakLinkTiers_HaveAbsoluteFloor()
    {
        var (gov, set) = Make(kbps: 400, fps: 30);
        long t = 1000;
        for (int i = 1; i <= 7; i++)
        {
            // 每档间隔走够（前 2 档 2.5s，其后 6s），连续拥塞降到最低档
            t += i <= 3 ? 3000 : 6500;
            set(t);
            Assert.True(gov.Report(0, outboundCongested: true));
            Assert.Equal(i, gov.Level);
        }
        Assert.True(gov.AtFloor);
        Assert.Equal(100, gov.BitrateKbps);
        Assert.Equal(5, gov.FrameRate);
        // 最低档后再拥塞：不再换档（返回 false），靠丢帧维持
        set(t + 10000);
        Assert.False(gov.Report(0, outboundCongested: true));
    }
}
