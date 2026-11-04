using Agent.Common;
using Xunit;

namespace Agent.Common.Tests;

/// <summary>
/// WorkerBridge 泵与看门狗（2026-10-09 VSAQ 事故加固的回归测试）：
/// 1. 正常消息双向透传；
/// 2. toWorker 分发卡死 → PumpWedged 触发 + 自动换泵，后续消息恢复投递
///    （现场表现：画面照常出，但键鼠/打开软件全部失灵——必须能自愈）。
/// </summary>
public class WorkerBridgeTests : IDisposable
{
    private readonly WorkerBridge _bus = new(64 * 1024);

    public void Dispose() => _bus.Dispose();

    private static ProtocolMessage TextMsg(MessageType t, string s)
        => new(t, System.Text.Encoding.UTF8.GetBytes(s));

    [Fact]
    public void 消息双向透传()
    {
        var gotWorker = new List<string>();
        var gotCoord = new List<string>();
        _bus.MessageFromCoordinator += m => gotWorker.Add(System.Text.Encoding.UTF8.GetString(m.Payload));
        _bus.MessageFromWorker += m => gotCoord.Add(System.Text.Encoding.UTF8.GetString(m.Payload));

        _bus.SendToWorker(TextMsg(MessageType.ClipboardText, "to-worker"));
        _bus.SendToCoordinator(TextMsg(MessageType.SoftwareState, "to-coord"));

        var deadline = Environment.TickCount64 + 5000;
        while ((gotWorker.Count < 1 || gotCoord.Count < 1) && Environment.TickCount64 < deadline)
            Thread.Sleep(20);

        Assert.Contains("to-worker", gotWorker);
        Assert.Contains("to-coord", gotCoord);
    }

    [Fact]
    public void 泵卡死后自动换泵恢复投递()
    {
        _bus.DispatchStallMs = 3000;   // 测试缩短阈值（生产 15s）

        var release = new ManualResetEventSlim(false);
        var wedged = new ManualResetEventSlim(false);
        var received = new List<string>();
        var gate = new object();

        _bus.PumpWedged += () => wedged.Set();
        // 第一条消息把分发线程卡住；换泵后新发的消息必须能送达（现场要的自愈）
        var blockFirst = true;
        _bus.MessageFromCoordinator += m =>
        {
            bool first;
            lock (gate) { first = blockFirst; blockFirst = false; }   // 锁内绝不等待（否则把后续投递也锁死）
            if (first) release.Wait(30_000);                           // 卡死模拟（测试结束前放开）
            lock (gate) received.Add(System.Text.Encoding.UTF8.GetString(m.Payload));
        };

        _bus.SendToWorker(TextMsg(MessageType.ClipboardText, "stuck-1"));
        Thread.Sleep(300);                       // 确保它已进入分发

        Assert.True(wedged.Wait(20_000), "看门狗未在 20s 内判定卡死（PumpWedged 未触发）");
        // 换泵发生在卡死判定时：之后新发的消息必须恢复投递（这正是现场要的自愈）
        _bus.SendToWorker(TextMsg(MessageType.ClipboardText, "after-recovery"));
        var deadline = Environment.TickCount64 + 10_000;
        while (!received.Contains("after-recovery") && Environment.TickCount64 < deadline)
            Thread.Sleep(50);

        release.Set();                            // 放行卡住的旧泵（"stuck-1" 迟到送达，属预期）
        Assert.Contains("after-recovery", received);
    }

    [Fact]
    public void 引擎重启时清空积压旧指令()
    {
        var got = new List<string>();
        _bus.MessageFromCoordinator += m => got.Add(System.Text.Encoding.UTF8.GetString(m.Payload));

        _bus.MarkWorkerConnected();               // 模拟引擎上线
        _bus.MarkWorkerDisconnected("测试");
        // 离线期间积压的旧指令（键鼠/打开软件）：
        _bus.SendToWorker(TextMsg(MessageType.ClipboardText, "stale"));
        _bus.MarkWorkerConnected();               // 新引擎上线：stale 应被清掉，不投递
        Thread.Sleep(800);

        Assert.DoesNotContain("stale", got);
    }
}
