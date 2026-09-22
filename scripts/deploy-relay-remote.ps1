<#
.SYNOPSIS
  把中继服务器部署到远程 Linux 主机（从本机 Windows 通过 ssh/scp 一条命令完成）。

.DESCRIPTION
  1) 检查目标架构（uname -m）挑选对应二进制
  2) scp 上传二进制 + deploy-relay.sh
  3) ssh 执行部署脚本（生成/使用令牌）

  认证方式三选一：
    - 已配置免密（密钥）：直接跑，无需任何交互
    - 有私钥文件： -KeyFile C:\path\id_rsa
    - 密码：本脚本无法非交互输入密码。请先执行一次
              ssh-copy-id / 或手动 scp+ssh 两步（见 -Manual 说明）
      也可以先在本机生成密钥并让服务器接受：
              ssh-keygen -t ed25519 -f $env:USERPROFILE\.ssh\rc_relay
              type $env:USERPROFILE\.ssh\rc_relay.pub | ssh user@host "cat >> ~/.ssh/authorized_keys"

.EXAMPLE
  .\deploy-relay-remote.ps1 -Host 1.2.3.4 -User root -Token 'MySecret'
  .\deploy-relay-remote.ps1 -Host hk.example.com -User ubuntu -KeyFile $env:USERPROFILE\.ssh\rc_relay -Port 22
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Host,
    [string]$User = "root",
    [int]$SshPort = 22,
    [int]$RelayPort = 8080,
    [string]$Token = "",
    [string]$KeyFile = "",
    [string]$RepoRoot = ""
)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($RepoRoot)) { $RepoRoot = Split-Path -Parent $PSScriptRoot }
$binDir = Join-Path $RepoRoot "build\server"
$script = Join-Path $PSScriptRoot "deploy-relay.sh"

$sshBase = @("-p", "$SshPort")
if ($KeyFile) { $sshBase += @("-i", $KeyFile) }
$sshTarget = "$User@$Host"

function Run-Ssh([string]$cmd) {
    Write-Host "  ssh> $cmd" -ForegroundColor DarkGray
    & ssh @sshBase -o StrictHostKeyChecking=accept-new $sshTarget $cmd
    if ($LASTEXITCODE -ne 0) { throw "ssh 执行失败（exit $LASTEXITCODE）" }
}

Write-Host "===== 0. 连通性与架构检测 =====" -ForegroundColor Cyan
$arch = (& ssh @sshBase -o StrictHostKeyChecking=accept-new $sshTarget "uname -m").Trim()
Write-Host "  远端架构: $arch"
$bin = switch -Wildcard ($arch) {
    "x86_64"  { "rcserver-linux-amd64" }
    "amd64"   { "rcserver-linux-amd64" }
    "aarch64" { "rcserver-linux-arm64" }
    "arm64"   { "rcserver-linux-arm64" }
    default   { throw "不支持的架构 $arch（可自行用 GOOS/GOARCH 交叉编译）" }
}
$binPath = Join-Path $binDir $bin
if (-not (Test-Path $binPath)) { throw "找不到 $binPath，请先跑 scripts\build-all.ps1" }
Write-Host "  使用二进制: $bin ($([math]::Round((Get-Item $binPath).Length/1MB,1)) MB)"

Write-Host "===== 1. 上传文件 =====" -ForegroundColor Cyan
& scp @sshBase -o StrictHostKeyChecking=accept-new $binPath "${sshTarget}:/tmp/$bin"
if ($LASTEXITCODE -ne 0) { throw "scp 二进制失败" }
& scp @sshBase -o StrictHostKeyChecking=accept-new $script "${sshTarget}:/tmp/deploy-relay.sh"
if ($LASTEXITCODE -ne 0) { throw "scp 脚本失败" }

Write-Host "===== 2. 执行部署 =====" -ForegroundColor Cyan
$tokenArg = if ($Token) { "--token '$Token'" } else { "" }
Run-Ssh "chmod +x /tmp/deploy-relay.sh && sudo bash /tmp/deploy-relay.sh --binary /tmp/$bin --port $RelayPort $tokenArg"

Write-Host ""
Write-Host "===== 完成 =====" -ForegroundColor Green
Write-Host "把上面输出里的 ServerUrl / FileServerUrl / 共享令牌 填进被控端与主控端的配置即可。"
