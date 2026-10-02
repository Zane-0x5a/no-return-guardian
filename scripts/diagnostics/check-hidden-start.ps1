param([string]$Exe = "$(Split-Path -Parent (Split-Path -Parent $PSScriptRoot))\dist\NoReturnGuardian\NoReturnGuardian.exe", [string]$Shot = "$(Split-Path -Parent (Split-Path -Parent $PSScriptRoot))\artifacts\ui-redesign-20261001\hidden-start-shown.png")

# 检查最小化启动：不显示窗口、不抢前台，浏览器在隐藏状态下初始化；再像玩家第二次打开那样把窗口叫出来，截下来看页面是否画好，
# 最后关回托盘。需要正在运行的守护器已经由这个 exe 以 --minimized 启动之前退出。
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class HiddenStart {
    public delegate bool EnumProc(IntPtr window, IntPtr data);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc proc, IntPtr data);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr window, uint message, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    public struct Rect { public int Left, Top, Right, Bottom; }
    public static List<IntPtr> VisibleWindows(uint process) {
        var found = new List<IntPtr>();
        EnumWindows((window, data) => {
            uint owner; GetWindowThreadProcessId(window, out owner);
            if (owner == process && IsWindowVisible(window)) found.Add(window);
            return true;
        }, IntPtr.Zero);
        return found;
    }
    public static uint Foreground() { uint process; GetWindowThreadProcessId(GetForegroundWindow(), out process); return process; }
}
'@
Add-Type -AssemblyName System.Drawing
# 按物理像素取窗口尺寸，截图才完整。
$null = [HiddenStart]::SetProcessDPIAware()

if (Get-Process -Name 'tlou-ii', 'tlou-ii-l' -ErrorAction SilentlyContinue) { Write-Error 'The game is running; this check shows a window.'; exit 1 }
if (Get-CimInstance Win32_Process -Filter "Name='NoReturnGuardian.exe'") { Write-Error 'A guardian is already running.'; exit 1 }

$before = [HiddenStart]::Foreground()
$guardian = Start-Process -FilePath $Exe -ArgumentList '--minimized' -WorkingDirectory (Split-Path $Exe) -PassThru
$stolen = $false; $shown = $false
$end = (Get-Date).AddSeconds(12)
while ((Get-Date) -lt $end) {
    if ([HiddenStart]::Foreground() -eq $guardian.Id) { $stolen = $true }
    if ([HiddenStart]::VisibleWindows($guardian.Id).Count -gt 0) { $shown = $true }
    Start-Sleep -Milliseconds 100
}
$browsers = @(Get-CimInstance Win32_Process -Filter "Name='msedgewebview2.exe'" | Where-Object { $_.ParentProcessId -eq $guardian.Id })
"minimized start: foreground before=$before stolen=$stolen window shown=$shown browser processes=$($browsers.Count) running=$(-not $guardian.HasExited)"

# 像玩家再打开一次那样把窗口叫出来。
Start-Process -FilePath $Exe -WorkingDirectory (Split-Path $Exe) | Out-Null
$window = [IntPtr]::Zero
$end = (Get-Date).AddSeconds(10)
while ((Get-Date) -lt $end -and $window -eq [IntPtr]::Zero) {
    $windows = [HiddenStart]::VisibleWindows($guardian.Id)
    if ($windows.Count) { $window = $windows[0] }
    Start-Sleep -Milliseconds 100
}
if ($window -eq [IntPtr]::Zero) { 'second launch: window did not appear'; exit 2 }
Start-Sleep -Milliseconds 1800
$rect = New-Object HiddenStart+Rect
$null = [HiddenStart]::GetWindowRect($window, [ref]$rect)
$bitmap = New-Object System.Drawing.Bitmap ($rect.Right - $rect.Left), ($rect.Bottom - $rect.Top)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$dc = $graphics.GetHdc()
$null = [HiddenStart]::PrintWindow($window, $dc, 2)
$graphics.ReleaseHdc($dc); $graphics.Dispose()
$bitmap.Save($Shot); $bitmap.Dispose()
"second launch: window shown, foreground is guardian=$([HiddenStart]::Foreground() -eq $guardian.Id), captured $Shot"

# 像玩家点关闭那样发 SC_CLOSE：只藏回托盘。（单发 WM_CLOSE 会被 WinForms 当成任务管理器的关闭请求，守护器会真的退出。）
$null = [HiddenStart]::PostMessage($window, 0x0112, [IntPtr]0xF060, [IntPtr]::Zero)
Start-Sleep -Seconds 2
"after close: visible windows=$([HiddenStart]::VisibleWindows($guardian.Id).Count) running=$(-not $guardian.HasExited)"
