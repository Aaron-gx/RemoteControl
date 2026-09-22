# 实验 v4：后台窗口能否接受"定向注入"的输入（不抢焦点、不动真实光标）
$ErrorActionPreference = "Continue"
Add-Type -AssemblyName System.Windows.Forms, System.Drawing

function WriteAll([string]$p, [string]$s) { [System.IO.File]::WriteAllText($p, $s, (New-Object System.Text.UTF8Encoding($true))) }

$dir      = "C:\Users\Panda\AppData\Local\Temp\rc-input-test"
$clickLog = Join-Path $dir "clicks.log"
$result   = "E:\Learn\MeProject6.9.20-uu\RemoteControl\scripts\_bginput_result.txt"
$errLog   = Join-Path $dir "target-err.txt"
$started  = Join-Path $dir "target-started.txt"
Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $dir | Out-Null

# ---------- 1) 目标窗口（独立进程，模拟"被控端上的应用"） ----------
$catchBody = "    `$e = `$_ | Out-String`r`n    [System.IO.File]::WriteAllText('$errLog', `$e)"
$targetScript = @"
try {
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
[System.IO.File]::WriteAllText('$started', 'ok')
`$log = '$clickLog'
`$f = New-Object System.Windows.Forms.Form
`$f.Text = 'RC-INPUT-TEST'
`$f.StartPosition = 'Manual'
`$f.Location = New-Object System.Drawing.Point(40, 40)
`$f.Size = New-Object System.Drawing.Size(640, 540)
`$tb = New-Object System.Windows.Forms.TextBox
`$tb.Multiline = `$true; `$tb.Size = New-Object System.Drawing.Size(600, 90); `$tb.Location = New-Object System.Drawing.Point(12, 12)
`$f.Controls.Add(`$tb)
`$lbl = New-Object System.Windows.Forms.Label
`$lbl.Size = New-Object System.Drawing.Size(600, 60); `$lbl.Location = New-Object System.Drawing.Point(12, 120)
`$f.Controls.Add(`$lbl)
`$state = @{ n = 0 }
`$f.Add_MouseClick({ param(`$s, `$e)
    `$state.n++
    `$c = [System.Windows.Forms.Cursor]::Position
    `$line = 'click#' + `$state.n + ' at ' + `$e.X + ',' + `$e.Y + ' button=' + `$e.Button + ' | 真实光标在 ' + `$c.X + ',' + `$c.Y
    Add-Content -Path `$log -Value `$line
    `$lbl.Text = `$line
})
`$lbl.Text = '等待注入'
[void]`$f.ShowDialog()
} catch {
$catchBody
}
"@
$targetPath = Join-Path $dir "target.ps1"
WriteAll $targetPath $targetScript
$target = Start-Process powershell -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$targetPath`"" -PassThru

# ---------- 2) 注入方（模拟"主控端的远程输入"） ----------
Add-Type -Namespace W -Name U -MemberDefinition @'
public delegate bool EnumProc(IntPtr h, IntPtr l);
[System.Runtime.InteropServices.DllImport("user32.dll", CharSet=System.Runtime.InteropServices.CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string title);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
[System.Runtime.InteropServices.DllImport("user32.dll", CharSet=System.Runtime.InteropServices.CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
[System.Runtime.InteropServices.DllImport("user32.dll", CharSet=System.Runtime.InteropServices.CharSet.Unicode)] public static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
[System.Runtime.InteropServices.DllImport("user32.dll", CharSet=System.Runtime.InteropServices.CharSet.Unicode)] public static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, System.Text.StringBuilder l);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool GetCursorPos(out P p);
[System.Runtime.InteropServices.DllImport("user32.dll", CharSet=System.Runtime.InteropServices.CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
public struct P { public int X; public int Y; }
'@
$WM_GETTEXT = 0x000D; $WM_GETTEXTLENGTH = 0x000E
$WM_CHAR = 0x0102; $WM_LBUTTONDOWN = 0x0201; $WM_LBUTTONUP = 0x0202
function MakeLParam([int]$x, [int]$y) { [IntPtr]((($y -shl 16) -bor ($x -band 0xFFFF))) }

$out = New-Object System.Collections.Generic.List[string]
$script:foundH = [IntPtr]::Zero
function Find-ByTitle([string]$title) {
    $script:foundH = [IntPtr]::Zero
    [void][W.U]::EnumWindows({ param($h, $l)
        if ([W.U]::IsWindowVisible($h)) {
            $sb2 = New-Object System.Text.StringBuilder 256
            [void][W.U]::GetWindowText($h, $sb2, 256)
            if ($sb2.ToString() -eq $title) { $script:foundH = $h; return $false }
        }
        return $true
    })
    return $script:foundH
}
$form = [IntPtr]::Zero
for ($i = 0; $i -lt 25; $i++) {
    Start-Sleep -Milliseconds 800
    $form = Find-ByTitle "RC-INPUT-TEST"
    if ($form -ne [IntPtr]::Zero) { break }
}
if ($form -eq [IntPtr]::Zero) {
    $out.Add("!! 20 秒内没找到目标窗口")
    $out.Add("target 启动标记: " + (Test-Path $started))
    if (Test-Path $errLog) { $out.Add("target 异常: " + (Get-Content $errLog -Raw)) }
    $alive = Get-Process -Id $target.Id -ErrorAction SilentlyContinue
    $out.Add("target 进程存活: " + [bool]$alive)
    $titles = New-Object System.Collections.Generic.List[string]
    [void][W.U]::EnumWindows({ param($h, $l)
        if ([W.U]::IsWindowVisible($h)) {
            $sb2 = New-Object System.Text.StringBuilder 256
            [void][W.U]::GetWindowText($h, $sb2, 256)
            $t = $sb2.ToString()
            if ($t -and ($t -match "RC-INPUT" -or $t -match "PowerShell")) { $titles.Add($t) }
        }
        return $true
    })
    $out.Add("相关窗口标题: " + ($titles -join " | "))
    $out | Set-Content $result -Encoding UTF8
    Stop-Process -Id $target.Id -Force -ErrorAction SilentlyContinue
    exit 1
}
$out.Add("找到目标窗口 hwnd=$form")

# 找文本框子窗口（UIA：比类名匹配稳）
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$edit = [IntPtr]::Zero
$h = [IntPtr]::Zero
$sb = New-Object System.Text.StringBuilder 256
while ($true) {
    $h = [W.U]::FindWindowEx($form, $h, $null, $null)
    if ($h -eq [IntPtr]::Zero) { break }
    [void][W.U]::GetClassName($h, $sb, 256)
    $out.Add("  子窗口: $h 类=" + $sb.ToString())
}
$rootAE = [System.Windows.Automation.AutomationElement]::FromHandle($form)
if ($rootAE) {
    foreach ($ct in @([System.Windows.Automation.ControlType]::Edit, [System.Windows.Automation.ControlType]::Document)) {
        $cond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $ct)
        $ed = $rootAE.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
        if ($ed) { $edit = [IntPtr]$ed.Current.NativeWindowHandle; break }
    }
}
$out.Add("文本框子窗口句柄: $edit")

$before = New-Object W.U+P; [void][W.U]::GetCursorPos([ref]$before)
$out.Add("注入前：真实光标 = $($before.X),$($before.Y)")

foreach ($ch in "HELLO-RC-123".ToCharArray()) {
    [void][W.U]::PostMessage($edit, $WM_CHAR, [IntPtr][int]$ch, [IntPtr]0)
}
Start-Sleep -Milliseconds 800
[void][W.U]::PostMessage($form, $WM_LBUTTONDOWN, [IntPtr]1, (MakeLParam 300 380))
[void][W.U]::PostMessage($form, $WM_LBUTTONUP, [IntPtr]0, (MakeLParam 300 380))
Start-Sleep -Seconds 1

$after = New-Object W.U+P; [void][W.U]::GetCursorPos([ref]$after)
$out.Add("注入后：真实光标 = $($after.X),$($after.Y)   没移动: " + ($before.X -eq $after.X -and $before.Y -eq $after.Y))
$out.Add("目标始终不在前台（后台注入）: " + ([W.U]::GetForegroundWindow() -ne $form))

$out.Add("--- 文本框内容（WM_GETTEXT，跨进程读）---")
$len = [int][W.U]::SendMessage($edit, $WM_GETTEXTLENGTH, [IntPtr]0, [IntPtr]0)
if ($len -gt 0) {
    $buf = New-Object System.Text.StringBuilder ($len + 2)
    [void][W.U]::SendMessage($edit, $WM_GETTEXT, [IntPtr]($len + 1), $buf)
    $out.Add("  期望: HELLO-RC-123")
    $out.Add("  实际: " + $buf.ToString())
} else { $out.Add("  (空 —— 打字注入失败)") }
$out.Add("--- 目标窗口自己的点击日志（含真实光标位置）---")
if (Test-Path $clickLog) { Get-Content $clickLog | ForEach-Object { $out.Add("  " + $_) } } else { $out.Add("  (无点击事件 —— 点击注入失败)") }

$out | ForEach-Object { Write-Host $_ }
$out | Set-Content $result -Encoding UTF8
Stop-Process -Id $target.Id -Force -ErrorAction SilentlyContinue
