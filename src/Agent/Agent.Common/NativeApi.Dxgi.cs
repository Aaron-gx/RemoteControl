using System.Runtime.InteropServices;

namespace Agent.Common;

/// <summary>DXGI 输出信息（枚举顺序即 output_idx）</summary>
public sealed record DxgiOutputInfo(int OutputIndex, int AdapterIndex, string DeviceName, string AdapterName, int Width, int Height);

/// <summary>
/// DXGI 适配器/输出枚举（2026-10-14 引入，对标 UU codec capability cache 的显卡维度）：
///  · 编码器能力缓存需要"默认显卡标识"（换卡/换驱动后旧缓存整体失效）；
///  · dda 显卡直通需要把 Windows 显示设备名（\\.\DISPLAYn）精确映射到 ddagrab 的 output_idx，
///    取代旧的"按分辨率唯一匹配"启发式（两块同分辨率屏时旧法直接放弃）。
/// 手写 vtable 调用，与 NativeWgc 同风格。全部只读、无副作用，失败返回空结果由调用方回退。
/// </summary>
public static unsafe class NativeDxgi
{
    private static readonly Guid IID_IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr ppFactory);

    // vtable 槽位（IUnknown=0..2，IDXGIObject=3..6）：
    //   IDXGIFactory1::EnumAdapters1 = 11
    //   IDXGIAdapter::EnumOutputs = 7，IDXGIAdapter1::GetDesc1 = 10
    //   IDXGIOutput::GetDesc = 7
    private const int SlotEnumAdapters1 = 11;
    private const int SlotEnumOutputs = 7;
    private const int SlotGetDesc1 = 10;
    private const int SlotOutputGetDesc = 7;

    // blittable 结构（fixed char 缓冲，否则取不了地址、也过不了 CS8500）
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct AdapterDesc1
    {
        public fixed char Description[128];
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public long AdapterLuid;   // LUID（两个 DWORD）
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct OutputDesc
    {
        public fixed char DeviceName[32];   // \\.\DISPLAYn
        public int DesktopLeft, DesktopTop, DesktopRight, DesktopBottom;
        public int AttachedToDesktop;
        public int Rotation;
        public IntPtr Monitor;
    }

    private static string ReadFixedString(char* src, int len)
    {
        int n = 0;
        while (n < len && src[n] != '\0') n++;
        return n == 0 ? "" : new string(src, 0, n);
    }

    private static string? _adapterIdentity;
    private static readonly object _identityGate = new();

    /// <summary>
    /// 默认显卡标识："VendorId:DeviceId:描述"（枚举到的第一个适配器 = D3D11CreateDevice(NULL)
    /// 实际会用到的默认适配器——我们的采集/编码链路全部走它）。DXGI 不可用返回 "unknown"
    /// （缓存仍可用，只是少一个失效维度）。进程内只查一次。
    /// </summary>
    public static string DefaultAdapterIdentity()
    {
        lock (_identityGate)
        {
            if (_adapterIdentity != null) return _adapterIdentity;
            try
            {
                var factory = CreateFactory();
                if (factory != IntPtr.Zero)
                {
                    try
                    {
                        if (GetAdapter(factory, 0, out var desc))
                        {
                            _adapterIdentity = $"0x{desc.VendorId:X}:0x{desc.DeviceId:X}:{ReadFixedString(desc.Description, 128)}";
                            return _adapterIdentity;
                        }
                    }
                    finally { Marshal.Release(factory); }
                }
            }
            catch { /* 回退 unknown */ }
            _adapterIdentity = "unknown";
            return _adapterIdentity;
        }
    }

    /// <summary>
    /// 枚举所有适配器的所有输出（每个适配器内 OutputIndex 从 0 递增 —— 这正是 ddagrab 的
    /// output_idx 语义：ddagrab 的 output_idx 是"整个系统的输出序号"，跨适配器连续累加，
    /// 与 ffmpeg ddagrab 滤镜的枚举一致）。任何失败返回空列表，调用方自行回退。
    /// </summary>
    public static List<DxgiOutputInfo> EnumOutputs()
    {
        var result = new List<DxgiOutputInfo>();
        try
        {
            var factory = CreateFactory();
            if (factory == IntPtr.Zero) return result;
            try
            {
                int globalIdx = 0;
                for (uint a = 0; ; a++)
                {
                    if (!GetAdapter(factory, a, out var adesc)) break;
                    var adapter = AdapterHandle(factory, a);
                    if (adapter == IntPtr.Zero) break;
                    try
                    {
                        for (uint o = 0; ; o++)
                        {
                            var output = EnumOutput(adapter, o);
                            if (output == IntPtr.Zero) break;
                            try
                            {
                                if (!GetOutputDesc(output, out var odesc)) break;
                                result.Add(new DxgiOutputInfo(globalIdx, (int)a, ReadFixedString(odesc.DeviceName, 32),
                                    ReadFixedString(adesc.Description, 128),
                                    odesc.DesktopRight - odesc.DesktopLeft,
                                    odesc.DesktopBottom - odesc.DesktopTop));
                            }
                            finally { Marshal.Release(output); }
                            globalIdx++;
                        }
                    }
                    finally { Marshal.Release(adapter); }
                }
            }
            finally { Marshal.Release(factory); }
        }
        catch { }
        return result;
    }

    private static IntPtr CreateFactory()
    {
        var iid = IID_IDXGIFactory1;
        if (CreateDXGIFactory1(ref iid, out var factory) < 0) return IntPtr.Zero;
        return factory;
    }

    private static bool GetAdapter(IntPtr factory, uint index, out AdapterDesc1 desc)
    {
        desc = default;
        var adapter = AdapterHandle(factory, index);
        if (adapter == IntPtr.Zero) return false;
        try
        {
            var d = desc;
            var vtbl = *(IntPtr**)adapter;
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, AdapterDesc1*, int>)vtbl[SlotGetDesc1];
            int hr = fn(adapter, &d);
            desc = d;
            return hr >= 0;
        }
        finally { Marshal.Release(adapter); }
    }    private static IntPtr AdapterHandle(IntPtr factory, uint index)
    {
        var vtbl = *(IntPtr**)factory;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)vtbl[SlotEnumAdapters1];
        IntPtr adapter;
        if (fn(factory, index, &adapter) < 0) return IntPtr.Zero;
        return adapter;
    }

    private static IntPtr EnumOutput(IntPtr adapter, uint index)
    {
        var vtbl = *(IntPtr**)adapter;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)vtbl[SlotEnumOutputs];
        IntPtr output;
        if (fn(adapter, index, &output) < 0) return IntPtr.Zero;
        return output;
    }

    private static bool GetOutputDesc(IntPtr output, out OutputDesc desc)
    {
        desc = default;
        var vtbl = *(IntPtr**)output;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, OutputDesc*, int>)vtbl[SlotOutputGetDesc];
        var d = desc;
        int hr = fn(output, &d);
        desc = d;
        return hr >= 0;
    }
}
