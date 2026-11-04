using System.Net.Http.Headers;
using Agent.Common;

namespace Agent.Coordinator;

/// <summary>
/// 剪贴板文件上传到中继服务器（策划 §4.2 / §8 文件复制流程）。
/// POST {FileServerUrl}/api/upload（multipart/form-data，字段名 file），
/// 返回 {fileId,fileName,fileSize,expiresAt}
/// </summary>
public sealed class FileUploader : IDisposable
{
    private readonly AgentConfig _cfg;
    private readonly Logger _log;
    private readonly HttpClient _http;

    public FileUploader(AgentConfig cfg, Logger log)
    {
        _cfg = cfg;
        _log = log;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
    }

    public async Task<ClipboardFileInfo?> UploadAsync(string localPath, string? fileName, CancellationToken ct = default)
    {
        if (!File.Exists(localPath))
        {
            _log.Warn($"上传失败：文件不存在 {localPath}");
            return null;
        }
        fileName ??= Path.GetFileName(localPath);
        var q = string.IsNullOrWhiteSpace(_cfg.AgentToken) ? "" : $"?token={Uri.EscapeDataString(_cfg.AgentToken)}";
        var url = $"{_cfg.ResolveFileServerUrl()}/api/upload{q}";
        try
        {
            await using var fs = File.OpenRead(localPath);
            using var form = new MultipartFormDataContent();
            var fileContent = new StreamContent(fs);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(fileContent, "file", fileName);
            form.Add(new StringContent(fileName), "fileName");

            _log.Info($"上传 {fileName}（{new FileInfo(localPath).Length} 字节）→ {url}");
            using var resp = await _http.PostAsync(url, form, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.Error($"上传失败 HTTP {(int)resp.StatusCode}：{Truncate(body, 300)}");
                return null;
            }
            var info = System.Text.Json.JsonSerializer.Deserialize<ClipboardFileInfo>(body,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (info == null || string.IsNullOrEmpty(info.FileId))
            {
                _log.Error($"上传返回无法解析：{Truncate(body, 300)}");
                return null;
            }
            var len = new FileInfo(localPath).Length;
            _log.Info($"上传完成 fileId={info.FileId} name={fileName} size={len}");
            return new ClipboardFileInfo
            {
                FileId = info.FileId,
                FileName = string.IsNullOrEmpty(info.FileName) ? fileName : info.FileName,
                FileSize = info.FileSize > 0 ? info.FileSize : len,
            };
        }
        catch (Exception ex)
        {
            _log.Error($"上传异常：{ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];

    public void Dispose() => _http.Dispose();
}
