<#
.SYNOPSIS
  远程控制 · 连通性自检：一次性检查"能不能连上服务器"，并给出具体原因和修法。

.DESCRIPTION
  会依次检查：配置读取 → 地址解析 → 域名解析 → TCP 端口连通 → HTTP 健康检查 →
  令牌与在线列表 → WebSocket 握手（等同主控端/被控端真正连接的那一步）。
  任何一步失败都会直接说明原因与怎么改。输出同时写入 selfcheck-YYYYMMDD-HHmmss.log。

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File 连接自检.ps1
  powershell -ExecutionPolicy Bypass -File 连接自检.ps1 -Server ws://1.2.3.4:8080/ws -Token XXX -AgentId AURA-XXXX
#>
[CmdletBinding()]
param(
    [string]$Server = "",
    [string]$Token = "",
    [string]$AgentId = "",
    [int]$TimeoutSec = 8,
    [switch]$NoPause
)

$ErrorActionPreference = "Continue"
$script:fail = 0
$logFile = Join-Path (Get-Location) ("selfcheck-" + (Get-Date -Format 'yyyyMMdd-HHmmss') + ".log")

function Say([string]$text, [string]$color = "Gray") {
    Write-Host $text -ForegroundColor $color
    try { Add-Content -Path $logFile -Value $text -Encoding UTF8 } catch { }
}
function Pass([string]$name, [string]$detail = "") { Say ("  [OK]   " + $name + $(if ($detail) { "  " + $detail })) "Green" }
function Fail([string]$name, [string]$why, [string]$fix) {
    $script:fail++
    Say ("  [失败] " + $name) "Red"
    Say ("         原因：" + $why) "Yellow"
    Say ("         处理：" + $fix) "Yellow"
}

Say ("远程控制 · 连通性自检  " + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss')) "Cyan"
Say ("日志：" + $logFile)
Say ""

# ---------------------------------------------------------------- 1. 读取配置
Say "1) 读取配置" "White"
$agentCfg = "E:\RemoteControl\agent\config\agent.json"
$viewerCfg = "E:\RemoteControl\viewer\config\viewer.json"
$cfgServer = ""; $cfgToken = ""; $cfgAgent = ""; $src = ""

foreach ($p in @($agentCfg, $viewerCfg)) {
    if (-not (Test-Path $p)) { continue }
    try {
        $j = Get-Content $p -Raw -Encoding UTF8 | ConvertFrom-Json
        if (-not $cfgServer -and $j.ServerUrl) { $cfgServer = $j.ServerUrl }
        if (-not $cfgToken) {
            if ($j.AgentToken) { $cfgToken = $j.AgentToken }
        }
        if (-not $cfgAgent) {
            if ($j.AgentId) { $cfgAgent = $j.AgentId }
            elseif ($j.TargetAgentId) { $cfgAgent = $j.TargetAgentId }
        }
        $src = $p
    } catch { }
}
if ($Server) { $cfgServer = $Server }
if ($Token) { $cfgToken = $Token }
if ($AgentId) { $cfgAgent = $AgentId }

if (-not $cfgServer) {
    Fail "读到服务器地址" "既没找到配置文件，也没用参数指定" "用 -Server ws://IP:8080/ws -Token 令牌 再跑一次"
    Say ""
    Say "自检结束：有 $script:fail 项失败，日志：$logFile" "Red"
    if (-not $NoPause) { Read-Host "按回车退出" | Out-Null }
    exit 1
}
Pass "读到服务器地址" $cfgServer
Pass "读到令牌" $(if ($cfgToken) { "长度 " + $cfgToken.Length } else { "（空：服务器可能未启用令牌）" })
if ($cfgAgent) { Pass "目标/本机 AgentId" $cfgAgent }
Say ("     配置来源：" + $src)

# ---------------------------------------------------------------- 2. 地址解析
Say ""
Say "2) 解析服务器地址" "White"
$ws = $cfgServer.Trim()
$isTls = $ws -like "wss://*"
$rest = $ws -replace '^wss?://', ''
$rest = $rest -replace '/.*$', ''
$host_ = $rest; $port = if ($isTls) { 443 } else { 80 }
if ($rest -match '^(.+):(\d+)$') { $host_ = $Matches[1]; $port = [int]$Matches[2] }
if (-not $rest) { Fail "地址格式" "解析不出来" "应为 ws://IP或域名:端口/ws" }
else { Pass "地址格式" ("主机=" + $host_ + " 端口=" + $port + $(if ($isTls) { " (TLS)" })) }

# ---------------------------------------------------------------- 3. 域名解析
Say ""
Say "3) 域名解析（若填的是 IP 则跳过）" "White"
if ($host_ -match '^\d+\.\d+\.\d+\.\d+$') { Pass "是 IP 地址，无需解析" $host_ }
else {
    try {
        $ips = [System.Net.Dns]::GetHostAddresses($host_) | ForEach-Object { $_.IPAddressToString }
        Pass "解析成功" ($ips -join ", ")
    } catch {
        Fail "域名解析" $_.Exception.Message "检查 DNS/网络；企业网可能需要代理"
    }
}

# ---------------------------------------------------------------- 4. TCP 端口
Say ""
Say "4) TCP 端口连通" "White"
$tcpOk = $false
try {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $client = New-Object System.Net.Sockets.TcpClient
    $iar = $client.BeginConnect($host_, $port, $null, $null)
    if ($iar.AsyncWaitHandle.WaitOne($TimeoutSec * 1000, $false) -and $client.Connected) {
        $client.EndConnect($iar); $sw.Stop()
        Pass ("能连上 " + $host_ + ":" + $port) ("耗时 " + $sw.ElapsedMilliseconds + " ms")
        $tcpOk = $true
    } else {
        Fail ("连不上 " + $host_ + ":" + $port) ("超时 " + $TimeoutSec + " 秒") `
             "该机器到服务器的网络被挡了（公司/校园网/运营商/防火墙）；换网络、走代理，或把服务器换到可直连的地址"
    }
    $client.Close()
} catch {
    Fail ("连不上 " + $host_ + ":" + $port) $_.Exception.Message `
         "同上：先确认能否 ping/访问该服务器，再检查目标机器出网策略"
}

if (-not $tcpOk) {
    Say ""
    Say "自检结束：有 $script:fail 项失败，日志：$logFile" "Red"
    if (-not $NoPause) { Read-Host "按回车退出" | Out-Null }
    exit 1
}

# ---------------------------------------------------------------- 5. HTTP 健康检查
Say ""
Say "5) HTTP 健康检查" "White"
if ($isTls) { $base = "https://" } else { $base = "http://" }
$base = $base + $host_ + ":" + $port
try {
    $h = Invoke-WebRequest -Uri ($base + "/healthz") -UseBasicParsing -TimeoutSec $TimeoutSec
    Pass "/healthz" ("HTTP " + $h.StatusCode + " → " + $h.Content)
} catch {
    Fail "/healthz" $_.Exception.Message "端口通但不是这个服务？确认服务器上 rcserver 在跑（rcctl status）"
}

$licState = ""
try {
    $lic = Invoke-WebRequest -Uri ($base + "/api/license") -UseBasicParsing -TimeoutSec $TimeoutSec
    $licState = ($lic.Content | ConvertFrom-Json).state
    Pass "/api/license" ("服务器返回 state=" + $licState)
} catch {
    Fail "/api/license" $_.Exception.Message "服务器程序版本过旧或未启动"
}
if ($licState -and $licState -ne "ok") {
    Say ("     提示：服务器当前 state=" + $licState + " → 它会拒绝一切连接（需要服务提供方在服务器上恢复授权）") "Yellow"
}

# ---------------------------------------------------------------- 6. 令牌与在线列表
Say ""
Say "6) 令牌与在线被控端列表" "White"
$tokenQ = if ($cfgToken) { "?token=" + [Uri]::EscapeDataString($cfgToken) } else { "" }
try {
    $a = Invoke-WebRequest -Uri ($base + "/api/agents" + $tokenQ) -UseBasicParsing -TimeoutSec $TimeoutSec
    $agents = ($a.Content | ConvertFrom-Json).agents
    if ($null -eq $agents -or @($agents).Count -eq 0) {
        Fail "在线被控端" "服务器上没有任何被控端在线" "确认被控端已安装并随开机自启；看它自己的日志 E:\RemoteControl\agent\logs"
    } else {
        Pass "在线被控端" ((@($agents) | ForEach-Object { $_.id }) -join ", ")
    }
} catch {
    $code = ""
    try { $code = [int]$_.Exception.Response.StatusCode } catch { }
    if ($code -eq 401) {
        Fail "令牌校验" "服务器返回 401（令牌不对）" "让两端的 AgentToken 与服务器上的令牌完全一致（区分大小写），改完重启程序"
    } else {
        Fail "查询在线列表" $_.Exception.Message "检查令牌与服务器状态"
    }
}

# ---------------------------------------------------------------- 7. WebSocket 握手（真正连接那一步）
Say ""
Say "7) WebSocket 握手（和主控端点『连接』是同一步）" "White"
if (-not $cfgAgent) {
    Say "     跳过：没有已知的 AgentId（可用 -AgentId 指定）" "Yellow"
} else {
    if ($isTls) { $scheme = "wss://" } else { $scheme = "ws://" }
    $url = $scheme + $host_ + ":" + $port + "/ws?role=viewer&target=" +
           [Uri]::EscapeDataString($cfgAgent) + $(if ($cfgToken) { "&token=" + [Uri]::EscapeDataString($cfgToken) } else { "" })
    try {
        $c = New-Object System.Net.WebSockets.ClientWebSocket
        $cts = New-Object System.Threading.CancellationTokenSource
        $cts.CancelAfter($TimeoutSec * 1000)
        $c.ConnectAsync([Uri]$url, $cts.Token).Wait()
        $buf = New-Object byte[] 2048
        $seg = New-Object System.ArraySegment[byte] -ArgumentList @(, $buf)
        $r = $c.ReceiveAsync($seg, $cts.Token).Result
        $text = [System.Text.Encoding]::UTF8.GetString($buf, 0, $r.Count)
        if ($text -match '"code"\s*:\s*"agent_offline"') {
            Fail "WebSocket 握手" "连上了服务器，但目标被控端不在线" "确认那台电脑的被控端在运行（托盘有图标）"
        } elseif ($text -match '"type"\s*:\s*"error"') {
            Fail "WebSocket 握手" ("服务器返回：" + $text) "按上面信息处理（多为令牌或授权问题）"
        } else {
            Pass "WebSocket 握手成功" "服务器已接受连接（收到首帧控制消息）"
        }
        $c.Dispose()
    } catch {
        Fail "WebSocket 握手" $_.Exception.Message "把这条报错发给我；也可先在浏览器打开 $base/healthz 看是否正常"
    }
}

# ---------------------------------------------------------------- 结论
Say ""
if ($script:fail -eq 0) {
    Say "自检通过：这台机器可以正常连上服务器。" "Green"
    Say "如果主控端仍看不到画面，请看被控端日志 E:\RemoteControl\agent\logs\coordinator-*.log" "Gray"
} else {
    Say ("自检结束：有 " + $script:fail + " 项失败，按上面的『处理』改，日志：" + $logFile) "Red"
}
if (-not $NoPause) { Read-Host "按回车退出" | Out-Null }
