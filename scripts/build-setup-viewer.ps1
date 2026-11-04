<#
.SYNOPSIS
  打包主控端安装程序（Inno Setup 正式安装程序）。

.DESCRIPTION
  产物：build\setup-viewer\虚拟服务器客户端-安装程序.exe —— 与被控端同一套形态的标准 Windows 安装程序：
    · 标准安装向导（欢迎 / 安装位置 / 附加任务 / 连接设置 / 安装 / 完成）
    · 装进 Program Files，注册到「应用和功能」，带卸载器与开始菜单入口
    · 支持 /SILENT、/VERYSILENT 等标准无人值守参数
    · **服务器地址与连接口令内嵌在安装程序里**（只拷这一个 exe 即装即用）

  口令与服务地址默认取本机已部署的主控端配置（E:\RemoteControl\viewer\config\viewer.json），
  也可以用 -ServerUrl / -Token 显式指定。

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\build-setup-viewer.ps1
  powershell -ExecutionPolicy Bypass -File scripts\build-setup-viewer.ps1 -SkipViewerBuild -Token xxxxxx
#>
[CmdletBinding()]
param(
    [string]$RepoRoot = "",
    [string]$ToolsRoot = "E:\tools",
    [string]$ServerUrl = "",       # 形如 IP:端口（不带 ws:// 与 /ws）；空 = 从 config\relay-token.txt 的 relay= 行读
    [string]$Token = "",           # 预置连接口令
    [switch]$SkipViewerBuild       # 跳过 dotnet publish（用现有 build\viewer）
)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($RepoRoot)) { $RepoRoot = Split-Path -Parent $PSScriptRoot }

$env:DOTNET_ROOT = Join-Path $ToolsRoot "dotnet"
$env:PATH = "$env:DOTNET_ROOT;" + $env:PATH
$env:NUGET_PACKAGES = Join-Path $ToolsRoot "nuget-packages"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"
$dotnet = Join-Path $env:DOTNET_ROOT "dotnet.exe"
$iscc = Join-Path $ToolsRoot "innosetup\ISCC.exe"

$viewerDir = Join-Path $RepoRoot "build\viewer"
$out = Join-Path $RepoRoot "build\setup-viewer"
New-Item -ItemType Directory -Force -Path $out | Out-Null

# ---------------------------------------------------------------- 1. 取预置值
# 默认沿用本机已部署的主控端配置（那是运维填好的、能连上的那一份）
$cfgPath = "E:\RemoteControl\viewer\config\viewer.json"
if ((-not $ServerUrl -or -not $Token) -and (Test-Path -LiteralPath $cfgPath)) {
    try {
        $cfg = Get-Content $cfgPath -Raw | ConvertFrom-Json
        if (-not $ServerUrl -and $cfg.ServerUrl) {
            # ws://ip:port/ws → ip:port（向导里给用户看的是这个形态，与被控端一致）
            $ServerUrl = ($cfg.ServerUrl -replace '^wss?://', '' -replace '/ws$', '').TrimEnd('/')
        }
        if (-not $Token -and $cfg.AgentToken) { $Token = $cfg.AgentToken }
        Write-Host "预置值来源：$cfgPath" -ForegroundColor Green
    } catch { Write-Host "读取 $cfgPath 失败（继续用命令行参数/默认值）：$_" -ForegroundColor Yellow }
}
if (-not $ServerUrl) {
    # 中继地址从 config\relay-token.txt 读（relay= 行；gitignore 已排除，绝不入版本库）
    $rcRelayFile = Join-Path $RepoRoot "config\relay-token.txt"
    if (Test-Path -LiteralPath $rcRelayFile) {
        foreach ($line in Get-Content -LiteralPath $rcRelayFile) {
            if ($line -match '^\s*relay\s*=\s*(.+?)\s*$') { $ServerUrl = $Matches[1] }
        }
    }
}
# 口令兜底：上次打包用的 setup-defaults.ini（安装程序同目录预置文件，仓库 build 目录里留了一份）
if (-not $Token) {
    $iniPath = Join-Path $out "setup-defaults.ini"
    if (Test-Path -LiteralPath $iniPath) {
        $iniToken = (Get-Content $iniPath | Where-Object { $_ -match '^\s*token\s*=' }) -replace '^\s*token\s*=\s*', ''
        if ($iniToken) {
            $Token = $iniToken.Trim()
            Write-Host "口令兜底来源：$iniPath" -ForegroundColor Green
        }
    }
}
# 【2026-10-10 客户装完连不上的事故】口令为空绝不能出包：生产中继开着鉴权，
# 空口令的主控端刷新列表就是 401，客户那边表现为"软件用不了"，而打包的人本机
# 部署着带口令的旧主控端测不出来。这里直接失败，逼着打包时把口令带上。
if (-not $Token) {
    throw "没有可用的连接口令：E:\RemoteControl\viewer\config\viewer.json 不存在且未传 -Token。生产中继启用鉴权时空口令包装出去客户必连不上 —— 用 -Token <口令> 重新打包，或把 token= 写进 build\setup-viewer\setup-defaults.ini"
}
Write-Host "预置服务器：$ServerUrl    口令长度：$($Token.Length)"

# ---------------------------------------------------------------- 2. 发布主控端
if (-not $SkipViewerBuild) {
    Write-Host "`n1) 发布主控端 → $viewerDir …" -ForegroundColor Cyan
    & $dotnet publish (Join-Path $RepoRoot "src\Viewer\Viewer.csproj") -c Release -r win-x64 `
        --self-contained true -o $viewerDir --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw "发布主控端失败" }
} else {
    Write-Host "`n1) 跳过发布（-SkipViewerBuild），使用现有 $viewerDir" -ForegroundColor Cyan
}
if (-not (Test-Path -LiteralPath (Join-Path $viewerDir "Viewer.exe"))) { throw "缺少 $viewerDir\Viewer.exe" }

# ffmpeg：解码远程画面要用（主控端会拉起它）
$ff = Join-Path $RepoRoot "third_party\ffmpeg\ffmpeg.exe"
if (Test-Path -LiteralPath $ff) {
    $ffDst = Join-Path $viewerDir "third_party\ffmpeg"
    New-Item -ItemType Directory -Force -Path $ffDst | Out-Null
    robocopy (Split-Path $ff -Parent) $ffDst "ffmpeg.exe" /NFL /NDL /NJH /NJS /NP | Out-Null
    Write-Host "   ffmpeg 已就位" -ForegroundColor Green
} else {
    Write-Host "   警告：找不到 $ff，主控端将无法解码画面" -ForegroundColor Yellow
}

# 使用说明（主控端「使用说明」按钮读的就是它）
$docs = Join-Path $RepoRoot "docs\使用说明.md"
if (Test-Path -LiteralPath $docs) {
    $docsDst = Join-Path $viewerDir "docs"
    New-Item -ItemType Directory -Force -Path $docsDst | Out-Null
    Copy-Item $docs $docsDst -Force
}

# 开发机上跑过的主控端会留下 config / logs：绝不进安装包（ISS 里也 Excludes 了一层）
foreach ($junk in @("config", "logs")) {
    $p = Join-Path $viewerDir $junk
    if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Recurse -Force }
}

# ---------------------------------------------------------------- 3. 编译安装程序
Write-Host "`n2) 编译安装程序（Inno Setup）…" -ForegroundColor Cyan
if (-not (Test-Path -LiteralPath $iscc)) {
    throw "找不到 $iscc。安装 Inno Setup：winget install JRSoftware.InnoSetup，或到 https://jrsoftware.org/isdl.php 下载后静默安装到 $ToolsRoot\innosetup"
}
$iss = Join-Path $RepoRoot "src\Tools\AgentSetup\Installer\setup-viewer.iss"
$isccArgs = @("/DRepoRoot=$RepoRoot", "/DServerUrl=$ServerUrl")
# 口令内嵌进安装程序：客户通常只拷这一个 exe 过去，不内嵌的话向导里口令是空的
if ($Token) { $isccArgs += "/DDefaultToken=$Token" }
& $iscc @isccArgs $iss
if ($LASTEXITCODE -ne 0) { throw "Inno 编译失败（退出码 $LASTEXITCODE）" }

$setupExe = Join-Path $out "虚拟服务器客户端-安装程序.exe"
if (-not (Test-Path -LiteralPath $setupExe)) { throw "没有产出 $setupExe" }
if ($Token) { Write-Host "   已把连接口令内嵌进安装程序（前 6 位 $($Token.Substring(0,6))…）：单独拷这一个 exe 也带着口令" -ForegroundColor Green }
else { Write-Host "   ⚠ 本次没有内嵌连接口令（未传 -Token）" -ForegroundColor Yellow }

$sizeMb = [math]::Round((Get-Item -LiteralPath $setupExe).Length / 1MB, 1)
Write-Host "`n=== 构建完成 ===" -ForegroundColor Cyan
Write-Host "  安装程序：$setupExe（$sizeMb MB）"
Write-Host "  用法：把这一个 exe 拷到目标电脑 → 双击 → 按向导装（也可 /VERYSILENT 静默装）"
Write-Host "        静默安装示例：虚拟服务器客户端-安装程序.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART"
Write-Host "  卸载：设置 → 应用 → 已安装的应用 → 虚拟服务器客户端 → 卸载"
Write-Host "  预置服务器：$ServerUrl（向导里可改；也可用同目录 setup-defaults.ini 覆盖口令）"
