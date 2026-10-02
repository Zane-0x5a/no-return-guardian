param(
    [Parameter(Mandatory = $true)][string]$Name,
    [string]$Scene = '',
    [string]$Field = 'silk',
    [string]$Script = '',
    [int]$Wait = 3200,
    [int]$Width = 0,
    [int]$Height = 0
)

# 用真实宿主（WebView2）截图界面。带 -Scene 时加载开发服务器的模拟场景，否则用本机真实状态。
$exe = "$(Split-Path -Parent $PSScriptRoot)\src\NoReturnGuardian.App\bin\Release\NoReturnGuardian.exe"
$out = "$(Split-Path -Parent $PSScriptRoot)\artifacts\ui-redesign-20261001\$Name.png"
$arguments = @('--render-ui', $out, '--render-wait', $Wait)
if ($Scene) { $arguments += @('--ui-url', "http://127.0.0.1:5178/?mock&scene=$Scene&field=$Field") }
if ($Script) { $arguments += @('--render-script', $Script) }
if ($Width -gt 0) { $arguments += @('--render-width', $Width, '--render-height', $Height) }
$quoted = $arguments | ForEach-Object { '"' + ($_ -replace '"', '\"') + '"' }
$process = Start-Process -FilePath $exe -ArgumentList $quoted -PassThru
if (-not $process.WaitForExit(90000)) { Stop-Process -Id $process.Id -Force; "$Name timed out"; return }
"$Name exit=$($process.ExitCode) size=$((Get-Item $out -ErrorAction SilentlyContinue).Length)"
