<#
.SYNOPSIS
  一键构建远程控制软件全部产物到 build\ 目录。

.DESCRIPTION
  - Agent.Common / Agent.Coordinator / Agent.Worker （C# .NET 8）
  - Viewer （C# WPF .NET 8）
  - Server （Go）
  - 单元测试
  - 拷贝 ffmpeg / drivers 到产物目录

.NOTES
  所有中间产物与包缓存都放在非系统盘（默认 E:\tools），不污染 C 盘。
#>
[CmdletBinding()]
param(
    [string]$RepoRoot = "",   # 留空 = 自动取脚本上级目录
    [string]$ToolsRoot = "E:\tools",
    [switch]$SkipTests,
    [switch]$FrameworkDependent
)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($RepoRoot)) { $RepoRoot = Split-Path -Parent $PSScriptRoot }
$env:DOTNET_ROOT = Join-Path $ToolsRoot "dotnet"
$env:PATH = "$env:DOTNET_ROOT;" + (Join-Path $ToolsRoot "go\bin") + ";" + $env:PATH
$env:NUGET_PACKAGES = Join-Path $ToolsRoot "nuget-packages"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"
$env:GOPATH = Join-Path $ToolsRoot "gopath"
$env:GOMODCACHE = Join-Path $ToolsRoot "gopath\pkg\mod"
$env:GOCACHE = Join-Path $ToolsRoot "gocache"
# 首次拉取 Go 依赖需要网络；本机走 Clash 代理（可按需改/去掉）
if (-not $env:HTTPS_PROXY) { $env:HTTPS_PROXY = "http://127.0.0.1:7897" }
if (-not $env:HTTP_PROXY) { $env:HTTP_PROXY = "http://127.0.0.1:7897" }

$dotnet = Join-Path $env:DOTNET_ROOT "dotnet.exe"
$go = Join-Path $ToolsRoot "go\bin\go.exe"
$build = Join-Path $RepoRoot "build"

function Step($msg) { Write-Host "`n=== $msg ===" -ForegroundColor Cyan }
function Fail($msg) { Write-Host "!! $msg" -ForegroundColor Red; exit 1 }

if (-not (Test-Path $dotnet)) { Fail "找不到 dotnet：$dotnet（请设置 -ToolsRoot）" }

Step "清理 build 目录"
New-Item -ItemType Directory -Force -Path $build | Out-Null
foreach ($d in @("agent", "viewer")) {
    $p = Join-Path $build $d
    if (Test-Path $p) {
        try { Remove-Item $p -Recurse -Force -ErrorAction Stop }
        catch { Write-Host "  警告：$d 目录有文件被占用，跳过清理（将由 publish 覆盖）" -ForegroundColor Yellow }
    }
}

$rtArgs = @()
if (-not $FrameworkDependent) { $rtArgs = @("-r", "win-x64", "--self-contained", "true") }
else { $rtArgs = @("-r", "win-x64", "--self-contained", "false") }

function Publish($proj, $outDir) {
    Step "发布 $proj → $outDir"
    & $dotnet publish $proj -c Release @rtArgs -o $outDir --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { Fail "发布失败：$proj" }
}

Publish (Join-Path $RepoRoot "src\Agent\Agent.Coordinator\Agent.Coordinator.csproj") (Join-Path $build "agent")
Publish (Join-Path $RepoRoot "src\Agent\Agent.Worker\Agent.Worker.csproj")           (Join-Path $build "agent")
Publish (Join-Path $RepoRoot "src\Viewer\Viewer.csproj")                             (Join-Path $build "viewer")
Publish (Join-Path $RepoRoot "src\Tests\E2E\E2E.csproj")                            (Join-Path $build "tests")
Publish (Join-Path $RepoRoot "src\Tools\LicenseKeygen\LicenseKeygen.csproj")        (Join-Path $build "tools")

# 主控端预置值：打包时把服务器地址与令牌写进 build\viewer\viewer-defaults.txt，
# 分发出去的主控端就是"双击即用"的（令牌不该让客户去改配置文件）。
# 要出分发包请跑 scripts\build-viewer-package.py（顺带打出 build\主控端安装包.zip）。
Step "写入主控端预置值（viewer-defaults.txt）"
$viewerDefaults = Join-Path $build "viewer\viewer-defaults.txt"
$srcCfg = "E:\RemoteControl\viewer\config\viewer.json"
if (Test-Path $srcCfg) {
    try {
        $vj = Get-Content $srcCfg -Raw | ConvertFrom-Json
        if ($vj.ServerUrl -and $vj.AgentToken) {
            Set-Content -Path $viewerDefaults -Encoding UTF8 -Value @(
                "# 主控端预置值（打包时写入）。config\viewer.json 里为空的项会用这里的值补齐。",
                "ServerUrl=$($vj.ServerUrl)",
                "Token=$($vj.AgentToken)"
            )
            Write-Host "  已写入（令牌长度 $($vj.AgentToken.Length)）" -ForegroundColor Green
        } else {
            Write-Host "  警告：$srcCfg 里缺 ServerUrl/AgentToken，跳过" -ForegroundColor Yellow
        }
    } catch { Write-Host "  警告：读 $srcCfg 失败：$_" -ForegroundColor Yellow }
} else {
    Write-Host "  警告：找不到 $srcCfg，跳过（分发前请跑 scripts\build-viewer-package.py）" -ForegroundColor Yellow
}

Step "构建 Go 中继服务器"
New-Item -ItemType Directory -Force -Path (Join-Path $build "server") | Out-Null
Push-Location (Join-Path $RepoRoot "src\Server")
try {
    & $go build -trimpath -ldflags "-s -w" -o (Join-Path $build "server\rcserver.exe") .
    if ($LASTEXITCODE -ne 0) { Fail "Go 构建失败" }
    # 交叉编译 Linux / arm64 版（部署到香港服务器用）
    foreach ($target in @(@("linux", "amd64"), @("linux", "arm64"), @("windows", "amd64"))) {
        $env:GOOS = $target[0]
        $env:GOARCH = $target[1]
        $ext = if ($target[0] -eq "windows") { ".exe" } else { "" }
        $name = "rcserver-" + $target[0] + "-" + $target[1] + $ext
        & $go build -trimpath -ldflags "-s -w" -o (Join-Path $build ("server\" + $name)) .
        if ($LASTEXITCODE -ne 0) { Fail ("Go 交叉编译失败：" + $name) }
        Write-Host ("  " + $name)
    }
    Remove-Item Env:GOOS -ErrorAction SilentlyContinue
    Remove-Item Env:GOARCH -ErrorAction SilentlyContinue
} finally { Pop-Location }

if (-not $SkipTests) {
    Step "运行单元测试"
    & $dotnet test (Join-Path $RepoRoot "src\Tests\Agent.Common.Tests\Agent.Common.Tests.csproj") -c Release --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { Fail "单元测试失败" }
}

Step "拷贝 ffmpeg / drivers"
$ff = Join-Path $RepoRoot "third_party\ffmpeg\ffmpeg.exe"
if (Test-Path $ff) {
    foreach ($target in @("agent", "viewer")) {
        $d = Join-Path $build "$target\third_party\ffmpeg"
        New-Item -ItemType Directory -Force -Path $d | Out-Null
        Copy-Item $ff $d -Force
    }
    Write-Host "ffmpeg 已拷贝"
} else {
    Write-Host "警告：未找到 $ff，运行时需要自行提供 ffmpeg.exe" -ForegroundColor Yellow
}

# 文档：Viewer 的「使用说明」窗口会读 docs\使用说明.md
foreach ($target in @("viewer", "agent")) {
    $d = Join-Path $build "$target\docs"
    New-Item -ItemType Directory -Force -Path $d | Out-Null
    if (Test-Path $d) { Remove-Item $d -Recurse -Force -ErrorAction SilentlyContinue }
    New-Item -ItemType Directory -Force -Path $d | Out-Null
    # 只发客户该看的：其余 docs\*.md 是内部资料 —— 部署-香港服务器.md 写了管理令牌放哪、
    # 运维-授权与防绕过.md 讲了授权机制、验收报告.md 含测试证据与历史口令、架构/参考项目分析含内部实现。
    # 以前这里拷的是 docs\*.md 全量，等于把内部资料一起发给客户。
    Copy-Item (Join-Path $RepoRoot "docs\使用说明.md") $d -Force -ErrorAction SilentlyContinue
}
Write-Host "docs 已拷贝到 buildiewer\docs 与 buildgent\docs"

$drv = Join-Path $RepoRoot "drivers"
if (Test-Path $drv) {
    Copy-Item $drv (Join-Path $build "agent\drivers") -Recurse -Force
    Write-Host "drivers 已拷贝到 build\agent\drivers"
}

Step "构建完成"
Get-ChildItem $build -Directory | ForEach-Object {
    $size = (Get-ChildItem $_.FullName -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB
    Write-Host ("{0,-10} {1,8:F1} MB" -f $_.Name, $size)
}
