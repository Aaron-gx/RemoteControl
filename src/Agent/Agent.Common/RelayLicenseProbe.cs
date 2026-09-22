using System.Net.Http;
using System.Text.Json;

namespace Agent.Common;

/// <summary>
/// 向中继的公开状态接口 /api/license 探一次授权状态。
///
/// 为什么需要它：被控端本地闸门只认激活码里的 exp（所以断网也能独立判到期），
/// 但"厂商在上位机里把这条码删掉了"这件事写不进激活码里 —— 只能问服务器。
/// 而服务器在授权不可用时**不让 /ws 握手过去**（403），那条路径上被控端收不到任何
/// 授权消息，于是会拿着本地缓存的旧码一直跑到期（还能走 P2P 直连）。
/// 所以这里主动问一次：服务器说被撤销了，就本地停机。
///
/// 只认"服务器明确回答 revoked"：网络不通、超时、HTTP 非 200、JSON 解析失败
/// 一律当作"不知道"，绝不停机 —— 不能因为服务器抖动就把客户的机器停了。
/// </summary>
public static class RelayLicenseProbe
{
    /// <summary>探测结果。Known=false 表示没问到（网络问题/接口异常），调用方应当什么都不做。</summary>
    public sealed record Result(bool Known, string State, string Reason);

    /// <summary>把 ws://host:port/ws 这类地址换成对应的 http://host:port/api/license。</summary>
    public static string? StatusUrl(string serverUrl)
    {
        if (string.IsNullOrWhiteSpace(serverUrl)) return null;
        var text = serverUrl.Trim();
        string scheme;
        if (text.StartsWith("wss://", StringComparison.OrdinalIgnoreCase)) scheme = "https";
        else if (text.StartsWith("ws://", StringComparison.OrdinalIgnoreCase)) scheme = "http";
        else if (text.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) scheme = "https";
        else if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) scheme = "http";
        else return null;
        try
        {
            var uri = new Uri(text, UriKind.Absolute);
            return $"{scheme}://{uri.Authority}/api/license";
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    /// <summary>探一次。任何异常/异常响应都返回 Known=false（不停机）。</summary>
    public static async Task<Result> QueryAsync(string serverUrl, TimeSpan timeout, CancellationToken ct = default)
    {
        var url = StatusUrl(serverUrl);
        if (url == null) return new Result(false, "", "服务器地址无法解析");
        try
        {
            using var http = new HttpClient { Timeout = timeout };
            using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return new Result(false, "", $"HTTP {(int)resp.StatusCode}");
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(text);
            if (!doc.RootElement.TryGetProperty("state", out var st))
                return new Result(false, "", "响应里没有 state 字段");
            var state = st.GetString() ?? "";
            var reason = doc.RootElement.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "";
            return new Result(true, state, reason);
        }
        catch (Exception ex)
        {
            // 超时 / DNS / 连接被拒 / JSON 坏了 —— 都属于"不知道"，绝不停机
            return new Result(false, "", ex.Message);
        }
    }
}
