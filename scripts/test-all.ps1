param(
    # 跑 Python 测试的解释器（3.13）；默认用 PATH 上的 python。
    [string]$Python = 'python',
    # 打包测试检查的原生组件；默认 dist\NoReturnGuardian\native-recovery。
    [string]$Runtime = '',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    # 要求重放本机证据的测试一个也不跳过（维护者在有 artifacts\ 的机器上发布前用）。
    [switch]$RequireEvidence
)

# 全部测试：Core（C#）、恢复脚本（Python 与 Node）、原生恢复的 WinForms 夹具。
# 重放实机记录的测试需要本机 artifacts\ 下的证据，没有时跳过（见 scripts\diagnostics\local_evidence.py）；
# 最后的汇总会列出跳过了多少、为什么跳过，不把它们算作已验证。
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'build-tools.ps1')
$repoRoot = $script:RepoRoot
$diagnostics = Join-Path $PSScriptRoot 'diagnostics'

& (Join-Path $PSScriptRoot 'test.ps1') -Configuration $Configuration

# 开发树里的恢复脚本从 tools\ 调用辅助程序、导入 frida；先让它们和当前源码一致。
Build-NativeHelpers $script:DevelopmentTools
Initialize-DevelopmentFrida | Out-Null

$skips = [ordered]@{}
function Add-Skip([string]$Reason, [int]$Count) {
    $skips[$Reason] = $(if ($skips.Contains($Reason)) { $skips[$Reason] } else { 0 }) + $Count
}

$pythonTotals = @{ Run = 0; Skipped = 0 }
if ($Runtime) { $env:NRG_RUNTIME = [IO.Path]::GetFullPath($Runtime) }
try {
    foreach ($start in @($diagnostics, $PSScriptRoot)) {
        $lines = & $Python -X utf8 -B (Join-Path $PSScriptRoot 'run-python-tests.py') $start
        $failed = $LASTEXITCODE -ne 0
        $line = @($lines | Where-Object { $_ -like 'SUMMARY *' }) | Select-Object -Last 1
        if ($failed -or -not $line) { throw "Python tests in $start failed." }
        $summary = $line.Substring(8) | ConvertFrom-Json
        $pythonTotals.Run += $summary.run
        $pythonTotals.Skipped += $summary.skipped
        foreach ($reason in $summary.reasons.PSObject.Properties) { Add-Skip $reason.Name $reason.Value }
    }
}
finally {
    Remove-Item Env:NRG_RUNTIME -ErrorAction SilentlyContinue
}

$node = Get-Command node.exe -ErrorAction SilentlyContinue
if (-not $node) { throw 'Node.js was not found.' }
New-Item -ItemType Directory -Path $script:BuildCache -Force | Out-Null
$tap = Join-Path $script:BuildCache 'node-tests.tap'
& $node.Source --test --test-reporter=spec --test-reporter-destination=stdout --test-reporter=tap "--test-reporter-destination=$tap" `
    @(Get-ChildItem -LiteralPath $diagnostics -Filter 'test_*.cjs' | ForEach-Object FullName)
if ($LASTEXITCODE -ne 0) { throw 'Node tests failed.' }
$report = Get-Content -LiteralPath $tap -Encoding UTF8
$nodeRun = [int](($report | Select-String -Pattern '^# tests (\d+)$').Matches[0].Groups[1].Value)
$nodeSkipped = 0
foreach ($match in ($report | Select-String -Pattern '# SKIP (.+)$').Matches) {
    $nodeSkipped++
    Add-Skip $match.Groups[1].Value 1
}

$app = Join-Path $repoRoot "src\NoReturnGuardian.App\bin\$Configuration\NoReturnGuardian.exe"
if (-not (Test-Path -LiteralPath $app)) { throw "Build the application first (scripts\build.ps1): $app" }
$fixture = Join-Path $script:BuildCache 'TestNativeRecoveryDialog.exe'
& (Find-Roslyn) /nologo /target:exe "/out:$fixture" /reference:System.Web.Extensions.dll /reference:System.Windows.Forms.dll /reference:System.Core.dll (Join-Path $diagnostics 'TestNativeRecoveryDialog.cs')
if ($LASTEXITCODE -ne 0) { throw 'The native recovery fixture did not compile.' }
$interpreter = (Get-Command $Python).Source
& $fixture $app $interpreter
if ($LASTEXITCODE -ne 0) { throw 'The native recovery fixture failed.' }

$skipped = $pythonTotals.Skipped + $nodeSkipped
Write-Output ''
Write-Output "Passed: Core, Python $($pythonTotals.Run - $pythonTotals.Skipped) of $($pythonTotals.Run), Node $($nodeRun - $nodeSkipped) of $nodeRun, native recovery fixture."
if ($skipped -eq 0) {
    Write-Output 'All tests passed, including every replay of local evidence.'
    return
}
Write-Output "$skipped tests were skipped and are NOT verified by this run:"
foreach ($reason in $skips.Keys) { Write-Output ('  {0,3}  {1}' -f $skips[$reason], $reason) }
if ($RequireEvidence) { throw "$skipped tests were skipped; -RequireEvidence needs every replay of local evidence to run." }
