using System.IO;
using System.Net.Http;
using Agent.Common;

namespace Viewer.Services;

/// <summary>
/// 远程剪贴板文件下载（策划 §8 文件复制流程末尾：GET /api/download/:id → 保存到本地）
/// </summary>
public sealed class FileDownloader : IDisposable
{
    private readonly HttpClient _http;
    private readonly Logger _log;

    public FileDownloader(Logger log)
    {
        _log = log;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
    }

    public async Task<string?> DownloadAsync(string baseUrl, ClipboardFileInfo info, string dir,
        IProgress<double>? progress = null, CancellationToken ct = default, string token = "")
    {
        try
        {
            Directory.CreateDirectory(dir);
            var q = string.IsNullOrEmpty(token) ? "" : $"?token={Uri.EscapeDataString(token)}";
            var url = $"{baseUrl.TrimEnd('/')}/api/download/{info.FileId}{q}";
            _log.Info($"开始下载 {info.FileName} ← {url}");
            using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.Error($"下载失败 HTTP {(int)resp.StatusCode}");
                return null;
            }
            long total = resp.Content.Headers.ContentLength ?? info.FileSize;
            var target = UniquePath(dir, Sanitize(info.FileName));
            var tmp = target + ".part";
            await using (var src = await resp.Content.ReadAsStreamAsync(ct))
            await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, true))
            {
                var buf = new byte[1 << 20];
                long done = 0;
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    done += n;
                    if (total > 0) progress?.Report((double)done / total);
                }
            }
            if (File.Exists(target)) File.Delete(target);
            File.Move(tmp, target);
            _log.Info($"下载完成：{target}（{new FileInfo(target).Length} 字节）");
            return target;
        }
        catch (Exception ex)
        {
            _log.Error($"下载异常：{ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        if (string.IsNullOrEmpty(clean)) clean = "remote_file";
        if (clean.Length > 180) clean = clean[..180];
        return clean;
    }

    private static string UniquePath(string dir, string name)
    {
        var path = Path.Combine(dir, name);
        if (!File.Exists(path)) return path;
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (int i = 1; i < 1000; i++)
        {
            var cand = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(cand)) return cand;
        }
        return Path.Combine(dir, $"{stem}_{DateTime.Now:HHmmss}{ext}");
    }

    public void Dispose() => _http.Dispose();
}
