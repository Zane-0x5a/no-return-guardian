param(
    [Parameter(Mandatory = $true)][string]$StoragePath,
    [Parameter(Mandatory = $true)][string]$SnapshotId,
    [Parameter(Mandatory = $true)][string]$ProfilePath,
    [switch]$ConfirmPostDeathMenu,
    [switch]$ConfirmPostDeathResults
)

$ErrorActionPreference = 'Stop'
$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
if ($ConfirmPostDeathResults) {
    throw 'Results-page input automation is disabled. Finish results in game, then use -ConfirmPostDeathMenu.'
}
if (-not $ConfirmPostDeathMenu) {
    throw 'Confirm that the game is at the post-death menu with -ConfirmPostDeathMenu.'
}
$repoRoot = Split-Path -Parent $PSScriptRoot
$runtimeRoot = Join-Path $repoRoot 'dist\NoReturnGuardian\native-recovery'
$configurationPath = Join-Path $runtimeRoot 'runtime.json'
if (-not (Test-Path -LiteralPath $configurationPath)) {
    throw 'Build the runtime first with scripts\build-native-recovery.ps1.'
}
$configuration = Get-Content -LiteralPath $configurationPath -Raw -Encoding UTF8 | ConvertFrom-Json
$games = @(Get-Process -Name 'tlou-ii' -ErrorAction SilentlyContinue)
if ($games.Count -ne 1) { throw 'Exactly one tlou-ii game process must be running.' }
$storageRoot = (Resolve-Path -LiteralPath $StoragePath).Path
$selectedProfile = (Resolve-Path -LiteralPath $ProfilePath).Path
$logDirectory = Join-Path $storageRoot 'native-recovery-logs'
New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
$logPath = Join-Path $logDirectory ("recovery-{0}-{1}.jsonl" -f (Get-Date -Format 'yyyyMMdd-HHmmss'), [Guid]::NewGuid().ToString('N'))
$entrypoint = Join-Path $runtimeRoot 'scripts\diagnostics\NativeCheckpointTrigger.py'
& (Join-Path $runtimeRoot $configuration.PythonPath) -X utf8 $entrypoint $games[0].Id --mode recover-preparation --duration 180 --log $logPath --recovery-storage $storageRoot --recovery-snapshot $SnapshotId --recovery-profile $selectedProfile --confirm-intrusive-experiment --entry menu
if ($LASTEXITCODE -ne 0) { throw "Native recovery did not complete. Inspect $logPath and its supervisor log." }
Write-Output "Native rebuild completed; verify the encounter, movement, equipment and resources in game. Log: $logPath"
