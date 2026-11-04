using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Drawing.Imaging;
using System.Security;
using Agent.Common;
using Microsoft.Win32;

namespace Agent.Coordinator;

/// <summary>
/// 枚举已安装软件（策划 §2.1「软件列表上报」）：
/// 注册表 Uninstall 三项 + 内置系统应用（记事本/计算器等），解析出可执行文件路径，
/// 附带运行状态与图标（PNG base64）。
/// </summary>
public sealed class SoftwareEnumerator
{
    private readonly Logger _log;
    private readonly bool _withIcons;
    private readonly Dictionary<string, string?> _iconCache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly (string Name, string Exe)[] BuiltinApps =
    {
        ("记事本", @"C:\Windows\System32\notepad.exe"),
        ("计算器", @"C:\Windows\System32\calc.exe"),
        ("画图", @"C:\Windows\System32\mspaint.exe"),
        ("命令提示符", @"C:\Windows\System32\cmd.exe"),
        ("Windows 资源管理器", @"C:\Windows\explorer.exe"),
        ("写字板", @"C:\Program Files\Windows NT\Accessories\wordpad.exe"),
    };

    private static readonly string[] UninstallRoots =
    {
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
    };

    public SoftwareEnumerator(Logger log, bool withIcons = true)
    {
        _log = log;
        _withIcons = withIcons;
    }

    public List<SoftwareInfo> Enumerate()
    {
        var dict = new Dictionary<string, SoftwareInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var rootPath in UninstallRoots)
        {
            try
            {
                using var root = Registry.LocalMachine.OpenSubKey(rootPath);
                if (root == null) continue;
                foreach (var sub in root.GetSubKeyNames())
                {
                    try
                    {
                        using var k = root.OpenSubKey(sub);
                        if (k == null) continue;
                        AddEntry(dict, k);
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                _log.Debug($"读取 {rootPath} 失败：{ex.Message}");
            }
        }

        try
        {
            using var hkcu = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            if (hkcu != null)
                foreach (var sub in hkcu.GetSubKeyNames())
                {
                    using var k = hkcu.OpenSubKey(sub);
                    if (k != null) AddEntry(dict, k);
                }
        }
        catch { }

        foreach (var (name, exe) in BuiltinApps)
        {
            if (File.Exists(exe) && !dict.ContainsKey(name))
            {
                dict[name] = new SoftwareInfo { Name = name, ExePath = exe };
            }
        }

        // 运行状态
        // 先按完整路径匹配；对不上再按**可执行文件名**兜一层。
        // 为什么要兜：注册表里记的往往是启动器路径（例如微信记的是 Weixin.exe，
        // 实际跑起来可能是版本子目录里的那个，或者进程名与安装路径不一致）——
        // 结果就是"用户明明开着微信，列表里却显示没打开"（现场反馈）。
        var (byPath, byName) = GetRunningProcesses();
        foreach (var sw in dict.Values)
        {
            if (byPath.TryGetValue(sw.ExePath, out int pid))
            {
                sw.IsRunning = true;
                sw.Pid = pid;
                continue;
            }
            var file = Path.GetFileName(sw.ExePath);
            if (!string.IsNullOrEmpty(file) && byName.TryGetValue(file, out int pid2))
            {
                sw.IsRunning = true;
                sw.Pid = pid2;
            }
        }

        // 图标
        if (_withIcons)
        {
            foreach (var sw in dict.Values)
            {
                sw.Icon = GetIconBase64(sw.ExePath);
            }
        }

        var list = dict.Values
            .Where(s => !string.IsNullOrWhiteSpace(s.Name) && File.Exists(s.ExePath))
            .OrderByDescending(s => s.IsRunning)
            .ThenBy(s => s.Name, StringComparer.CurrentCulture)
            .ToList();
        _log.Info($"枚举到 {list.Count} 个可启动软件（{list.Count(s => s.IsRunning)} 个运行中）");
        return list;
    }

    private static void AddEntry(Dictionary<string, SoftwareInfo> dict, RegistryKey k)
    {
        var name = k.GetValue("DisplayName") as string;
        if (string.IsNullOrWhiteSpace(name)) return;
        if (Convert.ToInt32(k.GetValue("SystemComponent") ?? 0) == 1) return;
        if (k.GetValue("ParentKeyName") != null) return; // 更新包
        var releaseType = k.GetValue("ReleaseType") as string;
        if (!string.IsNullOrEmpty(releaseType) &&
            (releaseType.Contains("Update", StringComparison.OrdinalIgnoreCase) ||
             releaseType.Contains("Hotfix", StringComparison.OrdinalIgnoreCase))) return;
        if (k.GetValue("UninstallString") == null && k.GetValue("DisplayIcon") == null && k.GetValue("InstallLocation") == null)
            return;

        var exe = ResolveExePath(k);
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return;

        // 同名只保留第一个（优先 HKLM 64 位）
        if (dict.ContainsKey(name)) return;
        dict[name] = new SoftwareInfo { Name = name.Trim(), ExePath = exe };
    }

    private static string? ResolveExePath(RegistryKey k)
    {
        foreach (var key in new[] { "DisplayIcon", "InstallLocation" })
        {
            var v = k.GetValue(key) as string;
            if (string.IsNullOrWhiteSpace(v)) continue;
            var cleaned = v.Trim().Trim('"');
            int comma = cleaned.IndexOf(',');
            if (comma > 0) cleaned = cleaned[..comma];
            cleaned = cleaned.Trim('"');
            if (cleaned.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(cleaned))
                return cleaned;
            if (Directory.Exists(cleaned))
            {
                var hit = FindMainExe(cleaned, k.GetValue("DisplayName") as string);
                if (hit != null) return hit;
            }
        }
        return null;
    }

    private static string? FindMainExe(string dir, string? displayName)
    {
        try
        {
            var exes = Directory.EnumerateFiles(dir, "*.exe", SearchOption.TopDirectoryOnly)
                .Where(e => !e.Contains("unins", StringComparison.OrdinalIgnoreCase))
                .Where(e => !e.Contains("setup", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (exes.Count == 0) return null;
            if (exes.Count == 1) return exes[0];
            if (!string.IsNullOrEmpty(displayName))
            {
                var norm = new string(displayName.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
                var best = exes.FirstOrDefault(e =>
                {
                    var n = Path.GetFileNameWithoutExtension(e).ToLowerInvariant();
                    return norm.Contains(n) || n.Contains(norm);
                });
                if (best != null) return best;
            }
            return exes[0];
        }
        catch { return null; }
    }

    private static Dictionary<string, int> GetRunningProcessPaths()
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                var path = p.MainModule?.FileName;
                if (!string.IsNullOrEmpty(path) && !map.ContainsKey(path))
                    map[path] = p.Id;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
            {
                // 受保护进程读不到路径，忽略
            }
            finally { p.Dispose(); }
        }
        return map;
    }

    /// <summary>
    /// 当前运行中的进程：返回 (按完整路径 → pid, 按可执行文件名 → pid) 两张表。
    /// 有第二张是因为注册表里记的常是启动器路径，与实际运行的进程路径对不上
    /// （现场：微信明明开着，列表里却显示"未打开"）。
    /// </summary>
    /// <summary>给"实时状态监测"用的轻量入口：只枚举进程，不做注册表/图标那套慢活儿</summary>
    public static (Dictionary<string, int> ByPath, Dictionary<string, int> ByName) GetRunningStates()
        => GetRunningProcesses();

    private static (Dictionary<string, int> ByPath, Dictionary<string, int> ByName) GetRunningProcesses()
    {
        var byPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                string name = p.ProcessName;
                if (!string.IsNullOrEmpty(name) && !byName.ContainsKey(name))
                    byName[name] = p.Id;

                var path = p.MainModule?.FileName;
                if (!string.IsNullOrEmpty(path))
                {
                    if (!byPath.ContainsKey(path)) byPath[path] = p.Id;
                    var file = Path.GetFileName(path);
                    if (!string.IsNullOrEmpty(file) && !byName.ContainsKey(file))
                        byName[file] = p.Id;
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
            {
                // 受保护进程读不到路径，但进程名通常还能拿到
                try
                {
                    var name = p.ProcessName;
                    if (!string.IsNullOrEmpty(name) && !byName.ContainsKey(name)) byName[name] = p.Id;
                }
                catch { }
            }
            finally { p.Dispose(); }
        }
        return (byPath, byName);
    }

    public string? GetIconBase64(string exePath)
    {
        if (!_withIcons) return null;
        if (_iconCache.TryGetValue(exePath, out var cached)) return cached;
        string? result = null;
        try
        {
            using var icon = Icon.ExtractAssociatedIcon(exePath);
            if (icon != null)
            {
                using var bmp = icon.ToBitmap();
                using var ms = new MemoryStream();
                bmp.Save(ms, ImageFormat.Png);
                result = Convert.ToBase64String(ms.ToArray());
            }
        }
        catch (Exception ex) when (ex is SecurityException or FileNotFoundException or ArgumentException or ExternalException)
        {
            result = null;
        }
        catch { result = null; }
        _iconCache[exePath] = result;
        return result;
    }
}
