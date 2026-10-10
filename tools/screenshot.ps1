# Starts Star Button with an isolated data folder, waits, saves a PNG of ITS WINDOW ONLY (PrintWindow, so other
# windows in front are never captured), then closes it.
# Usage: powershell -File tools\screenshot.ps1 -Page sticks -Out shot.png [-Data C:\temp\sbdata] [-Seconds 4]
#        [-Stick 1] [-InputName button3] [-Action spaceship_view/v_view_cycle_fwd] [-Exe path\StarButton.exe] [-KeepData]
param(
    [string]$Page = "sticks",
    [string]$Out = "shot.png",
    [string]$Data = (Join-Path $env:TEMP "starbutton-shot"),
    [int]$Seconds = 4,
    [int]$Stick = -1,
    [string]$InputName = "",
    [string]$Action = "",
    [string]$Exe = (Join-Path $PSScriptRoot "..\src\ScMapper.App\bin\Debug\net10.0-windows\StarButton.exe"),
    [switch]$KeepData
)
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class Win {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int a, out RECT r, int s);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
}
"@
Add-Type -AssemblyName System.Drawing
[Win]::SetProcessDPIAware() | Out-Null
if (-not $KeepData -and (Test-Path $Data)) { Remove-Item $Data -Recurse -Force }
$argList = @("--data", $Data, "--page", $Page)
if ($Stick -ge 0) { $argList += @("--stick", "$Stick") }
if ($InputName) { $argList += @("--input", $InputName) }
if ($Action) { $argList += @("--action", $Action) }
$p = Start-Process -FilePath $Exe -ArgumentList $argList -PassThru
$deadline = (Get-Date).AddSeconds(30)
while ($p.MainWindowHandle -eq 0 -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 200; $p.Refresh() }
Start-Sleep -Seconds $Seconds
$p.Refresh()
$h = $p.MainWindowHandle
$r = New-Object Win+RECT
[Win]::GetWindowRect($h, [ref]$r) | Out-Null
$w = $r.R - $r.L; $ht = $r.B - $r.T
$bmp = New-Object System.Drawing.Bitmap $w, $ht
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
$ok = [Win]::PrintWindow($h, $hdc, 2)   # PW_RENDERFULLCONTENT
$g.ReleaseHdc($hdc)
$g.Dispose()
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Stop-Process -Id $p.Id -Force
"saved $Out ($w x $ht) printwindow=$ok"
