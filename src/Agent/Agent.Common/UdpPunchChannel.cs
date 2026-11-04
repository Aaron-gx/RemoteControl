using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace Agent.Common;

/// <summary>打洞角色（HELLO 里的 HMAC 绑定它，双向校验"对面是另一端"）</summary>
public enum PunchRole : byte
{
    Agent = 1,
    Viewer = 2,
}

/// <summary>中继上交换的打洞信息（MessageType.PunchInfo 的 JSON 负载）</summary>
public sealed class PunchOffer
{
    /// <summary>会话随机数 base64 —— 双方 HELLO 的 HMAC 都用它做盐，
    /// 没偷看过中继这条消息的人伪造不出合法 HELLO</summary>
    public string Nonce { get; set; } = "";
    /// <summary>本端 UDP 候选地址（公网映射 + 内网直连）</summary>
    public List<string> Udp { get; set; } = new();
    /// <summary>
    /// 本端疑似对称 NAT（2026-10-10）：两台 STUN 服务器看到的映射端口不同 ——
    /// 对端拿到的候选地址打不进来，双方都不用白费打洞窗口，直接走中继
    /// （对标 UU 的 PunchCheckFireWall："确认打不动就立刻转 relay"）。
    /// </summary>
    public bool Sym { get; set; }
    /// <summary>
    /// 本端能力位（2026-10-14，对标 UU SDP a=x-uuremote-mix-kcp 的 min/AND 协商）：
    /// bit0=KCP 可靠路径、bit1=RS-FEC 视频。旧端不带此字段 = 0 = 完整走旧路径。
    /// </summary>
    public int Cap { get; set; }
}

/// <summary>
/// 【2026-10-10 UU 式直连】STUN 自省 + UDP 打洞 + 分片数据通道，一条类打满"跨网络不走中继"。
///
/// 背景：主控与被控不在同一网络、双方路由器都没开 UPnP 时，视频/输入全走香港中继，
/// RTT 地板 ~170ms 且国际链路晚高峰拥塞 → 自适应码率被打到 397kbps/16fps（实测截图），
/// 这就是"很卡"。UU 远程的解法是 WebRTC（STUN 打洞 + UDP 直传 + FEC）；这里做同思路的
/// 轻量版：
///   1. 双方从**同一个 UDP socket** 问公共 STUN 服务器"我的公网映射是什么"；
///   2. 通过**既有中继 WS**（中继对消息内容透明转发，服务器零改动）交换候选地址；
///   3. 双方互相向对方候选发 HELLO（HMAC 防伪），锥形 NAT 上双向包一通，映射即打通；
///   4. 打通后：视频走 UDP 不可靠分片（残帧直接丢，靠 1 秒关键帧自愈）；
///      输入/控制走 UDP 可靠分片（ACK+重传，250ms 没确认自动回落中继补发，接收端按序号去重，
///      绝不会双击变四击）；鼠标移动单包尽力而为；
///   5. 3.5 秒收不到对端任何包 → 判死，视频/输入自动回中继，并自动重新打洞（最多 3 次）。
/// 服务器（Go 中继）**完全不用改**——这是能纯靠热更新铺开的硬性约束。
/// </summary>
public sealed class UdpPunchChannel : IDisposable
{
    // ---------------------------------------------------------------- 协议常量

    private const byte KindData = 0x01;    // 分片数据（可靠/不可靠共用，区别在发送端行为）
    private const byte KindAck = 0x02;     // 可靠消息确认：[u16 seq]
    private const byte KindHello = 0x03;   // 打洞问候：[1B role][32B hmac]
    private const byte KindPing = 0x04;    // 保活+测RTT：[8B tick]
    private const byte KindPong = 0x05;    // [8B tick 原样回显]
    private const byte KindNack = 0x06;    // 视频分片缺失报告：[u16 seq][u16 n][u16 idx × n]（2026-10-11）
    private const byte KindKcp = 0x07;     // KCP 段（2026-10-14 可靠路径，UU dckcp 同思路）：[KCP段]
    private const byte KindKcpAck = 0x08;  // KCP 消息带外确认：[u32 msgId]（发送端据此停中继补发计时）

    /// <summary>单个 UDP 数据报上限（含协议头）。留足 PPPoE/VPN 的封装余量</summary>
    private const int DatagramSize = 1100;
    private const int ChunkPayload = DatagramSize - 8;      // kind + seq/idx/count + msgType
    /// <summary>可靠消息的大小上限（剪贴板大文本请走中继）</summary>
    private const int MaxReliableBytes = 256 * 1024;
    /// <summary>可靠消息从发送到彻底放弃的时限（含若干轮重传）</summary>
    private const long ReliableGiveUpMs = 1500;
    /// <summary>没确认多久后把同一条消息交中继补发（双路径并发，接收端去重）</summary>
    private const long ReliableFallbackMs = 250;

    // ---- 视频丢包恢复（2026-10-11，对标 UU 的 FEC+NACK；2026-10-14 升级 RS-FEC）----
    /// <summary>大帧（≥16 分片，通常是关键帧）每 8 个数据片附 1 个 XOR 校验片：组内丢 1 片零延迟恢复
    /// （RS-FEC 协商成功后由 RS 取代，XOR 仅在旧对端时保留）</summary>
    private const int ParityMinChunks = 16, ParityGroup = 8;
    /// <summary>帧缺片后等多久发 NACK（等重传的 RTT 预算；残帧回收阈值与之配合）</summary>
    private const long NackGraceMs = 80;
    /// <summary>残帧回收年龄（400→500ms：给 NACK 重传留够一个中继 RTT 的来回）</summary>
    private const long FrameStaleMs = 500;
    /// <summary>每个缺失分片最多 NACK 次数（防重传风暴；2 次 ≈ 两个 RTT 的机会）</summary>
    private const int MaxNackPerChunk = 2;
    /// <summary>RS-FEC 模式下的 NACK 上限（4 次：RS 救不了的突发丢包多给两轮机会）</summary>
    private const int MaxNackPerChunkRs = 4;
    /// <summary>发送端视频帧缓存上限（NACK 重传查这里）</summary>
    private const int VideoCacheFrames = 96;
    /// <summary>控制循环的上一次残帧 NACK 扫描时刻</summary>
    private long _lastNackScanTick;

    // ---- 能力协商（2026-10-14，对标 UU SDP 的 min/AND 语义：双方都报了才启用）----
    /// <summary>本端能力位：KCP 可靠路径 + RS-FEC 视频。PunchOffer.Cap 带给对端，双方 AND。</summary>
    internal const int CapKcp = 0x01, CapRsFec = 0x02;
    internal const int LocalCap = CapKcp | CapRsFec;

    // ---- KCP 可靠路径（2026-10-14）----
    /// <summary>KCP 消息没收到带外 ACK 多久后交中继补发（KCP 快重传通常 &lt;30ms 恢复，
    /// 250ms 旧值会频繁双发；放宽到 400ms 只在真正劣化时才走中继）</summary>
    private const long KcpFallbackMs = 400;
    private KcpSession? _kcp;                       // 只在 live && effKcp 时存在
    private int _kcpMsgId;                          // KCP 消息编号（u32）
    private readonly object _kcpPendingGate = new();
    private readonly Dictionary<uint, KcpPending> _kcpPending = new();
    private int _peerCap;                           // 对端上报能力位
    private bool _effKcp, _effRs;                   // 生效能力（双方 AND）

    private sealed class KcpPending
    {
        public required uint Id;
        public required long FirstSend;
        public required Action<ProtocolMessage>? Fallback;
        public required ProtocolMessage Msg;
        public bool FallbackSent;
    }

    // ---- RS-FEC 自适应（对标 UU DcKcpFecGenerator::UpdateNetWorkInfo）----
    private long _fecWinAt, _fecWinSent, _fecWinNacked;
    private double _fecLossFrac;
    private int _fecR;   // 0 = 未启用/旧模式（诊断口径）
    private long _fecRecoveredChunks;

    internal static int ParityCountFor(int chunkCount) =>
        chunkCount >= ParityMinChunks ? Math.Min(4, chunkCount / ParityGroup) : 0;

    private readonly Logger _log;
    private readonly PunchRole _role;
    private readonly byte[] _key;                 // SHA256(token || 域分隔)，HELLO 的 HMAC 密钥
    private readonly byte[] _sessionNonce = RandomNumberGenerator.GetBytes(16);
    // 会话随机数：**以被控端生成的为准**（主控端从被控端的 PunchInfo 里学到它再签 HELLO；
    // 被控端永远用自己的，主控端 PunchInfo 里带的不认）。构造时先等于自己的，保证被控端
    // 不依赖任何交换就能应答/发起 HELLO。
    private byte[] _lastNonce;
    private readonly Socket _udp;
    private readonly Thread _recvThread;
    private readonly Thread _ctrlThread;
    private readonly CancellationTokenSource _cts = new();

    // 打洞/连通状态（只在 lock(_gate) 里改）
    private readonly object _gate = new();
    private List<IPEndPoint> _targets = new();
    private long _punchUntil;                     // 打洞窗口截止（窗口内每 350ms 发一轮 HELLO）
    private bool _live;
    private IPEndPoint? _peer;                    // 实际观察到对端包的来源（以它为准，不信候选里报的）
    private long _lastRxTick;                     // 最近一次收到对端任何合法包
    private int _punchAttempts;                   // 死链后自动重打洞的次数（OfferPeers 重置）
    private long _retryAt;                        // 死链冷却到点后自动重打洞
    private List<IPEndPoint> _lastTargets = new();

    // 候选地址（STUN/内网）
    private List<string> _candidates = new();
    private long _candidatesAt;
    private bool _candidatesBusy;
    public bool SymmetricNat { get; private set; }

    // STUN 等待者：接收循环替在途的 Binding Request 收响应（STUN 响应和数据包共用一个
    // socket——绝不能让两处各自 Receive，谁先醒谁把对方的包吃掉）
    private sealed class StunWait
    {
        public required byte[] Tid;
        public required TaskCompletionSource<IPEndPoint?> Tcs;
    }
    private volatile StunWait? _stunWait;

    // 发送侧
    private int _reliableSeq;                     // 可靠消息序号（u16 回绕）
    private int _unreliableSeq;                   // 鼠标移动等不可靠小消息
    private int _videoSeq;                        // 视频帧序号
    private readonly object _pendingGate = new();
    private readonly List<Pending> _pending = new();     // 只在 ctrlThread 触碰（Send 里加锁插入）

    // ---- 视频帧缓存（NACK 重传源，2026-10-11）----
    private sealed class CachedVideoFrame
    {
        public required byte[][] Chunks;
        public required byte[][]? Parity;
        /// <summary>0 = XOR/无校验（旧协议），1..6 = RS 每组校验片数（重传时要还原描述符字节）</summary>
        public int FecR;
        public long Tick;
    }
    private readonly object _cacheGate = new();
    private readonly Dictionary<ushort, CachedVideoFrame> _videoCache = new();
    private long _videoCacheBytes;
    /// <summary>测试钩子：数据片发送前回调 (seq, idx)，返回 true = 模拟丢弃（只影响首传数据片）</summary>
    internal Func<ushort, ushort, bool>? TestDropVideoChunk;

    private sealed class Pending
    {
        public required ushort Seq;
        public required byte MsgType;
        public required byte[] Payload;
        public required Action<ProtocolMessage>? Fallback;
        public required ProtocolMessage Msg;
        public long FirstSend;
        public long LastSend;
        public bool FallbackSent;
    }

    // 接收侧重组（只在 recvThread 触碰）
    private sealed class Frame
    {
        public required byte MsgType;
        public required byte[][] Chunks;
        public int Got;
        public long Born;
        public ushort Count;
        // ---- 丢包恢复（2026-10-11；2026-10-14 加 RS）----
        public byte[][]? Parity;          // 校验片（XOR 布局或 RS 布局，按 FecR 区分）
        public int ParityGot;
        /// <summary>0 = 旧 XOR 布局，1..6 = RS（每组 r 个校验片，组大小 16）</summary>
        public int FecR;
        public long LastChunkTick;        // 最近一个分片到达时刻（NACK 触发依据：停了还在缺 = 真丢了）
        public byte[]? NackCounts;        // 每个数据片的 NACK 次数（限流防风暴）
    }
    private readonly Dictionary<ushort, Frame> _frames = new();
    private int _recvExpected = -1;                        // 可靠消息按序交付的期望序号
    private readonly Dictionary<ushort, ProtocolMessage> _hold = new();
    private readonly Dictionary<ushort, long> _dupGuard = new();  // 可靠序号去重（含中继补发的重复）
    private long _holdSince;

    // 统计
    private long _framesSent, _bytesSent, _bytesRecv, _rttMs;
    private long _videoStarted, _videoCompleted, _videoDropped;
    private long _sendErrLog;
    // ping 丢包（2026-10-10 路径质量监控）：滑窗内"发出 ping 但没等到 pong"的比例。
    // 与视频残帧率互补：它不依赖视频流量 —— 视频被切到中继期间仍能持续评估直连质量，
    // 恢复判定就靠它（UU 的 filter_network_monitor 思路）。
    private long _pingSent, _pongRecv, _pingLossPct, _pingWindowStart;

    // ---------------------------------------------------------------- 公开面

    public bool IsLive { get { lock (_gate) return _live; } }
    public string? PeerEndpoint { get { lock (_gate) return _peer?.ToString(); } }
    public int RttMs => (int)Interlocked.Read(ref _rttMs);
    /// <summary>视频帧不完整率（%）：UDP 直连质量观测，>15% 视为拥塞（协调器据此压码率）</summary>
    public int LossPercent
    {
        get
        {
            long started = Interlocked.Read(ref _videoStarted);
            long dropped = Interlocked.Read(ref _videoDropped);
            if (started < 30) return 0;
            return (int)Math.Min(100, dropped * 100 / started);
        }
    }
    public long FramesSent => Interlocked.Read(ref _framesSent);
    public long BytesSent => Interlocked.Read(ref _bytesSent);
    public long BytesReceived => Interlocked.Read(ref _bytesRecv);
    public int LocalPort => ((IPEndPoint?)_udp.LocalEndPoint)?.Port ?? 0;

    /// <summary>
    /// 近 10 秒窗口内保活 ping 的无应答率（%，0-100；样本 &lt;5 时保持上一个窗口的值）。
    /// 发送侧可测：只数"我发了几个 ping、收到了几个 pong"，不需要对端配合、也不依赖视频流量。
    /// </summary>
    public int PingLossPercent => (int)Interlocked.Read(ref _pingLossPct);

    // ---- 诊断计数（2026-10-14，铁律 22：新路径的计数器必须与旧路径一起上报）----
    /// <summary>RS-FEC 当前冗余度（每组校验片数 r；非 RS 模式恒 0）</summary>
    public int FecParityR => _fecR;
    /// <summary>RS-FEC 累计零重传恢复的分片数</summary>
    public long FecRecoveredChunks => Interlocked.Read(ref _fecRecoveredChunks);
    /// <summary>KCP 可靠路径是否在跑（双端协商成功 + 会话存在 + 直连活）</summary>
    public bool KcpActive { get { lock (_gate) return _live && _kcp != null; } }
    /// <summary>KCP 层段收发字节</summary>
    public long KcpBytesSent => _kcp?.BytesSent ?? 0;
    public long KcpBytesRecv => _kcp?.BytesRecv ?? 0;

    /// <summary>打洞成功（此后 SendVideo/Send 走直连；上层视频路径每帧自查 IsLive，无需订阅）</summary>
    public event Action? Live;
    /// <summary>直连判死（3.5s 无对端流量）：上层收到后把依赖直连的状态回退；通道自动重打洞</summary>
    public event Action? Dead;
    /// <summary>完整收到对端一条消息（输入/控制/视频帧都从这里出）</summary>
    public event Action<ProtocolMessage>? MessageReceived;

    public UdpPunchChannel(Logger log, PunchRole role, string token)
    {
        _log = log;
        _role = role;
        _key = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes((token ?? "") + "|rc-udp-punch-2026"));
        _lastNonce = _sessionNonce;
        _udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _udp.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.Bind(new IPEndPoint(IPAddress.Any, 0));
        // 收发缓冲给足：视频突发时内核兜得住，SendTo 基本不阻塞编码线程
        try { _udp.SendBufferSize = 4 * 1024 * 1024; } catch { }
        try { _udp.ReceiveBufferSize = 4 * 1024 * 1024; } catch { }

        _recvThread = new Thread(ReceiveLoop) { IsBackground = true, Name = "udp-punch-recv" };
        _ctrlThread = new Thread(ControlLoop) { IsBackground = true, Name = "udp-punch-ctrl" };
        _recvThread.Start();
        _ctrlThread.Start();
    }

    // ---------------------------------------------------------------- 候选地址

    /// <summary>
    /// 在**本通道的 socket** 上发 STUN Binding Request，响应由接收循环收下转交（见 HandleDatagram）。
    /// 映射地址必须从打洞用的同一个 socket 问出来，所以不能另开 StunClient 的独立查询。
    /// </summary>
    private async Task<IPEndPoint?> QueryStunAsync(string host, int port, int timeoutMs = 1500)
    {
        try
        {
            if (!IPAddress.TryParse(host, out var ip))
            {
                var addrs = await Dns.GetHostAddressesAsync(host);
                ip = addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                if (ip == null) return null;
            }
            var tid = RandomNumberGenerator.GetBytes(12);
            var req = new byte[20];
            req[1] = 0x01;
            req[4] = 0x21; req[5] = 0x12; req[6] = 0xA4; req[7] = 0x42;
            Array.Copy(tid, 0, req, 8, 12);

            var w = new StunWait
            {
                Tid = tid,
                Tcs = new TaskCompletionSource<IPEndPoint?>(TaskCreationOptions.RunContinuationsAsynchronously),
            };
            _stunWait = w;
            try
            {
                _udp.SendTo(req, SocketFlags.None, new IPEndPoint(ip, port));
                var done = await Task.WhenAny(w.Tcs.Task, Task.Delay(timeoutMs));
                return done == w.Tcs.Task ? await w.Tcs.Task : null;
            }
            finally
            {
                _stunWait = null;
            }
        }
        catch { return null; }
    }

    /// <summary>收集本端候选（公网 STUN 映射 + 内网 IP），3 分钟内复用。fire-and-forget 友好。</summary>
    public async Task EnsureCandidatesAsync()
    {
        lock (_gate)
        {
            if (_candidatesBusy) return;
            if (_candidates.Count > 0 && Environment.TickCount64 - _candidatesAt < 180_000) return;
            _candidatesBusy = true;
        }
        var list = new List<string>();
        try
        {
            // 依次问 STUN，直到拿到 2 个结果（第一台给候选，第二台用来识别对称 NAT——
            // 两台看到的映射不同=打洞无望）。个别服务器时好时坏，坏了就换下一台。
            IPEndPoint? first = null;
            int got = 0;
            foreach (var (host, port) in StunClient.Servers)
            {
                if (got >= 2) break;
                var ep = await QueryStunAsync(host, port);
                if (ep == null) continue;
                got++;
                if (first == null) { first = ep; AddCandidate(list, ep); }
                else if (!ep.Equals(first))
                {
                    SymmetricNat = true;
                    AddCandidate(list, ep);
                    _log.Warn($"STUN：两台服务器看到的映射不同（{first} vs {ep}）——疑似对称 NAT，UDP 打洞大概率失败，将回落中继");
                }
            }
            if (first == null)
                _log.Info("STUN 全部不可达（断网/防火墙拦 UDP？）：只剩内网候选，跨网络将走中继");
            foreach (var ip in NetHelper.LocalIPv4())
                AddCandidate(list, new IPEndPoint(IPAddress.Parse(ip), LocalPort));
            lock (_gate)
            {
                _candidates = list;
                _candidatesAt = Environment.TickCount64;
            }
            _log.Info($"UDP 打洞候选（本机端口 {LocalPort}）：{(list.Count > 0 ? string.Join(", ", list) : "无")}");
        }
        finally
        {
            lock (_gate) _candidatesBusy = false;
        }
    }

    private static void AddCandidate(List<string> list, IPEndPoint ep)
    {
        if (ep.Port == 0 || IPAddress.IsLoopback(ep.Address)) return;
        var s = ep.ToString();
        if (!list.Contains(s)) list.Add(s);
    }

    /// <summary>本端 PunchInfo 负载（nonce + 候选 + 对称 NAT 标志 + 能力位），通过中继发给对端</summary>
    public byte[] OfferPayload()
    {
        lock (_gate)
            return JsonSerializer.SerializeToUtf8Bytes(new PunchOffer
            {
                Nonce = Convert.ToBase64String(_sessionNonce),
                Udp = _candidates.ToList(),
                Sym = SymmetricNat,
                Cap = LocalCap,
            });
    }

    /// <summary>解析对端 PunchInfo 负载</summary>
    public static (byte[] nonce, List<string> endpoints, bool symmetric, int cap) DecodeOffer(byte[] payload)
    {
        try
        {
            var offer = JsonSerializer.Deserialize<PunchOffer>(payload) ?? new PunchOffer();
            byte[] nonce;
            try { nonce = Convert.FromBase64String(offer.Nonce); }
            catch { nonce = Array.Empty<byte>(); }
            return (nonce, offer.Udp.Where(s => !string.IsNullOrWhiteSpace(s)).ToList(), offer.Sym, offer.Cap);
        }
        catch { return (Array.Empty<byte>(), new List<string>(), false, 0); }
    }

    // ---------------------------------------------------------------- 打洞控制

    /// <summary>
    /// 收到对端 PunchInfo 后调用：记住对端候选与能力位并开始/续期打洞。
    /// 幂等：打洞期间每次 PunchInfo 重发都会续 20 秒窗口（HELLO 包很小，无妨）。
    /// </summary>
    public void OfferPeers(List<string> endpoints, byte[] theirNonce, int theirCap = 0)
    {
        if (theirNonce.Length == 0) return;
        var targets = new List<IPEndPoint>();
        foreach (var s in endpoints)
        {
            int i = s.LastIndexOf(':');
            if (i <= 0 || !int.TryParse(s[(i + 1)..], out var port)) continue;
            if (!IPAddress.TryParse(s[..i], out var ip)) continue;
            if (port <= 0 || port > 65535) continue;
            var ep = new IPEndPoint(ip, port);
            if (!targets.Contains(ep)) targets.Add(ep);
        }
        KcpSession? staleKcp = null;
        lock (_gate)
        {
            _lastTargets = targets;
            // 会话随机数以被控端为准：主控端从被控端的 PunchInfo 里学到它；被控端始终用自己的
            if (_role == PunchRole.Viewer && theirNonce.Length > 0)
                _lastNonce = theirNonce;
            // 能力协商（AND 语义，双方各自独立算出同样结论；旧端 cap=0 → 全旧路径）
            _peerCap = theirCap;
            _effKcp = (LocalCap & theirCap & CapKcp) != 0;
            _effRs = (LocalCap & theirCap & CapRsFec) != 0;
            _punchAttempts = 0;
            if (!_effKcp && _kcp != null) staleKcp = _kcp;   // 对端降级（理论少见）：跟回去
            else if (_effKcp) EnsureKcpLocked();             // 已连通且能力到位 → 即刻切换
            if (_live) return;                       // 已连通：只更新目标（备自动重打洞用）
            _targets = targets;
            _punchUntil = Environment.TickCount64 + 20_000;
        }
        staleKcp?.Dispose();   // 会话销毁要 Join tick 线程，放锁外
        if (targets.Count > 0)
            _log.Debug($"UDP 打洞目标：{string.Join(", ", targets)}");
    }

    // ---------------------------------------------------------------- 发送

    /// <summary>
    /// 视频帧：不可靠分片 + 丢包恢复（2026-10-11 引入 XOR+NACK；2026-10-14 升级 RS-FEC）：
    ///   · RS 模式（双端协商成功）：全片补齐到 ChunkPayload，按 16 片一组生成 r 个 RS 校验片
    ///     （r 随实测丢包率 1~6 自适应，对标 UU 的 UpdateNetWorkInfo）——组内丢 ≤r 片零重传恢复；
    ///     分片头 msgType 字节复用为 FEC 描述符（0x10+r），旧端收不到 RS 帧（能力协商挡住）；
    ///   · XOR 模式（旧对端兼容）：大帧每 8 片 1 个 XOR 校验片，组内丢 1 片零延迟恢复；
    ///   · 分片存入发送缓存，对端 NACK 缺片时重传（丢片超出 FEC 能力时 +1 RTT 恢复）；
    ///   · 两者都救不回来才放弃整帧（GOP=1s 内有下一个关键帧兜底）。
    /// </summary>
    public bool SendVideo(ProtocolMessage msg)
    {
        IPEndPoint? peer;
        bool effRs;
        lock (_gate) { peer = _live ? _peer : null; effRs = _effRs; }
        if (peer == null) return false;
        ushort seq = (ushort)Interlocked.Increment(ref _videoSeq);
        Interlocked.Increment(ref _videoStarted);

        bool rs = effRs;
        // RS 模式：负载前加 4B 裸长度前缀后分片、全片补齐到 ChunkPayload（末片零填充）。
        // 视频负载本身没有自描述长度（[8B ts][码流字节]），接收端靠这个前缀裁掉补零。
        var chunkSrc = msg.Payload;
        if (rs)
        {
            chunkSrc = new byte[4 + msg.Payload.Length];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(chunkSrc, msg.Payload.Length);
            Buffer.BlockCopy(msg.Payload, 0, chunkSrc, 4, msg.Payload.Length);
        }
        int count = Math.Max(1, (chunkSrc.Length + ChunkPayload - 1) / ChunkPayload);
        var chunks = new byte[count][];
        if (rs)
        {
            for (int i = 0; i < count; i++)
            {
                chunks[i] = new byte[ChunkPayload];
                int off = i * ChunkPayload;
                int len = Math.Min(ChunkPayload, chunkSrc.Length - off);
                if (len > 0) Buffer.BlockCopy(chunkSrc, off, chunks[i], 0, len);
            }
        }
        else
        {
            for (int i = 0; i < count; i++)
            {
                int off = i * ChunkPayload;
                int len = Math.Min(ChunkPayload, chunkSrc.Length - off);
                chunks[i] = new byte[len];
                Buffer.BlockCopy(chunkSrc, off, chunks[i], 0, len);
            }
        }

        // 校验片生成
        int parityCount;
        byte[][]? parity;
        int fecR = 0;   // 0 = XOR/无（旧协议），>0 = RS 且每组 r 个
        if (rs)
        {
            fecR = UpdateFecParity();
            int groups = (count + RsFec.MaxGroupSize - 1) / RsFec.MaxGroupSize;
            parityCount = groups * fecR;
            parity = new byte[parityCount][];
            var group = new byte[RsFec.MaxGroupSize][];
            for (int g = 0; g < groups; g++)
            {
                int first = g * RsFec.MaxGroupSize;
                int t = Math.Min(RsFec.MaxGroupSize, count - first);
                for (int i = 0; i < t; i++) group[i] = chunks[first + i];
                var enc = RsFec.Encode(group[..t], fecR);
                for (int j = 0; j < fecR; j++) parity[g * fecR + j] = enc[j];
            }
        }
        else
        {
            parityCount = ParityCountFor(count);
            parity = null;
            if (parityCount > 0)
            {
                parity = new byte[parityCount][];
                for (int p = 0; p < parityCount; p++)
                {
                    int first = p * ParityGroup;
                    int last = Math.Min(first + ParityGroup, count) - 1;
                    int len = 0;
                    for (int i = first; i <= last; i++) len = Math.Max(len, chunks[i].Length);
                    var acc = new byte[len];
                    for (int i = first; i <= last; i++)
                        for (int j = 0; j < chunks[i].Length; j++) acc[j] ^= chunks[i][j];
                    parity[p] = acc;
                }
            }
        }

        // 发送缓存（NACK 重传查这里；超限丢最旧）
        lock (_cacheGate)
        {
            if (_videoCache.TryGetValue(seq, out var old)) _videoCacheBytes -= old.Chunks.Sum(c => (long)c.Length);
            _videoCache[seq] = new CachedVideoFrame { Chunks = chunks, Parity = parity, FecR = fecR, Tick = Environment.TickCount64 };
            _videoCacheBytes += msg.Payload.Length;
            while (_videoCache.Count > VideoCacheFrames || _videoCacheBytes > 8 * 1024 * 1024)
            {
                var oldest = _videoCache.OrderBy(kv => kv.Value.Tick).FirstOrDefault();
                if (oldest.Value == null) break;
                _videoCacheBytes -= oldest.Value.Chunks.Sum(c => (long)c.Length);
                _videoCache.Remove(oldest.Key);
            }
        }
        Interlocked.Add(ref _fecWinSent, count);

        var head = new byte[8];
        var dropHook = TestDropVideoChunk;
        byte desc = (byte)(rs ? 0x10 + fecR : (byte)msg.Type);
        for (int i = 0; i < count; i++)
        {
            if (dropHook != null && dropHook(seq, (ushort)i))
            {
                Interlocked.Increment(ref _videoDropped);   // 测试模拟的丢片计入残帧统计
                continue;
            }
            head[0] = KindData;
            WriteU16(head, 1, seq);
            WriteU16(head, 3, (ushort)i);
            WriteU16(head, 5, (ushort)count);
            head[7] = desc;
            var dgram = new byte[8 + chunks[i].Length];
            Buffer.BlockCopy(head, 0, dgram, 0, 8);
            Buffer.BlockCopy(chunks[i], 0, dgram, 8, chunks[i].Length);
            TrySend(peer, dgram);
            if (count > 32 && (i & 31) == 31) Thread.Sleep(1);   // 大帧让速
        }
        for (int p = 0; p < parityCount; p++)
        {
            head[0] = KindData;
            WriteU16(head, 1, seq);
            WriteU16(head, 3, (ushort)(count + p));
            WriteU16(head, 5, (ushort)count);
            head[7] = desc;
            var dgram = new byte[8 + parity![p].Length];
            Buffer.BlockCopy(head, 0, dgram, 0, 8);
            Buffer.BlockCopy(parity[p], 0, dgram, 8, parity[p].Length);
            TrySend(peer, dgram);
        }
        Interlocked.Increment(ref _framesSent);
        return true;
    }

    /// <summary>
    /// RS 冗余度自适应（对标 UU DcKcpFecGenerator::UpdateNetWorkInfo(loss_ratio)）：
    /// 每 10s 结一次窗，丢包率 = max(NACK 重传占比, ping 丢包率)，r = RsFec.ParityForLoss。
    /// </summary>
    private int UpdateFecParity()
    {
        long now = Environment.TickCount64;
        if (now - _fecWinAt >= 10_000)
        {
            long sent = Interlocked.Read(ref _fecWinSent);
            long nacked = Interlocked.Read(ref _fecWinNacked);
            double nackFrac = sent > 0 ? Math.Min(1.0, (double)nacked / sent) : 0;
            double pingFrac = PingLossPercent / 100.0;
            double frac = Math.Max(nackFrac, pingFrac);
            // 平滑：与上一窗均值，避免瞬时抖动让 r 大起大落
            _fecLossFrac = _fecLossFrac <= 0 ? frac : (_fecLossFrac + frac) / 2;
            Interlocked.Exchange(ref _fecWinSent, 0);
            Interlocked.Exchange(ref _fecWinNacked, 0);
            _fecWinAt = now;
        }
        _fecR = RsFec.ParityForLoss(_fecLossFrac);
        return _fecR;
    }

    /// <summary>对端 NACK：把缺的视频分片（含校验片）重传一遍</summary>
    private void HandleNack(IPEndPoint peer, byte[] d, int n)
    {
        if (n < 6) return;
        ushort seq = ReadU16(d, 1);
        int cnt = ReadU16(d, 3);
        if (cnt <= 0 || 6 + cnt * 2 > n) return;
        lock (_gate) { if (!_live || peer.Equals(_peer) == false) return; }
        CachedVideoFrame? cached;
        lock (_cacheGate) _videoCache.TryGetValue(seq, out cached);
        if (cached == null)
        {
            _log.Debug($"[nack] 帧 {seq} 已不在缓存（太旧），无法重传");
            return;
        }
        _log.Debug($"[nack] 收到帧 {seq} 的 {cnt} 片重传请求 → 重发");
        Interlocked.Add(ref _fecWinNacked, cnt);   // RS 冗余度自适应的丢包信号
        var peerEp = peer;
        // RS 帧的分片头 msgType 字节是 FEC 描述符（0x10+r），重传必须原样还原，
        // 否则接收端会把 RS 帧误读成旧 XOR 布局
        byte desc = (byte)MessageType.VideoFrame;
        if (cached.FecR > 0) desc = (byte)(0x10 + cached.FecR);
        for (int k = 0; k < cnt; k++)
        {
            ushort idx = ReadU16(d, 6 + k * 2);
            byte[]? body = null;
            if (idx < cached.Chunks.Length) body = cached.Chunks[idx];
            else if (cached.Parity != null && idx < cached.Chunks.Length + cached.Parity.Length)
                body = cached.Parity[idx - cached.Chunks.Length];
            if (body == null) continue;
            var dgram = new byte[8 + body.Length];
            dgram[0] = KindData;
            WriteU16(dgram, 1, seq);
            WriteU16(dgram, 3, idx);
            WriteU16(dgram, 5, (ushort)cached.Chunks.Length);
            dgram[7] = desc;
            Buffer.BlockCopy(body, 0, dgram, 8, body.Length);
            TrySend(peerEp, dgram);
        }
    }

    /// <summary>鼠标移动：单数据报尽力而为（不可靠、不重传——旧位置重传毫无意义）</summary>
    public void SendMove(ProtocolMessage msg)
    {
        IPEndPoint? peer;
        lock (_gate) peer = _live ? _peer : null;
        if (peer == null || msg.Payload.Length > ChunkPayload) return;
        ushort seq = (ushort)Interlocked.Increment(ref _unreliableSeq);
        var dgram = new byte[8 + msg.Payload.Length];
        dgram[0] = KindData;
        WriteU16(dgram, 1, seq);
        WriteU16(dgram, 3, 0);
        WriteU16(dgram, 5, 1);
        dgram[7] = (byte)msg.Type;
        msg.Payload.CopyTo(dgram, 8);
        TrySend(peer, dgram);
    }

    /// <summary>
    /// 可靠消息（按键/点击/控制/剪贴板）。两条路径（2026-10-14）：
    ///  · KCP（双端协商成功，对标 UU dckcp：nc=1 关拥塞窗/5ms tick/快重传 2/minrto 10ms——
    ///    拥塞控制让位给上层码率阶梯；丢 1 包通常一个 RTT 内恢复）。交付确认 = 接收端带外
    ///    KindKcpAck；400ms 没确认仍交 relayFallback 中继补发（接收端按 msgId 去重语义不变）；
    ///  · 旧路径（旧对端）：分片 + 对端 ACK + 60ms 定时重传，250ms 没确认中继补发。
    /// 绝不双击变四击的保证两条路径同源：中继补发是"补"，交付主路径永远单份。
    /// </summary>
    public void Send(ProtocolMessage msg, Action<ProtocolMessage>? relayFallback = null)
    {
        IPEndPoint? peer;
        bool kcp;
        lock (_gate) { peer = _live ? _peer : null; kcp = _live && _effKcp && _kcp != null; }
        if (peer == null || msg.Payload.Length > MaxReliableBytes)
        {
            relayFallback?.Invoke(msg);
            return;
        }
        if (kcp)
        {
            uint id = (uint)Interlocked.Increment(ref _kcpMsgId);
            var p = new KcpPending
            {
                Id = id,
                FirstSend = Environment.TickCount64,
                Fallback = relayFallback,
                Msg = msg,
            };
            lock (_kcpPendingGate)
            {
                if (_kcpPending.Count > 64)
                {
                    _log.Warn("KCP 发送跟踪表过深，最旧一条放弃等待（应已走中继补发）");
                    var oldest = _kcpPending.Values.OrderBy(x => x.FirstSend).First();
                    _kcpPending.Remove(oldest.Id);
                }
                _kcpPending[id] = p;
            }
            if (!_kcp!.Send(id, msg.ToFrame()))
            {
                // KCP 发送队列积压（对端长时间不收）：立刻中继补发，不等 400ms
                lock (_kcpPendingGate) _kcpPending.Remove(id);
                relayFallback?.Invoke(msg);
            }
            return;
        }
        var pending = new Pending
        {
            Seq = (ushort)Interlocked.Increment(ref _reliableSeq),
            MsgType = (byte)msg.Type,
            Payload = msg.Payload,
            Fallback = relayFallback,
            Msg = msg,
            FirstSend = Environment.TickCount64,
            LastSend = Environment.TickCount64,
        };
        lock (_pendingGate)
        {
            if (_pending.Count > 64)
            {
                _log.Warn("可靠发送队列过深，最旧一条放弃等待（应已走中继补发）");
                _pending.RemoveAt(0);
            }
            _pending.Add(pending);
        }
        SendChunked(peer, pending.Seq, pending.MsgType, pending.Payload, reliable: true);
    }

    private void SendChunked(IPEndPoint peer, ushort seq, byte msgType, byte[] payload, bool reliable)
    {
        int count = Math.Max(1, (payload.Length + ChunkPayload - 1) / ChunkPayload);
        if (count > ushort.MaxValue) return;
        var head = new byte[8];
        for (int i = 0; i < count; i++)
        {
            int off = i * ChunkPayload;
            int len = Math.Min(ChunkPayload, payload.Length - off);
            head[0] = KindData;
            WriteU16(head, 1, seq);
            WriteU16(head, 3, (ushort)i);
            WriteU16(head, 5, (ushort)count);
            head[7] = msgType;
            var dgram = new byte[8 + len];
            Buffer.BlockCopy(head, 0, dgram, 0, 8);
            Buffer.BlockCopy(payload, off, dgram, 8, len);
            TrySend(peer, dgram);
            // 不可靠大帧（视频关键帧 ~200KB）让速：一口气几百个包会瞬间打爆对端接收循环
            if (!reliable && count > 32 && (i & 31) == 31) Thread.Sleep(1);
        }
    }

    private void TrySend(IPEndPoint peer, byte[] dgram)
    {
        try
        {
            _udp.SendTo(dgram, SocketFlags.None, peer);
            Interlocked.Add(ref _bytesSent, dgram.Length);
        }
        catch (Exception ex)
        {
            // 发送缓冲满（ICMP 不可达风暴/对端慢）：丢这一片。可靠消息靠重传，视频靠关键帧
            if (_sendErrLog == 0 || Environment.TickCount64 - _sendErrLog > 5000)
            {
                _sendErrLog = Environment.TickCount64;
                _log.Debug($"UDP 发送失败（{ex.Message}）");
            }
        }
    }

    private static void WriteU16(byte[] b, int at, ushort v) { b[at] = (byte)(v & 0xFF); b[at + 1] = (byte)(v >> 8); }
    private static ushort ReadU16(byte[] b, int at) => (ushort)(b[at] | (b[at + 1] << 8));

    // ---------------------------------------------------------------- 接收

    private void ReceiveLoop()
    {
        var buf = new byte[2048];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                int n;
                try { n = _udp.ReceiveFrom(buf, SocketFlags.None, ref remote); }
                catch (SocketException) { continue; }
                catch (ObjectDisposedException) { break; }
                if (n <= 0) continue;
                Interlocked.Add(ref _bytesRecv, n);
                HandleDatagram(buf, n, (IPEndPoint)remote);
            }
        }
        catch (Exception ex)
        {
            _log.Debug($"UDP 接收循环结束：{ex.Message}");
        }
    }

    private void HandleDatagram(byte[] d, int n, IPEndPoint from)
    {
        // STUN Binding Response（魔数在 4..7）：转交给在等的查询——它们和打洞数据共用这个 socket
        var w = _stunWait;
        if (n >= 20 && d[1] == 0x01 && d[4] == 0x21 && d[5] == 0x12 && d[6] == 0xA4 && d[7] == 0x42)
        {
            if (w != null)
            {
                var ep = StunClient.Parse(d, n, w.Tid);
                if (ep != null) w.Tcs.TrySetResult(ep);
            }
            return;
        }

        switch (d[0])
        {
            case KindHello:
                if (n < 34) return;
                byte role = d[1];
                if (role == (byte)_role || role is not ((byte)PunchRole.Agent or (byte)PunchRole.Viewer)) return;
                if (!VerifyHello(d, n)) return;
                OnPeerHeard(from);
                // 立即回一个 HELLO：对端可能刚进打洞窗口，等下一轮 350ms 太慢
                TrySend(from, BuildHello());
                return;

            case KindPing:
                if (n < 9 || !IsPeer(from)) return;
                OnPeerHeard(from);
                var pong = new byte[9];
                pong[0] = KindPong;
                Array.Copy(d, 1, pong, 1, 8);
                TrySend(from, pong);
                return;

            case KindPong:
                if (n < 9 || !IsPeer(from)) return;
                OnPeerHeard(from);
                Interlocked.Increment(ref _pongRecv);
                long tick = BitConverter.ToInt64(d, 1);
                long rtt = Environment.TickCount64 - tick;
                if (rtt is >= 0 and < 10_000) Interlocked.Exchange(ref _rttMs, rtt);
                return;

            case KindAck:
                if (n < 3 || !IsPeer(from)) return;
                OnPeerHeard(from);
                ushort aseq = ReadU16(d, 1);
                lock (_pendingGate)
                    _pending.RemoveAll(p => p.Seq == aseq);
                return;

            case KindNack:
                if (n < 5 || !IsPeer(from)) return;
                OnPeerHeard(from);
                HandleNack(from, d, n);
                return;

            case KindKcp:
                if (n < 2 || !IsPeer(from)) return;
                OnPeerHeard(from);
                _kcp?.Input(new ReadOnlySpan<byte>(d, 1, n - 1));
                return;

            case KindKcpAck:
                if (n < 5 || !IsPeer(from)) return;
                OnPeerHeard(from);
                uint kid = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(1));
                lock (_kcpPendingGate) _kcpPending.Remove(kid);
                return;

            case KindData:
                if (n < 9 || !IsPeer(from)) return;
                OnPeerHeard(from);
                HandleDataChunk(d, n);
                return;
        }
    }

    /// <summary>这个来源是不是（或可以成为）我们的对端：live 后只认 _peer，
    /// 防止第三方往我们的公网映射上灌包干扰数据流。live 前对地址宽容（打洞期来源未知）。</summary>
    private bool IsPeer(IPEndPoint from)
    {
        lock (_gate)
        {
            if (_live) return from.Equals(_peer);
            return true;   // 打洞期：任何通过 HMAC 的来源都可能就是对端（见 OnPeerHeard）
        }
    }

    /// <summary>收到对端合法包：刷新活性、首次则转 Live（以观察到的来源为对端地址）</summary>
    private void OnPeerHeard(IPEndPoint from)
    {
        bool becameLive = false;
        lock (_gate)
        {
            _lastRxTick = Environment.TickCount64;
            if (!_live)
            {
                _live = true;
                _peer = from;
                _punchUntil = 0;
                becameLive = true;
            }
            if (becameLive && _effKcp) EnsureKcpLocked();   // 能力已协商且刚连通 → 立即挂 KCP 会话
        }
        if (becameLive)
        {
            _log.Info($"UDP 打洞成功：与对端 {from} 直连已建立（RTT≈{RttMs}ms，视频/输入改走直连，中继保底）");
            try { Live?.Invoke(); } catch { }
        }
    }

    // ---------------------------------------------------------------- KCP 会话管理

    /// <summary>创建 KCP 会话（调用方持 _gate）。conv 从会话 nonce 前 4 字节派生——双方同源无需协商。</summary>
    private void EnsureKcpLocked()
    {
        if (_kcp != null || _lastNonce.Length < 4) return;
        var conv = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(_lastNonce.AsSpan(0, 4));
        var session = KcpSession.Create(conv, SendKcpSegment);
        session.FrameReceived += OnKcpFrame;
        _kcp = session;
        _log.Info($"UDP 可靠路径切换为 KCP（conv=0x{conv:X}，UU 参数：nc=1 关拥塞窗 / wnd 256 / 5ms / 快重传 2 / minrto 10ms）");
    }

    /// <summary>KCP 输出回调：段前面加 1 字节 KindKcp 标签从本 socket 发出（与其它 kind 多路复用）</summary>
    private void SendKcpSegment(ReadOnlySpan<byte> segment)
    {
        IPEndPoint? peer;
        lock (_gate) peer = _live && _effKcp ? _peer : null;
        if (peer == null) return;
        var dgram = new byte[1 + segment.Length];
        dgram[0] = KindKcp;
        segment.CopyTo(dgram.AsSpan(1));
        TrySend(peer, dgram);
    }

    /// <summary>KCP 流上收到完整消息：回带外 ACK（发送端停中继补发计时）+ 交付</summary>
    private void OnKcpFrame(uint msgId, byte[] frame)
    {
        IPEndPoint? peer;
        lock (_gate) peer = _live ? _peer : null;
        if (peer != null)
        {
            var ack = new byte[5];
            ack[0] = KindKcpAck;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(ack.AsSpan(1), msgId);
            TrySend(peer, ack);
        }
        // frame = [1B type][4B len][payload]（ProtocolMessage 帧格式）
        if (frame.Length < 5) return;
        int len = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(frame.AsSpan(1));
        if (len < 0 || 5 + len > frame.Length) return;
        var payload = new byte[len];
        Buffer.BlockCopy(frame, 5, payload, 0, len);
        Deliver(new ProtocolMessage((MessageType)frame[0], payload));
    }

    /// <summary>HELLO 的 HMAC 校验：密钥 = 中继令牌派生，内容 = 角色 + 会话随机数。
    /// 会话随机数经中继私下交换，第三方看不到，伪造不出合法 HELLO。</summary>
    private bool VerifyHello(byte[] d, int n)
    {
        byte[] nonce;
        lock (_gate) nonce = _lastNonce;
        if (nonce.Length == 0) return false;
        var content = new byte[1 + nonce.Length];
        content[0] = d[1];
        Buffer.BlockCopy(nonce, 0, content, 1, nonce.Length);
        var expect = HMACSHA256.HashData(_key, content);
        for (int i = 0; i < 32; i++)
            if (d[2 + i] != expect[i]) return false;
        return true;
    }

    private byte[] BuildHello()
    {
        byte[] nonce;
        lock (_gate) nonce = _lastNonce.Length > 0 ? _lastNonce : _sessionNonce;
        var content = new byte[1 + nonce.Length];
        content[0] = (byte)_role;
        Buffer.BlockCopy(nonce, 0, content, 1, nonce.Length);
        var d = new byte[2 + 32];
        d[0] = KindHello;
        d[1] = (byte)_role;
        Array.Copy(HMACSHA256.HashData(_key, content), 0, d, 2, 32);
        return d;
    }

    // ---------------------------------------------------------------- 数据分片重组

    /// <summary>帧字典锁：接收线程重组 + 控制线程的残帧 NACK 扫描共用（2026-10-11）</summary>
    private readonly object _framesGate = new();

    private void HandleDataChunk(byte[] d, int n)
    {
        lock (_framesGate)
        {
        HandleDataChunkCore(d, n);
        }
    }

    private void HandleDataChunkCore(byte[] d, int n)
    {
        ushort seq = ReadU16(d, 1);
        ushort idx = ReadU16(d, 3);
        ushort count = ReadU16(d, 5);
        byte b7 = d[7];
        // FEC 描述符（2026-10-14）：视频帧的 msgType 字节在 RS 模式下复用为 0x10+r；
        // 旧对端永远发 VideoFrame(0x01) 或普通消息类型，两个值域不相交
        bool rsDesc = b7 is >= 0x10 and <= 0x16;
        int fecR = rsDesc ? b7 - 0x10 : 0;
        var isVideo = b7 == (byte)MessageType.VideoFrame || rsDesc;
        var msgType = isVideo ? (byte)MessageType.VideoFrame : b7;
        int body = 8, bodyLen = n - 8;
        // 校验片（idx >= count）只对视频大帧存在；其它类型按原校验拒绝。
        // RS 布局：组数 × r；XOR 布局：ParityCountFor(count)
        int parityTotal = !isVideo ? 0
            : fecR > 0 ? ((count + RsFec.MaxGroupSize - 1) / RsFec.MaxGroupSize) * fecR
            : ParityCountFor(count);
        if (count == 0 || idx >= count + parityTotal || bodyLen <= 0) return;

        if (!_frames.TryGetValue(seq, out var f))
        {
            // 残帧回收：字典里积压超过 16 帧或单帧超龄还没收齐 → 放弃
            if (_frames.Count >= 16) EvictStaleFrames();
            var parity = isVideo && parityTotal > 0 ? new byte[parityTotal][] : null;
            f = new Frame
            {
                MsgType = msgType,
                Chunks = new byte[count][],
                Born = Environment.TickCount64,
                Count = count,
                Parity = parity,
                FecR = fecR,
                NackCounts = isVideo ? new byte[count] : null,
            };
            _frames[seq] = f;
        }
        f.LastChunkTick = Environment.TickCount64;

        var piece = new byte[bodyLen];
        Buffer.BlockCopy(d, body, piece, 0, bodyLen);

        if (idx >= f.Count)
        {
            // 校验片：单独存放（不占数据分片位）
            int p = idx - f.Count;
            if (f.Parity != null && p < f.Parity.Length && f.Parity[p] == null)
            {
                f.Parity[p] = piece;
                f.ParityGot++;
            }
        }
        else if (f.Chunks[idx] == null)
        {
            f.Chunks[idx] = piece;
            f.Got++;
        }
        else
        {
            return;   // 重复分片（重传撞车）
        }

        if (isVideo)
        {
            if (f.Got < f.Count && f.ParityGot > 0)
            {
                if (f.FecR > 0)
                {
                    // RS 恢复：每组丢 ≤r 片零延迟补齐（丢得更多交给 NACK）
                    if (TryRsRecover(f))
                        _log.Debug($"视频帧 {seq} 用 RS-FEC 恢复 {f.FecR} 冗余内的丢失分片（免重传）");
                }
                else if (f.Got == f.Count - 1 && TryParityRecover(f))
                {
                    _log.Debug($"视频帧 {seq} 用 XOR 校验恢复 1 个丢失分片（免重传）");
                }
            }

            if (f.Got < f.Count)
            {
                // 停收 80ms 还缺片 = 真丢了（不是乱序）：NACK 请求重传，等不到再放弃
                if (Environment.TickCount64 - f.LastChunkTick >= 0 && StallTooLong(f))
                    SendNack(seq, f);
                return;
            }

            _frames.Remove(seq);
            var payload = Assemble(f);
            if (payload == null) return;
            if (f.FecR > 0)
            {
                // RS 模式全片补齐过：按帧头声明的消息长度裁掉末片零填充
                payload = TrimToDeclaredLength(payload);
                if (payload == null) return;
            }
            Interlocked.Increment(ref _videoCompleted);
            Deliver(new ProtocolMessage((MessageType)msgType, payload));
            return;
        }

        // 可靠消息：先 ACK（哪怕重复/乱序也回，发送端靠它停重传）
        lock (_gate)
        {
            if (_peer != null)
            {
                var ack = new byte[3];
                ack[0] = KindAck;
                WriteU16(ack, 1, seq);
                TrySend(_peer, ack);
            }
        }

        // 去重（重传 + 中继补发会撞车）
        lock (_dupGuard)
        {
            if (_dupGuard.ContainsKey(seq)) return;
            _dupGuard[seq] = Environment.TickCount64;
            if (_dupGuard.Count > 512)
                foreach (var k in _dupGuard.Where(kv => Environment.TickCount64 - kv.Value > 60_000).Select(kv => kv.Key).ToList())
                    _dupGuard.Remove(k);
        }

        // 鼠标移动这类不可靠小消息：立即交付（顺序无所谓，最新位置说了算）
        if (IsMoveLike(msgType))
        {
            Deliver(new ProtocolMessage((MessageType)msgType, Assemble(f) ?? Array.Empty<byte>()));
            _frames.Remove(seq);
            return;
        }
        var assembled = Assemble(f);
        if (assembled == null) { _frames.Remove(seq); return; }
        var rmsg = new ProtocolMessage((MessageType)msgType, assembled);

        // 其余可靠消息按序交付：按下→抬起乱序会出现"按键卡住"
        if (_recvExpected < 0) _recvExpected = seq;
        if (seq == (ushort)_recvExpected)
        {
            Deliver(rmsg);
            _recvExpected = (_recvExpected + 1) & 0xFFFF;
            // 把已经到齐的后续消息按序倒出来
            while (_hold.Remove((ushort)_recvExpected, out var held))
            {
                Deliver(held);
                _recvExpected = (_recvExpected + 1) & 0xFFFF;
            }
            _holdSince = 0;
            _frames.Remove(seq);
        }
        else
        {
            if (_hold.Count == 0) _holdSince = Environment.TickCount64;
            _hold[seq] = rmsg;
            _frames.Remove(seq);
            // 前面缺的消息 300ms 还没到：跳过它（对面会走中继补发兜底，这里不无限等）
            if (_hold.Count > 0 && _holdSince > 0 && Environment.TickCount64 - _holdSince > 300)
            {
                _recvExpected = (_recvExpected + 1) & 0xFFFF;
                _holdSince = Environment.TickCount64;
            }
            if (_hold.Count > 16) _hold.Clear();    // 保险丝
        }
    }

    private static bool IsMoveLike(byte msgType) =>
        msgType == (byte)MessageType.MouseMove || msgType == (byte)MessageType.InputModeHint;

    /// <summary>帧停收多久算"真缺片"（NACK 触发窗口）</summary>
    private static bool StallTooLong(Frame f)
    {
        // 最后分片到达后 80ms 还没齐 —— 乱序在本地回环/局域网早就到了，这个窗口只在内网/直连下有意义；
        // 中继场景视频不走 UDP（走中继 TCP），不受影响。
        return Environment.TickCount64 - f.LastChunkTick >= NackGraceMs || Environment.TickCount64 - f.Born >= 200;
    }

    /// <summary>XOR 校验恢复：恰好缺 1 个数据片且其组可解时就地补齐</summary>
    private bool TryParityRecover(Frame f)
    {
        if (f.Parity == null) return false;
        int missing = -1;
        for (int i = 0; i < f.Count; i++)
        {
            if (f.Chunks[i] != null) continue;
            if (missing >= 0) return false;   // 缺不止一个：交给 NACK
            missing = i;
        }
        if (missing < 0) return false;
        int g = missing / ParityGroup;
        if (g >= f.Parity.Length || f.Parity[g] == null) return false;
        int first = g * ParityGroup, last = Math.Min(first + ParityGroup, f.Count) - 1;
        var parity = f.Parity[g];
        var result = (byte[])parity.Clone();
        for (int i = first; i <= last; i++)
        {
            if (i == missing || f.Chunks[i] == null) continue;
            for (int j = 0; j < f.Chunks[i].Length && j < result.Length; j++)
                result[j] ^= f.Chunks[i][j];
        }
        // 组内其余片必须全在（上面循环遇到 null 会 continue —— 这里显式核验）
        for (int i = first; i <= last; i++)
            if (i != missing && f.Chunks[i] == null) return false;
        f.Chunks[missing] = result;
        f.Got++;
        return true;
    }

    /// <summary>RS-FEC 恢复：逐组检查，组内丢片 ≤r 且存活分片够 t 个 → 矩阵解码补齐（零重传）</summary>
    private bool TryRsRecover(Frame f)
    {
        if (f.Parity == null || f.FecR <= 0) return false;
        int r = f.FecR;
        int groups = (f.Count + RsFec.MaxGroupSize - 1) / RsFec.MaxGroupSize;
        bool any = false;
        for (int g = 0; g < groups; g++)
        {
            int first = g * RsFec.MaxGroupSize;
            int t = Math.Min(RsFec.MaxGroupSize, f.Count - first);
            int missing = 0;
            for (int i = 0; i < t; i++)
                if (f.Chunks[first + i] == null) missing++;
            if (missing == 0) continue;
            // 该组的校验片到位数（wire 布局：parity[g*r .. g*r+r)）
            int parityHave = 0;
            for (int j = 0; j < r; j++)
                if (f.Parity[g * r + j] != null) parityHave++;
            if (t - missing + parityHave < t) continue;   // 存活不足 r+missing 中的下限：等 NACK
            var shards = new byte[t + r][];
            for (int i = 0; i < t; i++) shards[i] = f.Chunks[first + i];
            for (int j = 0; j < r; j++) shards[t + j] = f.Parity[g * r + j];
            var recovered = new byte[t][];
            if (!RsFec.TryDecode(shards, r, recovered)) continue;
            for (int i = 0; i < t; i++)
            {
                if (f.Chunks[first + i] == null && recovered[i] != null)
                {
                    f.Chunks[first + i] = recovered[i];
                    f.Got++;
                    any = true;
                    Interlocked.Increment(ref _fecRecoveredChunks);
                }
            }
        }
        return any;
    }

    /// <summary>RS 模式末片补过零：按发送端 4B 裸长度前缀裁剪并剥掉前缀，长度对不上判损坏丢帧</summary>
    private static byte[]? TrimToDeclaredLength(byte[] payload)
    {
        if (payload.Length < 4) return null;
        int len = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(0, 4));
        if (len < 0 || 4 + len > payload.Length) return null;
        var trimmed = new byte[len];
        Buffer.BlockCopy(payload, 4, trimmed, 0, len);
        return trimmed;
    }

    /// <summary>把缺片清单发给对端请求重传（每片限次防风暴：XOR 2 次 / RS 4 次）</summary>
    private void SendNack(ushort seq, Frame f)
    {
        IPEndPoint? peer;
        lock (_gate) peer = _live ? _peer : null;
        if (peer == null || f.NackCounts == null) return;
        int limit = f.FecR > 0 ? MaxNackPerChunkRs : MaxNackPerChunk;
        var missing = new List<ushort>(8);
        for (int i = 0; i < f.Count && missing.Count < 64; i++)
        {
            if (f.Chunks[i] == null && f.NackCounts[i] < limit)
            {
                missing.Add((ushort)i);
                f.NackCounts[i]++;
            }
        }
        if (missing.Count == 0) return;
        // 布局：[0]=kind [1..2]=seq [3..4]=missing 数 [5]=保留 [6..]=idx 列表
        // （[5] 保留位：以前 idx 从 4 开始会把 count 的高字节覆盖掉，agent 解出 1282 直接丢弃——实测踩到）
        var dgram = new byte[6 + missing.Count * 2];
        dgram[0] = KindNack;
        WriteU16(dgram, 1, seq);
        WriteU16(dgram, 3, (ushort)missing.Count);
        for (int k = 0; k < missing.Count; k++) WriteU16(dgram, 6 + k * 2, missing[k]);
        TrySend(peer, dgram);
        _log.Debug($"[nack] 请求重传帧 {seq} 的 {missing.Count} 片（缺 {string.Join(",", missing.Take(8))}）");
    }

    private static byte[]? Assemble(Frame f)
    {
        int total = 0;
        foreach (var c in f.Chunks) total += c?.Length ?? 0;
        var payload = new byte[total];
        int at = 0;
        foreach (var c in f.Chunks)
        {
            if (c == null) return null;
            Buffer.BlockCopy(c, 0, payload, at, c.Length);
            at += c.Length;
        }
        return payload;
    }

    private void EvictStaleFrames()
    {
        var now = Environment.TickCount64;
        foreach (var kv in _frames.Where(kv => now - kv.Value.Born > FrameStaleMs).ToList())
            _frames.Remove(kv.Key);
        if (_frames.Count >= 16)
        {
            foreach (var kv in _frames.OrderBy(kv => kv.Value.Born).Take(_frames.Count - 8).ToList())
                _frames.Remove(kv.Key);
        }
        Interlocked.Add(ref _videoDropped, 1);   // 逐出的大概率是残视频帧
    }

    private void Deliver(ProtocolMessage msg)
    {
        try { MessageReceived?.Invoke(msg); }
        catch (Exception ex) { _log.Debug($"UDP 消息处理异常（{msg.Type}）：{ex.Message}"); }
    }

    // ---------------------------------------------------------------- 控制循环（打洞/保活/重传/判死）

    private void ControlLoop()
    {
        long lastPunch = 0, lastPing = 0;
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                Thread.Sleep(25);
                long now = Environment.TickCount64;

                List<IPEndPoint> targets;
                bool live;
                IPEndPoint? peer;
                lock (_gate)
                {
                    live = _live;
                    peer = _peer;
                    targets = _targets;
                }

                if (live && peer != null)
                {
                    // 残缺视频帧的 NACK 扫描（2026-10-11）：接收线程只在"收到新分片"时检查，
                    // 而丢的往往是最后几片 —— 没有下一个包来触发检查，NACK 必须靠定时驱动。
                    if (now - _lastNackScanTick > 50)
                    {
                        _lastNackScanTick = now;
                        lock (_framesGate)
                        {
                            foreach (var kv in _frames)
                            {
                                var fr = kv.Value;
                                if (fr.Got < fr.Count && fr.MsgType == (byte)MessageType.VideoFrame && StallTooLong(fr))
                                    SendNack(kv.Key, fr);
                            }
                        }
                    }

                    // 判死：3.5s 没有任何对端包（NAT 映射过期/网络切换）
                    if (now - Interlocked.Read(ref _lastRxTick) > 3500)
                    {
                        lock (_gate)
                        {
                            _live = false;
                            _peer = null;
                            _lastRxTick = now;
                            _punchAttempts++;
                            _retryAt = now + 5000;
                        }
                        lock (_pendingGate) _pending.Clear();
                        lock (_kcpPendingGate) _kcpPending.Clear();
                        TeardownKcp();   // 判死即拆会话：重打洞成功后按（可能更新的）nonce 重建
                        ResetPingStats();
                        _log.Warn($"UDP 直连判死（3.5 秒无对端流量）：视频/输入回落中继，5 秒后自动重打洞（第 {_punchAttempts} 次）");
                        try { Dead?.Invoke(); } catch { }
                    }
                    else
                    {
                        // KCP 可靠消息的补发计时：没等到带外 ACK 的，400ms 后交中继补发一份
                        // （KCP 自己会送达，这里是中继兜底；补发时机放宽到 400ms——快重传
                        //  正常工作时 ACK 远早于这个点，避免频繁双路径）
                        if (_kcpPending.Count > 0)
                        {
                            List<KcpPending> snapshotKcp;
                            lock (_kcpPendingGate) snapshotKcp = _kcpPending.Values.ToList();
                            foreach (var kp in snapshotKcp)
                            {
                                if (!kp.FallbackSent && now - kp.FirstSend >= KcpFallbackMs)
                                {
                                    kp.FallbackSent = true;
                                    try { kp.Fallback?.Invoke(kp.Msg); } catch { }
                                }
                            }
                            lock (_kcpPendingGate)
                            {
                                foreach (var stale in _kcpPending.Values
                                             .Where(x => now - x.FirstSend > ReliableGiveUpMs + 500).Select(x => x.Id).ToList())
                                    _kcpPending.Remove(stale);
                            }
                        }

                        // ping 丢包滑窗轮转（10s 一个窗口，样本 ≥5 才出数）
                        if (now - Interlocked.Read(ref _pingWindowStart) >= 10_000)
                        {
                            long sent = Interlocked.Read(ref _pingSent);
                            long recv = Interlocked.Read(ref _pongRecv);
                            if (sent >= 5)
                                Interlocked.Exchange(ref _pingLossPct, Math.Min(100, (sent - recv) * 100 / sent));
                            Interlocked.Exchange(ref _pingSent, 0);
                            Interlocked.Exchange(ref _pongRecv, 0);
                            Interlocked.Exchange(ref _pingWindowStart, now);
                        }
                        if (now - lastPing > 1000)
                        {
                            lastPing = now;
                            Interlocked.Increment(ref _pingSent);
                            var ping = new byte[9];
                            ping[0] = KindPing;
                            Buffer.BlockCopy(BitConverter.GetBytes(now), 0, ping, 1, 8);
                            TrySend(peer, ping);
                        }
                    }
                }

                // 自动重打洞（死链冷却到点，目标还在）
                if (!live)
                {
                    bool due = false;
                    List<IPEndPoint> retryTargets = new();
                    lock (_gate)
                    {
                        if (_punchAttempts > 0 && _punchAttempts <= 3 && now >= _retryAt && _lastTargets.Count > 0)
                        {
                            due = true;
                            retryTargets = _lastTargets;
                            _retryAt = now + 20_000;
                            _targets = _lastTargets;
                            _punchUntil = now + 20_000;
                        }
                    }
                    if (due)
                    {
                        _log.Info($"自动重打洞（目标 {retryTargets.Count} 个）");
                        lastPunch = 0;   // 立即发一轮 HELLO
                    }
                }

                // 打洞窗口：每 350ms 向所有候选发 HELLO
                long punchUntil;
                lock (_gate) punchUntil = _punchUntil;
                if (!live && now < punchUntil && targets.Count > 0 && now - lastPunch > 350)
                {
                    lastPunch = now;
                    var hello = BuildHello();
                    foreach (var t in targets) TrySend(t, hello);
                }

                // 可靠消息重传/回落/放弃
                if (live)
                {
                    List<Pending> snapshot;
                    lock (_pendingGate) snapshot = _pending.ToList();
                    foreach (var p in snapshot)
                    {
                        if (now - p.LastSend >= 60 && now - p.FirstSend <= ReliableGiveUpMs && peer != null)
                        {
                            p.LastSend = now;
                            SendChunked(peer, p.Seq, p.MsgType, p.Payload, reliable: true);
                        }
                        if (!p.FallbackSent && now - p.FirstSend >= ReliableFallbackMs)
                        {
                            p.FallbackSent = true;
                            try { p.Fallback?.Invoke(p.Msg); } catch { }
                        }
                    }
                    lock (_pendingGate)
                        _pending.RemoveAll(p => now - p.FirstSend > ReliableGiveUpMs + 500);
                }
            }
            catch (Exception ex)
            {
                if (_cts.IsCancellationRequested) break;
                _log.Debug($"UDP 控制循环异常：{ex.Message}");
                Thread.Sleep(200);
            }
        }
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _udp.Close(); } catch { }
        TeardownKcp();
        lock (_gate)
        {
            _live = false;
            _peer = null;
        }
    }

    /// <summary>拆掉 KCP 会话（判死/降级/Dispose 时；锁外 Dispose——要 Join tick 线程）</summary>
    private void TeardownKcp()
    {
        KcpSession? old;
        lock (_gate) { old = _kcp; _kcp = null; }
        if (old == null) return;
        try { old.FrameReceived -= OnKcpFrame; } catch { }
        old.Dispose();
    }

    /// <summary>ping 丢包统计复位（判死/重建时调用：旧窗口的丢包不再代表新链路）</summary>
    private void ResetPingStats()
    {
        Interlocked.Exchange(ref _pingSent, 0);
        Interlocked.Exchange(ref _pongRecv, 0);
        Interlocked.Exchange(ref _pingLossPct, 0);
        Interlocked.Exchange(ref _pingWindowStart, Environment.TickCount64);
    }
}
