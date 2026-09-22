<#
.SYNOPSIS
  创建远程会话专用本地用户（策划 §5.4）并加入 Remote Desktop Users 组。

.DESCRIPTION
  幂等：用户已存在时只更新密码/组成员并报告状态。
  用 SID（S-1-5-32-555 = Remote Desktop Users）取组，避免中文系统上组名不是英文导致加组失败。
  返回码：0 成功；1 失败
#>
[CmdletBinding()]
param(
    [string]$UserName = "RemoteWorker",
    [string]$Password = "",
    [switch]$PassThru
)

$ErrorActionPreference = "Stop"

function New-RandomPassword {
    # 满足复杂度：大小写+数字+符号，长度 20
    $chars = "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789!@#%^*-_=+"
    $rnd = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    $bytes = New-Object byte[] 20
    $rnd.GetBytes($bytes)
    -join ($bytes | ForEach-Object { $chars[$_ % $chars.Length] })
}

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
        ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "需要管理员权限运行" -ForegroundColor Red
    exit 1
}

if ([string]::IsNullOrWhiteSpace($Password)) { $Password = New-RandomPassword }

$RDP_GROUP_SID = "S-1-5-32-555"   # Remote Desktop Users
$created = $false

try {
    $user = Get-LocalUser -Name $UserName -ErrorAction SilentlyContinue
    $secure = ConvertTo-SecureString $Password -AsPlainText -Force

    if (-not $user) {
        Write-Host "创建本地用户 $UserName ..."
        New-LocalUser -Name $UserName -Password $secure -PasswordNeverExpires `
            -UserMayNotChangePassword -AccountNeverExpires `
            -Description "远程控制软件专用会话用户" | Out-Null
        $created = $true
    } else {
        Write-Host "用户 $UserName 已存在，重置密码与策略"
        Set-LocalUser -Name $UserName -Password $secure
    }
    Set-LocalUser -Name $UserName -PasswordNeverExpires $true -AccountNeverExpires
    Enable-LocalUser -Name $UserName -ErrorAction SilentlyContinue
} catch {
    Write-Host "创建/更新用户失败：$_" -ForegroundColor Red
    exit 1
}

try {
    $rdpGroup = Get-LocalGroup -SID $RDP_GROUP_SID -ErrorAction Stop
    $members = @(Get-LocalGroupMember -Group $rdpGroup -ErrorAction SilentlyContinue)
    $isMember = $false
    foreach ($m in $members) {
        if ($m.Name -like "*\$UserName" -or $m.Name -eq $UserName) { $isMember = $true; break }
    }
    if (-not $isMember) {
        Add-LocalGroupMember -Group $rdpGroup -Member $UserName
        Write-Host "已将 $UserName 加入 $($rdpGroup.Name)"
    } else {
        Write-Host "$UserName 已在 $($rdpGroup.Name) 中"
    }
} catch {
    Write-Host "加入 Remote Desktop Users 失败：$_" -ForegroundColor Red
    exit 1
}

Write-Host "用户就绪：$UserName" -ForegroundColor Green
if ($PassThru) {
    [pscustomobject]@{ UserName = $UserName; Password = $Password; Created = $created }
} else {
    Write-Host "密码：$Password" -ForegroundColor Yellow
    Write-Host "（请把该密码填入 agent 配置的 WorkerPassword）"
}
exit 0
