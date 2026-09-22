using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Agent.Common;
using Windows.Foundation;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;

namespace Agent.Worker;

/// <summary>
/// Windows.Graphics.Capture 采集（策划推荐路径，RDP 会话可用）。
///
/// 与 D3D11 手写互操作相比，这里走 WinRT 投影 + SoftwareBitmap 取像素，
/// 只需要 3 个原生入口：D3D11CreateDevice、CreateDirect3D11DeviceFromDXGIDevice、
/// RoGetActivationFactory(GraphicsCaptureItem 的 IGraphicsCaptureItemInterop)。
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed unsafe class WgcFrameSource : FrameSource
{
    private readonly Logger _log;
    private readonly DisplayInfo _target;
    private readonly GraphicsCaptureItem _item;
    private readonly Direct3D11CaptureFramePool _pool;
    private readonly GraphicsCaptureSession _session;
    private IntPtr _d3dDevice;
    private IntPtr _dxgiDevice;
    private IntPtr _winrtDevice;
    private int _failCount;
    private byte[]? _lastFrame;
    /// <summary>采集项已被系统关闭（显示器移除/重建/显示模式切换），再取帧也没意义</summary>
    private volatile bool _dead;
    private long _lastFreshTick = Environment.TickCount64;
    private long _lastStaleLogTick;

    public override string Backend => "WGC";

    public WgcFrameSource(DisplayInfo target, Logger log)
    {
        _target = target;
        _log = log;
        Width = target.Width;
        Height = target.Height;

        // 1) D3D11 设备（不需要调用其方法，仅用于生成 WinRT 设备）
        var hr = NativeWgc.D3D11CreateDevice(IntPtr.Zero, NativeWgc.D3D_DRIVER_TYPE_HARDWARE, IntPtr.Zero,
            NativeWgc.D3D11_CREATE_DEVICE_BGRA_SUPPORT, IntPtr.Zero, 0, NativeWgc.D3D11_SDK_VERSION,
            out _d3dDevice, out _, out _);
        if (hr < 0 || _d3dDevice == IntPtr.Zero)
            throw new InvalidOperationException($"D3D11CreateDevice 失败 0x{hr:X8}");

        var iidDxgi = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c"); // IDXGIDevice
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(_d3dDevice, ref iidDxgi, out _dxgiDevice));

        hr = NativeWgc.CreateDirect3D11DeviceFromDXGIDevice(_dxgiDevice, out _winrtDevice);
        if (hr < 0 || _winrtDevice == IntPtr.Zero)
            throw new InvalidOperationException($"CreateDirect3D11DeviceFromDXGIDevice 失败 0x{hr:X8}");

        // 2) 按显示器创建 GraphicsCaptureItem
        _item = CreateItemForMonitor(target);
        _log.Info($"WGC 采集目标：{target}");

        // 3) 帧池 + 会话（B8G8R8A8，缓冲 2 帧）
        var device = WinRT.MarshalInspectable<IDirect3DDevice>.FromAbi(_winrtDevice);
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device,
            DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _item.Size);
        _session = _pool.CreateCaptureSession(_item);
        try
        {
            // 刻意**不**把系统光标烤进画面：远端光标由主控端自绘（RemoteScreenControl）。
            // 这样本机用户的屏幕上永远不会出现远端的光标，而远端在任何模式下都能看见自己的指针
            // （后台定向注入模式下，系统光标压根不在外屏上）。
            _session.IsCursorCaptureEnabled = false;
        }
        catch (Exception ex)
        {
            _log.Debug($"设置会话选项失败（可忽略）：{ex.Message}");
        }
        _session.StartCapture();

        // 显示器被移除/重建时系统会关掉采集项，此后 TryGetNextFrame 永远返回 null。
        // 不接这个事件的话，表现就是"画面永久冻在黑屏，日志里一句话都没有"。
        try { _item.Closed += OnItemClosed; }
        catch (Exception ex) { _log.Debug($"订阅采集项关闭事件失败（可忽略）：{ex.Message}"); }

        _log.Info("WGC 会话已启动");
    }

    private void OnItemClosed(GraphicsCaptureItem sender, object args)
    {
        _dead = true;
        _log.Warn("WGC 采集项已被系统关闭（显示器移除或显示模式重建），等待上层重建采集器");
    }

    private static GraphicsCaptureItem CreateItemForMonitor(DisplayInfo target)
    {
        // 用显示器设备名（\\.\DISPLAY1）找到 HMONITOR
        var pt = new NativeApi.POINT { X = target.Left + 1, Y = target.Top + 1 };
        var hmon = NativeApi.MonitorFromPoint(pt, 2 /*MONITOR_DEFAULTTONEAREST*/);
        if (hmon == IntPtr.Zero) throw new InvalidOperationException("MonitorFromPoint 失败");

        IntPtr factory = NativeWgc.GetActivationFactory("Windows.Graphics.Capture.GraphicsCaptureItem");
        var iidInterop = new Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"); // IGraphicsCaptureItemInterop
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(factory, ref iidInterop, out var interop));
        try
        {
            var iidItem = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760"); // IGraphicsCaptureItem
            IntPtr itemPtr = NativeWgc.CallCreateForMonitor(interop, hmon, ref iidItem);
            if (itemPtr == IntPtr.Zero) throw new InvalidOperationException("CreateForMonitor 返回空");
            try
            {
                return WinRT.MarshalInspectable<GraphicsCaptureItem>.FromAbi(itemPtr);
            }
            finally
            {
                Marshal.Release(itemPtr);
            }
        }
        finally
        {
            Marshal.Release(interop);
            Marshal.Release(factory);
        }
    }

    public override bool Capture(Stream output, out string error)
    {
        error = "";
        try
        {
            if (_dead)
            {
                // 必须给非空 error：上层就是靠它累计失败次数并重建采集器的
                error = "WGC 采集项已关闭（显示器被移除或显示模式已重建）";
                if (++_failCount <= 3) _log.Warn(error);
                return false;
            }

            using var frame = _pool.TryGetNextFrame();
            if (frame == null)
            {
                // 画面静止时 WGC 不会出新帧：重复上一帧，保证恒定帧率与 FPS 显示。
                // 但"长时间一帧都没有"也可能是采集面已经死了（此时日志会一片空白），
                // 所以这里补一条限流告警，方便从日志上把两种情形分开。
                long now = Environment.TickCount64;
                long staleMs = now - _lastFreshTick;
                if (staleMs > 10_000 && now - _lastStaleLogTick > 30_000)
                {
                    _lastStaleLogTick = now;
                    _log.Warn($"WGC 已 {staleMs / 1000}s 没有新画面（画面完全静止，或采集面已失效）");
                }
                if (_lastFrame != null)
                {
                    output.Write(_lastFrame, 0, _lastFrame.Length);
                    return true;
                }
                // 没有新帧、也没有上一帧可以顶 → 采集面刚失效（首次或刚换过分辨率）
                error = $"WGC 没有可用的帧（会话或显示模式可能已变化，第 {++_failCount} 次）";
                if (_failCount <= 3) _log.Warn(error);
                return false;
            }
            _lastFreshTick = Environment.TickCount64;
            _failCount = 0;

            var bmp = SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface, BitmapAlphaMode.Premultiplied)
                .AsTask().GetAwaiter().GetResult();
            using (bmp)
            {
                if (bmp.PixelWidth != Width || bmp.PixelHeight != Height)
                {
                    Width = bmp.PixelWidth;
                    Height = bmp.PixelHeight;
                    _lastFrame = null;
                }
                using var buffer = bmp.LockBuffer(BitmapBufferAccessMode.Read);
                using var reference = buffer.CreateReference();
                var desc = buffer.GetPlaneDescription(0);
                NativeWgc.GetBuffer(reference, out byte* data, out uint capacity);
                int stride = desc.Stride;
                int rowBytes = Width * 4;
                int total = rowBytes * Height;
                if (_lastFrame == null || _lastFrame.Length != total) _lastFrame = new byte[total];
                for (int y = 0; y < Height; y++)
                {
                    var src = new ReadOnlySpan<byte>(data + desc.StartIndex + y * stride, rowBytes);
                    src.CopyTo(new Span<byte>(_lastFrame, y * rowBytes, rowBytes));
                }
                output.Write(_lastFrame, 0, total);
            }
            return true;
        }
        catch (Exception ex)
        {
            error = $"WGC 采集失败：{ex.GetType().Name}: {ex.Message}";
            if (++_failCount <= 3) _log.Warn(error);
            return false;
        }
    }

    public override void Dispose()
    {
        try { _session?.Dispose(); } catch { }
        try { _pool?.Dispose(); } catch { }
        try { if (_winrtDevice != IntPtr.Zero) Marshal.Release(_winrtDevice); } catch { }
        try { if (_dxgiDevice != IntPtr.Zero) Marshal.Release(_dxgiDevice); } catch { }
        try { if (_d3dDevice != IntPtr.Zero) Marshal.Release(_d3dDevice); } catch { }
    }
}

/// <summary>WGC 需要的少量原生入口（手写 vtable 调用，避开 CsWinRT 内部 API）</summary>
internal static unsafe class NativeWgc
{
    public const int D3D_DRIVER_TYPE_HARDWARE = 1;
    public const int D3D11_SDK_VERSION = 7;
    public const int D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;

    [DllImport("d3d11.dll")]
    public static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, int flags,
        IntPtr featureLevels, int featureLevelCount, int sdkVersion,
        out IntPtr device, out int featureLevel, out IntPtr context);

    [DllImport("d3d11.dll")]
    public static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [DllImport("combase.dll")]
    private static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string source, int length, out IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

    public static IntPtr GetActivationFactory(string className)
    {
        int hr = WindowsCreateString(className, className.Length, out var hstr);
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        try
        {
            var iid = new Guid("00000035-0000-0000-C000-000000000046"); // IActivationFactory
            hr = RoGetActivationFactory(hstr, ref iid, out var factory);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            return factory;
        }
        finally
        {
            WindowsDeleteString(hstr);
        }
    }

    /// <summary>IGraphicsCaptureItemInterop::CreateForMonitor（vtable 槽位 4）</summary>
    public static IntPtr CallCreateForMonitor(IntPtr interop, IntPtr hmon, ref Guid iid)
    {
        var vtbl = *(IntPtr**)interop;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)vtbl[4];
        IntPtr result;
        fixed (Guid* piid = &iid)
        {
            int hr = fn(interop, hmon, piid, &result);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        }
        return result;
    }

    /// <summary>IMemoryBufferByteAccess::GetBuffer（vtable 槽位 3）</summary>
    public static void GetBuffer(object memoryBufferReference, out byte* buffer, out uint capacity)
    {
        buffer = null;
        capacity = 0;
        // CsWinRT 投影对象 → ABI 指针
        IntPtr thisPtr = WinRTInterop.GetAbiPointer(memoryBufferReference);
        var iid = new Guid("5b0d3235-4dba-4d44-865e-8f1d0e4fd04d"); // IMemoryBufferByteAccess
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(thisPtr, ref iid, out var access));
        try
        {
            var vtbl = *(IntPtr**)access;
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, byte**, uint*, int>)vtbl[3];
            byte* p;
            uint c;
            int hr = fn(access, &p, &c);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            buffer = p;
            capacity = c;
        }
        finally
        {
            Marshal.Release(access);
        }
    }
}

/// <summary>取 CsWinRT 投影对象的 ABI 指针（用反射拿 IWinRTObject，避免绑死内部 API 名称）</summary>
internal static class WinRTInterop
{
    private static readonly System.Reflection.PropertyInfo? NativeObjectProp;
    private static readonly System.Reflection.PropertyInfo? ThisPtrProp;

    static WinRTInterop()
    {
        try
        {
            var t = Type.GetType("WinRT.IWinRTObject, WinRT.Runtime");
            NativeObjectProp = t?.GetProperty("NativeObject");
            var objRefType = NativeObjectProp?.PropertyType;
            ThisPtrProp = objRefType?.GetProperty("ThisPtr");
        }
        catch { }
    }

    public static IntPtr GetAbiPointer(object projected)
    {
        if (NativeObjectProp == null || ThisPtrProp == null)
            throw new InvalidOperationException("无法解析 CsWinRT 投影对象的 ABI 指针（WinRT.IWinRTObject 不可用）");
        var objRef = NativeObjectProp.GetValue(projected)
                     ?? throw new InvalidOperationException("NativeObject 为空");
        var ptr = ThisPtrProp.GetValue(objRef);
        return ptr is IntPtr p ? p : throw new InvalidOperationException("ThisPtr 不是 IntPtr");
    }
}
