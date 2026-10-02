param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'build-tools.ps1')
$repoRoot = $script:RepoRoot
$msbuild = Find-MSBuild

$project = Join-Path $repoRoot 'src\NoReturnGuardian.Tests\NoReturnGuardian.Tests.csproj'
& $msbuild $project /t:Rebuild /p:Configuration=$Configuration /p:Platform=AnyCPU "/p:FrameworkPathOverride=$(Get-FrameworkPath)" /nologo /verbosity:minimal
if ($LASTEXITCODE -ne 0) {
    throw "Test build failed with exit code $LASTEXITCODE."
}

$testExe = Join-Path $repoRoot "src\NoReturnGuardian.Tests\bin\$Configuration\NoReturnGuardian.Tests.exe"
& $testExe
if ($LASTEXITCODE -ne 0) {
    throw "Tests failed with exit code $LASTEXITCODE."
}
