param([string]$Out = "$(Split-Path -Parent (Split-Path -Parent $PSScriptRoot))\artifacts\ui-redesign-20261001\live-window.png")

# 真实窗口检查：只读查询窗口矩形、DWM 可见边界与各点的命中测试结果，再从屏幕截下窗口区域。不发送任何键鼠输入。
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class Win {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, int flags);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int a, out RECT r, int size);
}
'@
$process = Get-Process NoReturnGuardian | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
$h = $process.MainWindowHandle
[Win]::SetForegroundWindow($h) | Out-Null
Start-Sleep -Milliseconds 1200
$w = New-Object Win+RECT; [Win]::GetWindowRect($h, [ref]$w) | Out-Null
$c = New-Object Win+RECT; [Win]::GetClientRect($h, [ref]$c) | Out-Null
$v = New-Object Win+RECT; [Win]::DwmGetWindowAttribute($h, 9, [ref]$v, 16) | Out-Null
"window  {0},{1} - {2},{3}  ({4}x{5})" -f $w.L, $w.T, $w.R, $w.B, ($w.R - $w.L), ($w.B - $w.T)
"visible {0},{1} - {2},{3}  ({4}x{5})" -f $v.L, $v.T, $v.R, $v.B, ($v.R - $v.L), ($v.B - $v.T)
"client  {0}x{1}" -f $c.R, $c.B
$hit = @{ 1 = 'CLIENT'; 2 = 'CAPTION'; 10 = 'LEFT'; 11 = 'RIGHT'; 12 = 'TOP'; 15 = 'BOTTOM'; 16 = 'BOTTOMLEFT'; 17 = 'BOTTOMRIGHT'; -1 = 'TRANSPARENT'; 0 = 'NOWHERE' }
$points = [ordered]@{
    'left border'   = @(($w.L + 3), (($w.T + $w.B) / 2))
    'right border'  = @(($w.R - 3), (($w.T + $w.B) / 2))
    'bottom border' = @((($w.L + $w.R) / 2), ($w.B - 3))
    'stage blank'   = @(($v.L + 300), ($v.T + 300))
    'panel title'   = @(($v.R - 500), ($v.T + 45))
}
foreach ($name in $points.Keys) {
    $p = New-Object Win+POINT; $p.X = [int]$points[$name][0]; $p.Y = [int]$points[$name][1]
    $target = [Win]::WindowFromPoint($p)
    $class = New-Object System.Text.StringBuilder 128; [Win]::GetClassName($target, $class, 128) | Out-Null
    $lParam = [IntPtr](($p.Y -shl 16) -bor ($p.X -band 0xFFFF))
    $code = [int][Win]::SendMessage($target, 0x84, [IntPtr]::Zero, $lParam)
    $root = [Win]::GetAncestor($target, 2)
    "{0,-14} -> {1} ({2}) root={3}" -f $name, $class, $(if ($hit.ContainsKey($code)) { $hit[$code] } else { $code }), ($root -eq $h)
}
$bitmap = New-Object System.Drawing.Bitmap (($v.R - $v.L) + 40), (($v.B - $v.T) + 40)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.CopyFromScreen($v.L - 20, $v.T - 20, 0, 0, $bitmap.Size)
$bitmap.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
"saved $Out"
