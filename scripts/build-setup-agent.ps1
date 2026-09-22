<#
.SYNOPSIS
  打包被控端安装包（Inno Setup 正式安装程序）。

.DESCRIPTION
  产物：build\setup-agent\远程控制被控端-安装程序.exe —— 一个标准的 Windows 安装程序：
    · 标准安装向导（欢迎 / 安装位置 / 附加任务 / 连接设置 / 安装 / 完成）
    · 装进 Program Files，注册到「应用和功能」，带卸载器与开始菜单入口
    · 支持 /SILENT、/VERYSILENT 等标准无人值守参数
    · 文件部署由 Inno 负责；建远程用户 / RDPWrap / 虚拟屏 / 计划任务 / 防火墙 / Defender
      仍由 scripts\install-agent.ps1（-NoDeploy 模式）完成

  可选：在产物目录放一个 setup-defaults.ini 可以预置中继令牌，随包分发给客户（内容形如：
      [relay]
      token=xxxxxxxx
  没有这个文件就由向导里填/留空自动生成）。

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\build-setup-agent.ps1
#>
[CmdletBinding()]
param(
    [string]$RepoRoot = "",
    [string]$ToolsRoot = "E:\tools",
    [string]$RelayServer = "202.60.232.209:8080",
    [string]$Token = "",            # 预置中继令牌（会写进 setup-defaults.ini）
    [switch]$SkipAgentBuild,        # 跳过 dotnet publish（用现有 build\agent）
    [switch]$AlsoLegacyExe          # 额外产出自制版单文件安装器（老脚本分发用，+210MB）
)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($RepoRoot)) { $RepoRoot = Split-Path -Parent $PSScriptRoot }

$env:DOTNET_ROOT = Join-Path $ToolsRoot "dotnet"
$env:PATH = "$env:DOTNET_ROOT;" + $env:PATH
$env:NUGET_PACKAGES = Join-Path $ToolsRoot "nuget-packages"
$dotnet = Join-Path $env:DOTNET_ROOT "dotnet.exe"
$iscc = Join-Path $ToolsRoot "innosetup\ISCC.exe"

$out = Join-Path $RepoRoot "build\setup-agent"
$agentDir = Join-Path $RepoRoot "build\agent"
New-Item -ItemType Directory -Force -Path $out | Out-Null

# 安装/卸载脚本会随包分发、由安装程序用 PowerShell 5.1 调起。
# PS 5.1 对"无 BOM 的 UTF-8"会按 ANSI 读，中文变乱码 → 解析报错、脚本根本跑不起来
# （卸载清理就这么静默失败过一次）。install-agent.ps1 是 UTF-16、uninstall-agent.ps1 是
# UTF-8，都靠 BOM 认编码，所以这里只修"缺 BOM 的 UTF-8"，已有 BOM 的一律不动。
foreach ($ps1 in @("install-agent.ps1", "create-remote-user.ps1", "uninstall-agent.ps1")) {
    $p = Join-Path $RepoRoot "scripts\$ps1"
    if (-not (Test-Path -LiteralPath $p)) { throw "缺少 $p" }
    $bytes = [System.IO.File]::ReadAllBytes($p)
    $hasBom = ($bytes.Length -ge 2 -and $bytes[0] -eq 0xFF -and $bytes[1] -eq 0xFE) -or   # UTF-16 LE
              ($bytes.Length -ge 2 -and $bytes[0] -eq 0xFE -and $bytes[1] -eq 0xFF) -or   # UTF-16 BE
              ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)  # UTF-8
    if (-not $hasBom) {
        $text = [System.IO.File]::ReadAllText($p, [System.Text.UTF8Encoding]::new($false))
        [System.IO.File]::WriteAllText($p, $text, [System.Text.UTF8Encoding]::new($true))
        Write-Host "   已补 UTF-8 BOM：$ps1" -ForegroundColor Yellow
    }
}

# ---------------------------------------------------------------- 1. 被控端程序
if (-not $SkipAgentBuild) {
    Write-Host "1) 构建被控端程序 + ffmpeg..." -ForegroundColor Cyan
    & (Join-Path $PSScriptRoot "build-all.ps1") -RepoRoot $RepoRoot -ToolsRoot $ToolsRoot -SkipTests
    if ($LASTEXITCODE -ne 0) { throw "build-all.ps1 失败" }
} else {
    Write-Host "1) 跳过构建，使用现有 build\agent" -ForegroundColor Yellow
}
if (-not (Test-Path -LiteralPath (Join-Path $agentDir "Agent.Coordinator.exe"))) {
    throw "缺少 $agentDir\Agent.Coordinator.exe（先跑 scripts\build-all.ps1）"
}

# ---------------------------------------------------------------- 2. 从仓库正规来源补齐依赖
Write-Host "2) 补齐 ffmpeg / 驱动..." -ForegroundColor Cyan
$ffSrc = Join-Path $RepoRoot "third_party\ffmpeg\ffmpeg.exe"
if (-not (Test-Path -LiteralPath $ffSrc)) { throw "缺少 $ffSrc（被控端编解码必需）" }
$ffDst = Join-Path $agentDir "third_party\ffmpeg"
New-Item -ItemType Directory -Force -Path $ffDst | Out-Null
Copy-Item -LiteralPath $ffSrc (Join-Path $ffDst "ffmpeg.exe") -Force

# 驱动随程序目录一起部署：install-agent.ps1 装 RDPWrap / 虚拟屏时从这里取
$drvSrc = Join-Path $RepoRoot "drivers"
if (-not (Test-Path -LiteralPath $drvSrc)) { throw "缺少 $drvSrc" }
robocopy $drvSrc (Join-Path $agentDir "drivers") /MIR /NFL /NDL /NJH /NJS /NP | Out-Null

# 安装期不带着运行期产物走（升级时要保留机器上的 config/logs，Inno 也不会覆盖）
foreach ($junk in @("config", "logs")) {
    $p = Join-Path $agentDir $junk
    if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Recurse -Force }
}

# ---------------------------------------------------------------- 3. 编译 Inno 安装程序
Write-Host "3) 编译安装程序（Inno Setup）..." -ForegroundColor Cyan

if (-not (Test-Path -LiteralPath $iscc)) {
    throw "找不到 $iscc。安装 Inno Setup：winget install JRSoftware.InnoSetup，或到 https://jrsoftware.org/isdl.php 下载后静默安装到 $ToolsRoot\innosetup"
}
$iss = Join-Path $RepoRoot "src\Tools\AgentSetup\Installer\setup-agent.iss"
$isccArgs = @("/DRelayServer=$RelayServer", "/DRepoRoot=$RepoRoot")
if ($Token) { $isccArgs += "/DDefaultToken=$Token" }
& $iscc @isccArgs $iss
if ($LASTEXITCODE -ne 0) { throw "Inno 编译失败（退出码 $LASTEXITCODE）" }

$setupExe = Join-Path $out "远程控制被控端-安装程序.exe"
if (-not (Test-Path -LiteralPath $setupExe)) { throw "没有产出 $setupExe" }

# 可选：预置令牌（只给客户"开箱即用"，不带则向导里填/自动生成）
$defaults = Join-Path $out "setup-defaults.ini"
if ($Token) {
    Set-Content -LiteralPath $defaults -Encoding UTF8 -Value @(
        "[relay]",
        "token=$Token"
    )
    Write-Host "   已写入预置令牌：$defaults" -ForegroundColor Yellow
} elseif (Test-Path -LiteralPath $defaults) {
    Write-Host "   沿用现有预置令牌：$defaults" -ForegroundColor Yellow
}

# ---------------------------------------------------------------- 4. 老的 WPF 安装器（可选）
if ($AlsoLegacyExe) {
    Write-Host "4) 构建自制版单文件安装器（legacy）..." -ForegroundColor Cyan
    $payloadZip = Join-Path $out "payload.zip"
    $legacyOut = Join-Path $out "legacy"
    # 打成 zip 内嵌（exe + payload 一体，方便老流程直接拷单个文件）
    if (Test-Path -LiteralPath $payloadZip) { Remove-Item -LiteralPath $payloadZip -Force }
    Compress-Archive -Path (Join-Path $agentDir "*") -DestinationPath $payloadZip -CompressionLevel Optimal
    & $dotnet publish (Join-Path $RepoRoot "src\Tools\AgentSetup\AgentSetup.csproj") `
        -c Release -r win-x64 --self-contained true -o $legacyOut -p:PayloadZip=$payloadZip --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw "AgentSetup 发布失败" }
    Move-Item -LiteralPath (Join-Path $legacyOut "被控端安装程序.exe") -Destination (Join-Path $out "被控端安装程序.exe") -Force
    Remove-Item -LiteralPath $legacyOut -Recurse -Force
    Remove-Item -LiteralPath $payloadZip -Force
} else {
    # 默认不产出老的单文件安装器：清掉历史产物，避免"两个安装程序"混淆
    foreach ($stale in @("被控端安装程序.exe", "被控端安装程序.pdb", "payload.zip", "payload")) {
        $p = Join-Path $out $stale
        if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Recurse -Force }
    }
    # 早期自制安装器在 build\ 根下留过 payload.zip，混淆视听，顺手清掉
    $oldPayload = Join-Path $RepoRoot "build\payload.zip"
    if (Test-Path -LiteralPath $oldPayload) { Remove-Item -LiteralPath $oldPayload -Force }
}

# ---------------------------------------------------------------- 5. 自检
Write-Host "5) 自检..." -ForegroundColor Cyan
foreach ($m in @(
    $setupExe,
    (Join-Path $agentDir "Agent.Coordinator.exe"),
    (Join-Path $agentDir "third_party\ffmpeg\ffmpeg.exe"),
    (Join-Path $agentDir "drivers\RDPWrap\rdpwrap.ini"),
    (Join-Path $agentDir "drivers\VirtualDisplayDriver"),
    (Join-Path $RepoRoot "scripts\install-agent.ps1"),
    (Join-Path $RepoRoot "scripts\uninstall-agent.ps1")
)) {
    if (-not (Test-Path -LiteralPath $m)) { throw "缺少 $m" }
}
$mb = [math]::Round((Get-Item -LiteralPath $setupExe).Length / 1MB, 1)
Write-Host ""
Write-Host "=== 构建完成 ===" -ForegroundColor Green
Write-Host ("  安装程序：$setupExe（$mb MB）")
Write-Host  "  用法：把这一个 exe 拷到目标电脑 → 双击 → 按向导装（也可 /VERYSILENT 静默装）"
Write-Host  "  卸载：设置 → 应用 → 已安装的应用 → 远程控制 · 被控端 → 卸载"
if (Test-Path -LiteralPath $defaults) { Write-Host "  预置令牌：$defaults（随包分发，装的时候自动填好）" }

# ---------------------------------------------------------------- 6. 分发用 zip（可选）
$zip = Join-Path $RepoRoot "build\被控端安装包.zip"
try {
    if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
    $zipItems = @($setupExe)
    if (Test-Path -LiteralPath $defaults) { $zipItems += $defaults }
    Compress-Archive -LiteralPath $zipItems -DestinationPath $zip -CompressionLevel Optimal
    $zipMb = [math]::Round((Get-Item -LiteralPath $zip).Length / 1MB, 1)
    Write-Host ("  整包分发：$zip（$zipMb MB，含安装程序" + $(if (Test-Path -LiteralPath $defaults) { " + 预设令牌" } else { "" }) + "）")
} catch {
    Write-Host ("  打包 zip 失败（不影响安装程序本体）：" + $_.Exception.Message) -ForegroundColor Yellow
}
