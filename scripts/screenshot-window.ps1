param(
    [string]$ProcessName = "SlimeRancher",
    [string]$Out = "$env:TEMP\sr_shot.png",
    [switch]$NoFocus
)
# Captures the client area of a process' main window (falls back to the full primary screen).
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class W32 {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
"@
[W32]::SetProcessDPIAware() | Out-Null
$p = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if ($p) {
    $h = $p.MainWindowHandle
    if (-not $NoFocus) { [W32]::ShowWindow($h, 9) | Out-Null; [W32]::SetForegroundWindow($h) | Out-Null; Start-Sleep -Milliseconds 400 }
    $r = New-Object W32+RECT; [W32]::GetClientRect($h, [ref]$r) | Out-Null
    $pt = New-Object W32+POINT; [W32]::ClientToScreen($h, [ref]$pt) | Out-Null
    $w = $r.Right - $r.Left; $hgt = $r.Bottom - $r.Top
    $x = $pt.X; $y = $pt.Y
} else {
    $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $x = $b.X; $y = $b.Y; $w = $b.Width; $hgt = $b.Height
}
if ($w -le 0 -or $hgt -le 0) { Write-Output "bad window size"; exit 1 }
$bmp = New-Object System.Drawing.Bitmap $w, $hgt
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($x, $y, 0, 0, $bmp.Size)
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Output "saved $Out ($w x $hgt) from $(if ($p) { 'window' } else { 'screen' })"
