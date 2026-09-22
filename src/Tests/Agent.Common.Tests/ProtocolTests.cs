using System.Text;
using Agent.Common;
using Xunit;

namespace Agent.Common.Tests;

/// <summary>协议序列化 / 反序列化（策划 §7.1 + §十一 自动化测试）</summary>
public class ProtocolTests
{
    [Fact]
    public void 帧头格式_为1字节类型加4字节大端长度()
    {
        var msg = new ProtocolMessage(MessageType.MouseMove, new byte[] { 0x01, 0x02, 0x03, 0x04 });
        var frame = msg.ToFrame();
        Assert.Equal(9, frame.Length);
        Assert.Equal(0x02, frame[0]);
        Assert.Equal(0, frame[1]);
        Assert.Equal(0, frame[2]);
        Assert.Equal(0, frame[3]);
        Assert.Equal(4, frame[4]);
        Assert.Equal(new byte[] { 0x01, 0x02, 0x03, 0x04 }, frame[5..]);
    }

    [Fact]
    public void 大端长度_超过255字节也正确()
    {
        var payload = new byte[300];
        var frame = new ProtocolMessage(MessageType.VideoFrame, payload).ToFrame();
        // 300 = 0x0000012C → 大端四字节 00 00 01 2C
        Assert.Equal(0x01, frame[0]);
        Assert.Equal(0x00, frame[1]);
        Assert.Equal(0x00, frame[2]);
        Assert.Equal(0x01, frame[3]);
        Assert.Equal(0x2C, frame[4]);
        Assert.Equal(300 + 5, frame.Length);
    }

    [Theory]
    [InlineData(0x01)]
    [InlineData(0x10)]
    [InlineData(0x30)]
    [InlineData(0xFF)]
    public void 所有类型都能往返(byte type)
    {
        var msg = new ProtocolMessage((MessageType)type, new byte[] { 1, 2, 3 });
        var parsed = ProtocolMessage.TryParse(msg.ToFrame(), out int consumed);
        Assert.NotNull(parsed);
        Assert.Equal(type, (byte)parsed!.Type);
        Assert.Equal(new byte[] { 1, 2, 3 }, parsed.Payload);
        Assert.Equal(8, consumed);
    }

    [Fact]
    public void 数据不足时返回null而不是抛异常()
    {
        var frame = new ProtocolMessage(MessageType.Error, new byte[100]).ToFrame();
        for (int len = 0; len < frame.Length; len++)
        {
            var sliced = frame.AsSpan(0, len).ToArray();
            Assert.Null(ProtocolMessage.TryParse(sliced, out _));
        }
        Assert.NotNull(ProtocolMessage.TryParse(frame, out _));
    }

    [Fact]
    public void 长度非法时抛协议异常()
    {
        var bogus = new byte[] { 0x01, 0x7F, 0xFF, 0xFF, 0xFF };
        Assert.Throws<ProtocolException>(() => ProtocolMessage.TryParse(bogus, out _));
    }

    [Fact]
    public void 流式读写_往返一致()
    {
        var msgs = new List<ProtocolMessage>
        {
            new(MessageType.MouseMove, PayloadCodec.EncodeMouseMove(1234, 5678)),
            new(MessageType.Heartbeat, PayloadCodec.EncodeHeartbeat(99999)),
            new(MessageType.ClipboardText, PayloadCodec.EncodeText("你好，远程控制")),
            new(MessageType.Disconnect, PayloadCodec.Empty()),
        };
        using var ms = new MemoryStream();
        foreach (var m in msgs) m.WriteTo(ms);
        ms.Position = 0;
        var read = new List<ProtocolMessage>();
        ProtocolMessage? got;
        while ((got = ProtocolMessage.ReadFrom(ms)) != null) read.Add(got);

        Assert.Equal(msgs.Count, read.Count);
        for (int i = 0; i < msgs.Count; i++)
        {
            Assert.Equal(msgs[i].Type, read[i].Type);
            Assert.Equal(msgs[i].Payload, read[i].Payload);
        }
    }

    [Fact]
    public void 一帧拆成多段也能解析()
    {
        var frame = new ProtocolMessage(MessageType.VideoFrame, new byte[5000]).ToFrame();
        var acc = new FrameAccumulator(ipcFraming: false);
        var results = new List<ProtocolMessage>();
        foreach (var b in frame)
        {
            acc.Append(new[] { b }, 0, 1);
            acc.Drain(results);
        }
        Assert.Single(results);
        Assert.Equal(5000, results[0].Payload.Length);
    }

    [Fact]
    public void 多帧粘连一次喂入全部解析()
    {
        using var ms = new MemoryStream();
        for (int i = 0; i < 10; i++)
            new ProtocolMessage(MessageType.KeyEvent, PayloadCodec.EncodeKey((ushort)i, false)).WriteTo(ms);
        var data = ms.ToArray();
        var acc = new FrameAccumulator(ipcFraming: false);
        acc.Append(data, 0, data.Length);
        var results = new List<ProtocolMessage>();
        acc.Drain(results);
        Assert.Equal(10, results.Count);
        Assert.Equal(0, acc.BufferedBytes);
    }
}

public class PayloadCodecTests
{
    [Fact]
    public void 鼠标移动往返()
    {
        var (x, y) = PayloadCodec.DecodeMouseMove(PayloadCodec.EncodeMouseMove(1920, 1080));
        Assert.Equal(1920, x);
        Assert.Equal(1080, y);
    }

    [Fact]
    public void 鼠标按键往返()
    {
        var p = PayloadCodec.EncodeMouseButton(100, 200, 1, true);
        Assert.Equal(6, p.Length);
        var (x, y, btn, up) = PayloadCodec.DecodeMouseButton(p);
        Assert.Equal(100, x);
        Assert.Equal(200, y);
        Assert.Equal(1, btn);
        Assert.True(up);
    }

    [Fact]
    public void 滚轮负数往返()
    {
        var (_, _, d) = PayloadCodec.DecodeMouseWheel(PayloadCodec.EncodeMouseWheel(5, 6, -120));
        Assert.Equal(-120, d);
    }

    [Fact]
    public void 键盘往返()
    {
        var (vk, up, flags) = PayloadCodec.DecodeKey(PayloadCodec.EncodeKey(0x41, false, 1));
        Assert.Equal(0x41, vk);
        Assert.False(up);
        Assert.Equal(1, flags);
    }

    [Fact]
    public void 视频帧时间戳与负载()
    {
        var h264 = new byte[] { 0, 0, 0, 1, 0x67, 0x42 };
        var payload = PayloadCodec.EncodeVideoFrame(123456789, h264);
        var (ts, data) = PayloadCodec.DecodeVideoFrame(payload);
        Assert.Equal(123456789, ts);
        Assert.Equal(h264, data);
    }

    [Fact]
    public void 文件块往返_含中文文件名无关()
    {
        var data = Encoding.UTF8.GetBytes("chunk-data");
        var p = PayloadCodec.EncodeFileChunk("abc123", 4096, data);
        var (id, off, d) = PayloadCodec.DecodeFileChunk(p);
        Assert.Equal("abc123", id);
        Assert.Equal(4096, off);
        Assert.Equal(data, d);
    }

    [Fact]
    public void 关闭软件pid往返_负数也能()
    {
        Assert.Equal(4321, PayloadCodec.DecodeCloseSoftware(PayloadCodec.EncodeCloseSoftware(4321)));
        Assert.Equal(-1, PayloadCodec.DecodeCloseSoftware(PayloadCodec.EncodeCloseSoftware(-1)));
    }

    [Fact]
    public void 文本往返_UTF8中文()
    {
        const string text = "远程剪贴板：中文、emoji 🚀、符号 <>&\"'";
        Assert.Equal(text, PayloadCodec.DecodeText(PayloadCodec.EncodeText(text)));
    }

    [Fact]
    public void 负载过短时抛异常()
    {
        Assert.Throws<ProtocolException>(() => PayloadCodec.DecodeMouseMove(new byte[3]));
        Assert.Throws<ProtocolException>(() => PayloadCodec.DecodeKey(new byte[1]));
        Assert.Throws<ProtocolException>(() => PayloadCodec.DecodeHeartbeat(new byte[4]));
        Assert.Throws<ProtocolException>(() => PayloadCodec.DecodeVideoFrame(new byte[7]));
    }
}

public class IpcProtocolTests
{
    [Fact]
    public void IPC帧_往返一致()
    {
        var msg = new ProtocolMessage(MessageType.MouseButton, PayloadCodec.EncodeMouseButton(10, 20, 0, false));
        var wrapped = IpcProtocol.Wrap(msg);
        var parsed = IpcProtocol.TryParse(wrapped, out int consumed);
        Assert.NotNull(parsed);
        Assert.Equal(msg.Type, parsed!.Type);
        Assert.Equal(msg.Payload, parsed.Payload);
        Assert.Equal(wrapped.Length, consumed);
    }

    [Fact]
    public void IPC_多条消息依次解析()
    {
        using var ms = new MemoryStream();
        var sent = new List<ProtocolMessage>();
        for (int i = 0; i < 5; i++)
        {
            var m = new ProtocolMessage(MessageType.KeyEvent, PayloadCodec.EncodeKey((ushort)(i + 65), i % 2 == 0, 0));
            sent.Add(m);
            IpcProtocol.WriteTo(ms, m);
        }
        ms.Position = 0;
        var got = new List<ProtocolMessage>();
        ProtocolMessage? m2;
        while ((m2 = IpcProtocol.ReadFrom(ms)) != null) got.Add(m2);
        Assert.Equal(sent.Count, got.Count);
        for (int i = 0; i < sent.Count; i++)
            Assert.Equal(sent[i].Payload, got[i].Payload);
    }

    [Fact]
    public void IPC_半包返回null()
    {
        var wrapped = IpcProtocol.Wrap(new ProtocolMessage(MessageType.Error, new byte[50]));
        Assert.Null(IpcProtocol.TryParse(wrapped.AsSpan(0, wrapped.Length - 1), out _));
    }
}

public class JsonCodecTests
{
    [Fact]
    public void 软件列表往返_含图标与运行状态()
    {
        var list = new List<SoftwareInfo>
        {
            new() { Name = "记事本", ExePath = @"C:\Windows\System32\notepad.exe", IsRunning = true, Pid = 1234, Icon = "AAAB" },
            new() { Name = "Chrome", ExePath = @"C:\Program Files\Google\Chrome\chrome.exe", IsRunning = false },
        };
        var json = JsonCodec.Encode(list);
        var back = JsonCodec.Decode<List<SoftwareInfo>>(json);
        Assert.NotNull(back);
        Assert.Equal(2, back!.Count);
        Assert.Equal("记事本", back[0].Name);
        Assert.True(back[0].IsRunning);
        Assert.Equal(1234, back[0].Pid);
        Assert.Equal("AAAB", back[0].Icon);
        Assert.False(back[1].IsRunning);
        Assert.Null(back[1].Icon);
    }

    [Fact]
    public void 剪贴板文件通知往返()
    {
        var info = new ClipboardFileInfo { FileId = "0a1b2c3d4e5f6071", FileName = "报告.docx", FileSize = 123456 };
        var back = JsonCodec.Decode<ClipboardFileInfo>(JsonCodec.Encode(info));
        Assert.Equal(info.FileId, back!.FileId);
        Assert.Equal(info.FileName, back.FileName);
        Assert.Equal(info.FileSize, back.FileSize);
    }

    [Fact]
    public void 显示器信息往返()
    {
        var mi = new MonitorInfo { Left = 0, Top = 0, Width = 1920, Height = 1080, Dpi = 96, Count = 2 };
        var back = JsonCodec.Decode<MonitorInfo>(JsonCodec.Encode(mi));
        Assert.Equal(1920, back!.Width);
        Assert.Equal(1080, back.Height);
        Assert.Equal(2, back.Count);
    }

    [Fact]
    public void 状态与小文件投递往返()
    {
        var st = JsonCodec.Decode<SoftwareStateInfo>(JsonCodec.Encode(new SoftwareStateInfo
        { ExePath = "x.exe", Pid = 7, IsRunning = true }));
        Assert.True(st!.IsRunning);
        Assert.Equal(7, st.Pid);

        var fd = JsonCodec.Decode<FileDropInfo>(JsonCodec.Encode(new FileDropInfo
        { LocalPath = @"C:\a\b.txt", FileName = "b.txt", FileSize = 10 }));
        Assert.Equal("b.txt", fd!.FileName);
        Assert.Equal(10, fd.FileSize);
    }
}

/// <summary>P2P 直连的协议细节（握手常量、候选 JSON 解析、帧格式兼容）</summary>
public class DirectLinkTests
{
    [Fact]
    public void 握手常量与客户端一致()
    {
        Assert.Equal("RC-DIRECT-1 ", Agent.Common.DirectLinkHandshake.Prefix);
        Assert.Equal("RC-DIRECT-OK", Agent.Common.DirectLinkHandshake.Ok);
        Assert.Equal("RC-DIRECT-DENY", Agent.Common.DirectLinkHandshake.Deny);
    }

    [Fact]
    public void 直连候选消息类型为0x50_且负载可解析()
    {
        Assert.Equal(0x50, (byte)MessageType.DirectCandidates);
        var json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "candidates",
            port = 47890,
            tcp = new[] { "192.168.1.5:47890", "10.0.0.3:47890" },
            @public = "203.0.113.7:47890",
            relay = true,
        });
        var msg = new ProtocolMessage(MessageType.DirectCandidates, json);
        var parsed = ProtocolMessage.TryParse(msg.ToFrame(), out int consumed);
        Assert.NotNull(parsed);
        Assert.Equal(msg.GetTotalLength(), consumed);
        using var doc = System.Text.Json.JsonDocument.Parse(parsed!.Payload);
        Assert.Equal(47890, doc.RootElement.GetProperty("port").GetInt32());
        Assert.Equal(2, doc.RootElement.GetProperty("tcp").GetArrayLength());
        Assert.Equal("203.0.113.7:47890", doc.RootElement.GetProperty("public").GetString());
    }

    [Fact]
    public void 直连链路上的视频帧与中继链路格式一致()
    {
        // 直连只是换传输层，帧格式必须与线上协议完全相同（便于主控端复用解析）
        var payload = PayloadCodec.EncodeVideoFrame(12345, new byte[] { 0, 0, 0, 1, 0x65 });
        var frame = new ProtocolMessage(MessageType.VideoFrame, payload).ToFrame();
        var acc = new FrameAccumulator(ipcFraming: false);
        acc.Append(frame, 0, frame.Length);
        var list = new List<ProtocolMessage>();
        acc.Drain(list);
        Assert.Single(list);
        Assert.Equal(MessageType.VideoFrame, list[0].Type);
        var (ts, data) = PayloadCodec.DecodeVideoFrame(list[0].Payload);
        Assert.Equal(12345, ts);
        Assert.Equal(new byte[] { 0, 0, 0, 1, 0x65 }, data);
    }

    [Fact]
    public void 本机内网地址枚举可用且不含回环()
    {
        var list = Agent.Common.NetHelper.LocalIPv4();
        Assert.All(list, ip => Assert.False(ip.StartsWith("127.")));
        Assert.All(list, ip => Assert.False(ip.StartsWith("169.254.")));
        Assert.NotEmpty(list);   // 任何正常联网的机器至少有一个内网地址
    }

    [Fact]
    public void 私网判断正确()
    {
        Assert.True(Agent.Common.NetHelper.IsPrivate("192.168.1.1"));
        Assert.True(Agent.Common.NetHelper.IsPrivate("10.1.2.3"));
        Assert.True(Agent.Common.NetHelper.IsPrivate("172.16.5.5"));
        Assert.False(Agent.Common.NetHelper.IsPrivate("203.0.113.7"));
        Assert.False(Agent.Common.NetHelper.IsPrivate("8.8.8.8"));
    }
}
