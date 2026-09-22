using System.Runtime.InteropServices;
using System.Security.AccessControl;

namespace Agent.Common;

/// <summary>
/// 跨会话共享内存环形缓冲（帧通道）
///
/// 布局（Global\FrameBuffer，默认 4MB）：
///   [64B 控制头]
///     +0  uint32 magic 'RCBF'
///     +4  int32  version
///     +8  int32  capacity（数据区字节数）
///     +12 int32  reserved
///     +16 int64  writePos   （单调递增，非取模）
///     +24 int64  readPos
///     +32 int64  frameCount
///     +40 int64  lastWriteTicks
///     +48 int64  dropCount
///     +56 int64  reserved
///   [数据区 capacity 字节]
///     记录 = [4B BE 长度][N 字节数据]；长度 0xFFFFFFFF 为换圈填充标记（跳到本圈末尾）
///
/// 同步：Global\FrameReady（自动重置，写者写完置位）、Global\FrameConsumed（读者读完置位）
/// DACL：SYSTEM/Administrators 完全控制，Authenticated Users 读写 → 允许跨 Session
/// </summary>
public sealed unsafe class SharedFrameRing : IDisposable
{
    private const uint Magic = 0x46424352; // 'RCBF'
    private const int HeaderSize = 64;
    private const uint WrapMarker = 0xFFFFFFFF;

    private IntPtr _hMap = IntPtr.Zero;
    private IntPtr _view = IntPtr.Zero;
    private EventWaitHandle? _ready;
    private EventWaitHandle? _consumed;
    private bool _ownsMap;

    public int Capacity { get; }
    public string Name { get; }

    private SharedFrameRing(string name, IntPtr hMap, IntPtr view, int capacity, bool ownsMap)
    {
        Name = name;
        _hMap = hMap;
        _view = view;
        Capacity = capacity;
        _ownsMap = ownsMap;
    }

    public static SharedFrameRing Create(string name = IpcProtocol.FrameBufferName,
        string readyEvent = IpcProtocol.FrameReadyEvent,
        string consumedEvent = IpcProtocol.FrameConsumedEvent,
        int capacity = IpcProtocol.FrameBufferSize)
    {
        SharedFrameRing? created = null;
        string lastError = "";
        // 先试"带低完整性标签"的安全描述符；没有 SeSecurityPrivilege 时降级为纯 DACL
        foreach (var sddl in new[] { NativeApi.CrossSessionSddl, NativeApi.CrossSessionSddlNoLabel })
        {
            var saTry = NativeApi.CreateSecurityAttributes(sddl);
            try
            {
                created = CreateWith(saTry, name, readyEvent, consumedEvent, capacity);
                if (sddl == NativeApi.CrossSessionSddlNoLabel)
                    LastCreateUsedLabel = false;
                return created;
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
            }
            finally
            {
                if (saTry.lpSecurityDescriptor != IntPtr.Zero) NativeApi.LocalFree(saTry.lpSecurityDescriptor);
            }
        }
        throw new ProtocolException($"创建共享内存失败：{lastError}");
    }

    /// <summary>上一次 Create 是否成功带上了低完整性标签（false 表示已降级）</summary>
    public static bool LastCreateUsedLabel { get; private set; } = true;

    private static SharedFrameRing CreateWith(NativeApi.SECURITY_ATTRIBUTES sa, string name,
        string readyEvent, string consumedEvent, int capacity)
    {
        IntPtr hMap = IntPtr.Zero, hReady = IntPtr.Zero, hConsumed = IntPtr.Zero;
        try
        {
            var saPtr = Marshal.AllocHGlobal(Marshal.SizeOf<NativeApi.SECURITY_ATTRIBUTES>());
            try
            {
                Marshal.StructureToPtr(sa, saPtr, false);
                hMap = NativeApi.CreateFileMappingW(new IntPtr(-1), saPtr, NativeApi.PAGE_READWRITE, 0,
                    (uint)(HeaderSize + capacity), name);
            }
            finally { Marshal.FreeHGlobal(saPtr); }
            if (hMap == IntPtr.Zero)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), $"创建共享内存 {name} 失败");

            hReady = CreateEventWithDacl(sa, readyEvent);
            hConsumed = CreateEventWithDacl(sa, consumedEvent);

            var view = NativeApi.MapViewOfFile(hMap, NativeApi.FILE_MAP_ALL_ACCESS, 0, 0, (UIntPtr)(HeaderSize + capacity));
            if (view == IntPtr.Zero)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "映射共享内存失败");

            var ring = new SharedFrameRing(name, hMap, view, capacity, true);
            ring.InitializeHeader();
            ring.OpenEvents(readyEvent, consumedEvent);
            return ring;
        }
        catch
        {
            if (hConsumed != IntPtr.Zero) NativeApi.CloseHandle(hConsumed);
            if (hReady != IntPtr.Zero) NativeApi.CloseHandle(hReady);
            if (hMap != IntPtr.Zero) NativeApi.CloseHandle(hMap);
            throw;
        }
    }

    public static SharedFrameRing Open(string name = IpcProtocol.FrameBufferName,
        string readyEvent = IpcProtocol.FrameReadyEvent,
        string consumedEvent = IpcProtocol.FrameConsumedEvent)
    {
        var hMap = NativeApi.OpenFileMappingW(NativeApi.FILE_MAP_ALL_ACCESS, false, name);
        if (hMap == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            throw new System.ComponentModel.Win32Exception(err,
                $"打开共享内存 {name} 失败：Win32={err}（{NativeApi.DescribeWin32Error(err)}）");
        }
        try
        {
            // size = 0 → 映射整个 section，再从控制头读容量
            var view = NativeApi.MapViewOfFile(hMap, NativeApi.FILE_MAP_ALL_ACCESS, 0, 0, UIntPtr.Zero);
            if (view == IntPtr.Zero)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "映射共享内存失败");
            uint magic;
            int capacity;
            unsafe
            {
                magic = *(uint*)view;
                capacity = *(int*)((byte*)view + 8);
            }
            if (magic != Magic)
                throw new ProtocolException($"共享内存 {name} magic 不匹配（0x{magic:X8}）");
            if (capacity <= 0 || capacity > 256 * 1024 * 1024)
                throw new ProtocolException($"共享内存 {name} 容量非法：{capacity}");
            var ring = new SharedFrameRing(name, hMap, view, capacity, false);
            ring.OpenEvents(readyEvent, consumedEvent);
            return ring;
        }
        catch
        {
            NativeApi.CloseHandle(hMap);
            throw;
        }
    }

    private static IntPtr CreateEventWithDacl(NativeApi.SECURITY_ATTRIBUTES sa, string name)
    {
        var saPtr = Marshal.AllocHGlobal(Marshal.SizeOf<NativeApi.SECURITY_ATTRIBUTES>());
        try
        {
            Marshal.StructureToPtr(sa, saPtr, false);
            var h = NativeApi.CreateEventExW(saPtr, name, 0, 0x1F0003 /* EVENT_ALL_ACCESS */);
            if (h == IntPtr.Zero)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), $"创建事件 {name} 失败");
            return h;
        }
        finally { Marshal.FreeHGlobal(saPtr); }
    }

    private void OpenEvents(string ready, string consumed)
    {
        // 说明：只支持"打开已存在的命名的对象"，容器/服务进程创建的 DACL 已放开给 Authenticated Users
        _ready = EventWaitHandle.OpenExisting(ready);
        _consumed = EventWaitHandle.OpenExisting(consumed);
    }

    private void InitializeHeader()
    {
        var p = (byte*)_view;
        new Span<byte>(p, HeaderSize).Clear();
        *(uint*)(p + 0) = Magic;
        *(int*)(p + 4) = 1;
        *(int*)(p + 8) = Capacity;
        *(long*)(p + 16) = 0;
        *(long*)(p + 24) = 0;
        *(long*)(p + 32) = 0;
        *(long*)(p + 40) = 0;
        *(long*)(p + 48) = 0;
        *(long*)(p + 56) = 0;
    }

    private byte* Data => (byte*)_view + HeaderSize;

    public long WritePos => Volatile.Read(ref *(long*)((byte*)_view + 16));
    public long ReadPos => Volatile.Read(ref *(long*)((byte*)_view + 24));
    public long FrameCount => Volatile.Read(ref *(long*)((byte*)_view + 32));
    public long DropCount => Volatile.Read(ref *(long*)((byte*)_view + 48));
    public long UsedBytes => WritePos - ReadPos;
    public long FreeBytes => Capacity - UsedBytes;
    public double FillRatio => Capacity <= 0 ? 0 : (double)UsedBytes / Capacity;
    public DateTime LastWriteUtc
    {
        get
        {
            long t = Volatile.Read(ref *(long*)((byte*)_view + 40));
            return t <= 0 ? DateTime.MinValue : new DateTime(t, DateTimeKind.Utc);
        }
    }

    /// <summary>重置读写指针（仅在确认对端未在写时调用，例如 Worker 重启挂接瞬间）</summary>
    public void Reset()
    {
        Volatile.Write(ref *(long*)((byte*)_view + 16), 0);
        Volatile.Write(ref *(long*)((byte*)_view + 24), 0);
    }

    /// <summary>
    /// 写入一帧。返回 false 表示缓冲已满（调用方应重启编码流）。
    /// </summary>
    public bool Write(ReadOnlySpan<byte> data, int timeoutMs = 1000, double dropThreshold = 0.90)
    {
        int need = 4 + data.Length;
        if (need > Capacity - 8) throw new ProtocolException($"帧过大：{data.Length} 字节 > 环形缓冲 {Capacity} 字节");

        long deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            long wp = WritePos, rp = ReadPos;
            long used = wp - rp;
            int offsetInLap = (int)(wp % Capacity);
            int availToEnd = Capacity - offsetInLap;

            if (used + need > Capacity)
            {
                // 满：先看是否已超丢弃阈值（保低延迟）
                if (used > Capacity * dropThreshold)
                {
                    Interlocked.Increment(ref *(long*)((byte*)_view + 48));
                    return false;
                }
                if (Environment.TickCount64 >= deadline)
                {
                    Interlocked.Increment(ref *(long*)((byte*)_view + 48));
                    return false;
                }
                _consumed!.WaitOne(50);
                continue;
            }

            if (need > availToEnd)
            {
                // 本圈放不下 → 写换圈标记，跳到下一圈起点
                if (used + 4 > Capacity)
                {
                    if (Environment.TickCount64 >= deadline || used > Capacity * dropThreshold)
                    {
                        Interlocked.Increment(ref *(long*)((byte*)_view + 48));
                        return false;
                    }
                    _consumed!.WaitOne(50);
                    continue;
                }
                *(uint*)(Data + offsetInLap) = WrapMarker;
                Volatile.Write(ref *(long*)((byte*)_view + 16), wp + availToEnd);
                continue;
            }

            // 正常写入
            var dst = Data + offsetInLap;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(new Span<byte>(dst, 4), (uint)data.Length);
            data.CopyTo(new Span<byte>(dst + 4, data.Length));
            Volatile.Write(ref *(long*)((byte*)_view + 32), Volatile.Read(ref *(long*)((byte*)_view + 32)) + 1);
            Volatile.Write(ref *(long*)((byte*)_view + 40), DateTime.UtcNow.Ticks);
            Volatile.Write(ref *(long*)((byte*)_view + 16), wp + need);
            _ready!.Set();
            return true;
        }
    }

    /// <summary>读取一帧；无数据返回 null（等待 timeoutMs）</summary>
    public byte[]? Read(int timeoutMs)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            long wp = WritePos, rp = ReadPos;
            if (wp == rp)
            {
                if (Environment.TickCount64 >= deadline) return null;
                _ready!.WaitOne(Math.Min(50, Math.Max(1, (int)(deadline - Environment.TickCount64))));
                continue;
            }
            int offsetInLap = (int)(rp % Capacity);
            uint len = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(new ReadOnlySpan<byte>(Data + offsetInLap, 4));
            if (len == WrapMarker)
            {
                Volatile.Write(ref *(long*)((byte*)_view + 24), rp + (Capacity - offsetInLap));
                continue;
            }
            if (len > Capacity - 8)
                throw new ProtocolException($"共享内存记录长度非法 {len}");
            if (rp + 4 + (long)len > wp)
            {
                // 数据尚未完全发布（正常不应发生）
                if (Environment.TickCount64 >= deadline) return null;
                Thread.Sleep(1);
                continue;
            }
            var buf = new byte[len];
            new ReadOnlySpan<byte>(Data + offsetInLap + 4, (int)len).CopyTo(buf);
            Volatile.Write(ref *(long*)((byte*)_view + 24), rp + 4 + len);
            _consumed!.Set();
            return buf;
        }
    }

    public void Dispose()
    {
        try
        {
            _ready?.Dispose();
            _consumed?.Dispose();
        }
        catch { }
        _ready = null;
        _consumed = null;
        if (_view != IntPtr.Zero) { NativeApi.UnmapViewOfFile(_view); _view = IntPtr.Zero; }
        if (_hMap != IntPtr.Zero && _ownsMap) { NativeApi.CloseHandle(_hMap); }
        _hMap = IntPtr.Zero;
    }
}
