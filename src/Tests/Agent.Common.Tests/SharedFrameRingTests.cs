using Agent.Common;
using Xunit;

namespace Agent.Common.Tests;

/// <summary>
/// 共享内存环形缓冲测试（策划 §2.3 + §十一「共享内存测试：帧写入/读取正确性」）
/// 每个测试用独立名字，避免并行冲突
/// </summary>
public class SharedFrameRingTests
{
    /// <summary>
    /// 创建共享内存需要 SeCreateGlobalPrivilege（管理员），因为跨会话对象必须在 Global 命名空间。
    /// 非管理员环境下这些用例无法成立 —— 直接跳过并说明原因，避免误报失败。
    /// 产品运行时 Coordinator 始终以管理员启动（app.manifest 要求），所以这是测试环境限制。
    /// </summary>
    public static bool Elevated
    {
        get
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(id)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
    }

    private static (SharedFrameRing Ring, string Name, string Ready, string Consumed) CreateRing(int capacity = 64 * 1024)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var name = $@"Global\RCTest_{suffix}";
        var ready = $@"Global\RCTestReady_{suffix}";
        var consumed = $@"Global\RCTestConsumed_{suffix}";
        return (SharedFrameRing.Create(name, ready, consumed, capacity), name, ready, consumed);
    }

    private const string SkipNote = "需要管理员权限（Global 命名空间共享内存）—— 本次跳过";

    [Fact]
    public void 写入后读出内容一致()
    {
        if (!Elevated) { Console.WriteLine(SkipNote); return; }
        var (ring, name, ready, consumed) = CreateRing();
        using (ring)
        {
            var data = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
            Assert.True(ring.Write(data));
            var read = ring.Read(1000);
            Assert.NotNull(read);
            Assert.Equal(data, read);
            Assert.Equal(1, ring.FrameCount);
        }
        Cleanup(name, ready, consumed);
    }


    [Fact]
    public void 多帧顺序_先进先出()
    {
        if (!Elevated) { Console.WriteLine(SkipNote); return; }
        var (ring, name, ready, consumed) = CreateRing();
        using (ring)
        {
            for (int i = 0; i < 50; i++)
                Assert.True(ring.Write(new byte[] { (byte)i, (byte)(i + 1) }));
            for (int i = 0; i < 50; i++)
            {
                var frame = ring.Read(500);
                Assert.NotNull(frame);
                Assert.Equal(new byte[] { (byte)i, (byte)(i + 1) }, frame);
            }
            Assert.Equal(50, ring.FrameCount);
            Assert.Null(ring.Read(50));
        }
        Cleanup(name, ready, consumed);
    }


    [Fact]
    public void 换圈边界_大数据量仍然正确()
    {
        if (!Elevated) { Console.WriteLine(SkipNote); return; }
        // 16KB 容量 + 1KB 记录 → 必然多次换圈
        var (ring, name, ready, consumed) = CreateRing(16 * 1024);
        using (ring)
        {
            var rnd = new Random(1234);
            for (int i = 0; i < 200; i++)
            {
                int size = 900 + rnd.Next(200);
                var data = new byte[size];
                rnd.NextBytes(data);
                Assert.True(ring.Write(data, 2000));
                var read = ring.Read(2000);
                Assert.NotNull(read);
                Assert.Equal(data, read);
            }
            Assert.Equal(0, ring.DropCount);
        }
        Cleanup(name, ready, consumed);
    }


    [Fact]
    public void 写满时返回false并计入丢弃()
    {
        if (!Elevated) { Console.WriteLine(SkipNote); return; }
        var (ring, name, ready, consumed) = CreateRing(8 * 1024);
        using (ring)
        {
            // 不读，一直写 → 很快触发丢弃
            var payload = new byte[1024];
            int ok = 0, dropped = 0;
            for (int i = 0; i < 50; i++)
            {
                if (ring.Write(payload, timeoutMs: 20)) ok++;
                else dropped++;
            }
            Assert.True(dropped > 0, "应该出现缓冲满被丢弃的情况");
            Assert.True(ok > 0);
            Assert.Equal(dropped, ring.DropCount);
            // 丢弃后仍能正常读出已写入的帧
            Assert.NotNull(ring.Read(500));
        }
        Cleanup(name, ready, consumed);
    }


    [Fact]
    public void 超大帧抛异常()
    {
        if (!Elevated) { Console.WriteLine(SkipNote); return; }
        var (ring, name, ready, consumed) = CreateRing(8 * 1024);
        using (ring)
        {
            Assert.Throws<ProtocolException>(() => ring.Write(new byte[16 * 1024]));
        }
        Cleanup(name, ready, consumed);
    }


    [Fact]
    public void Reset清空读写位置()
    {
        if (!Elevated) { Console.WriteLine(SkipNote); return; }
        var (ring, name, ready, consumed) = CreateRing();
        using (ring)
        {
            ring.Write(new byte[100]);
            Assert.True(ring.UsedBytes > 0);
            ring.Reset();
            Assert.Equal(0, ring.UsedBytes);
            Assert.Equal(0, ring.WritePos);
            Assert.Equal(0, ring.ReadPos);
            Assert.Null(ring.Read(50));
        }
        Cleanup(name, ready, consumed);
    }


    [Fact]
    public void 另一个句柄可以打开并读写()
    {
        if (!Elevated) { Console.WriteLine(SkipNote); return; }
        var (ring, name, ready, consumed) = CreateRing();
        using (ring)
        {
            using var reader = SharedFrameRing.Open(name, ready, consumed);
            Assert.Equal(ring.Capacity, reader.Capacity);

            var payload = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
            Assert.True(ring.Write(payload));
            var got = reader.Read(1000);
            Assert.Equal(payload, got);

            // 反向：reader 写、ring 读（模拟双向）
            Assert.True(reader.Write(new byte[] { 1, 1, 1 }));
            Assert.Equal(new byte[] { 1, 1, 1 }, ring.Read(1000));
        }
        Cleanup(name, ready, consumed);
    }


    [Fact]
    public void 空读超时返回null()
    {
        if (!Elevated) { Console.WriteLine(SkipNote); return; }
        var (ring, name, ready, consumed) = CreateRing();
        using (ring)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Assert.Null(ring.Read(300));
            Assert.True(sw.ElapsedMilliseconds >= 250, $"应等待约 300ms，实际 {sw.ElapsedMilliseconds}ms");
        }
        Cleanup(name, ready, consumed);
    }


    [Fact]
    public void 容量与统计字段正确()
    {
        if (!Elevated) { Console.WriteLine(SkipNote); return; }
        var (ring, name, ready, consumed) = CreateRing(32 * 1024);
        using (ring)
        {
            Assert.Equal(32 * 1024, ring.Capacity);
            ring.Write(new byte[1000]);
            Assert.True(ring.UsedBytes >= 1004);
            Assert.True(ring.FreeBytes <= 32 * 1024 - 1004);
            Assert.True(ring.LastWriteUtc > DateTime.UtcNow.AddMinutes(-1));
        }
        Cleanup(name, ready, consumed);
    }

    private static void Cleanup(string name, string ready, string consumed)
    {
        // 命名对象随句柄关闭由内核回收；这里只做 GC 提示，避免测试间互相干扰
        GC.KeepAlive(name);
        GC.KeepAlive(ready);
        GC.KeepAlive(consumed);
    }
}
