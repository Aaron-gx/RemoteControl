using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LicenseKeygen;

/// <summary>
/// 一条签发记录（台账里的一行）。
///
/// 存完整激活码原文：要能"复制/重发"，也要能算指纹与服务器比对（服务器只公布指纹，
/// 不公布码，所以只能本地算）。台账落在**私钥所在目录**，跟着私钥一起备份 ——
/// 换台机器、换份 exe 都还是同一本账。
/// </summary>
public sealed class LicenseRecord
{
    public string Code { get; set; } = "";
    public string Lic { get; set; } = "";
    public string Sub { get; set; } = "";
    public string Srv { get; set; } = "";
    public string IP { get; set; } = "";
    public int MaxViewers { get; set; }
    public long Nbf { get; set; }
    public long Exp { get; set; }
    public long Iat { get; set; }        // 生成时间（Unix 秒）

    /// <summary>是否已删除（= 已撤销，客户那边会停机，且装不回来）</summary>
    public bool Revoked { get; set; }
    public long RevokedAt { get; set; }
    public string Note { get; set; } = "";

    /// <summary>撤销记录最后一次**成功推送到服务器**的时间（0 = 还没同步，客户那边暂时还能用）</summary>
    public long SyncedAt { get; set; }

    /// <summary>曾经观察到它被安装到服务器上的时间（本地记录，用于"哪些用过了"）</summary>
    public long InstalledAt { get; set; }

    public string Hash() => LicenseHistory.CodeHash(Code);
}

/// <summary>
/// 上位机的签发台账 + 撤销名单（本地账）。
///
/// 为什么要有它：中继只在 license.json 里保存"当前装的那一条"，没有已签发台账，
/// 所以"历史上发过哪些码、哪些在用、哪些删了"只能在上位机这边记。
/// 删除（撤销）要求"删了就肯定不能用"，因此删除会同时：① 本地标记失效；
/// ② 把指纹推给中继（中继在安装与复验两处拦截，并让在线被控端停机）。
/// ②没成功时界面上会明确显示"未同步"，因为那种状态下码其实还有效。
/// </summary>
public sealed class LicenseHistory
{
    public List<LicenseRecord> Records { get; set; } = new();

    /// <summary>已"恢复"、需要从服务器撤销名单里移除的指纹（下次同步时带上）</summary>
    public List<string> PendingRemove { get; set; } = new();

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>激活码指纹：sha256(去掉首尾空白的码原文) 的十六进制，与中继侧完全一致。</summary>
    public static string CodeHash(string code)
    {
        var sum = SHA256.HashData(Encoding.UTF8.GetBytes((code ?? "").Trim()));
        return Convert.ToHexString(sum).ToLowerInvariant();
    }

    /// <summary>台账文件路径：与私钥同一个目录（跟着私钥备份/迁移）。</summary>
    public static string PathForKey(string privateKeyPath)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(privateKeyPath))
            {
                var full = Path.GetFullPath(privateKeyPath.Trim());
                var dir = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(dir)) return Path.Combine(dir, "license-history.json");
            }
        }
        catch { }
        return Path.Combine(AppContext.BaseDirectory, "license-history.json");
    }

    public static LicenseHistory Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<LicenseHistory>(File.ReadAllText(path));
                if (loaded != null) return loaded;
            }
        }
        catch { }
        return new LicenseHistory();
    }

    public void Save(string path)
    {
        try
        {
            // 先写临时文件再替换：避免断电/崩溃时把台账写坏（写坏就等于丢了所有记录）
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, path, overwrite: true);
        }
        catch { }
    }

    /// <summary>按生成时间倒序（新的在上面）。</summary>
    public IEnumerable<LicenseRecord> Newest()
    {
        return Records.OrderByDescending(r => r.Iat).ThenByDescending(r => r.RevokedAt);
    }

    public LicenseRecord? FindByCode(string code)
    {
        var hash = CodeHash(code);
        return Records.FirstOrDefault(r => r.Hash() == hash);
    }

    public void Add(LicenseRecord rec) => Records.Add(rec);

    /// <summary>删除（撤销）：标记失效并等待推送。返回 false 表示这条已在撤销状态。</summary>
    public bool Revoke(LicenseRecord rec, string note)
    {
        if (rec.Revoked) return false;
        rec.Revoked = true;
        rec.RevokedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        rec.SyncedAt = 0;
        rec.Note = note;
        return true;
    }

    /// <summary>恢复：取消撤销，并记下"要从服务器名单里移除"。</summary>
    public bool Unrevoke(LicenseRecord rec)
    {
        if (!rec.Revoked) return false;
        var wasSynced = rec.SyncedAt > 0;
        rec.Revoked = false;
        rec.RevokedAt = 0;
        rec.SyncedAt = 0;
        rec.Note = "";
        if (wasSynced)
        {
            var hash = rec.Hash();
            if (!PendingRemove.Contains(hash)) PendingRemove.Add(hash);
        }
        return true;
    }
}
