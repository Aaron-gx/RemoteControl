<#
.SYNOPSIS
  快速重部署被控端（迭代用）：重新发布 Agent 到安装目录，保留 config/logs。

.DESCRIPTION
  与 install-agent.ps1 的区别：不动用户/驱动/计划任务，只更新程序文件，
  用于改代码后快速验证。会话保持不动（Worker 由协调器看护自动重启）。
#>
[CmdletBinding()]
param(
    [string]$RepoRoot = "",
    [string]$InstallDir = "E:\RemoteControl\agent",
    [string]$ToolsRoot = "E:\tools"
)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($RepoRoot)) { $RepoRoot = Split-Path -Parent $PSScriptRoot }

$env:DOTNET_ROOT = Join-Path $ToolsRoot "dotnet"
$env:PATH = "$env:DOTNET_ROOT;" + $env:PATH
$env:NUGET_PACKAGES = Join-Path $ToolsRoot "nuget-packages"

$dotnet = Join-Path $env:DOTNET_ROOT "dotnet.exe"
$out = Join-Path $RepoRoot "build\agent"

Write-Host "1) 发布 Agent（Coordinator，内置采集引擎；Agent.Worker 是它的类库依赖，随包一起出）..." -ForegroundColor Cyan
$full = Join-Path $RepoRoot "src\Agent\Agent.Coordinator\Agent.Coordinator.csproj"
& $dotnet publish $full -c Release -r win-x64 --self-contained true -o $out --nologo -v minimal
if ($LASTEXITCODE -ne 0) { Write-Host "发布失败：$full" -ForegroundColor Red; exit 1 }

Write-Host "2) 停止旧进程（协调器持有 Agent.*.dll，必须先停，否则 robocopy 会静默跳过被锁文件；Agent.Worker 是旧版残留进程，顺手清掉）..." -ForegroundColor Cyan
Get-Process -Name 'Agent.Coordinator', 'Agent.Worker', 'ffmpeg' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 3

Write-Host "3) 同步文件到 $InstallDir（保留 config/logs）..." -ForegroundColor Cyan
robocopy $out $InstallDir /MIR `
    /XD (Join-Path $out "config") (Join-Path $out "logs") `
        (Join-Path $InstallDir "config") (Join-Path $InstallDir "logs") `
        (Join-Path $InstallDir "third_party") (Join-Path $InstallDir "drivers") `
        (Join-Path $InstallDir "docs") `
    /NFL /NDL /NJH /NJS /NP | Out-Null
# 注意：third_party（ffmpeg）与 drivers 不在build\agent 里（由 build-all.ps1 额外拷贝），
# 必须从 /MIR 的删除范围里排除，否则每次部署都会把它们从运行目录删掉 → 被控端找不到 ffmpeg 直接不起画面。

Write-Host "4) 重新启动协调器（采集引擎是它内部的线程，看 coordinator-*.log 里"采集引擎启动"即为就绪）..." -ForegroundColor Cyan
Start-Process -FilePath (Join-Path $InstallDir 'Agent.Coordinator.exe') -WorkingDirectory $InstallDir
for ($i = 0; $i -lt 24; $i++) {
    Start-Sleep -Seconds 5
    $c = Get-Process -Name 'Agent.Coordinator' -ErrorAction SilentlyContinue
    if ($c) {
        Write-Host ("   协调器已运行 pid=" + $c.Id) -ForegroundColor Green
        exit 0
    }
    Write-Host "   等待中（$($i*5+5)s）..."
}
Write-Host "警告：协调器尚未启动，请查看协调器日志" -ForegroundColor Yellow
exit 0
