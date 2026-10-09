using System.Diagnostics;
using System.Text;
using System.Security.Cryptography;
using Agent.Common;
using Agent.Worker;

namespace Agent.Coordinator;

/// <summary>
/// 被控端自动更新（2026-10 新增）。流程（每一环失败都停在原地、绝不碰现有程序）：
///   查清单(GET /api/update/manifest) → 验签(RCU1, 与授权码同一把厂商 ECDSA 公钥)
///   → 比版本 → 下载 zip → sha256/大小比对 → 等空闲窗口(无主控端连着)
///   → 生成应用脚本(PowerShell)并分离启动：停旧 → 备份 → 解压 → 启新
///   → 新版本 45 秒内写"报平安"标记；超时未写 → 脚本自动回滚备份并启动旧版。
///
/// 【安全边界】更新码与更新包**双重校验**且互不信任：地址不在签名里（服务器换成别的地址也没用，
/// 包哈希对不上就不应用）；哈希在签名里（改哈希=改签名=验不过）。私钥只在厂商上位机
/// （LicenseKeygen），拿走中继也伪造不了更新 —— 与授权体系同一威胁模型。
/// </summary>
public sealed class Updater : IDisposable
{
    private readonly AgentConfig _cfg;
    private readonly Logger _log;
    private readonly HttpClient _http;
    private readonly Func<bool> _hasViewer;
    private readonly CancellationTokenSource _cts = new();

    /// <summary>状态一句话（供状态上报/日志排查）：无更新 / 已最新 / 下载中 / 待应用 / 已应用 / 失败:原因</summary>
    private volatile string _state = "未检查";
    public string State => _state;

    /// <summary>已就绪待应用的版本（无则空）</summary>
    private volatile string _stagedVersion = "";
    public string StagedVersion => _stagedVersion;

    private string WorkDir => Path.Combine(Path.GetTempPath(), "RC-Update");
    private string MarkerPath => Path.Combine(WorkDir, "ok.marker");

    public Updater(AgentConfig cfg, Logger log, Func<bool> hasViewer)
    {
        _cfg = cfg;
        _log = log;
        _hasViewer = hasViewer;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
    }

    public void Start()
    {
        if (!_cfg.AutoUpdateEnabled)
        {
            _state = "已关闭（AutoUpdateEnabled=false）";
            _log.Info("自动更新已按配置关闭");
            return;
        }
        _ = Task.Run(async () =>
        {
            // 启动后先等 30 秒（别抢启动期的网络/磁盘），以后每 6 小时查一次
            // 分钟优先；旧配置里的 Hours 仍认（>0 时用小时值）。下限 5 分钟。
            var interval = _cfg.UpdateCheckIntervalMinutes > 0
                ? TimeSpan.FromMinutes(Math.Max(5, _cfg.UpdateCheckIntervalMinutes))
                : TimeSpan.FromHours(Math.Max(1, _cfg.UpdateCheckIntervalHours));
            while (!_cts.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(30), _cts.Token); } catch { return; }
                await CheckAndStageAsync();
                await ApplyIfIdleAsync();
                // 有暂存包时切 30 秒快循环：否则"空闲 5 分钟就应用"要等下一个 6 小时大周期才被评估
                //（真机联调踩到：状态停在"空闲观察中"不动）
                var wait = string.IsNullOrEmpty(_stagedVersion) ? interval : TimeSpan.FromSeconds(30);
                try { await Task.Delay(wait, _cts.Token); } catch { return; }
            }
        });
    }

    /// <summary>新版本启动后调用：进程活了 45 秒即写"报平安"标记（应用脚本据此判定成功/回滚）。
    /// 不等中继连接 —— 网络抖动不该导致好版本被回滚；45 秒不崩本身就是"能跑"的证明。</summary>
    public void ReportAlive()
    {
        try
        {
            Directory.CreateDirectory(WorkDir);
            File.WriteAllText(MarkerPath, $"alive since {DateTime.Now:yyyy-MM-dd HH:mm:ss}, version {WorkerContext.Version}");
            _log.Info($"更新自检通过：已写报平安标记（运行 {Process.GetCurrentProcess().StartTime:HH:mm:ss} 起 45 秒未退出）");
        }
        catch (Exception ex) { _log.Debug($"写更新标记失败（不影响运行）：{ex.Message}"); }
    }

    // ---------------------------------------------------------------- 检查 + 落盘

    private async Task CheckAndStageAsync()
    {
        try
        {
            _state = "检查中";
            var baseUri = _cfg.ResolveFileServerUrl();
            var q = string.IsNullOrWhiteSpace(_cfg.AgentToken) ? "" : $"?token={Uri.EscapeDataString(_cfg.AgentToken)}";
            var url = $"{baseUri}/api/update/manifest{q}";
            using var resp = await _http.GetAsync(url);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _state = "无更新（服务器未配置清单）";
                return;                     // 服务器没上更新功能：安静等待，绝不算错误
            }
            if (!resp.IsSuccessStatusCode)
            {
                _state = $"清单拉取失败 HTTP {(int)resp.StatusCode}";
                _log.Debug($"更新清单拉取失败 HTTP {(int)resp.StatusCode}");
                return;
            }
            var body = await resp.Content.ReadAsStringAsync();
            var manifest = System.Text.Json.JsonSerializer.Deserialize<UpdateManifest>(body,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            var entry = manifest?.Agent;
            if (entry == null || string.IsNullOrEmpty(entry.Code))
            {
                _state = "无更新";
                return;
            }

            // 1) 验签 + 解析（失败=清单不可信，原地不动）
            var v = UpdateCodec.Verify(entry.Code);
            if (!v.Ok)
            {
                _state = "失败:清单验签未通过";
                _log.Warn($"更新清单验签未通过，忽略本次更新：{v.Error}");
                return;
            }
            var payload = v.Payload!;
            if (!string.Equals(payload.Kind, "agent", StringComparison.OrdinalIgnoreCase))
            {
                _state = "无更新（清单目标不是被控端）";
                return;
            }

            // 2) 版本比较（只升不降）
            if (UpdateCodec.CompareVersions(payload.Ver, WorkerContext.Version) <= 0)
            {
                _state = $"已最新（{WorkerContext.Version}）";
                return;
            }

            // 3) 下载 + 哈希比对（地址不在签名里，但包内容必须与签名里的哈希一致）
            _state = $"下载中 v{payload.Ver}";
            Directory.CreateDirectory(WorkDir);
            var zipPath = Path.Combine(WorkDir, $"agent-{payload.Ver}.zip");
            // 已有同版本包且哈希对得上就不重复下载：省流量，也避免覆盖正在被应用脚本读取的 zip
            //（2026-10-09 现场：上一轮应用脚本还没退干净，File.Create 直接 IOException，状态卡在"失败"）
            var needDownload = true;
            try
            {
                if (File.Exists(zipPath))
                {
                    using var fsOld = File.OpenRead(zipPath);
                    var oldHex = Convert.ToHexString(await SHA256.HashDataAsync(fsOld)).ToLowerInvariant();
                    if (string.Equals(oldHex, payload.Sha256.ToLowerInvariant(), StringComparison.Ordinal))
                        needDownload = false;
                }
            }
            catch { /* 读不了就当没有，重新下载 */ }
            if (needDownload)
            {
                // 先写 .tmp 再原子改名：目标被占用（应用脚本/杀毒）时本轮失败下轮重试，不再一击致命
                var tmp = zipPath + ".tmp";
                using (var fs = File.Create(tmp))
                {
                    using var dl = await _http.GetAsync($"{baseUri.TrimEnd('/')}/{entry.PackageUrl.TrimStart('/')}" + q);
                    if (!dl.IsSuccessStatusCode)
                    {
                        _state = $"失败:下载 HTTP {(int)dl.StatusCode}";
                        _log.Warn($"更新包下载失败 HTTP {(int)dl.StatusCode}");
                        return;
                    }
                    await dl.Content.CopyToAsync(fs);
                }
                File.Move(tmp, zipPath, overwrite: true);
            }
            string actualHex;
            using (var fsVerify = File.OpenRead(zipPath))
            {
                actualHex = Convert.ToHexString(await SHA256.HashDataAsync(fsVerify)).ToLowerInvariant();
            }
            if (!string.Equals(actualHex, payload.Sha256.ToLowerInvariant(), StringComparison.Ordinal))
            {
                _state = "失败:哈希不一致";
                _log.Error($"更新包哈希不一致：期望 {payload.Sha256[..12]}… 实得 {actualHex[..12]}…（传输损坏或被篡改，已丢弃）");
                try { File.Delete(zipPath); } catch { }
                return;
            }
            if (payload.Size > 0 && new FileInfo(zipPath).Length != payload.Size)
            {
                _state = "失败:大小不一致";
                _log.Error($"更新包大小不一致：期望 {payload.Size} 实得 {new FileInfo(zipPath).Length}");
                try { File.Delete(zipPath); } catch { }
                return;
            }

            _stagedVersion = payload.Ver;
            _state = $"待应用 v{payload.Ver}（等空闲）";
            _log.Info($"更新包已就绪并验签通过：v{WorkerContext.Version} → v{payload.Ver}（{payload.Notes}）。" +
                      "将在无主控端连接的空闲窗口自动应用。");
        }
        catch (Exception ex)
        {
            _state = $"失败:{ex.GetType().Name}";
            _log.Debug($"更新检查异常（下轮重试）：{ex.Message}");
        }
    }

    // ---------------------------------------------------------------- 空闲窗口 + 应用

    private DateTime _idleSince = DateTime.MinValue;

    /// <summary>有就绪包时：连续 5 分钟无主控端连接 → 应用（有人正在远程干活时绝不重启它）。</summary>
    private async Task ApplyIfIdleAsync()
    {
        if (string.IsNullOrEmpty(_stagedVersion)) return;
        try
        {
            if (_hasViewer())
            {
                _idleSince = DateTime.MinValue;
                _state = $"待应用 v{_stagedVersion}（有主控端在线，等空闲）";
                return;
            }
            if (_idleSince == DateTime.MinValue)
            {
                _idleSince = DateTime.UtcNow;
                _state = $"待应用 v{_stagedVersion}（空闲观察中）";
                return;
            }
            if (DateTime.UtcNow - _idleSince < TimeSpan.FromMinutes(5)) return;

            _state = $"应用中 v{_stagedVersion}";
            ApplyStaged(_stagedVersion);
            // 应用脚本会结束本进程；走到这里说明脚本没启动成功
            _state = "失败:应用脚本未启动";
            _log.Error("更新应用脚本启动失败（本轮放弃，下轮重试）");
        }
        catch (Exception ex)
        {
            _state = $"失败:应用异常";
            _log.Warn($"应用更新异常：{ex.Message}");
        }
        await Task.CompletedTask;
    }

    /// <summary>
    /// 生成并**分离启动**应用脚本（独立 PowerShell 子进程：父进程（本程序）死后继续执行）。
    /// 步骤：等 3s → 停旧 → 备份 Agent.*.dll → 解压新包 → 启新 → 等 90s"报平安"标记 →
    /// 没等到就回滚备份并启动旧版。这套"停-盖-起-回滚"在真机上已验证过 6 轮（2026-10-05 联调）。
    /// </summary>
    private void ApplyStaged(string version)
    {
        var dir = AppContext.BaseDirectory.TrimEnd('\\');
        var zipPath = Path.Combine(WorkDir, $"agent-{version}.zip");
        var bak = Path.Combine(WorkDir, "backup");
        // 报平安标记先清掉（新版本启动 45 秒后重写；90 秒内没写=回滚）
        try { if (File.Exists(MarkerPath)) File.Delete(MarkerPath); } catch { }

        var ps = new StringBuilder();
        ps.Append("$ErrorActionPreference='Continue';");
        ps.Append($"$dir='{psEsc(dir)}';$bak='{psEsc(bak)}';$zip='{psEsc(zipPath)}';$ok='{psEsc(MarkerPath)}';");
        ps.Append("Start-Sleep 3;" +                          // 给调用方时间退出/日志落盘
                  "Stop-Process -Name Agent.Coordinator -Force -ErrorAction SilentlyContinue;" +
                  "Wait-Process -Name Agent.Coordinator -Timeout 15 -ErrorAction SilentlyContinue;" +  // 等进程死透再动文件（2026-10-09：2秒不够，SDK DLL被占→IOException）
                  "Start-Sleep 2;" +
                  "New-Item -ItemType Directory -Force -Path $bak | Out-Null;" +
                  "Remove-Item \"$bak\\*\" -Force -ErrorAction SilentlyContinue;" +
                  "Copy-Item \"$dir\\Agent.*.dll\" $bak -Force;" +      // 备份：业务 DLL
                  // SDK 投影与 WinRT 运行时（1.5.9 起热更包也带它们——回滚必须一起还原，
                  // 否则"旧业务 DLL + 新 SDK DLL"版本对不上，WGC 直接加载失败）
                  "Copy-Item \"$dir\\Microsoft.Windows.SDK.NET.dll\",\"$dir\\WinRT.Runtime.dll\" $bak -Force -ErrorAction SilentlyContinue;" +
                  // 解压重试 ×3：文件偶被杀毒/残留句柄占用，一次失败就回滚太脆
                  "$ok2=$false;for($i=0;$i -lt 3 -and -not $ok2;$i++){try{Expand-Archive $zip $dir -Force -ErrorAction Stop;$ok2=$true}catch{Start-Sleep 3}};" +
                  "if(-not $ok2){Copy-Item \"$bak\\*\" $dir -Force;Start-Process (Join-Path $dir 'Agent.Coordinator.exe') -WorkingDirectory $dir;exit 1};" +
                  "Start-Process (Join-Path $dir 'Agent.Coordinator.exe') -WorkingDirectory $dir;" +
                  "$deadline=(Get-Date).AddSeconds(90);" +
                  "while((Get-Date) -lt $deadline -and -not (Test-Path $ok)){Start-Sleep 3};" +
                  "if(Test-Path $ok){exit 0};" +
                  "Copy-Item \"$bak\\*\" $dir -Force;" +                 // 90 秒没报平安：回滚（含 SDK DLL）
                  "Start-Process (Join-Path $dir 'Agent.Coordinator.exe') -WorkingDirectory $dir;");
        var scriptPath = Path.Combine(WorkDir, "apply.ps1");
        File.WriteAllText(scriptPath, ps.ToString(), System.Text.Encoding.UTF8);

        var psi = new ProcessStartInfo("powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"")
        {
            UseShellExecute = true,          // 分离：脚本独立于本进程存活（这是整个方案的关键之一）
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = WorkDir,
        };
        _log.Info($"已启动更新应用脚本（v{version}）：停旧 → 备份 → 覆盖 → 启新 → 90 秒内未报平安自动回滚");
        Process.Start(psi);
    }

    private static string psEsc(string s) => s.Replace("'", "''");

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        _http.Dispose();
        _cts.Dispose();
    }
}
