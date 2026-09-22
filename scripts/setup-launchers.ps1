$ErrorActionPreference = 'Stop'
$root = 'E:\RemoteControl'
$exe = Join-Path $root 'server\rcserver.exe'
New-Item -ItemType Directory -Force -Path (Join-Path $root 'serverdata') | Out-Null

$currentUser = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name   # 形如 AURA\Panda
Write-Host "当前用户: $currentUser"

$existing = Get-ScheduledTask -TaskName 'RemoteControlRelay' -ErrorAction SilentlyContinue
if ($existing) { Unregister-ScheduledTask -TaskName 'RemoteControlRelay' -Confirm:$false }

$action = New-ScheduledTaskAction -Execute $exe -Argument '-addr :8080 -data E:\RemoteControl\serverdata' -WorkingDirectory (Join-Path $root 'server')
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $currentUser
$principal = New-ScheduledTaskPrincipal -UserId $currentUser -LogonType Interactive -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew
Register-ScheduledTask -TaskName 'RemoteControlRelay' -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null

$t = Get-ScheduledTask -TaskName 'RemoteControlRelay'
Write-Host ("[OK] 中继开机自启已注册: " + $t.TaskName + "  state=" + $t.State + "  runAs=" + $t.Principal.UserId + "  runLevel=" + $t.Principal.RunLevel)

Write-Host ""
Write-Host "=== 启动脚本内容校验 ==="
foreach ($f in @('start-viewer.cmd', 'start-server.cmd', 'start-agent.cmd')) {
    $p = Join-Path $root $f
    Write-Host ("--- $f")
    Get-Content $p | ForEach-Object { Write-Host ("    " + $_) }
}
