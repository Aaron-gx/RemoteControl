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
    /// 暂存盘选择见 <see cref="PickStageRoot"/>（2026-10-09 现场：客户机 C 盘满，
    /// 写死 %TEMP% 直接"磁盘空间不足"——那台机器连普通软件都打不开了）。
    /// </summary>
    public static string? PackFolder(string dir, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        {
            error = $"文件夹不存在：{dir}";
            return null;
        }
        var root = PickStageRoot(dir, out var why, out var note);
        if (root == null)
        {
            error = why ?? "没有可用的打包暂存盘";
            return null;
        }
        try
        {
            Directory.CreateDirectory(root);
            PruneOldStages(root);   // 顺手清掉历史残留的半成品（旧版本失败时会漏）
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
                try { Directory.Delete(root, true); } catch { }
                error = $"文件夹里 {skipped} 个文件全部被其他程序占用（如 Telegram/微信正在运行），无法打包";
                return null;
            }

            var size = new FileInfo(zipPath).Length;
            if (size > MaxZipBytes)
            {
                try { Directory.Delete(root, true); } catch { }
                error = $"文件夹压缩后 {size / 1024.0 / 1024.0:F0}MB，超过单次传输上限 {MaxZipBytes / 1024 / 1024}MB";
                return null;
            }
            if (skipped > 0)
                error = $"已跳过 {skipped} 个被占用的文件（打包 {packed} 个）";  // 非致命：放在 error 里只是传递信息
            if (!string.IsNullOrEmpty(note)) error = string.IsNullOrEmpty(error) ? note : $"{error}；{note}";
            return zipPath;
        }
        catch (Exception ex)
        {
            // 打包中途失败：清掉半成品，否则每失败一次就往暂存盘漏一个残 zip（越积越满）
            try { Directory.Delete(root, true); } catch { }
            error = $"文件夹打包失败（{ex.GetType().Name}: {ex.Message}）";
            return null;
        }
    }

    /// <summary>
    /// 挑打包暂存目录（按优先级返回第一个剩余空间够的）：
    ///   1. 被控端安装目录同盘（装在 D/E 就天然避开 C；就是"%产品目录%\temp"）
    ///   2. 源文件夹所在盘的根下 RC-FolderTransfer（数据就在那个盘，装得下原件基本就装得下压缩包）
    ///   3. %TEMP%（=C 盘，最后手段）
    /// 一个都不够就返回 null 并在 why 里列清各盘剩余空间，让主控端能看懂原因。
    /// note 带回非致命信息（比如清理了多少历史残留）。
    /// </summary>
    private static string? PickStageRoot(string sourceDir, out string? why, out string? note)
    {
        why = null; note = null;
        long need;
        try
        {
            need = new DirectoryInfo(sourceDir).EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(f => { try { return f.Length; } catch { return 0; } });
        }
        catch { need = 0; }
        need = Math.Max(need, 50L * 1024 * 1024);   // 下限 50MB：压缩过程本身要些余量

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<(string root, long free)>();
        void Add(string? dirRoot)
        {
            if (string.IsNullOrWhiteSpace(dirRoot)) return;
            var drive = Path.GetPathRoot(Path.GetFullPath(dirRoot));
            if (string.IsNullOrEmpty(drive) || !seen.Add(drive)) return;
            try
            {
                var d = new DriveInfo(drive);
                if (!d.IsReady || d.DriveType is DriveType.NoRootDirectory or DriveType.CDRom) return;
                var free = d.AvailableFreeSpace;
                string root;
                if (string.Equals(drive, Path.GetPathRoot(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
                    root = Path.Combine(Path.GetTempPath(), "RC-FolderTransfer");          // C 盘：留在 Temp 里
                else if (string.Equals(drive, Path.GetPathRoot(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase))
                    root = Path.Combine(AppContext.BaseDirectory, "temp", "RC-FolderTransfer");  // 安装盘：产品目录下
                else
                    root = Path.Combine(drive, "RC-FolderTransfer");                        // 源数据盘：根下固定目录
                candidates.Add((root, free));
            }
            catch { }
        }
        Add(AppContext.BaseDirectory);   // 1) 安装盘
        Add(sourceDir);                  // 2) 源数据盘
        Add(Path.GetTempPath());         // 3) %TEMP%

        // 不管最后用哪个：把 %TEMP% 里我们自己的历史残留清一清（旧版本失败漏的半成品，救 C 盘）
        try
        {
            var tempRc = Path.Combine(Path.GetTempPath(), "RC-FolderTransfer");
            if (Directory.Exists(tempRc))
            {
                var killed = 0;
                foreach (var d in Directory.EnumerateDirectories(tempRc))
                {
                    try { if (Directory.GetLastWriteTime(d) < DateTime.Now - TimeSpan.FromDays(1)) { Directory.Delete(d, true); killed++; } }
                    catch { }
                }
                if (killed > 0) note = $"已清理 {killed} 个历史打包残留（{tempRc}）";
            }
        }
        catch { }

        var ok = candidates.FirstOrDefault(c => c.free >= need);
        if (ok.root != null) return Path.Combine(ok.root, Guid.NewGuid().ToString("N"));

        why = "磁盘空间不足：候选暂存盘剩余空间为 " +
              string.Join("、", candidates.Select(c => $"{Path.GetPathRoot(c.root)} {c.free / 1024.0 / 1024:F0}MB")) +
              $"，本次打包约需 {need / 1024.0 / 1024:F0}MB";
        return null;
    }

    /// <summary>清掉同一暂存目录里超过 1 天的旧 stage（上一次传输中断留下的）</summary>
    private static void PruneOldStages(string newRoot)
    {
        try
        {
            var parent = Path.GetDirectoryName(newRoot);
            if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent)) return;
            foreach (var d in Directory.EnumerateDirectories(parent))
            {
                try { if (Directory.GetLastWriteTime(d) < DateTime.Now - TimeSpan.FromDays(1)) Directory.Delete(d, true); }
                catch { }
            }
        }
        catch { }
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
