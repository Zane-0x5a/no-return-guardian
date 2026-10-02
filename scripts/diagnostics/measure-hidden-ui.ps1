param([int]$HideSeconds = 20)

# 藏到托盘后的资源占用：按正常（不关遮挡检测）的参数启动截图模式，截图后隐藏并休眠页面，再逐进程测 CPU 与内存。
# 游戏在运行时会抢同一块 GPU，测出的数字不能比较，所以直接拒绝。
if (Get-Process -Name 'tlou-ii', 'tlou-ii-l' -ErrorAction SilentlyContinue) { Write-Error 'The game is running; close it before measuring.'; exit 1 }
$exe = "$(Split-Path -Parent (Split-Path -Parent $PSScriptRoot))\src\NoReturnGuardian.App\bin\Release\NoReturnGuardian.exe"
$log = Join-Path $env:TEMP 'render-hide.txt'
$host_ = Start-Process -FilePath $exe -ArgumentList @('--render-ui', "`"$env:TEMP\perf-hide.png`"", '--render-wait', '2500', '--render-hide', $HideSeconds) -PassThru -RedirectStandardError $log
$deadline = (Get-Date).AddSeconds(40)
while (-not ((Test-Path $log) -and (Get-Content $log -Raw) -match 'hidden') -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
Start-Sleep -Seconds 3
$views = @(Get-CimInstance Win32_Process -Filter "Name='msedgewebview2.exe'" | Where-Object { $_.CommandLine -like '*NoReturnGuardian-render-webview2*' })
$all = @($views | ForEach-Object { [pscustomobject]@{ Id = $_.ProcessId; Kind = ([regex]::Match($_.CommandLine, '--type=([a-z-]+)').Groups[1].Value) } }) + [pscustomobject]@{ Id = $host_.Id; Kind = 'host' }
$before = @{}; foreach ($p in $all) { $before[$p.Id] = (Get-Process -Id $p.Id).TotalProcessorTime.TotalMilliseconds }
Start-Sleep -Seconds 6
foreach ($p in $all) {
    $process = Get-Process -Id $p.Id -ErrorAction SilentlyContinue
    if ($process) {
        $kind = if ($p.Kind) { $p.Kind } else { 'browser' }
        "{0,-10} cpu={1,5}% mem={2,4}MB" -f $kind, [math]::Round(($process.TotalProcessorTime.TotalMilliseconds - $before[$p.Id]) / 6000 * 100, 2), [math]::Round($process.WorkingSet64 / 1MB)
    }
}
$null = $host_.WaitForExit(($HideSeconds + 10) * 1000)
