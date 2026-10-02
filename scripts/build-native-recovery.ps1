param(
    # 守护器所在目录；原生组件放在它下面的 native-recovery。
    [string]$Output = (Join-Path (Split-Path -Parent $PSScriptRoot) 'dist\NoReturnGuardian')
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'build-tools.ps1')
$repoRoot = $script:RepoRoot

$runtimeRoot = Join-Path ([IO.Path]::GetFullPath($Output)) 'native-recovery'
$runtimeScripts = Join-Path $runtimeRoot 'scripts\diagnostics'
$runtimeTools = Join-Path $runtimeRoot 'tools'
$runtimeDeps = Join-Path $runtimeTools 'native-probe-deps'
$runtimePython = Join-Path $runtimeRoot 'python'
# 旧版本把辅助程序和 frida 放在 artifacts\；同一目录升级时不留着它。
$legacyArtifacts = Join-Path $runtimeRoot 'artifacts'
if (Test-Path -LiteralPath $legacyArtifacts) { Remove-Item -LiteralPath $legacyArtifacts -Recurse -Force }
New-Item -ItemType Directory -Path $runtimeScripts, $runtimeTools, $runtimeDeps, $runtimePython -Force | Out-Null

# 把目标目录同步成源目录的样子：内容相同的跳过，源里没有的删掉。正在运行的记录器会占着 _frida.pyd，
# 内容没变时不必覆盖它。
function Sync-Tree([string]$Source, [string]$Destination) {
    $wanted = @{}
    Get-ChildItem -LiteralPath $Source -Recurse -File | ForEach-Object {
        $relative = $_.FullName.Substring($Source.TrimEnd('\').Length + 1)
        $wanted[$relative] = $true
        $target = Join-Path $Destination $relative
        if ((Test-Path -LiteralPath $target) -and (Get-Item -LiteralPath $target).Length -eq $_.Length `
                -and (Get-FileHash -LiteralPath $target).Hash -eq (Get-FileHash -LiteralPath $_.FullName).Hash) {
            return
        }
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        Copy-Item -LiteralPath $_.FullName -Destination $target -Force
    }
    Get-ChildItem -LiteralPath $Destination -Recurse -File | Where-Object {
        -not $wanted.ContainsKey($_.FullName.Substring($Destination.TrimEnd('\').Length + 1))
    } | Remove-Item -Force
}

# 自带的 Python：官方嵌入式包。._pth 让它处于隔离模式，不读 PYTHONPATH、用户 site 和注册表，
# 也不把脚本所在目录放进 sys.path，所以在这里把恢复脚本和 frida 的目录写进去（相对 python 目录）。
$pythonStage = Join-Path $script:BuildCache ('python-' + $script:PinnedPython.Version + '-embed-amd64')
if (-not (Test-Path -LiteralPath (Join-Path $pythonStage 'python.exe'))) {
    if (Test-Path -LiteralPath $pythonStage) { Remove-Item -LiteralPath $pythonStage -Recurse -Force }
    Expand-Zip (Get-PinnedFile $script:PinnedPython) $pythonStage
}
$pth = Get-ChildItem -LiteralPath $pythonStage -Filter 'python*._pth' | Select-Object -First 1
if (-not $pth) { throw 'The embeddable Python has no ._pth file.' }
$archive = (Get-ChildItem -LiteralPath $pythonStage -Filter 'python*.zip' | Select-Object -First 1).Name
[IO.File]::WriteAllText($pth.FullName, (@($archive, '.', '..\scripts\diagnostics', '..\scripts', '..\tools\native-probe-deps') -join "`r`n") + "`r`n",
    [Text.UTF8Encoding]::new($false))
Sync-Tree $pythonStage $runtimePython

$fridaStage = Join-Path $script:BuildCache ('frida-' + $script:PinnedFrida.Version)
if (-not (Test-Path -LiteralPath (Join-Path $fridaStage 'frida\_frida.pyd'))) {
    if (Test-Path -LiteralPath $fridaStage) { Remove-Item -LiteralPath $fridaStage -Recurse -Force }
    Expand-Zip (Get-PinnedFile $script:PinnedFrida) $fridaStage
}
Sync-Tree $fridaStage $runtimeDeps

Build-NativeHelpers $runtimeTools
# 恢复脚本按清单整体同步：清单之外的旧脚本不留在运行时里。
$scriptStage = Join-Path $script:BuildCache 'native-recovery-scripts'
if (Test-Path -LiteralPath $scriptStage) { Remove-Item -LiteralPath $scriptStage -Recurse -Force }
New-Item -ItemType Directory -Path (Join-Path $scriptStage 'diagnostics') -Force | Out-Null
foreach ($source in @('NativeCheckpointTrigger.py', 'NativeCheckpointProbe.py', 'NativeRecoverySource.py',
    'NativeRecoveryLock.py', 'NativeRecoverySession.py', 'NativeResultsExit.py', 'NativeResultsUi.py',
    'NativeSerializedArena.py', 'NativeSaveBuffer.py',
    'NativeRecoveryContract.js', 'NativeRecoveryBridge.js', 'NativePreparationRecovery.js',
    'NativeResultsContinue.js', 'NativeEncounterRestart.py', 'NativeEncounterControl.js', 'NativeCodeSignature.py',
    'NativeDepartureRecord.py', 'NativeDepartureStage.py', 'NativeDepartureRecorder.py')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "diagnostics\$source") -Destination (Join-Path $scriptStage 'diagnostics') -Force
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'InspectPeVa.py') -Destination $scriptStage -Force
Sync-Tree $scriptStage (Join-Path $runtimeRoot 'scripts')

$configuration = [ordered]@{
    PythonPath = 'python\python.exe'
    PythonVersion = $script:PinnedPython.Version
    FridaVersion = $script:PinnedFrida.Version
    Experimental = $true
}
[IO.File]::WriteAllText((Join-Path $runtimeRoot 'runtime.json'), ($configuration | ConvertTo-Json), [Text.UTF8Encoding]::new($false))

# 用自带的解释器把恢复脚本和 frida 各导入一遍：路径或依赖缺了，在构建时就失败，而不是在玩家按下恢复时。
$python = Join-Path $runtimePython 'python.exe'
$check = 'import sys, frida, NativeCheckpointTrigger, NativeDepartureRecorder, NativeRecoverySource, NativeResultsExit; ' +
    'print(sys.version.split()[0], frida.__version__)'
Push-Location $runtimeRoot
try {
    $versions = & $python -X utf8 -B -c $check
    if ($LASTEXITCODE -ne 0) { throw 'The bundled Python could not import the native recovery scripts.' }
}
finally {
    Pop-Location
}
if ($versions -ne ($script:PinnedPython.Version + ' ' + $script:PinnedFrida.Version)) {
    throw "The bundled runtime reports '$versions'."
}
Write-Output "Built native recovery ($versions): $runtimeRoot"
