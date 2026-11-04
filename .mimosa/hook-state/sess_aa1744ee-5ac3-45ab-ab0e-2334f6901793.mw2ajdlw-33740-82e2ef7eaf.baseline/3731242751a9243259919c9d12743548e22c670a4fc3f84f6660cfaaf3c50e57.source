using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agent.Common;

/// <summary>
/// 授权（激活码）编解码：与中继服务器（Go 侧）**完全同一套格式**，双方互相验签。
///
/// 激活码 = <c>RC1.&lt;base64url(payload_json)&gt;.&lt;base64url(signature&gt;&gt;</c>
/// 签名算法 = ECDSA P-256 + SHA-256，签名字节 = r||s 各 32 字节（IEEE P1363）。
///
/// 安全要点：**私钥只在厂商上位机里**；中继与客户端只内置公钥 →
/// 反编译任何交付物都拿不到能伪造激活码的材料（这是对抗 AI 破解的核心）。
/// </summary>
public static class LicenseCodec
{
    /// <summary>厂商公钥（P-256 未压缩点 0x04||X||Y 的 base64），与 Go 侧 Server/license.go 中的常量一致。</summary>
    public const string PublicKeyB64 =
        "BONwhpMRJYtkD91ULHKeW9hltBLfWSJtMEvU5/cbwRAKhgLUx+uoh90uVAtH/BMfNkGHUZ18OOA5BYjaf9jWXY8=";

    public const string Prefix = "RC1";

    public static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    // ---------------------------------------------------------------- 签发（仅上位机使用）

    /// <summary>用厂商私钥签发激活码（PEM 私钥）。</summary>
    public static string Sign(LicensePayload payload, string privateKeyPem)
    {
        payload.V = 1;
        if (payload.Iat == 0) payload.Iat = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var body = JsonSerializer.SerializeToUtf8Bytes(payload, Json);
        var payloadB64 = Base64Url(body);
        var signingInput = $"{Prefix}.{payloadB64}";
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(privateKeyPem);
        var sig = ecdsa.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return $"{signingInput}.{Base64Url(sig)}";
    }

    // ---------------------------------------------------------------- 校验（中继/客户端使用）

    public sealed record VerifyResult(bool Ok, LicensePayload? Payload, string Error, LicenseState State);

    /// <summary>
    /// 完整校验：格式 → 签名 → 部署/IP 绑定 → 时间窗。
    /// 任何一步失败都返回 Ok=false（调用方不得把失败当通过）。
    /// </summary>
    public static VerifyResult Verify(string code, DateTime nowUtc, string deploymentId = "", string ip = "")
    {
        if (string.IsNullOrWhiteSpace(code))
            return new VerifyResult(false, null, "尚未安装激活码", LicenseState.Missing);
        var parts = code.Trim().Split('.');
        if (parts.Length != 3 || parts[0] != Prefix)
            return new VerifyResult(false, null, "激活码格式不正确（应为 RC1.<payload>.<signature>）", LicenseState.Invalid);

        byte[] sig;
        byte[] body;
        try
        {
            sig = FromBase64Url(parts[2]);
            body = FromBase64Url(parts[1]);
        }
        catch (Exception ex)
        {
            return new VerifyResult(false, null, "激活码解码失败：" + ex.Message, LicenseState.Invalid);
        }
        if (sig.Length != 64)
            return new VerifyResult(false, null, "激活码签名长度不正确", LicenseState.Invalid);

        // 1) 验签
        try
        {
            using var ecdsa = CreateVerifier();
            var signingInput = Encoding.ASCII.GetBytes($"{Prefix}.{parts[1]}");
            if (!ecdsa.VerifyData(signingInput, sig, HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                return new VerifyResult(false, null, "激活码签名校验失败（无效或已被篡改）", LicenseState.Invalid);
        }
        catch (Exception ex)
        {
            return new VerifyResult(false, null, "验签异常：" + ex.Message, LicenseState.Invalid);
        }

        // 2) 解析内容
        LicensePayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<LicensePayload>(body, Json);
        }
        catch (Exception ex)
        {
            return new VerifyResult(false, null, "激活码内容解析失败：" + ex.Message, LicenseState.Invalid);
        }
        if (payload == null) return new VerifyResult(false, null, "激活码内容为空", LicenseState.Invalid);
        if (payload.V != 1) return new VerifyResult(false, null, $"不支持的激活码版本 {payload.V}", LicenseState.Invalid);

        // 3) 绑定
        if (!string.IsNullOrEmpty(payload.Srv) && !string.IsNullOrEmpty(deploymentId) &&
            !string.Equals(payload.Srv, deploymentId, StringComparison.Ordinal))
            return new VerifyResult(false, null, $"激活码绑定的部署 {payload.Srv} 与本机 {deploymentId} 不一致", LicenseState.Invalid);
        if (!string.IsNullOrEmpty(payload.IP) && !string.IsNullOrEmpty(ip) &&
            !string.Equals(payload.IP, ip, StringComparison.Ordinal))
            return new VerifyResult(false, null, $"激活码绑定的 IP {payload.IP} 与本机 {ip} 不一致", LicenseState.Invalid);

        // 4) 时间窗
        long now = new DateTimeOffset(nowUtc.Kind == DateTimeKind.Utc ? nowUtc : nowUtc.ToUniversalTime()).ToUnixTimeSeconds();
        if (payload.Nbf > 0 && now < payload.Nbf)
            return new VerifyResult(false, payload, $"激活码尚未生效（生效时间 {payload.NotBefore():yyyy-MM-dd HH:mm:ss} UTC）", LicenseState.NotYet);
        if (payload.Exp > 0 && now > payload.Exp)
            return new VerifyResult(false, payload, $"激活码已过期（到期时间 {payload.ExpiresAt():yyyy-MM-dd HH:mm:ss} UTC）", LicenseState.Expired);

        return new VerifyResult(true, payload, "", LicenseState.Ok);
    }

    /// <summary>只验签（不做时间/绑定判断），用于展示"这条码是厂商签发的"。</summary>
    public static bool VerifySignatureOnly(string code, out string error)
    {
        var r = Verify(code, DateTime.UnixEpoch.AddYears(100)); // 用一个远期时间绕过时间窗
        if (!r.Ok && r.State == LicenseState.Invalid)
        {
            error = r.Error;
            return false;
        }
        error = "";
        return true;
    }

    /// <summary>从内置公钥构造验签器。</summary>
    public static ECDsa CreateVerifier()
    {
        var raw = Convert.FromBase64String(PublicKeyB64.Trim());
        if (raw.Length != 65 || raw[0] != 0x04)
            throw new InvalidOperationException("内置公钥格式不正确（应为 0x04||X||Y 共 65 字节）");
        var x = raw.AsSpan(1, 32).ToArray();
        var y = raw.AsSpan(33, 32).ToArray();
        var ecdsa = ECDsa.Create();
        ecdsa.ImportParameters(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = x, Y = y },
        });
        return ecdsa;
    }

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string s)
    {
        var t = s.Replace('-', '+').Replace('_', '/');
        switch (t.Length % 4)
        {
            case 2: t += "=="; break;
            case 3: t += "="; break;
        }
        return Convert.FromBase64String(t);
    }
}

/// <summary>激活码里携带的授权信息（与 Go 侧 LicensePayload 字段一一对应）。</summary>
public sealed class LicensePayload
{
    [JsonPropertyName("v")] public int V { get; set; } = 1;

    /// <summary>授权编号（订单/客户编号）</summary>
    [JsonPropertyName("lic")] public string Lic { get; set; } = "";

    /// <summary>客户名/备注</summary>
    [JsonPropertyName("sub")] public string Sub { get; set; } = "";

    /// <summary>绑定的部署 ID（空 = 不绑定）</summary>
    [JsonPropertyName("srv")] public string Srv { get; set; } = "";

    /// <summary>绑定的公网 IP（空 = 不绑定）</summary>
    [JsonPropertyName("ip")] public string IP { get; set; } = "";

    [JsonPropertyName("maxview")] public int MaxViewers { get; set; }

    [JsonPropertyName("feat")] public string[]? Feat { get; set; }

    /// <summary>生效时间（Unix 秒）</summary>
    [JsonPropertyName("nbf")] public long Nbf { get; set; }

    /// <summary>到期时间（Unix 秒）</summary>
    [JsonPropertyName("exp")] public long Exp { get; set; }

    /// <summary>签发时间（Unix 秒）</summary>
    [JsonPropertyName("iat")] public long Iat { get; set; }

    public DateTime NotBefore() => DateTimeOffset.FromUnixTimeSeconds(Nbf).UtcDateTime;
    public DateTime ExpiresAt() => DateTimeOffset.FromUnixTimeSeconds(Exp).UtcDateTime;

    /// <summary>剩余天数（负数表示已过期）</summary>
    public double DaysLeft() => (ExpiresAt() - DateTime.UtcNow).TotalDays;
}

public enum LicenseState
{
    Ok,
    Missing,
    Invalid,
    Expired,
    NotYet,
}
