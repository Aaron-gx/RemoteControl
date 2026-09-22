using System.Text.Json;

namespace Agent.Common;

/// <summary>
/// 被控端本地授权闸门 —— 防"屏蔽中继走 P2P 白嫖"。
///
/// 为什么需要它：中继服务器上的时间限制只拦得住"经过服务器的流量"。P2P 直连（L1）一旦建立，
/// 视频就不再经过服务器，如果只靠服务器判定授权，攻击者只要**屏蔽中继域名/IP**就能
/// 让时间限制失效。所以在被控端本地再判一次，而且判据来自**已验签的激活码**：
///
/// 1. 服务器下发的激活码在本机用同一把厂商公钥再验一次（中继被改也拦得住），验过就缓存到磁盘；
/// 2. 缓存里的到期时间到了 → 本地直接停机（不依赖任何网络）；
/// 3. 系统时间被往回拨（超过容差）→ 视为异常停机（防止"改本机时间续命"）；
/// 4. 停机动作是"硬"的：停直连服务、清掉已接入的直连主控端、停 Worker、拒绝新的直连握手。
///
/// 说明：这里只做**减法**（能不能继续用），不做加法；重新联网拿到新激活码后自动恢复。
/// </summary>
public sealed class LicenseGate
{
    /// <summary>时钟回拨容差：小于这个幅度当作正常对时/NTP 抖动</summary>
    private static readonly TimeSpan RollbackTolerance = TimeSpan.FromHours(6);

    private readonly Logger _log;
    private readonly string _cachePath;
    private readonly Func<string, LicenseCodec.VerifyResult> _verify;
    private readonly TimeSpan _maxOffline;      // 离线宽限；Zero = 不启用
    private readonly object _lock = new();

    private string _code = "";
    private LicensePayload? _payload;
    private DateTime _lastSeenUtc = DateTime.MinValue;
    private DateTime _contactUtc = DateTime.MinValue;    // 最后一次成功联系上服务器
    private DateTime _firstRunUtc = DateTime.MinValue;   // 首次运行（没联系过服务器时的计时起点）
    private bool _locked;
    private string _lockReason = "";

    /// <summary>授权不可用（停机）时触发，参数为原因</summary>
    public event Action<string>? LockedOut;

    public bool Locked { get { lock (_lock) return _locked; } }
    public string LockReason { get { lock (_lock) return _lockReason; } }
    public LicensePayload? Payload { get { lock (_lock) return _payload; } }
    public string Code { get { lock (_lock) return _code; } }

    /// <param name="cachePath">本地授权缓存路径</param>
    /// <param name="log">日志</param>
    /// <param name="verifier">验签函数（默认内置厂商公钥；单测可注入测试密钥）</param>
    /// <param name="maxOffline">离线宽限：多久联系不上服务器就停机（TimeSpan.Zero = 不启用）</param>
    public LicenseGate(string cachePath, Logger log, Func<string, LicenseCodec.VerifyResult>? verifier = null,
        TimeSpan maxOffline = default)
    {
        _cachePath = cachePath;
        _log = log;
        _verify = verifier ?? (code => LicenseCodec.Verify(code, DateTime.UtcNow));
        _maxOffline = maxOffline;
        LoadFromCache();
    }

    /// <summary>当前是否允许提供远程控制服务（含 P2P 直连）</summary>
    public bool IsUsable
    {
        get { lock (_lock) return !_locked; }
    }

    /// <summary>服务器下发的激活码（每次下发都会刷新缓存与"见过的最大时间"）</summary>
    public void UpdateFromServer(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return;
        var v = _verify(code);
        if (!v.Ok || v.Payload == null)
        {
            Lock($"服务器下发的激活码本机复验不通过：{v.Error}", code);
            return;
        }
        lock (_lock)
        {
            _code = code.Trim();
            _payload = v.Payload;
            _lastSeenUtc = DateTime.UtcNow;
            _locked = false;
            _lockReason = "";
        }
        Save();
        _log.Info($"授权已缓存到本地（{v.Payload.Lic}，至 {v.Payload.ExpiresAt():yyyy-MM-dd}，剩余 {v.Payload.DaysLeft():F0} 天）" +
                  "——即使之后断网，本地也能独立判定到期");
    }

    /// <summary>服务器明确拒绝授权（过期/无效/绑定不符）时调用</summary>
    public void LockFromServer(string reason) => Lock($"服务器判定授权不可用：{reason}", null);

    /// <summary>定时调用（每 60 秒）：离线宽限 + 本地判到期 + 时钟回拨。</summary>
    public void Tick()
    {
        LicensePayload? payload;
        string code;
        DateTime lastSeen;
        DateTime contact;
        DateTime firstRun;
        lock (_lock)
        {
            payload = _payload;
            code = _code;
            lastSeen = _lastSeenUtc;
            contact = _contactUtc;
            firstRun = _firstRunUtc;
        }
        var now = DateTime.UtcNow;

        // 离线宽限放在最前面（连"从没拿到过激活码"的机器也要管）：
        // 太久联系不上服务器就停机 —— 撤销/续期必须在一个有界时间内生效，
        // 否则拔网线（或屏蔽中继走 P2P）就能把旧授权一直用到旧到期时间。
        // 联网后中继会下发当前激活码，走 UpdateFromServer 自动恢复，不需要人工处理。
        if (_maxOffline > TimeSpan.Zero)
        {
            var baseline = contact != DateTime.MinValue ? contact : firstRun;
            if (baseline != DateTime.MinValue && now - baseline > _maxOffline)
            {
                Lock($"已连续 {_maxOffline.TotalHours:F0} 小时联系不上服务器" +
                     $"（最后一次 {baseline:yyyy-MM-dd HH:mm} UTC）—— 按授权策略停机，联网后自动恢复", code);
                return;
            }
        }

        if (payload == null) return;    // 还没拿到过激活码：不额外停机（此时也没有可用候选，P2P 无从谈起）

        // 时钟回拨检测：见过的最大时间比现在早很多 → 时间被人为往回拨
        if (lastSeen != DateTime.MinValue && now < lastSeen - RollbackTolerance)
        {
            Lock($"系统时间被往回拨（本次 {now:yyyy-MM-dd HH:mm}，此前已见过 {lastSeen:yyyy-MM-dd HH:mm}）", code);
            return;
        }

        // 缓存推进"见过的最大时间"，让回拨无处遁形
        if (now > lastSeen)
        {
            lock (_lock) _lastSeenUtc = now;
            Save();
        }

        // 本地判到期：到点即停，不依赖服务器
        if (now >= payload.ExpiresAt())
        {
            Lock($"授权已在本地到期（{payload.ExpiresAt():yyyy-MM-dd HH:mm} UTC，编号 {payload.Lic}）", code);
            return;
        }

        // 顺带做一次本机复验：缓存文件被改 / 公钥不符，同样停机
        if (!string.IsNullOrEmpty(code))
        {
            var v = _verify(code);
            if (!v.Ok) Lock($"本地缓存复验失败：{v.Error}", code);
        }
    }

    private void Lock(string reason, string? code)
    {
        bool already;
        lock (_lock)
        {
            already = _locked;
            _locked = true;
            _lockReason = reason;
            if (!string.IsNullOrEmpty(code)) _code = code;
        }
        if (already) return;
        _log.Error($"授权闸门关闭：{reason} —— 停止直连与采集，等待续期");
        try { LockedOut?.Invoke(reason); } catch (Exception ex) { _log.Debug($"LockedOut 处理异常：{ex.Message}"); }
    }

    // ---------------------------------------------------------------- 缓存

    private sealed class CacheRecord
    {
        public string Code { get; set; } = "";
        public long LastSeenUnix { get; set; }
        /// <summary>最后一次成功联系上服务器的时间（离线宽限按它计时）</summary>
        public long ContactUnix { get; set; }
        /// <summary>首次运行时间：还没联系过服务器时，宽限从安装时刻起算</summary>
        public long FirstRunUnix { get; set; }
    }

    private void LoadFromCache()
    {
        try
        {
            if (!File.Exists(_cachePath))
            {
                // 还没有缓存文件：把"首次运行时刻"记下来并落盘。
                // 必须落盘，否则重启一次宽限就重新计时 —— 那样"拔网线一直用"照样能绕过去。
                lock (_lock) _firstRunUtc = DateTime.UtcNow;
                Save();
                return;
            }
            var rec = JsonSerializer.Deserialize<CacheRecord>(File.ReadAllText(_cachePath));
            if (rec == null) return;
            var now = DateTime.UtcNow;
            lock (_lock)
            {
                _contactUtc = rec.ContactUnix > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(rec.ContactUnix).UtcDateTime
                    : DateTime.MinValue;
                _firstRunUtc = rec.FirstRunUnix > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(rec.FirstRunUnix).UtcDateTime
                    : now;
            }
            if (rec.FirstRunUnix == 0) Save();     // 老版本写的缓存没有这个字段，补上
            if (string.IsNullOrWhiteSpace(rec.Code)) return;
            var v = _verify(rec.Code);
            lock (_lock)
            {
                _code = rec.Code;
                _payload = v.Payload;
                _lastSeenUtc = rec.LastSeenUnix > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(rec.LastSeenUnix).UtcDateTime
                    : DateTime.MinValue;
            }
            if (!v.Ok)
            {
                // 缓存已过期/被改：先记下来，Tick() 会正式停机（启动阶段保持可用，好让中继把新码推下来）
                _log.Warn($"本地授权缓存不可用：{v.Error}；启动后若仍未续期将被停机");
            }
            else
            {
                _log.Info($"本地授权缓存：{v.Payload?.Lic}，至 {v.Payload?.ExpiresAt():yyyy-MM-dd}");
            }
            Tick();   // 启动时立刻判一次
        }
        catch (Exception ex)
        {
            _log.Warn($"读取授权缓存失败（将重新向服务器索取）：{ex.Message}");
        }
    }

    private void Save()
    {
        try
        {
            string code;
            DateTime lastSeen, contact, firstRun;
            lock (_lock)
            {
                code = _code;
                lastSeen = _lastSeenUtc;
                contact = _contactUtc;
                firstRun = _firstRunUtc;
            }
            var dir = Path.GetDirectoryName(_cachePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var rec = new CacheRecord
            {
                Code = code,
                LastSeenUnix = lastSeen == DateTime.MinValue ? 0 : new DateTimeOffset(lastSeen, TimeSpan.Zero).ToUnixTimeSeconds(),
                ContactUnix = contact == DateTime.MinValue ? 0 : new DateTimeOffset(contact, TimeSpan.Zero).ToUnixTimeSeconds(),
                FirstRunUnix = firstRun == DateTime.MinValue ? 0 : new DateTimeOffset(firstRun, TimeSpan.Zero).ToUnixTimeSeconds(),
            };
            File.WriteAllText(_cachePath, JsonSerializer.Serialize(rec, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            _log.Debug($"写授权缓存失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 记一次"成功联系上服务器"（中继连上、或 /api/license 探测成功都算）。
    /// 离线宽限以这个时间为准：超过宽限没联系上就停机。
    /// 这里**不解锁** —— 解锁由服务器下发激活码那条路径负责（UpdateFromServer），
    /// 保证"能继续用"永远意味着"服务器刚刚确认过授权"。
    /// </summary>
    public void NoteServerContact()
    {
        var now = DateTime.UtcNow;
        bool needSave;
        lock (_lock)
        {
            // 内存里每次都更新；落盘每 10 分钟最多一次，别频繁写盘
            needSave = _contactUtc == DateTime.MinValue || now - _contactUtc > TimeSpan.FromMinutes(10);
            _contactUtc = now;
            if (_firstRunUtc == DateTime.MinValue) _firstRunUtc = now;
        }
        if (needSave) Save();
    }
}
