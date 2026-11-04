using System;

namespace Agent.Common;

/// <summary>
/// 像素格式转换（2026-10-10 NV12 管道）：BGRA → NV12（BT.601 limited range）。
///
/// 【为什么】WGC/GDI 采集出来是 BGRA（4 字节/像素），1080p30 过 ffmpeg 管道 ≈ 250MB/s；
/// NV12 是 1.5 字节/像素（Y 全分辨率 + UV 半分辨率），管道流量直降 62%，ffmpeg 侧
/// 还省掉一次 BGRA→NV12 的色彩转换（qsv/nvenc/amf 的原生输入就是 NV12）——
/// 这是 Intel 核显机器采集帧率上不去（实测 21/30fps）的对症解法。
///
/// 【色彩矩阵选 BT.601 limited】与旧管线完全一致：旧管线由 ffmpeg 内部把 BGRA 转
/// NV12/yuv420p 时默认用的就是 BT.601 有限范围，自己转换按同一矩阵来，主控端看到的
/// 颜色一帧都不变（不换矩阵=不引入验收变量）。
///
/// 整数系数与 libyuv 同款（8bit 精度，误差 &lt;1）：
///   Y = ((66R + 129G + 25B + 128) &gt;&gt; 8) + 16
///   U = ((-38R - 74G + 112B + 128) &gt;&gt; 8) + 128
///   V = ((112R - 94G - 18B + 128) &gt;&gt; 8) + 128
/// UV 为 2×2 下采样（先对 2×2 块的 RGB 取平均，再算 U/V）。
/// </summary>
public static unsafe class PixelConvert
{
    /// <summary>
    /// BGRA（行距可含 padding）→ NV12（Y 平面 width*height，紧随 UV 交错平面 width*height/2）。
    /// nv12 缓冲长度必须 ≥ width*height*3/2。
    /// </summary>
    public static void BgraToNv12(byte* bgra, int bgraStride, byte* nv12, int width, int height)
    {
        byte* yPlane = nv12;
        byte* uvPlane = nv12 + (long)width * height;
        int uvStride = width;   // UV 交错平面每行 width 字节（U,V 各占一半）

        // ---- Y 平面（全分辨率）----
        for (int row = 0; row < height; row++)
        {
            byte* src = bgra + (long)row * bgraStride;
            byte* dst = yPlane + (long)row * width;
            ConvertRowY(src, dst, width);
        }

        // ---- UV 平面（2×2 下采样，奇数宽/高时最后一列/行与相邻像素重复使用）----
        int uvRows = (height + 1) / 2;
        for (int row = 0; row < uvRows; row++)
        {
            int y0 = row * 2;
            int y1 = Math.Min(y0 + 1, height - 1);
            byte* s0 = bgra + (long)y0 * bgraStride;
            byte* s1 = bgra + (long)y1 * bgraStride;
            byte* dst = uvPlane + (long)row * uvStride;
            int uvCols = (width + 1) / 2;
            for (int col = 0; col < uvCols; col++)
            {
                int x0 = col * 2;
                int x1 = Math.Min(x0 + 1, width - 1);
                // 2×2 块的 B,G,R 各取平均（BGRA 内存序：B,G,R,A）
                int b = s0[x0 * 4] + s0[x1 * 4] + s1[x0 * 4] + s1[x1 * 4];
                int g = s0[x0 * 4 + 1] + s0[x1 * 4 + 1] + s1[x0 * 4 + 1] + s1[x1 * 4 + 1];
                int r = s0[x0 * 4 + 2] + s0[x1 * 4 + 2] + s1[x0 * 4 + 2] + s1[x1 * 4 + 2];
                dst[col * 2] = Uv(b, g, r, isU: true);
                dst[col * 2 + 1] = Uv(b, g, r, isU: false);
            }
        }
    }

    private static void ConvertRowY(byte* src, byte* dst, int width)
    {
        // 标量实现（Release 实测：1080p Y≈5.7ms + UV≈2.5ms ≈ 8.2ms/帧，帧预算 33ms 内可行）。
        // 试过 Vector128(Shuffle+Widen) 向量化，本机 .NET 8 JIT 下实测反而 18.7ms（慢 3 倍），
        // 已删除；后续若要再压，方向是 Vector256 整行处理或 WinRT SoftwareBitmap.Convert。
        for (int i = 0; i < width; i++)
        {
            byte* s = src + i * 4;
            dst[i] = (byte)(((66 * s[2] + 129 * s[1] + 25 * s[0] + 128) >> 8) + 16);
        }
    }

    private static byte Uv(int b4, int g4, int r4, bool isU)
    {
        // 入参是 2×2 块的四像素和（除 4 得均值，用 +2 舍入）
        int b = b4 >> 2, g = g4 >> 2, r = r4 >> 2;
        int v = isU
            ? ((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128
            : ((112 * r - 94 * g - 18 * b + 128) >> 8) + 128;
        return (byte)Math.Clamp(v, 0, 255);
    }

    /// <summary>托管包装（测试与非 unsafe 调用方用）</summary>
    public static byte[] BgraToNv12(byte[] bgra, int bgraStride, int width, int height)
    {
        var nv12 = new byte[width * height * 3 / 2];
        fixed (byte* pSrc = bgra)
        fixed (byte* pDst = nv12)
        {
            BgraToNv12(pSrc, bgraStride, pDst, width, height);
        }
        return nv12;
    }
}
