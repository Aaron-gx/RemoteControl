using System.Runtime.InteropServices;
using Agent.Common;

namespace Agent.Worker;

/// <summary>
/// 画面采集抽象：把一帧像素写入目标流（尺寸 = Width×Height）
/// </summary>
public abstract class FrameSource : IDisposable
{
    public int Width { get; protected set; }
    public int Height { get; protected set; }
    public abstract string Backend { get; }

    /// <summary>
    /// 输出像素格式（"bgra" / "nv12"，2026-10-10 NV12 管道）：编码器据此定 ffmpeg 的
    /// -pixel_format。NV12 管道流量比 BGRA 省 62%（1080p30：250MB/s → 93MB/s），
    /// 且 qsv/nvenc/amf 的原生输入就是 NV12（ffmpeg 内不再做色彩转换）。
    /// </summary>
    public virtual string PixelFormat => "bgra";

    /// <summary>采集一帧并写入 output（按 <see cref="PixelFormat"/> 的布局，行序自上而下，无 padding）</summary>
    public abstract bool Capture(Stream output, out string error);

    public virtual void Dispose() { }
}

/// <summary>
/// GDI BitBlt 采集（兜底路径，任何会话都可用；RDP 会话内同样有效）
/// </summary>
public sealed unsafe class GdiFrameSource : FrameSource
{
    private readonly Logger _log;
    private readonly DisplayInfo _target;
    private IntPtr _screenDc, _memDc, _bmp, _oldBmp, _bits;
    private readonly int _pixelBytes;
    private int _failCount;

    public override string Backend => "GDI";

    private readonly bool _useCaptureBlt;

    public GdiFrameSource(DisplayInfo target, Logger log, bool useCaptureBlt = true)
    {
        _target = target;
        _log = log;
        _useCaptureBlt = useCaptureBlt;
        Width = target.Width;
        Height = target.Height;
        _pixelBytes = Width * Height * 4;

        _screenDc = NativeApi.GetDC(IntPtr.Zero);
        if (_screenDc == IntPtr.Zero) throw new InvalidOperationException("GetDC 失败");
        _memDc = NativeApi.CreateCompatibleDC(_screenDc);
        if (_memDc == IntPtr.Zero) throw new InvalidOperationException("CreateCompatibleDC 失败");

        var bmi = new NativeApi.BITMAPINFO
        {
            bmiHeader = new NativeApi.BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<NativeApi.BITMAPINFOHEADER>(),
                biWidth = Width,
                biHeight = -Height,      // 负值 = 自上而下
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0,       // BI_RGB
                biSizeImage = _pixelBytes,
            },
        };
        _bmp = NativeApi.CreateDIBSection(_screenDc, ref bmi, 0, out _bits, IntPtr.Zero, 0);
        if (_bmp == IntPtr.Zero || _bits == IntPtr.Zero)
            throw new InvalidOperationException($"CreateDIBSection 失败：{NativeApi.LastWin32Error()}");
        _oldBmp = NativeApi.SelectObject(_memDc, _bmp);
        _log.Info($"GDI 采集初始化完成：{Width}x{Height}（源 {target.DeviceName} @({target.Left},{target.Top})）");
    }

    public override bool Capture(Stream output, out string error)
    {
        error = "";
        bool ok = NativeApi.BitBlt(_memDc, 0, 0, Width, Height, _screenDc, _target.Left, _target.Top,
            _useCaptureBlt ? NativeApi.SRCCOPY | NativeApi.CAPTUREBLT : NativeApi.SRCCOPY);
        if (!ok)
        {
            error = $"BitBlt 失败：{NativeApi.LastWin32Error()}";
            if (++_failCount <= 3) _log.Warn(error);
            return false;
        }
        // 不再把系统光标画进画面：远端光标改由主控端自绘（RemoteScreenControl），
        // 这样"远端光标"只存在于主控端窗口里，被控机上不会有任何光标被画到本机用户眼前。
        output.Write(new ReadOnlySpan<byte>((void*)_bits, _pixelBytes));
        return true;
    }

    public override void Dispose()
    {
        try { if (_oldBmp != IntPtr.Zero && _memDc != IntPtr.Zero) NativeApi.SelectObject(_memDc, _oldBmp); } catch { }
        try { if (_bmp != IntPtr.Zero) NativeApi.DeleteObject(_bmp); } catch { }
        try { if (_memDc != IntPtr.Zero) NativeApi.DeleteDC(_memDc); } catch { }
        try { if (_screenDc != IntPtr.Zero) NativeApi.ReleaseDC(IntPtr.Zero, _screenDc); } catch { }
        _bmp = _memDc = _screenDc = IntPtr.Zero;
        _bits = IntPtr.Zero;
    }
}

/// <summary>
/// DDA 显卡直通的占位采集源（2026-10-10）：ffmpeg 自己用 ddagrab 在显卡上采集，
/// 外部不需要也不应该喂帧 —— 这个类只为让 WorkerContext 的"采集目标尺寸"逻辑照常工作。
/// </summary>
public sealed class DdaStubSource : FrameSource
{
    public override string Backend => "DDA";

    public DdaStubSource(DisplayInfo target)
    {
        Width = target.Width;
        Height = target.Height;
    }

    public override bool Capture(Stream output, out string error)
    {
        // 正常流程不会调到这里（采集循环对 SelfCapture 编码器跳过喂帧）
        error = "DDA 自采集模式：ffmpeg 内部采集，外部不喂帧";
        return false;
    }
}

// PixelFormat 说明：GDI/画布拼接输出 BGRA（基类默认），WGC 输出 NV12（见 WgcFrameSource）。

public static class FrameSourceFactory
{
    /// <summary>按配置创建采集源：auto → 先试 WGC，失败退 GDI。
    /// pixelPipeline：bgra（默认）/ nv12（opt-in，见 AppConfig.PixelPipeline 的真机问题说明）。</summary>
    public static FrameSource Create(string backend, DisplayInfo target, Logger log, string pixelPipeline = "bgra")
    {
        backend = (backend ?? "auto").Trim().ToLowerInvariant();
        if (backend == "gdi")
            return new GdiFrameSource(target, log);
        if (backend == "gdi-noblt")
            return new GdiFrameSource(target, log, useCaptureBlt: false);

        if (backend is "auto" or "wgc")
        {
            try
            {
                var wgc = new WgcFrameSource(target, log, nv12Pipeline: string.Equals(pixelPipeline, "nv12", StringComparison.OrdinalIgnoreCase));
                log.Info("使用 Windows.Graphics.Capture 采集");
                return wgc;
            }
            catch (Exception ex)
            {
                log.Warn($"Windows.Graphics.Capture 不可用（{ex.GetType().Name}: {ex.Message}），回退 GDI BitBlt");
                if (backend == "wgc") throw;
            }
        }
        return new GdiFrameSource(target, log);
    }
}
