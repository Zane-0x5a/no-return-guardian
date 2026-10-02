param(
    # 要测的安装程序；默认 out\ 里当前版本的那份。
    [string]$Setup = '',
    # 本机测试：会真的启动、退出守护器，它读写的是这台机器上 %LOCALAPPDATA%\NoReturnGuardian 里的真实数据。
    [switch]$AllowLocal
)

# 安装程序冒烟测试：静默安装到临时目录，在守护器运行时升级，模拟不响应 --exit 的旧守护器看安装程序拒绝，
# 最后在守护器运行时卸载；核对文件、卸载登记、开机启动项和保留的玩家数据。
# 守护器读写的数据目录无法重定向，所以默认只在 CI 的 Windows 虚拟机里跑；有守护器在运行时一律拒绝。
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'build-tools.ps1')
if ($env:GITHUB_ACTIONS -ne 'true' -and -not $AllowLocal) {
    throw 'The installer smoke test starts and stops a real Guardian. It runs in CI; pass -AllowLocal to run it here.'
}
if (Get-Process -Name NoReturnGuardian -ErrorAction SilentlyContinue) {
    throw 'A Guardian is running on this machine; the smoke test would stop it. Quit it first.'
}
if (-not $Setup) { $Setup = Join-Path $script:RepoRoot "out\NoReturnGuardian-$(Get-ProductVersion)-setup.exe" }
$Setup = [IO.Path]::GetFullPath($Setup)
if (-not (Test-Path -LiteralPath $Setup)) { throw "Installer not found: $Setup" }

$appId = '{295032DA-7333-4CA6-B405-D53B639C79A9}_is1'
$uninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$appId"
if (Test-Path -LiteralPath $uninstallKey) {
    throw 'Guardian is installed for this user; the smoke test installs under the same AppId and would replace it.'
}
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$mutexName = 'Local\NoReturnGuardian-2FE084E5-A526-474D-9E78-22F0068C78A1'
$root = Join-Path ([IO.Path]::GetTempPath()) ('nrg-installer-' + [guid]::NewGuid().ToString('N'))
$app = Join-Path $root 'app'
New-Item -ItemType Directory -Path $root -Force | Out-Null
$previousRun = (Get-ItemProperty -LiteralPath $runKey -Name NoReturnGuardian -ErrorAction SilentlyContinue).NoReturnGuardian
# 新机器上守护器启动后不一定写下玩家数据，卸载前放一个哨兵文件代替它。
$data = Join-Path $env:LOCALAPPDATA 'NoReturnGuardian'
$dataExisted = Test-Path -LiteralPath $data
$sentinel = Join-Path $data ('installer-smoke-' + [guid]::NewGuid().ToString('N') + '.txt')

function Assert-That([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "Installer smoke test failed: $Message" }
    Write-Output "  ok  $Message"
}

function Invoke-Setup([string]$Name) {
    $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER', "/DIR=`"$app`"", "/LOG=`"$(Join-Path $root "$Name.log")`"")
    (Start-Process -FilePath $Setup -ArgumentList $arguments -Wait -PassThru).ExitCode
}

function Test-GuardianRunning {
    $mutex = $null
    if ([Threading.Mutex]::TryOpenExisting($mutexName, [ref]$mutex)) { $mutex.Dispose(); return $true }
    $false
}

function Start-Guardian {
    Start-Process -FilePath (Join-Path $app 'NoReturnGuardian.exe') -ArgumentList '--minimized' | Out-Null
    for ($index = 0; $index -lt 100 -and -not (Test-GuardianRunning); $index++) { Start-Sleep -Milliseconds 200 }
    Assert-That (Test-GuardianRunning) 'the installed Guardian starts'
}

function Wait-Until([scriptblock]$Condition, [int]$Seconds) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    while (-not (& $Condition) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 250 }
    & $Condition
}

# 失败时打印安装日志和守护器日志：临时目录随后就删掉了，CI 上只剩这里的输出。
function Write-Diagnostics {
    foreach ($log in @(Get-ChildItem -LiteralPath $root -Filter '*.log' -ErrorAction SilentlyContinue)) {
        Write-Output "---- $($log.Name), last 40 lines"
        Get-Content -LiteralPath $log.FullName -Tail 40
    }
    $guardianLog = Join-Path $data 'guardian.log'
    if (Test-Path -LiteralPath $guardianLog) {
        Write-Output '---- guardian.log, last 40 lines'
        Get-Content -LiteralPath $guardianLog -Tail 40
    }
}

try {
    Write-Output "Installer: $Setup"
    $code = Invoke-Setup 'install'
    Assert-That ($code -eq 0) "a fresh per-user install succeeds (exit $code)"
    foreach ($file in @('NoReturnGuardian.exe', 'LICENSE', 'THIRD_PARTY_NOTICES.md', 'native-recovery\runtime.json',
            'native-recovery\python\python.exe', 'native-recovery\tools\VerifySnapshot.exe',
            'native-recovery\tools\native-probe-deps\frida\_frida.pyd', 'native-recovery\scripts\diagnostics\NativeDepartureRecorder.py')) {
        Assert-That (Test-Path -LiteralPath (Join-Path $app $file)) "installed $file"
    }
    Assert-That ((Get-ItemProperty -LiteralPath $uninstallKey).InstallLocation.TrimEnd('\') -eq $app) 'the uninstall entry points at the install'

    # 升级：守护器在运行，安装程序用 --exit 请它退出；原生组件整体替换，旧文件不留下。
    Start-Guardian
    Set-ItemProperty -LiteralPath $runKey -Name NoReturnGuardian -Value "`"$(Join-Path $app 'NoReturnGuardian.exe')`" --minimized"
    Set-Content -LiteralPath (Join-Path $app 'native-recovery\stale.txt') -Value 'left by an older version'
    $code = Invoke-Setup 'upgrade'
    Assert-That ($code -eq 0) "an upgrade over a running Guardian succeeds (exit $code)"
    Assert-That (-not (Test-GuardianRunning)) 'the upgrade asked the running Guardian to exit'
    Assert-That (-not (Test-Path -LiteralPath (Join-Path $app 'native-recovery\stale.txt'))) 'the upgrade replaces the native components as a whole'

    # 不响应 --exit 的旧守护器：安装程序等不到它退出，就拒绝安装，什么也不改。
    $holder = [Threading.Mutex]::new($true, $mutexName)
    try {
        $code = Invoke-Setup 'refused'
        Assert-That ($code -eq 7) "setup refuses while a Guardian that ignores --exit runs (exit $code)"
    }
    finally {
        $holder.ReleaseMutex()
        $holder.Dispose()
    }

    # 卸载：守护器在运行，卸载程序先请它退出；只删指向这份安装的开机启动，玩家数据保留。
    Start-Guardian
    New-Item -ItemType Directory -Path $data -Force | Out-Null
    Set-Content -LiteralPath $sentinel -Value 'stands in for the player data'
    Start-Process -FilePath (Join-Path $app 'unins000.exe') -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait
    Assert-That (Wait-Until { -not (Test-Path -LiteralPath $uninstallKey) -and -not (Test-Path -LiteralPath (Join-Path $app 'NoReturnGuardian.exe')) } 60) 'uninstall removes the program and its entry'
    Assert-That (-not (Test-GuardianRunning)) 'uninstall asked the running Guardian to exit'
    Assert-That ($null -eq (Get-ItemProperty -LiteralPath $runKey -Name NoReturnGuardian -ErrorAction SilentlyContinue)) 'uninstall removes the startup entry that pointed at it'
    Assert-That (Test-Path -LiteralPath $sentinel) 'uninstall keeps the player data'
    Write-Output 'Installer smoke test passed.'
}
catch {
    Write-Diagnostics
    throw
}
finally {
    Get-Process -Name NoReturnGuardian -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$app*" } | Stop-Process -Force
    # 中途失败时也不留下这次安装：卸载登记、指向它的开机启动。
    $uninstaller = Join-Path $app 'unins000.exe'
    if ((Test-Path -LiteralPath $uninstallKey) -and (Test-Path -LiteralPath $uninstaller)) {
        Start-Process -FilePath $uninstaller -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait
        Wait-Until { -not (Test-Path -LiteralPath $uninstallKey) } 60 | Out-Null
    }
    $current = (Get-ItemProperty -LiteralPath $runKey -Name NoReturnGuardian -ErrorAction SilentlyContinue).NoReturnGuardian
    if ($null -ne $previousRun) {
        Set-ItemProperty -LiteralPath $runKey -Name NoReturnGuardian -Value $previousRun
    }
    elseif ($null -ne $current -and $current -like "*$app*") {
        Remove-ItemProperty -LiteralPath $runKey -Name NoReturnGuardian
    }
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $sentinel -Force -ErrorAction SilentlyContinue
    if (-not $dataExisted) { Remove-Item -LiteralPath $data -Recurse -Force -ErrorAction SilentlyContinue }
}
