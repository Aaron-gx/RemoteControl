namespace LicenseKeygen;

/// <summary>
/// 激活码里的时间统一用 UTC（中继服务器、被控端、/admin 页面全都按 UTC 显示/判定），
/// 所以界面日期框里的年月日也按 UTC 日界换算：选 2026-09-21 表示
/// 2026-09-21 00:00:00 UTC 生效、2026-09-21 23:59:59 UTC 到期。
/// 换算集中在这里，不要在各处自己拼 DateTimeOffset。
/// </summary>
internal static class LicenseTime
{
    /// <summary>今天（UTC 日期）。生效日期用它做默认值 —— 生成出来的码立刻生效，不会"尚未生效"。</summary>
    public static DateTime Today => DateTime.UtcNow.Date;

    /// <summary>所选日期 00:00:00 UTC 的 Unix 秒。</summary>
    public static long DayStart(DateTime date) =>
        new DateTimeOffset(DateTime.SpecifyKind(date.Date, DateTimeKind.Utc), TimeSpan.Zero).ToUnixTimeSeconds();

    /// <summary>所选日期 23:59:59 UTC 的 Unix 秒 —— 到期日当天仍然有效。</summary>
    public static long DayEnd(DateTime date) => DayStart(date.AddDays(1)) - 1;

    /// <summary>某个 UTC 时刻的 Unix 秒（不做日界截断）。</summary>
    public static long Unix(DateTime utcInstant) =>
        new DateTimeOffset(DateTime.SpecifyKind(utcInstant, DateTimeKind.Utc), TimeSpan.Zero).ToUnixTimeSeconds();
}
