using System.IO.Compression;

namespace Agent.Common;

/// <summary>
/// 文件夹复制（2026-10 新增）的打包侧：把被控端复制到剪贴板的目录压成临时 zip，
/// 由协调器上传、主控端解压还原。放在 Common 里是为了能直接单元测试。
/// </summary>
public static class FolderTransfer
{
    /// <summary>与中继 /api/upload 的单文件上限保持一致（filestore 的 512MiB）</summary>
    public const long MaxZipBytes = 512L * 1024 * 1024;

    /// <summary>
    /// 把目录打包成临时 zip。成功返回 zip 路径（调用方负责用完删除），失败返回 null 并给出原因。
    /// includeBaseDirectory=false：解压出来直接就是"文件夹名/内容"想要的形态（解压目标目录自己起名）。
    /// 被其他进程占用的文件**跳过不打包**（Telegram/微信等运行中的数据目录会锁文件——整包失败不可接受）。
    /// </summary>
    public static string? PackFolder(string dir, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        {
            error = $"文件夹不存在：{dir}";
            return null;
        }
        try
        {
            var root = Path.Combine(Path.GetTempPath(), "RC-FolderTransfer", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var folderName = Path.GetFileName(dir.TrimEnd('\\', '/'));
            if (string.IsNullOrEmpty(folderName)) folderName = "文件夹";
            var zipPath = Path.Combine(root, folderName + ".zip");

            // 手动逐文件打包（不用 ZipFile.CreateFromDirectory：它遇到被占用的文件直接 IOException 整包失败）
            int skipped = 0, packed = 0;
            using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(dir, file);
                    try
                    {
                        // FileShare.ReadWrite：允许其他进程正在写（能读就走，读不了才跳过）
                        using var src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        var entry = archive.CreateEntry(rel, CompressionLevel.Optimal);
                        using var dst = entry.Open();
                        src.CopyTo(dst);
                        packed++;
                    }
                    catch (IOException)
                    {
                        skipped++;   // 被锁定：跳过（Telegram 的 tdata\working 等运行时文件）
                    }
                    catch (UnauthorizedAccessException)
                    {
                        skipped++;   // 无权限：跳过
                    }
                }
            }
            if (packed == 0 && skipped > 0)
            {
                try { File.Delete(zipPath); } catch { }
                try { Directory.Delete(root); } catch { }
                error = $"文件夹里 {skipped} 个文件全部被其他程序占用（如 Telegram/微信正在运行），无法打包";
                return null;
            }

            var size = new FileInfo(zipPath).Length;
            if (size > MaxZipBytes)
            {
                try { File.Delete(zipPath); } catch { }
                try { Directory.Delete(root); } catch { }
                error = $"文件夹压缩后 {size / 1024.0 / 1024.0:F0}MB，超过单次传输上限 {MaxZipBytes / 1024 / 1024}MB";
                return null;
            }
            if (skipped > 0)
                error = $"已跳过 {skipped} 个被占用的文件（打包 {packed} 个）";  // 非致命：放在 error 里只是传递信息
            return zipPath;
        }
        catch (Exception ex)
        {
            error = $"文件夹打包失败（{ex.GetType().Name}: {ex.Message}）";
            return null;
        }
    }

    /// <summary>
    /// 主控端侧：把下载到的 zip 解压成目录。返回解压出的目录路径（重名自动加 " (2)"/" (3)"…），
    /// 失败返回 null。.NET 的 ExtractToDirectory 自带 zip-slip 防护（条目不允许逃出目标目录）。
    /// </summary>
    public static string? ExtractToUniqueDir(string zipPath, string destParent, string folderName)
    {
        try
        {
            Directory.CreateDirectory(destParent);
            var name = SanitizeFolderName(folderName);
            if (string.IsNullOrWhiteSpace(name)) name = "远程文件夹";
            var dest = Path.Combine(destParent, name);
            for (int i = 2; Directory.Exists(dest); i++)
                dest = Path.Combine(destParent, $"{name} ({i})");
            // 必须先建目标目录：空 zip（空文件夹）解压不会自己创建它
            Directory.CreateDirectory(dest);
            ZipFile.ExtractToDirectory(zipPath, dest);
            return dest;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>目录名里的非法字符换成下划线（文件夹名来自远端，不能直接当本机目录名用）</summary>
    public static string SanitizeFolderName(string name)
    {
        var chars = (name ?? "").Trim().ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (Path.GetInvalidFileNameChars().Contains(chars[i]) || chars[i] is '/' or '\\') chars[i] = '_';
        }
        // 结尾的点和空格在 Windows 目录名里也非法
        return new string(chars).TrimEnd('.', ' ').Trim();
    }
}
