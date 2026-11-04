using System.Windows.Media;

namespace LicenseKeygen;

/// <summary>
/// 台账表格里的一行（把 LicenseRecord 变成"给人看"的文字与颜色）。
///
/// 单独做一层是为了让状态判定只有一处：一行到底是"使用中/曾使用/未使用/已过期/已删除"，
/// 要同时看本地撤销标记、服务器当前装的指纹、以及有没有同步成功 —— 分散到 XAML 触发器里会失控。
/// </summary>
public sealed class LicenseRow
{
    private static readonly Brush Green = new SolidColorBrush(Color.FromRgb(0x10, 0x7C, 0x10));
    private static readonly Brush Blue = new SolidColorBrush(Color.FromRgb(0x0F, 0x6C, 0xBD));
    private static readonly Brush Gray = new SolidColorBrush(Color.FromRgb(0x7A, 0x8A, 0x99));
    private static readonly Brush Red = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B));
    private static readonly Brush Orange = new SolidColorBrush(Color.FromRgb(0xB8, 0x6A, 0x00));

    public LicenseRecord Record { get; }

    public string Lic { get; }
    public string Sub { get; }
    public string Srv { get; }
    public string Validity { get; }
    public string CreatedText { get; }
    public string InstalledText { get; }
    public string StatusText { get; }
    public Brush StatusBrush { get; }

    /// <param name="serverHash">服务器当前安装的激活码指纹（空 = 没读到/未安装）</param>
    public LicenseRow(LicenseRecord rec, string serverHash)
    {
        Record = rec;
        Lic = string.IsNullOrWhiteSpace(rec.Lic) ? "（无编号）" : rec.Lic;
        Sub = rec.Sub;
        Srv = string.IsNullOrWhiteSpace(rec.Srv) ? "不绑定" : rec.Srv;
        Validity = $"{Unix(rec.Nbf):yyyy-MM-dd} ~ {Unix(rec.Exp):yyyy-MM-dd}";
        CreatedText = Unix(rec.Iat).ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        InstalledText = rec.InstalledAt == 0
            ? "—"
            : Unix(rec.InstalledAt).ToLocalTime().ToString("yyyy-MM-dd HH:mm");

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var isCurrent = !string.IsNullOrEmpty(serverHash) && rec.Hash() == serverHash;

        if (rec.Revoked)
        {
            // 删除过但还没推送到服务器 —— 这种情况下客户其实还能用到到期，必须显眼
            if (rec.SyncedAt == 0)
            {
                StatusText = "已删除（未同步！）";
                StatusBrush = Orange;
            }
            else
            {
                StatusText = "已删除（已失效）";
                StatusBrush = Red;
            }
        }
        else if (isCurrent)
        {
            StatusText = "使用中";
            StatusBrush = Green;
        }
        else if (rec.InstalledAt > 0)
        {
            StatusText = "曾使用（已换码）";
            StatusBrush = Blue;
        }
        else if (rec.Exp > 0 && now > rec.Exp)
        {
            StatusText = "已过期";
            StatusBrush = Gray;
        }
        else
        {
            StatusText = "未使用";
            StatusBrush = Gray;
        }
    }

    private static DateTimeOffset Unix(long seconds) =>
        DateTimeOffset.FromUnixTimeSeconds(seconds == 0 ? 0 : seconds);
}
