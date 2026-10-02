param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$IncludeNativeRecovery,
    # 发布构建：界面依赖总是按 package-lock.json 重新安装，不沿用本机已有的 node_modules。
    [switch]$CleanInstall,
    # 产物目录；package.ps1 用一个全新的目录，日常开发默认覆盖 dist\NoReturnGuardian。
    [string]$Output = (Join-Path (Split-Path -Parent $PSScriptRoot) 'dist\NoReturnGuardian')
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'build-tools.ps1')
$repoRoot = $script:RepoRoot
$msbuild = Find-MSBuild

$iconPath = Join-Path $repoRoot 'src\NoReturnGuardian.App\assets\guardian.ico'
if (-not (Test-Path -LiteralPath $iconPath)) {
    throw 'Icons are missing. Generate them with: python scripts\make-icons.py'
}

$project = Join-Path $repoRoot 'src\NoReturnGuardian.App\NoReturnGuardian.csproj'
# 界面是 src\NoReturnGuardian.Web 构建出的单个 index.html，作为资源编进 exe；先构建它。
$web = Join-Path $repoRoot 'src\NoReturnGuardian.Web'
$npm = Get-Command npm.cmd -ErrorAction SilentlyContinue
if (-not $npm) {
    throw 'npm was not found. Install Node.js to build the interface (src\NoReturnGuardian.Web).'
}
Push-Location $web
try {
    if ($CleanInstall -or -not (Test-Path -LiteralPath (Join-Path $web 'node_modules'))) {
        & $npm.Source ci
        if ($LASTEXITCODE -ne 0) { throw "npm ci failed with exit code $LASTEXITCODE." }
    }
    & $npm.Source run build
    if ($LASTEXITCODE -ne 0) { throw "Interface build failed with exit code $LASTEXITCODE." }
}
finally {
    Pop-Location
}

& $msbuild $project /t:Rebuild /p:Configuration=$Configuration /p:Platform=AnyCPU "/p:FrameworkPathOverride=$(Get-FrameworkPath)" /nologo /verbosity:minimal
if ($LASTEXITCODE -ne 0) {
    throw "Application build failed with exit code $LASTEXITCODE."
}

$dist = [IO.Path]::GetFullPath($Output)
New-Item -ItemType Directory -Path $dist -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $repoRoot "src\NoReturnGuardian.App\bin\$Configuration\NoReturnGuardian.exe") -Destination $dist -Force
$configPath = Join-Path $repoRoot "src\NoReturnGuardian.App\bin\$Configuration\NoReturnGuardian.exe.config"
if (Test-Path -LiteralPath $configPath) {
    Copy-Item -LiteralPath $configPath -Destination $dist -Force
}
foreach ($document in @('README.md', 'LICENSE', 'THIRD_PARTY_NOTICES.md')) {
    $path = Join-Path $repoRoot $document
    if (Test-Path -LiteralPath $path) {
        Copy-Item -LiteralPath $path -Destination $dist -Force
    }
}
# 编进 exe 的第三方组件的许可证全文（清单见 THIRD_PARTY_NOTICES.md）。
$licenses = Join-Path $dist 'licenses'
New-Item -ItemType Directory -Path $licenses -Force | Out-Null
foreach ($license in @(
    @('src\NoReturnGuardian.App\lib\webview2\LICENSE.txt', 'WebView2-LICENSE.txt'),
    @('src\NoReturnGuardian.App\lib\webview2\NOTICE.txt', 'WebView2-NOTICE.txt'),
    @('src\NoReturnGuardian.Web\node_modules\react\LICENSE', 'react-LICENSE.txt'),
    @('src\NoReturnGuardian.Web\node_modules\react-dom\LICENSE', 'react-dom-LICENSE.txt'),
    @('src\NoReturnGuardian.Web\node_modules\scheduler\LICENSE', 'scheduler-LICENSE.txt'),
    @('src\NoReturnGuardian.Web\node_modules\@paper-design\shaders\LICENSE', 'paper-shaders-LICENSE.txt'),
    @('src\NoReturnGuardian.Web\node_modules\@paper-design\shaders\NOTICE', 'paper-shaders-NOTICE.txt'))) {
    Copy-Item -LiteralPath (Join-Path $repoRoot $license[0]) -Destination (Join-Path $licenses $license[1]) -Force
}

if ($IncludeNativeRecovery) {
    & (Join-Path $PSScriptRoot 'build-native-recovery.ps1') -Output $dist
}

Write-Output "Built: $(Join-Path $dist 'NoReturnGuardian.exe')"
