param(
    # 发布到 GitHub 后填仓库地址，安装程序的“支持”“更新”链接指向它。
    [string]$RepositoryUrl = '',
    [string]$Python = 'python',
    [switch]$SkipTests
)

# 发布包：在全新的 out\NoReturnGuardian 里构建（含原生组件），跑全部测试，然后生成
#   out\NoReturnGuardian-<版本>-setup.exe   安装程序（可选安装位置，默认当前用户、无需管理员）
#   out\NoReturnGuardian-<版本>-win-x64.zip 便携版（解压即用）
#   out\SHA256SUMS.txt
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'build-tools.ps1')
$repoRoot = $script:RepoRoot
$version = Get-ProductVersion
$out = Join-Path $repoRoot 'out'
$stage = Join-Path $out 'NoReturnGuardian'
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage -Force | Out-Null

& (Join-Path $PSScriptRoot 'build.ps1') -IncludeNativeRecovery -CleanInstall -Output $stage
if (-not $SkipTests) {
    & (Join-Path $PSScriptRoot 'test-all.ps1') -Python $Python -Runtime (Join-Path $stage 'native-recovery')
}

# 构建时的导入检查不留下 __pycache__；万一有，也不进发布包。
Get-ChildItem -LiteralPath $stage -Recurse -Directory -Filter '__pycache__' | Remove-Item -Recurse -Force

$zip = Join-Path $out "NoReturnGuardian-$version-win-x64.zip"
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [IO.Compression.CompressionLevel]::Optimal, $true)

$iscc = Find-InnoSetup
& $iscc /Qp "/DAppVersion=$version" "/DSourceDir=$stage" "/DAppUrl=$RepositoryUrl" "/O$out" (Join-Path $repoRoot 'installer\NoReturnGuardian.iss')
if ($LASTEXITCODE -ne 0) { throw "Installer build failed with exit code $LASTEXITCODE." }
$setup = Join-Path $out "NoReturnGuardian-$version-setup.exe"

$sums = foreach ($file in @($setup, $zip)) {
    '{0}  {1}' -f (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant(), (Split-Path -Leaf $file)
}
[IO.File]::WriteAllLines((Join-Path $out 'SHA256SUMS.txt'), $sums)
Write-Output "Packaged $version"
$sums | ForEach-Object { Write-Output "  $_" }
