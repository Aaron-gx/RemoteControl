<#
.SYNOPSIS
  抓取某个进程主窗口截图并保存 PNG（WPF 走 DirectX，必须用 PW_RENDERFULLCONTENT）。
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\capture-window.ps1 -ProcessName Viewer -Out E:	mpiewer.png
#>
param(
    [Parameter(Mandatory = $true)][string]$ProcessName,
    [string]$Out = "",
    [int]$Index = 0,
    [long]$Hwnd = 0
)

Add-Type -AssemblyName System.Drawing
Add-Type -Namespace Cap -Name W -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
[System.Runtime.InteropServices.StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
'@

if ($Hwnd -gt 0) {
    $hwnd = [IntPtr]$Hwnd
} else {
    $procs = @(Get-Process -Name $ProcessName -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowHandle -ne 0 })
    if ($procs.Count -eq 0) { Write-Error "找不到 $ProcessName 的可见窗口（也可用 -Hwnd 直接指定句柄）"; exit 1 }
    $hwnd = $procs[[Math]::Min($Index, $procs.Count - 1)].MainWindowHandle
}
if ($hwnd -eq [IntPtr]::Zero) { Write-Error "窗口句柄无效"; exit 1 }

$r = New-Object Cap.W+RECT
[void][Cap.W]::GetWindowRect($hwnd, [ref]$r)
$w = $r.Right - $r.Left
$h = $r.Bottom - $r.Top
if ($w -le 0 -or $h -le 0) { Write-Error "窗口尺寸异常 ${w}x${h}"; exit 1 }

$bmp = New-Object System.Drawing.Bitmap($w, $h)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
# 2 = PW_RENDERFULLCONTENT（WPF 用 DirectX 渲染，必须用这个标志才抓得到内容）
[void][Cap.W]::PrintWindow($hwnd, $hdc, 2)
$g.ReleaseHdc($hdc)
$g.Dispose()

if ([string]::IsNullOrWhiteSpace($Out)) {
    $Out = Join-Path $env:TEMP ("shot-" + $ProcessName + "-" + (Get-Date -Format 'HHmmss') + ".png")
}
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Output ("已保存: {0} ({1}x{2})" -f $Out, $w, $h)
