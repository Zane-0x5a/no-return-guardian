param([string]$Label = 'run', [int]$Seconds = 8)

# 渲染模式下测界面的 GPU/CPU/内存占用（截图模式关闭了遮挡检测，窗口在屏幕外仍照常渲染）。
# 游戏在运行时会抢同一块 GPU，测出的数字不能比较，所以直接拒绝。
if (Get-Process -Name 'tlou-ii', 'tlou-ii-l' -ErrorAction SilentlyContinue) { Write-Error 'The game is running; close it before measuring.'; exit 1 }
$exe = "$(Split-Path -Parent (Split-Path -Parent $PSScriptRoot))\src\NoReturnGuardian.App\bin\Release\NoReturnGuardian.exe"
$out = Join-Path $env:TEMP "perf-$Label.png"
$host_ = Start-Process -FilePath $exe -ArgumentList @('--render-ui', "`"$out`"", '--render-wait', ([string](($Seconds + 10) * 1000))) -PassThru
Start-Sleep -Seconds 6
$views = @(Get-CimInstance Win32_Process -Filter "Name='msedgewebview2.exe'" | Where-Object { $_.CommandLine -like '*NoReturnGuardian-render-webview2*' })
$ids = @($views.ProcessId) + $host_.Id
$cpuStart = 0; foreach ($id in $ids) { $cpuStart += (Get-Process -Id $id).TotalProcessorTime.TotalMilliseconds }
$samples = (Get-Counter '\GPU Engine(*engtype_3D)\Utilization Percentage' -SampleInterval 1 -MaxSamples $Seconds).CounterSamples
$cpuEnd = 0; foreach ($id in $ids) { $cpuEnd += (Get-Process -Id $id).TotalProcessorTime.TotalMilliseconds }
$memory = 0; foreach ($id in $ids) { $memory += (Get-Process -Id $id).WorkingSet64 }
$gpu = $samples | Where-Object { $ids -contains [int]([regex]::Match($_.InstanceName, 'pid_(\d+)').Groups[1].Value) } |
    Group-Object { [regex]::Match($_.InstanceName, 'pid_(\d+)').Groups[1].Value } |
    ForEach-Object { [math]::Round((($_.Group | Group-Object Timestamp | ForEach-Object { ($_.Group | Measure-Object CookedValue -Sum).Sum }) | Measure-Object -Average).Average, 2) }
"$Label gpu3d=$(( $gpu | Measure-Object -Sum).Sum)% cpu=$([math]::Round(($cpuEnd - $cpuStart) / ($Seconds * 1000) * 100, 1))% of one core mem=$([math]::Round($memory / 1MB))MB processes=$($ids.Count)"
Stop-Process -Id $host_.Id -Force -ErrorAction SilentlyContinue
