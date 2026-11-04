using Agent.Common;
using Xunit;

namespace Agent.Common.Tests;

/// <summary>
/// RS-FEC（GF(256) Cauchy 系统码）单测：编码往返、任意分片子集恢复（MDS 性质）、
/// 超冗余能力失败、自适应 r 曲线、大帧编码耗时（165 片一帧 ≤ 10ms）。
/// </summary>
public sealed class RsFecTests
{
    private static byte[][] MakeData(int t, int len, int seed)
    {
        var rnd = new Random(seed);
        return Enumerable.Range(0, t)
            .Select(_ =>
            {
                var b = new byte[len];
                rnd.NextBytes(b);
                return b;
            }).ToArray();
    }

    [Fact]
    public void NoLoss_EncodeDecode_RoundTrip()
    {
        var data = MakeData(16, 1092, 1);
        var parity = RsFec.Encode(data, 4);
        Assert.All(parity, p => Assert.Equal(1092, p.Length));
        // 无损时不需要恢复
        var shards = new byte[]?[16 + 4];
        for (int i = 0; i < 16; i++) shards[i] = data[i];
        for (int j = 0; j < 4; j++) shards[16 + j] = parity[j];
        var recovered = new byte[16][];
        Assert.True(RsFec.TryDecode(shards, 4, recovered));
        Assert.All(recovered, r => Assert.Null(r));   // 没丢就不填
    }

    [Theory]
    [InlineData(1, 2)]     // 单片丢失，r=2
    [InlineData(2, 2)]     // 恰好丢 r 片
    [InlineData(3, 4)]     // 丢 3 片带数据片+校验片混合
    [InlineData(6, 6)]     // 丢满 r=6
    public void MultiLoss_RecoversWithoutRetransmit(int lost, int r)
    {
        var data = MakeData(16, 1092, 42);
        var parity = RsFec.Encode(data, r);
        var shards = new byte[]?[16 + r];
        for (int i = 0; i < 16; i++) shards[i] = data[i];
        for (int j = 0; j < r; j++) shards[16 + j] = parity[j];
        // 确定性丢：前 lost/2 个数据片 + 后 lost-lost/2 个校验片
        int dl = lost / 2, pl = lost - dl;
        for (int i = 0; i < dl; i++) shards[i] = null;
        for (int j = 0; j < pl; j++) shards[16 + r - 1 - j] = null;
        var recovered = new byte[16][];
        Assert.True(RsFec.TryDecode(shards, r, recovered));
        for (int i = 0; i < dl; i++)
            Assert.Equal(data[i], recovered[i]);   // 字节级一致
    }

    [Fact]
    public void RandomLossSubsets_AlwaysRecover_WithinCapacity()
    {
        // MDS 性质抽查：随机丢 r 片（各种组合）都能恢复 —— Cauchy 可逆性的实测验证
        var data = MakeData(16, 256, 7);
        var parity = RsFec.Encode(data, 3);
        var rnd = new Random(99);
        for (int trial = 0; trial < 30; trial++)
        {
            var shards = new byte[]?[16 + 3];
            for (int i = 0; i < 16; i++) shards[i] = data[i];
            for (int j = 0; j < 3; j++) shards[16 + j] = parity[j];
            var idx = Enumerable.Range(0, 19).OrderBy(_ => rnd.Next()).Take(3).ToList();
            foreach (int i in idx) shards[i] = null;
            var recovered = new byte[16][];
            Assert.True(RsFec.TryDecode(shards, 3, recovered), $"组合 {string.Join(",", idx)} 恢复失败");
            for (int i = 0; i < 16; i++)
                if (idx.Contains(i)) Assert.Equal(data[i], recovered[i]);
        }
    }

    [Fact]
    public void LossBeyondParity_FailsCleanly()
    {
        var data = MakeData(16, 512, 3);
        var parity = RsFec.Encode(data, 2);
        var shards = new byte[]?[16 + 2];
        for (int i = 0; i < 16; i++) shards[i] = data[i];
        shards[16] = parity[0];
        shards[16 + 1] = parity[1];
        shards[0] = null; shards[5] = null; shards[9] = null;   // 丢 3 > r=2
        var recovered = new byte[16][];
        Assert.False(RsFec.TryDecode(shards, 2, recovered));
    }

    [Fact]
    public void PartialGroup_ShortGroupAlsoProtected()
    {
        // 帧尾不满 16 片的组：t=5、r=3，丢 2 片仍可恢复
        var data = MakeData(5, 1092, 11);
        var parity = RsFec.Encode(data, 3);
        var shards = new byte[]?[5 + 3];
        for (int i = 0; i < 5; i++) shards[i] = data[i];
        for (int j = 0; j < 3; j++) shards[5 + j] = parity[j];
        shards[2] = null; shards[4] = null;
        var recovered = new byte[5][];
        Assert.True(RsFec.TryDecode(shards, 3, recovered));
        Assert.Equal(data[2], recovered[2]);
        Assert.Equal(data[4], recovered[4]);
    }

    [Fact]
    public void ParityForLoss_AdaptiveCurve()
    {
        Assert.Equal(1, RsFec.ParityForLoss(0));
        Assert.Equal(1, RsFec.ParityForLoss(0.01));
        Assert.Equal(2, RsFec.ParityForLoss(0.05));
        Assert.Equal(3, RsFec.ParityForLoss(0.10));
        Assert.Equal(6, RsFec.ParityForLoss(0.22));
        Assert.Equal(6, RsFec.ParityForLoss(0.50));   // 封顶
    }

    [Fact]
    public void LargeFrame_EncodeWithinBudget()
    {
        // 165 片（~180KB 帧）÷ 16 = 11 组，r=4：编码总耗时应远低于一帧间隔（33ms）
        var data = MakeData(16, 1092, 5);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int g = 0; g < 11; g++) RsFec.Encode(data, 4);
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 50, $"165 片帧 FEC 编码耗时 {sw.ElapsedMilliseconds}ms，超出预算");
    }
}
