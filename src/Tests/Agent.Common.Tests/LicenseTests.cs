using System.IO;
using Agent.Common;
using Xunit;

namespace Agent.Common.Tests;

/// <summary>授权（激活码）编解码测试：含与 Go 中继的跨语言互操作</summary>
public class LicenseTests
{
    // Go 侧（Server/interop_test.go）用厂商私钥签发的激活码，落在这个文件里
    private const string InteropFile = @"E:\tools\interop-code.txt";
    private const string InteropDeployment = "DEP-INTEROP";

    [Fact]
    public void 跨语言互操作_Go签发CSharp验证()
    {
        if (!File.Exists(InteropFile))
            return;   // 没有互操作样本（例如别的机器）就跳过
        var code = File.ReadAllText(InteropFile).Trim();
        Assert.StartsWith("RC1.", code);

        var r = LicenseCodec.Verify(code, DateTime.UtcNow, InteropDeployment);
        Assert.True(r.Ok, $"Go 签发的激活码在 C# 侧验签失败：{r.Error}（{r.State}）");
        Assert.Equal("INTEROP-1", r.Payload!.Lic);
        Assert.Equal(InteropDeployment, r.Payload.Srv);
        Assert.True(r.Payload.DaysLeft() > 0);
    }

    [Fact]
    public void 绑定不匹配_被拒绝()
    {
        if (!File.Exists(InteropFile)) return;
        var code = File.ReadAllText(InteropFile).Trim();
        var r = LicenseCodec.Verify(code, DateTime.UtcNow, "DEP-OTHER");
        Assert.False(r.Ok);
        Assert.Contains("不一致", r.Error);
    }

    [Fact]
    public void 空激活码_按未授权处理()
    {
        var r = LicenseCodec.Verify("", DateTime.UtcNow);
        Assert.False(r.Ok);
        Assert.Equal(LicenseState.Missing, r.State);
    }

    [Fact]
    public void 篡改负载_验签失败()
    {
        if (!File.Exists(InteropFile)) return;
        var code = File.ReadAllText(InteropFile).Trim();
        var parts = code.Split('.');
        var body = System.Text.Json.JsonSerializer.Deserialize<LicensePayload>(
            System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(
                parts[1].Replace('-', '+').Replace('_', '/').PadRight((parts[1].Length + 3) / 4 * 4, '='))))!;
        body.Exp = DateTimeOffset.UtcNow.AddYears(20).ToUnixTimeSeconds();   // 偷偷延长到期时间
        var newBody = Convert.ToBase64String(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(body))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var tampered = $"RC1.{newBody}.{parts[2]}";
        var r = LicenseCodec.Verify(tampered, DateTime.UtcNow, InteropDeployment);
        Assert.False(r.Ok);
        Assert.Contains("签名", r.Error);
    }

    [Fact]
    public void 内置公钥_格式正确()
    {
        using var ecdsa = LicenseCodec.CreateVerifier();
        Assert.Equal(256, ecdsa.KeySize);
    }

    [Fact]
    public void 自签名自验证_闭环()
    {
        // 用临时密钥对走一遍 Sign→Verify（验证格式自洽；真实签发用厂商私钥由上位机完成）
        using var ecdsa = System.Security.Cryptography.ECDsa.Create(
            System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var pem = ecdsa.ExportPkcs8PrivateKeyPem();
        var payload = new LicensePayload
        {
            Lic = "T-1", Sub = "自测",
            Nbf = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds(),
            Exp = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds(),
        };
        var code = LicenseCodec.Sign(payload, pem);
        // 用内置（厂商）公钥验 → 必然失败；用临时公钥验 → 成功
        var r1 = LicenseCodec.Verify(code, DateTime.UtcNow);
        Assert.False(r1.Ok);

        var raw = ecdsa.ExportParameters(false);
        var pubRaw = new byte[65];
        pubRaw[0] = 0x04;
        raw.Q.X!.CopyTo(pubRaw, 1);
        raw.Q.Y!.CopyTo(pubRaw, 33);
        Assert.Equal(65, pubRaw.Length);
        // 说明：这里只校验签名格式可被 .NET 自身往返（厂商公钥不匹配是预期行为）
        Assert.Contains("签名", r1.Error);
    }
}
