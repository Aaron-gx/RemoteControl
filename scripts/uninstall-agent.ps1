<#
.SYNOPSIS
  被控端卸载清理：由 Inno 卸载器在删除文件**之前**调用。

.DESCRIPTION
  做的是"让这台机器不再跑被控端"这件必须做的事：
    · 停并删除开机自启计划任务（RemoteControlAgent）
    · 结束被控端进程（Coordinator / Worker）
    · 删除 Defender 排除项、关闭防火墙里为远程桌面开的入站规则
    · 个别目录（虚拟屏驱动配置、RemoteWorker 用户）按参数处理

  **默认保留**系统级改动（RDPWrap、虚拟屏驱动、RemoteWorker 用户）——它们是给
  "装第二台/重装"用的底层能力，卸载掉会让重装变慢，而且删用户有丢数据的风险。
  真要彻底清理，加 -RemoveVdd / -RemoveWorkerUser 参数单独跑。

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File uninstall-agent.ps1 -InstallDir "C:\Program Files\远程控制被控端"
#>
[CmdletBinding()]
param(
    [string]$InstallDir = "",
    [string]$TaskName = "RemoteControlAgent",
    [string]$WorkerUser = "RemoteWorker",
    [switch]$RemoveVdd,           # 卸载虚拟屏驱动（默认保留：重装要靠它）
    [switch]$RemoveWorkerUser,    # 删除 RemoteWorker 用户（默认保留：可能有用户数据）
    [switch]$Quiet
)

$ErrorActionPreference = "Continue"
function Say($t) { if (-not $Quiet) { Write-Host "  $t" } }

if ([string]::IsNullOrWhiteSpace($InstallDir)) {
    # 没给安装目录就按脚本自己的位置推（安装时脚本被放在 <安装目录>\installer）
    $InstallDir = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
}
Say "安装目录：$InstallDir"

# ---------------------------------------------------------------- 1. 计划任务
try {
    $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    if ($task) {
        Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction Stop
        Say "已删除开机自启计划任务 $TaskName"
    } else {
        Say "计划任务 $TaskName 不存在（跳过）"
    }
} catch { Say "删除计划任务失败：$_" }

# ---------------------------------------------------------------- 2. 进程
foreach ($name in @("Agent.Coordinator", "Agent.Worker")) {
    try {
        $procs = Get-Process -Name $name -ErrorAction SilentlyContinue
        if ($procs) {
            $procs | Stop-Process -Force -ErrorAction SilentlyContinue
            Say "已结束进程 $name"
        }
    } catch { Say "结束进程 $name 失败：$_" }
}
Start-Sleep -Milliseconds 500

# ---------------------------------------------------------------- 3. Defender 排除
try {
    $pref = Get-MpPreference -ErrorAction Stop
    $hits = @($pref.ExclusionPath | Where-Object { $_ -and $InstallDir -and ($_ -like "$InstallDir*") })
    foreach ($h in $hits) {
        Remove-MpPreference -ExclusionPath $h -ErrorAction Stop
        Say "已移除 Defender 排除项：$h"
    }
    if ($hits.Count -eq 0) { Say "没有被控端的 Defender 排除项（跳过）" }
} catch { Say "清理 Defender 排除项失败（可能未启用 Defender）：$_" }

# ---------------------------------------------------------------- 4. 防火墙
try {
    $rule = Get-NetFirewallRule -DisplayName "RemoteControl-RDP" -ErrorAction SilentlyContinue
    if ($rule) {
        Remove-NetFirewallRule -DisplayName "RemoteControl-RDP" -ErrorAction Stop
        Say "已删除防火墙规则 RemoteControl-RDP"
    } else {
        Say "防火墙规则 RemoteControl-RDP 不存在（跳过）"
    }
} catch { Say "删除防火墙规则失败：$_" }

# ---------------------------------------------------------------- 5. 可选项
if ($RemoveVdd) {
    try {
        Get-PnpDevice -ErrorAction SilentlyContinue |
            Where-Object { $_.HardwareID -and ($_.HardwareID -contains 'Root\MttVDD') -and $_.Status -ne 'Unknown' } |
            ForEach-Object { pnputil /remove-device $_.InstanceId 2>&1 | Out-Null; Say "已移除虚拟屏设备 $($_.InstanceId)" }
    } catch { Say "移除虚拟屏设备失败：$_" }
}
if ($RemoveWorkerUser) {
    try {
        if (Get-LocalUser -Name $WorkerUser -ErrorAction SilentlyContinue) {
            Remove-LocalUser -Name $WorkerUser -ErrorAction Stop
            Say "已删除本地用户 $WorkerUser"
        }
    } catch { Say "删除用户 $WorkerUser 失败：$_" }
}

Say "清理完成（RDPWrap / 虚拟屏驱动 / $WorkerUser 用户默认保留；如需彻底清理请带 -RemoveVdd -RemoveWorkerUser 再跑一次）"
exit 0
