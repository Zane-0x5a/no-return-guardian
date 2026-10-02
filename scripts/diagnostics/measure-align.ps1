param([string[]]$Steps = @('main', 'protect', 'remove', 'native', 'settings', 'toast'))

# 在真实宿主（截图模式）里量对齐：每个 STEP 跑一次 ui-align-probe.js，截图后用 measure-align.py 量墨迹偏移。
# 截图窗口在屏幕外、不激活；这里同时核对前台窗口没有被它抢走。
$ErrorActionPreference = 'Stop'
$root = "$(Split-Path -Parent (Split-Path -Parent $PSScriptRoot))"
$exe = "$root\src\NoReturnGuardian.App\bin\Release\NoReturnGuardian.exe"
$out = "$root\artifacts\ui-redesign-20261001\align"
New-Item -ItemType Directory -Force $out | Out-Null
Add-Type -Namespace Align -Name Win -MemberDefinition @'
[DllImport("user32.dll")] public static extern System.IntPtr GetForegroundWindow();
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(System.IntPtr window, out uint process);
'@
function Foreground { $id = 0; $null = [Align.Win]::GetWindowThreadProcessId([Align.Win]::GetForegroundWindow(), [ref]$id); $id }

# 注释行去掉后压成一行，作为命令行参数传给截图模式。
$source = (Get-Content "$root\scripts\diagnostics\ui-align-probe.js" -Encoding UTF8 | Where-Object { $_.Trim() -notlike '//*' }) -join ' '
foreach ($step in $Steps) {
    $script = $source.Replace("'STEP'", "'$step'").Replace('"', '\"')
    $shot = "$out\$step.png"
    $log = "$out\$step.txt"
    $before = Foreground
    $process = Start-Process -FilePath $exe -ArgumentList @('--render-ui', "`"$shot`"", '--render-wait', '3000', '--render-script', "`"$script`"") -PassThru -RedirectStandardError $log
    try { $process.PriorityClass = 'BelowNormal' } catch {}
    # 整个运行期间每 100ms 看一次前台，只要截图进程当过一次前台就算抢了。
    $stolen = $false
    $deadline = (Get-Date).AddSeconds(90)
    while (-not $process.HasExited -and (Get-Date) -lt $deadline) {
        if ((Foreground) -eq $process.Id) { $stolen = $true }
        Start-Sleep -Milliseconds 100
    }
    "$step foreground before=$before after=$(Foreground) render=$($process.Id) stolen=$stolen"
    python "$root\scripts\diagnostics\measure-align.py" $shot $log
}
