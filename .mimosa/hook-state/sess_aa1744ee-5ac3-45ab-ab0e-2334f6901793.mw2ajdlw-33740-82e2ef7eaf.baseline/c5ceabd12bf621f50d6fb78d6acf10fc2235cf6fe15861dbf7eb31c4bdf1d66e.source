using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agent.Common;

/// <summary>
/// 更新包清单编解码（2026-10 新增，热更新用）：与授权码**同一套签名体系** ——
/// ECDSA P-256 + SHA-256（IEEE P1363）、同一对密钥（私钥只在厂商上位机/LicenseKeygen，
/// 客户端与中继只内置公钥）。前缀用 RCU1 与授权码（RC1）区分，互不通用。
///
/// 更新码 = <c>RCU1.&lt;base64url(payload_json)&gt;.&lt;base64url(signature)&gt;</c>
/// 签名内容只覆盖 payload（版本号/哈希/大小/说明）—— 下载地址不在签名内，
/// 但下载完必须比对签名内的 sha256，所以地址被篡改也拿不到合法包。
/// </summary>
public static class UpdateCodec
{
    /// <summary>与授权码同一把厂商公钥（见 LicenseCodec：反编译交付物拿不到私钥）</summary>
    public const string PublicKeyB64 = LicenseCodec.PublicKeyB64;

    public const string Prefix = "RCU1";

    public static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    // ---------------------------------------------------------------- 签发（仅厂商上位机使用）

    /// <summary>用厂商私钥签一个更新码（PEM 私钥，与授权码同一把）。</summary>
    public static string Sign(UpdatePayload payload, string privateKeyPem)
    {
        payload.V = 1;
        if (payload.Ts == 0) payload.Ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var body = JsonSerializer.SerializeToUtf8Bytes(payload, Json);
        var payloadB64 = Base64Url(body);
        var signingInput = $"{Prefix}.{payloadB64}";
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(privateKeyPem);
        var sig = ecdsa.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return $"{signingInput}.{Base64Url(sig)}";
    }

    // ---------------------------------------------------------------- 校验（被控端/主控端使用）

    public sealed record VerifyResult(bool Ok, UpdatePayload? Payload, string Error);

    /// <summary>格式 → 签名 → 内容 三步校验，任何一步失败 Ok=false（调用方不得把失败当通过）。</summary>
    public static VerifyResult Verify(string code) => VerifyUsing(code, LicenseCodec.PublicKeyB64);

    /// <summary>用指定公钥验（内置公钥走 Verify()；这里给单测/上位机自校验用）。</summary>
    public static VerifyResult VerifyUsing(string code, string publicKeyB64)
    {
        if (string.IsNullOrWhiteSpace(code))
            return new VerifyResult(false, null, "清单里没有更新码");
        var parts = code.Trim().Split('.');
        if (parts.Length != 3 || parts[0] != Prefix)
            return new VerifyResult(false, null, "更新码格式不正确（应为 RCU1.<payload>.<signature>）");
        byte[] sig, body;
        try
        {
            sig = FromBase64Url(parts[2]);
            body = FromBase64Url(parts[1]);
        }
        catch (Exception ex)
        {
            return new VerifyResult(false, null, "更新码解码失败：" + ex.Message);
        }
        if (sig.Length != 64)
            return new VerifyResult(false, null, "更新码签名长度不正确");
        try
        {
            using var ecdsa = LicenseCodec.CreateVerifier();
            if (!string.Equals(publicKeyB64, LicenseCodec.PublicKeyB64, StringComparison.Ordinal))
            {
                // 非内置公钥（测试/自校验）：按传入公钥构造
                var raw = Convert.FromBase64String(publicKeyB64.Trim());
                if (raw.Length != 65 || raw[0] != 0x04)
                    return new VerifyResult(false, null, "公钥格式不正确");
                ecdsa.ImportParameters(new ECParameters
                {
                    Curve = ECCurve.NamedCurves.nistP256,
                    Q = new ECPoint { X = raw.AsSpan(1, 32).ToArray(), Y = raw.AsSpan(33, 32).ToArray() },
                });
            }
            var signingInput = Encoding.ASCII.GetBytes($"{Prefix}.{parts[1]}");
            if (!ecdsa.VerifyData(signingInput, sig, HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                return new VerifyResult(false, null, "更新码验签失败（被篡改或不是厂商签发）");
        }
        catch (Exception ex)
        {
            return new VerifyResult(false, null, "更新码验签异常：" + ex.Message);
        }
        UpdatePayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<UpdatePayload>(body, Json);
        }
        catch (Exception ex)
        {
            return new VerifyResult(false, null, "更新码内容解析失败：" + ex.Message);
        }
        if (payload == null) return new VerifyResult(false, null, "更新码内容为空");
        if (payload.V != 1) return new VerifyResult(false, null, $"不支持的更新码版本 {payload.V}");
        if (string.IsNullOrEmpty(payload.Ver)) return new VerifyResult(false, null, "更新码缺少版本号");
        if (string.IsNullOrEmpty(payload.Sha256)) return new VerifyResult(false, null, "更新码缺少 sha256");
        return new VerifyResult(true, payload, "");
    }

    /// <summary>语义化版本比较（"1.2.10" &gt; "1.2.9" &gt; "1.2"）。解析失败按 0 处理。</summary>
    public static int CompareVersions(string a, string b)
    {
        static int[] Parse(string s)
        {
            // 固定返回 4 段（缺位补 0）："1.2" 与 "1.2.0" 才能正确相等
            var nums = new int[4];
            var parts = (s ?? "").Trim().TrimStart('v', 'V').Split('.');
            for (int i = 0; i < parts.Length && i < 4; i++)
                int.TryParse(parts[i], out nums[i]);
            return nums;
        }
        var x = Parse(a);
        var y = Parse(b);
        for (int i = 0; i < 4; i++)
        {
            if (x[i] != y[i]) return x[i].CompareTo(y[i]);
        }
        return 0;
    }

    private static string Base64Url(byte[] data)
        => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

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

/// <summary>更新码携带的信息（签名覆盖的内容）</summary>
public sealed class UpdatePayload
{
    [JsonPropertyName("v")] public int V { get; set; } = 1;

    /// <summary>更新目标：agent（被控端）| viewer（主控端）</summary>
    [JsonPropertyName("kind")] public string Kind { get; set; } = "agent";

    /// <summary>新版本号（如 1.2.0）</summary>
    [JsonPropertyName("ver")] public string Ver { get; set; } = "";

    /// <summary>更新包（zip）的 sha256（小写 hex）—— 下载后必须比对，防止包被换/传坏</summary>
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";

    /// <summary>更新包字节数（双重校验）</summary>
    [JsonPropertyName("size")] public long Size { get; set; }

    [JsonPropertyName("notes")] public string Notes { get; set; } = "";

    /// <summary>签发时间（Unix 秒）</summary>
    [JsonPropertyName("ts")] public long Ts { get; set; }
}

/// <summary>中继下发的版本清单（JSON）。code 是 RCU1 更新码；rolloutPercent 由**服务器**控制灰度
/// （策略不进签名：改灰度比例不需要重签）。</summary>
public sealed class UpdateManifest
{
    /// <summary>被控端当前可更新的版本（无更新可为 null）</summary>
    public UpdateEntry? Agent { get; set; }
    public UpdateEntry? Viewer { get; set; }

    /// <summary>灰度比例 0~100（服务器按 AgentId 哈希决定这条清单给不给这台机器）</summary>
    public int RolloutPercent { get; set; } = 100;
}

public sealed class UpdateEntry
{
    /// <summary>RCU1 更新码（含版本/哈希/签名）</summary>
    public string Code { get; set; } = "";
    /// <summary>更新包下载路径（相对中继根，如 /api/update/file/agent-1.2.0.zip）</summary>
    public string PackageUrl { get; set; } = "";
}
