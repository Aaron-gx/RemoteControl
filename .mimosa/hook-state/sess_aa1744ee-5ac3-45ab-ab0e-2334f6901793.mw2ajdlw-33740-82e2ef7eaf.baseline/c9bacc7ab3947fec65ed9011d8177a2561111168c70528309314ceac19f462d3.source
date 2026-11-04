using System.Security.Cryptography;
using Agent.Common;
using Xunit;

namespace Agent.Common.Tests;

/// <summary>
/// 本地授权闸门测试 —— 这是"屏蔽中继走 P2P 白嫖"的拦截点，必须有回归覆盖：
/// 到期停机（完全离线也能判）、时钟回拨停机、缓存被改停机、正常时放行且续期后恢复。
/// 为了能签出"有效码"，这里注入一对测试密钥（生产用内置厂商公钥，行为一致）。
/// </summary>
public class LicenseGateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rc-licgate-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly Logger _log = new("licgate-test", minLevel: LogLevel.Error, echoConsole: false);

    public LicenseGateTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
        _key.Dispose();
    }

    private string CachePath => Path.Combine(_dir, "license.cache.json");

    /// <summary>用测试密钥验签（替代内置厂商公钥）</summary>
    private LicenseCodec.VerifyResult TestVerifier(string code)
    {
        try
        {
            var parts = code.Split('.');
            if (parts.Length != 3) return new LicenseCodec.VerifyResult(false, null, "格式错误", LicenseState.Invalid);
            var sig = Convert.FromBase64String(parts[2].Replace('-', '+').Replace('_', '/').PadRight(parts[2].Length + (4 - parts[2].Length % 4) % 4, '='));
            var body = Convert.FromBase64String(parts[1].Replace('-', '+').Replace('_', '/').PadRight(parts[1].Length + (4 - parts[1].Length % 4) % 4, '='));
            if (!_key.VerifyData(body, sig, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                return new LicenseCodec.VerifyResult(false, null, "签名无效", LicenseState.Invalid);
            var payload = System.Text.Json.JsonSerializer.Deserialize<LicensePayload>(body, LicenseCodec.Json);
            if (payload == null) return new LicenseCodec.VerifyResult(false, null, "载荷解析失败", LicenseState.Invalid);
            var now = DateTime.UtcNow;
            if (now < payload.NotBefore()) return new LicenseCodec.VerifyResult(false, payload, "尚未生效", LicenseState.NotYet);
            if (now >= payload.ExpiresAt()) return new LicenseCodec.VerifyResult(false, payload, "已过期", LicenseState.Expired);
            return new LicenseCodec.VerifyResult(true, payload, "", LicenseState.Ok);
        }
        catch (Exception ex) { return new LicenseCodec.VerifyResult(false, null, ex.Message, LicenseState.Invalid); }
    }

    private string SignWithTestKey(LicensePayload payload)
    {
        var body = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(payload, LicenseCodec.Json);
        var sig = _key.SignData(body, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"RC1.{B64Url(body)}.{B64Url(sig)}";
    }

    private LicensePayload Payload(int validHours)
    {
        var exp = DateTime.UtcNow.AddHours(validHours);
        return new LicensePayload
        {
            Lic = "TEST-0001",
            Sub = "单测",
            Nbf = new DateTimeOffset(DateTime.UtcNow.AddHours(-1), TimeSpan.Zero).ToUnixTimeSeconds(),
            Exp = new DateTimeOffset(exp, TimeSpan.Zero).ToUnixTimeSeconds(),
        };
    }

    [Fact]
    public void 没有缓存时不主动停机_但拿到有效码后放行()
    {
        var gate = new LicenseGate(CachePath, _log, TestVerifier);
        gate.Tick();
        Assert.False(gate.Locked);          // 没有缓存：无从提供 P2P，不额外停机

        gate.UpdateFromServer(SignWithTestKey(Payload(24)));
        Assert.True(gate.IsUsable);
        Assert.False(gate.Locked);
        Assert.True(File.Exists(CachePath));   // 已缓存到本地
    }

    [Fact]
    public void 缓存里的最大时间在未来一小时内_属于正常对时抖动_不停机()
    {
        var gate = new LicenseGate(CachePath, _log, TestVerifier);
        gate.UpdateFromServer(SignWithTestKey(Payload(48)));
        Assert.True(gate.IsUsable);

        // 模拟：本机时钟比"见过的最大时间"慢 1 小时（NTP 校时/时区抖动都在容差内）
        var rec = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(
            File.ReadAllText(CachePath))!;
        File.WriteAllText(CachePath, System.Text.Json.JsonSerializer.Serialize(new
        {
            Code = rec["Code"].GetString(),
            LastSeenUnix = new DateTimeOffset(DateTime.UtcNow.AddHours(1), TimeSpan.Zero).ToUnixTimeSeconds(),
        }));

        var gate2 = new LicenseGate(CachePath, _log, TestVerifier);
        gate2.Tick();
        Assert.False(gate2.Locked);       // 容差内的抖动不该误杀
        Assert.True(gate2.IsUsable);
    }

    [Fact]
    public void 到期后本地判停_不依赖服务器()
    {
        // 构造一个"1 秒后到期"的合法码，等它过期
        var p = Payload(0);
        p.Exp = new DateTimeOffset(DateTime.UtcNow.AddSeconds(1), TimeSpan.Zero).ToUnixTimeSeconds();
        var gate = new LicenseGate(CachePath, _log, TestVerifier);
        gate.UpdateFromServer(SignWithTestKey(p));
        Assert.True(gate.IsUsable);

        Thread.Sleep(1500);
        gate.Tick();
        Assert.True(gate.Locked);
        Assert.Contains("到期", gate.LockReason);
    }

    [Fact]
    public void 时钟被往回拨超过容差会停机()
    {
        var gate = new LicenseGate(CachePath, _log, TestVerifier);
        gate.UpdateFromServer(SignWithTestKey(Payload(48)));
        Assert.True(gate.IsUsable);

        // 模拟：见过的时间比"现在"晚 12 小时（= 系统时间被往回拨了）
        var future = DateTime.UtcNow.AddHours(12);
        var rec = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(
            File.ReadAllText(CachePath))!;
        File.WriteAllText(CachePath, System.Text.Json.JsonSerializer.Serialize(new
        {
            Code = rec["Code"].GetString(),
            LastSeenUnix = new DateTimeOffset(future, TimeSpan.Zero).ToUnixTimeSeconds(),
        }));

        var gate2 = new LicenseGate(CachePath, _log, TestVerifier);
        gate2.Tick();
        Assert.True(gate2.Locked);
        Assert.Contains("往回拨", gate2.LockReason);
    }

    [Fact]
    public void 服务器下发的码本机复验不通过时停机()
    {
        var gate = new LicenseGate(CachePath, _log, TestVerifier);
        gate.UpdateFromServer("RC1.bm90LWEtcmVhbC1jb2Rl.bm90LWEtc2ln");
        Assert.True(gate.Locked);
        Assert.Contains("复验", gate.LockReason);
    }

    [Fact]
    public void 服务器判定不可用时立即停机并触发事件()
    {
        var gate = new LicenseGate(CachePath, _log, TestVerifier);
        string? reason = null;
        gate.LockedOut += r => reason = r;
        gate.LockFromServer("expired：授权已到期");
        Assert.True(gate.Locked);
        Assert.False(gate.IsUsable);
        Assert.NotNull(reason);
        Assert.Contains("expired", gate.LockReason);
    }

    [Fact]
    public void 续期后可以自动恢复使用()
    {
        var gate = new LicenseGate(CachePath, _log, TestVerifier);
        gate.LockFromServer("expired");
        Assert.False(gate.IsUsable);
        gate.UpdateFromServer(SignWithTestKey(Payload(24)));
        Assert.True(gate.IsUsable);
        Assert.Equal("", gate.LockReason);
    }
}

/// <summary>
/// 离线宽限（"必须联网"策略）：
/// 太久联系不上服务器就停机，联网后自动恢复。目的是让撤销/续期在一个有界时间内生效 ——
/// 否则把网线一拔（或屏蔽中继走 P2P）就能把旧授权一直用到旧到期时间。
/// </summary>
public class LicenseGateOfflineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rc-offline-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly Logger _log = new("offline-test", minLevel: LogLevel.Error, echoConsole: false);

    public LicenseGateOfflineTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
        _key.Dispose();
    }

    private string CachePath => Path.Combine(_dir, "license.cache.json");
    private static TimeSpan Grace48 => TimeSpan.FromHours(48);

    private LicenseCodec.VerifyResult TestVerifier(string code)
    {
        var parts = code.Split('.');
        if (parts.Length != 3) return new LicenseCodec.VerifyResult(false, null, "格式", LicenseState.Invalid);
        var body = Convert.FromBase64String(parts[1].Replace('-', '+').Replace('_', '/')
            .PadRight(parts[1].Length + (4 - parts[1].Length % 4) % 4, '='));
        var sig = Convert.FromBase64String(parts[2].Replace('-', '+').Replace('_', '/')
            .PadRight(parts[2].Length + (4 - parts[2].Length % 4) % 4, '='));
        if (!_key.VerifyData(System.Text.Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), sig,
                HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            return new LicenseCodec.VerifyResult(false, null, "签名", LicenseState.Invalid);
        var p = System.Text.Json.JsonSerializer.Deserialize<LicensePayload>(body)!;
        return new LicenseCodec.VerifyResult(true, p, "", LicenseState.Ok);
    }

    private string SignWithTestKey(LicensePayload p)
    {
        p.V = 1;
        var body = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(p);
        var b64 = Convert.ToBase64String(body).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var sig = _key.SignData(System.Text.Encoding.ASCII.GetBytes($"RC1.{b64}"), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return $"RC1.{b64}.{Convert.ToBase64String(sig).TrimEnd('=').Replace('+', '-').Replace('/', '_')}";
    }

    private LicensePayload Payload(int validHours) => new()
    {
        Lic = "OFF-0001", Sub = "单测",
        Nbf = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds(),
        Exp = DateTimeOffset.UtcNow.AddHours(validHours).ToUnixTimeSeconds(),
    };

    /// <summary>写一份缓存文件：可以指定"最后联系服务器"与"首次运行"的时间。</summary>
    private void WriteCache(string code, DateTime? contact, DateTime? firstRun) =>
        File.WriteAllText(CachePath, System.Text.Json.JsonSerializer.Serialize(new
        {
            Code = code,
            LastSeenUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ContactUnix = contact is null ? 0 : new DateTimeOffset(contact.Value, TimeSpan.Zero).ToUnixTimeSeconds(),
            FirstRunUnix = firstRun is null ? 0 : new DateTimeOffset(firstRun.Value, TimeSpan.Zero).ToUnixTimeSeconds(),
        }));

    [Fact]
    public void 离线超过宽限_启动即判停_重新联系服务器后自动恢复()
    {
        var code = SignWithTestKey(Payload(24 * 365));
        // 最后一次联系是 100 小时前（超过 48 小时宽限）
        WriteCache(code, DateTime.UtcNow.AddHours(-100), DateTime.UtcNow.AddHours(-500));

        var gate = new LicenseGate(CachePath, _log, TestVerifier, Grace48);
        Assert.True(gate.Locked);
        Assert.Contains("联系不上服务器", gate.LockReason);
        Assert.False(gate.IsUsable);

        // 只是"能连上"（探测成功）还不够：解锁必须来自服务器下发的激活码
        gate.NoteServerContact();
        Assert.True(gate.Locked);

        // 服务器下发当前激活码 → 自动恢复
        gate.UpdateFromServer(code);
        Assert.False(gate.Locked);
        Assert.True(gate.IsUsable);
    }

    [Fact]
    public void 宽限内断开_不停机()
    {
        var code = SignWithTestKey(Payload(24 * 365));
        WriteCache(code, DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(-1));   // 1 小时前刚联系过
        var gate = new LicenseGate(CachePath, _log, TestVerifier, Grace48);
        gate.Tick();
        Assert.False(gate.Locked);
    }

    [Fact]
    public void 宽限设为0_回到旧行为_不停机()
    {
        var code = SignWithTestKey(Payload(24 * 365));
        WriteCache(code, DateTime.UtcNow.AddDays(-60), DateTime.UtcNow.AddDays(-60));
        var gate = new LicenseGate(CachePath, _log, TestVerifier, TimeSpan.Zero);
        gate.Tick();
        Assert.False(gate.Locked);
    }

    [Fact]
    public void 从没联系过服务器_从首次运行起算宽限()
    {
        var code = SignWithTestKey(Payload(24 * 365));
        WriteCache(code, contact: null, firstRun: DateTime.UtcNow.AddHours(-50));   // 装好 50 小时、一次都没联上
        var gate = new LicenseGate(CachePath, _log, TestVerifier, Grace48);
        Assert.True(gate.Locked);
        Assert.Contains("联系不上服务器", gate.LockReason);
    }

    [Fact]
    public void 首次运行时间会落盘_重启不能重置宽限()
    {
        // 全新的机器：第一次运行就该把"首次运行时刻"写下（否则重启一次宽限就重新计时）
        var gate = new LicenseGate(CachePath, _log, TestVerifier, Grace48);
        Assert.True(File.Exists(CachePath));
        var first = System.Text.Json.JsonSerializer
            .Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(File.ReadAllText(CachePath))!;
        var firstRun = first["FirstRunUnix"].GetInt64();
        Assert.True(firstRun > 0, "首次运行时间没有落盘");

        // 再构造一次（相当于重启）：首次运行时间不变
        var gate2 = new LicenseGate(CachePath, _log, TestVerifier, Grace48);
        var second = System.Text.Json.JsonSerializer
            .Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(File.ReadAllText(CachePath))!;
        Assert.Equal(firstRun, second["FirstRunUnix"].GetInt64());
    }

    [Fact]
    public void 联系成功会刷新落盘的联系时间()
    {
        new LicenseGate(CachePath, _log, TestVerifier, Grace48).NoteServerContact();
        var rec = System.Text.Json.JsonSerializer
            .Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(File.ReadAllText(CachePath))!;
        var contact = rec["ContactUnix"].GetInt64();
        Assert.True(Math.Abs(contact - DateTimeOffset.UtcNow.ToUnixTimeSeconds()) < 60,
            $"联系时间没刷新：{contact}");
    }
}
