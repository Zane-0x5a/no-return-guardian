param(
    [Parameter(Mandatory = $true)][string]$LogPath,
    # 游戏安装目录（tlou-ii.exe 所在的文件夹）。
    [Parameter(Mandatory = $true)][string]$GameDirectory,
    [ValidateRange(1, 20)][int]$MaximumMinutes = 20
)

$ErrorActionPreference = 'Stop'
$gameDirectory = $GameDirectory
$started = [DateTime]::UtcNow
$deadline = $started.AddMinutes($MaximumMinutes)
$tracked = @{}
$encoding = [System.Text.UTF8Encoding]::new($false)

function Write-Event([string]$message) {
    [System.IO.File]::AppendAllText($LogPath, ([DateTime]::UtcNow.ToString('o') + ' ' + $message + [Environment]::NewLine), $encoding)
}

Write-Event ('ARMED deadline=' + $deadline.ToString('o'))
while ([DateTime]::UtcNow -lt $deadline) {
    foreach ($process in @(Get-Process -Name 'tlou-ii', 'tlou-ii-l' -ErrorAction SilentlyContinue)) {
        try {
            if ([System.IO.Path]::GetDirectoryName($process.Path) -ne $gameDirectory) { continue }
            if ($process.StartTime.ToUniversalTime() -lt $started) { continue }
            if (-not $tracked.ContainsKey($process.Id)) {
                $tracked[$process.Id] = $process.StartTime.ToUniversalTime().Ticks
                Write-Event ('TRACK pid=' + $process.Id)
            }
        } catch { Write-Event ('OBSERVATION_ERROR ' + $_.Exception.Message) }
    }
    if ($tracked.Count -gt 0) {
        $remaining = @($tracked.Keys | Where-Object {
            $candidate = Get-Process -Id $_ -ErrorAction SilentlyContinue
            $candidate -and $candidate.StartTime.ToUniversalTime().Ticks -eq $tracked[$_]
        })
        if ($remaining.Count -eq 0) { Write-Event 'ALL_TRACKED_EXITED'; exit 0 }
    }
    Start-Sleep -Seconds 2
}

foreach ($processId in $tracked.Keys) {
    $candidate = Get-Process -Id $processId -ErrorAction SilentlyContinue
    if (-not $candidate) { continue }
    if ($candidate.StartTime.ToUniversalTime().Ticks -ne $tracked[$processId]) { continue }
    if ([System.IO.Path]::GetDirectoryName($candidate.Path) -ne $gameDirectory) { continue }
    Write-Event ('DEADLINE_STOP pid=' + $processId)
    Stop-Process -InputObject $candidate -Force
    if (-not $candidate.WaitForExit(10000)) { throw 'Game did not exit at safety deadline.' }
}
Write-Event 'DEADLINE_COMPLETE'
