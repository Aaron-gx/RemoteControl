using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agent.Common;

/// <summary>
/// 消息类型（WebSocket 二进制帧 / Named Pipe 帧 共用）
/// </summary>
public enum MessageType : byte
{
    /// <summary>视频帧： [8B timestamp][N bytes H.264]</summary>
    VideoFrame = 0x01,
    /// <summary>鼠标移动： [2B x][2B y]</summary>
    MouseMove = 0x02,
    /// <summary>鼠标按键： [2B x][2B y][1B button][1B up/down]</summary>
    MouseButton = 0x03,
    /// <summary>鼠标滚轮： [2B x][2B y][2B delta]</summary>
    MouseWheel = 0x04,
    /// <summary>键盘事件： [2B vkCode][1B up/down][1B flags]</summary>
    KeyEvent = 0x05,

    /// <summary>软件列表： JSON [{name, exePath, icon(base64), isRunning}]</summary>
    SoftwareList = 0x10,
    /// <summary>打开软件： UTF-8 exePath</summary>
    OpenSoftware = 0x11,
    /// <summary>关闭软件： [4B pid]</summary>
    CloseSoftware = 0x12,
    /// <summary>重置视频流（请求对端重建编解码器；空负载）</summary>
    StreamReset = 0x13,
    /// <summary>显示器信息： JSON {left,top,width,height,dpi,count}</summary>
    MonitorInfo = 0x14,
    /// <summary>请求软件列表（空负载）</summary>
    RequestSoftwareList = 0x15,
    /// <summary>软件运行状态变化： JSON {exePath,pid,isRunning}</summary>
    SoftwareState = 0x16,

    /// <summary>剪贴板文本： UTF-8 text</summary>
    ClipboardText = 0x20,
    /// <summary>剪贴板文件通知： JSON {fileId, fileName, fileSize}</summary>
    ClipboardFile = 0x21,
    /// <summary>文件块： [16B fileId][4B offset][N bytes data]</summary>
    FileChunk = 0x22,

    /// <summary>心跳： [8B timestamp]（0 = 保活，非 0 = 回声请求）</summary>
    Heartbeat = 0x30,

    /// <summary>IPC: Worker 就绪 JSON {sessionId,width,height,monitor,backend}</summary>
    WorkerReady = 0x40,
    /// <summary>IPC: Worker 状态 JSON {captureFps,encodeFps,backend,framesSent}</summary>
    WorkerStatus = 0x41,
    /// <summary>IPC: 停止（Coordinator → Worker，空负载）</summary>
    Stop = 0x42,
    /// <summary>IPC: 文件传输入队（Worker → Coordinator，JSON FileDropInfo）</summary>
    FileDrop = 0x43,
    /// <summary>IPC: 视频出口拥塞提示（Coordinator → Worker，JSON BitrateHintInfo）</summary>
    IpcBitrateHint = 0x44,

    /// <summary>主控端选择远端鼠标走哪条路： [1B RemoteInputMode]</summary>
    InputModeHint = 0x60,
    /// <summary>主控端选择"画面显示哪些屏"： [1B CaptureMode]</summary>
    CaptureModeHint = 0x61,
    /// <summary>主控端请求"把画面坐标 (x,y) 处的窗口搬到虚拟外屏"： [2B x][2B y]（画面坐标）</summary>
    MoveWindowHint = 0x62,
    /// <summary>主控端请求"把某个已运行程序的窗口搬到虚拟外屏"： [4B pid]</summary>
    MoveAppWindowHint = 0x63,

    /// <summary>P2P 直连候选地址（被控端 → 主控端，JSON {port,tcp[],public}）</summary>
    DirectCandidates = 0x50,

    /// <summary>错误： UTF-8 message</summary>
    Error = 0xFE,
    /// <summary>断开： 空</summary>
    Disconnect = 0xFF,
}

/// <summary>
/// 线上协议实体：一个完整消息 = 1B Type + 4B Length(BE) + N Payload
/// </summary>
public sealed class ProtocolMessage
{
    public const int HeaderSize = 5;
    public const int MaxPayload = 64 * 1024 * 1024;

    public MessageType Type { get; set; }
    public byte[] Payload { get; set; } = Array.Empty<byte>();

    public ProtocolMessage() { }

    public ProtocolMessage(MessageType type, byte[] payload)
    {
        Type = type;
        Payload = payload;
    }

    public int GetTotalLength() => HeaderSize + Payload.Length;

    /// <summary>序列化为线上字节： [1B Type][4B Length][Payload]</summary>
    public byte[] ToFrame()
    {
        var buf = new byte[HeaderSize + Payload.Length];
        buf[0] = (byte)Type;
        BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(1, 4), Payload.Length);
        if (Payload.Length > 0)
            Payload.CopyTo(buf, HeaderSize);
        return buf;
    }

    /// <summary>把帧写入流（并 flush）</summary>
    public void WriteTo(Stream stream)
    {
        stream.Write(FrameHeader());
        if (Payload.Length > 0)
            stream.Write(Payload, 0, Payload.Length);
        stream.Flush();
    }

    /// <summary>帧头 5 字节（与 payload 分开发送时使用）</summary>
    public byte[] FrameHeader()
    {
        var buf = new byte[HeaderSize];
        buf[0] = (byte)Type;
        BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(1, 4), Payload.Length);
        return buf;
    }

    /// <summary>从流中读取一个完整消息；对端正常关闭返回 null</summary>
    public static ProtocolMessage? ReadFrom(Stream stream)
    {
        var header = ReadExactly(stream, HeaderSize);
        if (header == null) return null;
        var type = (MessageType)header[0];
        int len = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1, 4));
        if (len < 0 || len > MaxPayload)
            throw new ProtocolException($"非法负载长度 {len}");
        byte[] payload = Array.Empty<byte>();
        if (len > 0)
        {
            payload = ReadExactly(stream, len) ?? throw new ProtocolException("负载被截断");
        }
        return new ProtocolMessage(type, payload);
    }

    /// <summary>从缓冲区中解析一个消息，返回消耗字节数；数据不足返回 null</summary>
    public static ProtocolMessage? TryParse(ReadOnlySpan<byte> buffer, out int consumed)
    {
        consumed = 0;
        if (buffer.Length < HeaderSize) return null;
        int len = BinaryPrimitives.ReadInt32BigEndian(buffer.Slice(1, 4));
        if (len < 0 || len > MaxPayload)
            throw new ProtocolException($"非法负载长度 {len}");
        if (buffer.Length < HeaderSize + len) return null;
        var payload = buffer.Slice(HeaderSize, len).ToArray();
        consumed = HeaderSize + len;
        return new ProtocolMessage((MessageType)buffer[0], payload);
    }

    private static byte[]? ReadExactly(Stream s, int count)
    {
        var buf = new byte[count];
        int off = 0;
        while (off < count)
        {
            int n = s.Read(buf, off, count - off);
            if (n <= 0) return off == 0 ? null : throw new ProtocolException("连接提前关闭");
            off += n;
        }
        return buf;
    }
}

public sealed class ProtocolException : Exception
{
    public ProtocolException(string message) : base(message) { }
}

// ---------------------------------------------------------------- 负载编解码

public static class PayloadCodec
{
    public static byte[] Empty() => Array.Empty<byte>();

    // 0x01 视频帧 [8B ts][H.264]
    public static byte[] EncodeVideoFrame(long timestampMs, ReadOnlySpan<byte> h264)
    {
        var buf = new byte[8 + h264.Length];
        BinaryPrimitives.WriteInt64BigEndian(buf.AsSpan(0, 8), timestampMs);
        h264.CopyTo(buf.AsSpan(8));
        return buf;
    }

    public static (long TimestampMs, byte[] Data) DecodeVideoFrame(byte[] payload)
    {
        if (payload.Length < 8) throw new ProtocolException("视频帧负载过短");
        long ts = BinaryPrimitives.ReadInt64BigEndian(payload.AsSpan(0, 8));
        return (ts, payload.AsSpan(8).ToArray());
    }

    // 0x02 鼠标移动 [2B x][2B y]
    public static byte[] EncodeMouseMove(ushort x, ushort y)
    {
        var buf = new byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(0, 2), x);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(2, 2), y);
        return buf;
    }

    public static (ushort X, ushort Y) DecodeMouseMove(byte[] p)
    {
        Require(p, 4, "鼠标移动");
        return (BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(0, 2)),
                BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(2, 2)));
    }

    // 0x03 鼠标按键 [2B x][2B y][1B button][1B up/down]
    public static byte[] EncodeMouseButton(ushort x, ushort y, byte button, bool up)
    {
        var buf = new byte[6];
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(0, 2), x);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(2, 2), y);
        buf[4] = button;
        buf[5] = (byte)(up ? 1 : 0);
        return buf;
    }

    public static (ushort X, ushort Y, byte Button, bool Up) DecodeMouseButton(byte[] p)
    {
        Require(p, 6, "鼠标按键");
        return (BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(0, 2)),
                BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(2, 2)),
                p[4], p[5] != 0);
    }

    // 0x04 鼠标滚轮 [2B x][2B y][2B delta]
    public static byte[] EncodeMouseWheel(ushort x, ushort y, short delta)
    {
        var buf = new byte[6];
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(0, 2), x);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(2, 2), y);
        BinaryPrimitives.WriteInt16BigEndian(buf.AsSpan(4, 2), delta);
        return buf;
    }

    // 0x62 搬窗口 [2B x][2B y]（画面坐标）
    public static byte[] EncodePoint(ushort x, ushort y)
    {
        var buf = new byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(0, 2), x);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(2, 2), y);
        return buf;
    }

    public static (ushort X, ushort Y) DecodePoint(byte[] p)
    {
        Require(p, 4, "坐标");
        return (BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(0, 2)),
                BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(2, 2)));
    }

    public static (ushort X, ushort Y, short Delta) DecodeMouseWheel(byte[] p)
    {
        Require(p, 6, "鼠标滚轮");
        return (BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(0, 2)),
                BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(2, 2)),
                BinaryPrimitives.ReadInt16BigEndian(p.AsSpan(4, 2)));
    }

    // 0x05 键盘 [2B vkCode][1B up/down][1B flags]   flags: bit0=Extended
    public static byte[] EncodeKey(ushort vkCode, bool up, byte flags = 0)
    {
        var buf = new byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(0, 2), vkCode);
        buf[2] = (byte)(up ? 1 : 0);
        buf[3] = flags;
        return buf;
    }

    public static (ushort Vk, bool Up, byte Flags) DecodeKey(byte[] p)
    {
        Require(p, 4, "键盘事件");
        return (BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(0, 2)), p[2] != 0, p[3]);
    }

    // 0x12 关闭软件 [4B pid]
    public static byte[] EncodeCloseSoftware(int pid)
    {
        var buf = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buf, pid);
        return buf;
    }

    public static int DecodeCloseSoftware(byte[] p)
    {
        Require(p, 4, "关闭软件");
        return BinaryPrimitives.ReadInt32BigEndian(p);
    }

    // 0x30 心跳 [8B ts]
    public static byte[] EncodeHeartbeat(long timestampMs)
    {
        var buf = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(buf, timestampMs);
        return buf;
    }

    public static long DecodeHeartbeat(byte[] p)
    {
        Require(p, 8, "心跳");
        return BinaryPrimitives.ReadInt64BigEndian(p);
    }

    // 0x22 文件块 [16B fileId][4B offset][N data]
    public static byte[] EncodeFileChunk(string fileId, int offset, ReadOnlySpan<byte> data)
    {
        var id = Encoding.ASCII.GetBytes(fileId);
        if (id.Length > 16) throw new ProtocolException("fileId 超过 16 字节");
        var buf = new byte[20 + data.Length];
        id.CopyTo(buf, 0);
        BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(16, 4), offset);
        data.CopyTo(buf.AsSpan(20));
        return buf;
    }

    public static (string FileId, int Offset, byte[] Data) DecodeFileChunk(byte[] p)
    {
        Require(p, 20, "文件块");
        int end = Array.IndexOf(p, (byte)0, 0, 16);
        if (end < 0) end = 16;
        var id = Encoding.ASCII.GetString(p, 0, end);
        int off = BinaryPrimitives.ReadInt32BigEndian(p.AsSpan(16, 4));
        return (id, off, p.AsSpan(20).ToArray());
    }

    public static byte[] EncodeText(string text) => Encoding.UTF8.GetBytes(text);
    public static string DecodeText(byte[] p) => Encoding.UTF8.GetString(p);

    private static void Require(byte[] p, int min, string what)
    {
        if (p.Length < min) throw new ProtocolException($"{what} 负载过短：{p.Length} < {min}");
    }
}

// ---------------------------------------------------------------- JSON 模型

public static class JsonCodec
{
    public static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        // 跨进程/跨语言（Go、匿名类型）序列化时属性名大小写常常不一致，一律不敏感
        PropertyNameCaseInsensitive = true,
    };

    public static byte[] Encode<T>(T value) =>
        JsonSerializer.SerializeToUtf8Bytes(value, Options);

    public static T? Decode<T>(byte[] payload) =>
        JsonSerializer.Deserialize<T>(payload, Options);

    public static T? Decode<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options);
}

/// <summary>软件列表项（0x10）</summary>
public sealed class SoftwareInfo
{
    public string Name { get; set; } = "";
    public string ExePath { get; set; } = "";
    /// <summary>PNG 图标的 base64（可选）</summary>
    public string? Icon { get; set; } = null;
    public bool IsRunning { get; set; } = false;
    public int Pid { get; set; } = 0;
}

/// <summary>软件运行状态变化（0x16）</summary>
public sealed class SoftwareStateInfo
{
    public string ExePath { get; set; } = "";
    public int Pid { get; set; }
    public bool IsRunning { get; set; }
}

/// <summary>剪贴板文件通知（0x21）</summary>
public sealed class ClipboardFileInfo
{
    public string FileId { get; set; } = "";
    public string FileName { get; set; } = "";
    public long FileSize { get; set; }
}

/// <summary>显示器信息（0x14）</summary>
public sealed class MonitorInfo
{
    public int Left { get; set; }
    public int Top { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int Dpi { get; set; } = 96;
    public int Count { get; set; } = 1;

    /// <summary>当前是否显示"全部屏幕"（false = 只显示虚拟外屏）</summary>
    public bool AllScreens { get; set; }

    /// <summary>
    /// 被控端每块屏在当前画面里的位置（画面坐标系 = 从 Left/Top 起算）。
    /// 主控端据此把"物理屏"那几块画成只读区域（远端只能看、点不到），
    /// 而虚拟外屏那块才是可操作的 —— 这样既能看到主屏上的东西，又不会打扰机器前面的人。
    /// </summary>
    public List<ScreenRect> Screens { get; set; } = new();
}

/// <summary>画面里的一块屏</summary>
public sealed class ScreenRect
{
    public int Left { get; set; }
    public int Top { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    /// <summary>是不是虚拟外屏（可操作的那块）</summary>
    public bool IsVirtual { get; set; }
    /// <summary>主屏（用于画标注）</summary>
    public bool IsPrimary { get; set; }
    public string Device { get; set; } = "";
}

/// <summary>画面模式（主控端 → 被控端）</summary>
public enum CaptureMode : byte
{
    /// <summary>只显示虚拟外屏（默认：省带宽，1080p 一块屏）</summary>
    VirtualOnly = 0,
    /// <summary>显示被控端全部屏幕（一块画布：主屏 + 副屏，带宽约等于屏数倍）</summary>
    AllScreens = 1,
    /// <summary>只显示物理主屏（看本机用户屏幕上在干什么；那块屏只读，点击窗口=把它搬到副屏）</summary>
    PrimaryOnly = 2,
}

/// <summary>Worker 就绪（0x40，IPC）</summary>
public sealed class WorkerReadyInfo
{
    public int SessionId { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string Monitor { get; set; } = "";
    public string Backend { get; set; } = "";
    public string Version { get; set; } = "";
}

/// <summary>
/// 主控端可选的"远端鼠标走哪条路"。默认用真实光标：兼容性最好（悬停/拖拽/原生右键菜单全都能用），
/// 代价是本机用户的光标会被短暂借用（CursorArbiter 会在他一动鼠标时立刻还回去）。
/// 后台定向注入完全不碰本机光标，但部分应用（微信这类不吃合成消息的）点了没反应 —— 实测踩到。
/// </summary>
public enum RemoteInputMode : byte
{
    /// <summary>自动：本机用户空闲 → 真实光标；他在用键鼠 → 后台定向注入（互不打扰，但兼容性看应用）</summary>
    Auto = 0,
    /// <summary>始终真实光标（默认，兼容性最好）</summary>
    AlwaysRealCursor = 1,
    /// <summary>始终后台定向注入（完全不打扰本机用户，代价是部分应用点不动）</summary>
    AlwaysBackground = 2,
}

/// <summary>视频出口拥塞提示（IPC，协调器 → Worker）：让码率自适应知道"中继这条腿堵了"</summary>
public sealed class BitrateHintInfo
{
    /// <summary>视频出口是否拥塞（队列积压或刚丢过帧）</summary>
    public bool Congested { get; set; }
    /// <summary>视频发送队列当前积压条数</summary>
    public int VideoQueued { get; set; }
    /// <summary>被挤掉的视频块数（累计，单调递增）</summary>
    public long VideoDropped { get; set; }
}

/// <summary>Worker 状态（0x41，IPC）</summary>
public sealed class WorkerStatusInfo
{
    public double CaptureFps { get; set; }
    public double EncodeFps { get; set; }
    public string Backend { get; set; } = "";
    public long FramesSent { get; set; }
    public long BytesSent { get; set; }

    // ---- 下面几个字段说明"为什么画面不动"：链路拥塞 / 自适应降档。
    // 以前这些只有被控端日志里有，主控端界面上一片黑，用户只能猜是不是软件坏了。

    /// <summary>当前生效的码率 kbps（自适应会降它；0 = 未知）</summary>
    public int BitrateKbps { get; set; }

    /// <summary>当前生效的帧率（自适应会降它；0 = 未知）</summary>
    public int FrameRate { get; set; }

    /// <summary>码率自适应档位（0 = 未降档）</summary>
    public int AdaptiveLevel { get; set; }

    /// <summary>因帧缓冲写不进去而丢弃的块数（被控端→中继这条链路拥塞的信号，单调递增）</summary>
    public long RingDropped { get; set; }

    /// <summary>中继发送队列当前积压的消息数（协调器填）</summary>
    public long RelayQueued { get; set; }

    /// <summary>因中继发送队列满而丢弃的消息数（协调器填，单调递增）</summary>
    public long RelayDropped { get; set; }

    // ---- 以下字段由协调器补上：说明视频**实际**走了哪条路 ----
    // 主控端据此显示"直连/中继"，验收脚本据此断言 P2P 是否真的生效。

    /// <summary>经 P2P 直连发出的视频帧数（0 = 未走直连）</summary>
    public long DirectFrames { get; set; }

    /// <summary>经 P2P 直连发出的视频字节数</summary>
    public long DirectBytes { get; set; }

    /// <summary>经中继发出的视频块数；直连生效后不应再增长</summary>
    public long RelayChunks { get; set; }

    /// <summary>直连当前是否已建立</summary>
    public bool DirectActive { get; set; }

    // ---- 授权信息（同样由协调器补上）：主控端据此显示"激活码到期时间" ----

    /// <summary>授权编号（空 = 尚未拿到有效授权）</summary>
    public string LicenseId { get; set; } = "";

    /// <summary>客户/备注</summary>
    public string LicenseSub { get; set; } = "";

    /// <summary>到期时间（Unix 秒，0 = 未知）</summary>
    public long LicenseExpUnix { get; set; }

    /// <summary>剩余天数（负数 = 已过期）</summary>
    public double LicenseDaysLeft { get; set; }

    /// <summary>本地授权闸门是否已停机（true = 被控端已停止服务）</summary>
    public bool LicenseLocked { get; set; }

    /// <summary>停机原因</summary>
    public string LicenseLockReason { get; set; } = "";

    // ---- 输入侧状态（主控端用来显示"远端鼠标点在哪块屏、走的哪条路"）----
    // 现场最费时间的问题就是"看着副屏点，结果点到主屏"，而那时日志和界面都是正常的：
    // 把这三个事实直接上报，界面上一眼可辨。

    /// <summary>输入换算基准：被采集屏左上角在虚拟桌面中的坐标（远端画面 (x,y) → 桌面 (base+x, base+y)）</summary>
    public int InputBaseX { get; set; }
    public int InputBaseY { get; set; }

    /// <summary>被采集屏是不是虚拟外屏（false = 虚拟外屏没装上/被关掉，远端只能操作物理主屏）</summary>
    public bool CaptureIsVirtual { get; set; }

    /// <summary>当前画面是不是"全部屏幕"（主屏 + 副屏 一块画布）</summary>
    public bool AllScreens { get; set; }

    /// <summary>远端鼠标当前走哪条路："real" = 真实光标，"background" = 后台定向注入</summary>
    public string InputPath { get; set; } = "";

    /// <summary>
    /// 本机光标被别人控制住了（注入的移动到不了目标点，通常是同机还有别的远控软件/旧被控端）。
    /// 此时被控端已自动改走后台定向注入，点击不会再落到主屏。
    /// </summary>
    public bool CursorContested { get; set; }

    /// <summary>光标不受控时，可能是哪些程序在抢（远控/鼠标类进程名，供用户照着关掉）</summary>
    public string CursorBlockerHint { get; set; } = "";

    // ---- 被控端主动上报的自身信息（主控端用来显示"这是哪台电脑"）----

    /// <summary>计算机名</summary>
    public string HostName { get; set; } = "";

    /// <summary>用户名</summary>
    public string UserName { get; set; } = "";

    /// <summary>操作系统版本（如 Windows 11 24H2 build 26100）</summary>
    public string OsVersion { get; set; } = "";

    /// <summary>被控端程序版本</summary>
    public string AgentVersion { get; set; } = "";

    // ---- 被控端桌面状态 ----

    /// <summary>
    /// 被控端此刻不在自己的桌面/输入桌面上：UAC 提权窗口、锁屏、Ctrl+Alt+Del 安全界面。
    /// 这段时间画面抓不到（主控端看到的是黑屏或冻结帧）、键鼠也进不去 —— 主控端据此给出
    /// 明确提示，而不是让人对着黑屏猜是不是软件坏了。
    /// </summary>
    public bool SecureDesktop { get; set; }
}

/// <summary>剪贴板文件投递（0x43，IPC）</summary>
public sealed class FileDropInfo
{
    public string LocalPath { get; set; } = "";
    public string FileName { get; set; } = "";
    public long FileSize { get; set; }
}
