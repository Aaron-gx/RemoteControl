using System.Diagnostics;
using Xunit;

namespace Agent.Common.Tests;

/// <summary>
/// BGRA→NV12 转换（PixelConvert，2026-10-10 NV12 管道）：
///  · 颜色正确性：用 BT.601 公式手算期望值（白/黑/红/绿/蓝）逐字节断言
///  · stride 处理：源行距带 padding 也要对
///  · 性能基准：1080p 单帧耗时应显著低于 33ms 帧预算（记录数字，不给硬阈值断言防 CI 抖动）
/// </summary>
public class PixelConvertTests
{
    private static byte[] MakeBgra(int width, int height, Func<int, int, (byte b, byte g, byte r)> pixel)
    {
        var buf = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                var (b, g, r) = pixel(x, y);
                int i = (y * width + x) * 4;
                buf[i] = b; buf[i + 1] = g; buf[i + 2] = r; buf[i + 3] = 255;
            }
        return buf;
    }

    private static byte Y(byte b, byte g, byte r) => (byte)(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16);
    private static byte U(byte b, byte g, byte r) => (byte)(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128);
    private static byte V(byte b, byte g, byte r) => (byte)(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128);

    [Fact]
    public void SolidColors_ConvertExactly()
    {
        // 6×4 的纯色块：每像素相同 → 2×2 均值仍相同 → 可直接用公式断言
        (byte b, byte g, byte r, string name)[] colors =
        {
            (255, 255, 255, "白"), (0, 0, 0, "黑"),
            (0, 0, 255, "红"), (0, 255, 0, "绿"), (255, 0, 0, "蓝"),
            (64, 128, 200, "混合"),
        };
        foreach (var (b, g, r, name) in colors)
        {
            var bgra = MakeBgra(6, 4, (_, _) => (b, g, r));
            var nv12 = PixelConvert.BgraToNv12(bgra, 6 * 4, 6, 4);
            Assert.Equal(6 * 4 * 3 / 2, nv12.Length);
            byte wantY = Y(b, g, r), wantU = U(b, g, r), wantV = V(b, g, r);
            for (int i = 0; i < 24; i++) Assert.Equal(wantY, nv12[i]);          // Y 全部
            for (int i = 24; i < 36; i += 2)
            {
                Assert.Equal(wantU, nv12[i]);                                     // U
                Assert.Equal(wantV, nv12[i + 1]);                                 // V
            }
            _ = name;
        }
    }

    [Fact]
    public void StridedSource_MatchesTight()
    {
        // 带 padding 的行距（WGC 的 SoftwareBitmap 常见 stride > width*4）必须与紧排结果一致
        int w = 8, h = 6, stride = w * 4 + 12;
        var tight = MakeBgra(w, h, (x, y) => ((byte)(x * 20), (byte)(y * 30), (byte)(x * y)));
        var padded = new byte[stride * h];
        for (int y = 0; y < h; y++)
            Array.Copy(tight, y * w * 4, padded, y * stride, w * 4);
        var a = PixelConvert.BgraToNv12(tight, w * 4, w, h);
        var b2 = PixelConvert.BgraToNv12(padded, stride, w, h);
        Assert.Equal(a, b2);
    }

    [Fact]
    public void OddDimensions_DoNotCrash()
    {
        var bgra = MakeBgra(5, 3, (x, y) => ((byte)x, (byte)y, (byte)(x + y)));
        var nv12 = PixelConvert.BgraToNv12(bgra, 5 * 4, 5, 3);
        Assert.Equal(5 * 3 * 3 / 2, nv12.Length);
    }

    [Fact]
    public void Benchmark_1080p_WellWithinFrameBudget()
    {
        var bgra = MakeBgra(1920, 1080, (x, y) => ((byte)(x & 0xFF), (byte)(y & 0xFF), (byte)((x + y) & 0xFF)));
        // 预热（JIT）
        PixelConvert.BgraToNv12(bgra, 1920 * 4, 1920, 1080);
        var sw = Stopwatch.StartNew();
        const int n = 10;
        for (int i = 0; i < n; i++) PixelConvert.BgraToNv12(bgra, 1920 * 4, 1920, 1080);
        sw.Stop();
        double ms = sw.Elapsed.TotalMilliseconds / n;
        // Release 实测 ≈8.2ms（Y 5.7 + UV 2.5），Debug 慢一档。阈值放到 20ms：
        // 两种配置都过（防 CI 抖动），同时拦住实现级退化（帧预算 33ms）。
        Assert.True(ms < 20.0, $"1080p BGRA→NV12 单帧 {ms:F2}ms 明显退化（Release 基线 ≈8ms，帧预算 33ms）");
    }
}
