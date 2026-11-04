using System.Security.Cryptography;
using Agent.Common;
using Xunit;

/// <summary>热更新：更新码签名/校验、版本比较、清单往返（2026-10 新增）</summary>
public class UpdateCodecTests
{
    /// <summary>生成一对临时密钥（测试不能也不该动厂商真私钥），返回 (私钥PEM, 公钥B64-未压缩点)。</summary>
    private static (string Pem, string PubB64) NewKey()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pem = ecdsa.ExportPkcs8PrivateKeyPem();
        var p = ecdsa.ExportParameters(false);
        var raw = new byte[65];
        raw[0] = 0x04;
        p.Q.X!.CopyTo(raw, 1);
        p.Q.Y!.CopyTo(raw, 33);
        return (pem, Convert.ToBase64String(raw));
    }

    private static UpdatePayload Sample(string ver = "1.2.0", string sha = "aabbcc") => new()
    {
        Kind = "agent",
        Ver = ver,
        Sha256 = sha,
        Size = 12345,
        Notes = "测试更新",
    };

    [Fact]
    public void 签名与验签_往返成功()
    {
        var (pem, pub) = NewKey();
        var code = UpdateCodec.Sign(Sample(), pem);
        Assert.StartsWith("RCU1.", code);
        var v = UpdateCodec.VerifyUsing(code, pub);
        Assert.True(v.Ok, v.Error);
        Assert.Equal("1.2.0", v.Payload!.Ver);
        Assert.Equal("agent", v.Payload.Kind);
        Assert.Equal(12345, v.Payload.Size);
        Assert.Equal("测试更新", v.Payload.Notes);
        Assert.True(v.Payload.Ts > 0);
    }

    [Fact]
    public void 篡改任意一段_验签必须失败()
    {
        var (pem, pub) = NewKey();
        var code = UpdateCodec.Sign(Sample(), pem);
        var parts = code.Split('.');

        // 篡改 payload：换成另一个版本号的合法 base64url（格式对但签名对不上）
        var forged = UpdateCodec.Sign(Sample(ver: "9.9.9"), pem);
        var swapped = $"{parts[0]}.{forged.Split('.')[1]}.{parts[2]}";
        Assert.False(UpdateCodec.VerifyUsing(swapped, pub).Ok);

        // 篡改签名：翻转首字符
        var badSig = ("A" == parts[2][0].ToString() ? "B" : "A") + parts[2][1..];
        Assert.False(UpdateCodec.VerifyUsing($"{parts[0]}.{parts[1]}.{badSig}", pub).Ok);

        // 整体改一个字符（内容级篡改）
        Assert.False(UpdateCodec.VerifyUsing(code[..^2] + "xx", pub).Ok);
    }

    [Fact]
    public void 换一把公钥_验签必须失败()
    {
        var (pem, _) = NewKey();
        var (_, otherPub) = NewKey();          // 用另一对的公钥去验
        var code = UpdateCodec.Sign(Sample(), pem);
        Assert.False(UpdateCodec.VerifyUsing(code, otherPub).Ok);
    }

    [Fact]
    public void 授权码前缀不能当更新码用_反之亦然()
    {
        var (pem, pub) = NewKey();
        var upd = UpdateCodec.Sign(Sample(), pem);
        Assert.StartsWith("RCU1.", upd);
        // 改成 RC1 前缀（伪装授权码格式）→ 更新验签必须拒绝
        Assert.False(UpdateCodec.VerifyUsing("RC1" + upd[3..], pub).Ok);
        // 乱格式
        Assert.False(UpdateCodec.VerifyUsing("", pub).Ok);
        Assert.False(UpdateCodec.VerifyUsing("junk", pub).Ok);
        Assert.False(UpdateCodec.VerifyUsing("RCU1.a.b", pub).Ok);
    }

    [Fact]
    public void 清单JSON_往返()
    {
        var m = new UpdateManifest
        {
            Agent = new UpdateEntry { Code = "RCU1.x.y", PackageUrl = "/api/update/file/agent-1.2.0.zip" },
            RolloutPercent = 10,
        };
        var json = System.Text.Json.JsonSerializer.Serialize(m);
        var back = System.Text.Json.JsonSerializer.Deserialize<UpdateManifest>(json,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(back);
        Assert.Equal("RCU1.x.y", back!.Agent!.Code);
        Assert.Equal(10, back.RolloutPercent);
        Assert.Null(back.Viewer);
    }

    [Theory]
    [InlineData("1.2.0", "1.1.9", 1)]
    [InlineData("1.2.10", "1.2.9", 1)]
    [InlineData("1.2", "1.2.0", 0)]
    [InlineData("1.2.0", "1.2.0", 0)]
    [InlineData("1.0.0", "1.1.9", -1)]
    [InlineData("v1.3", "1.2.9", 1)]
    [InlineData("2.0.0", "1.9.9", 1)]
    public void 版本比较(string a, string b, int expectedSign)
    {
        int cmp = UpdateCodec.CompareVersions(a, b);
        int sign = cmp == 0 ? 0 : cmp > 0 ? 1 : -1;
        Assert.Equal(expectedSign, sign);
    }
}
