# 构建脚本共用的工具查找与固定依赖下载。由 build.ps1、build-native-recovery.ps1、test.ps1、package.ps1 点引用。

$script:RepoRoot = Split-Path -Parent $PSScriptRoot
$script:BuildCache = Join-Path $script:RepoRoot 'build-cache'

# 原生组件随包带的运行时。换版本时同时改地址和 SHA-256，并重新跑原生恢复的全部测试。
# Python 与实机验证所用的解释器同为 3.13.7；Frida 的 abi3 轮子与开发时用的 _frida.pyd 逐字节相同。
$script:PinnedPython = @{
    Version = '3.13.7'
    Url = 'https://www.python.org/ftp/python/3.13.7/python-3.13.7-embed-amd64.zip'
    Sha256 = 'F6CCA216A359BE84797CABB54149CE5E062AFB16CC7567EB7FC51CACB2D86B65'
}
$script:PinnedFrida = @{
    Version = '17.17.0'
    Url = 'https://files.pythonhosted.org/packages/73/5d/2a06ea09a854581bec9840232c939d1191da8df70ec90d3ac2db930f9e1f/frida-17.17.0-cp37-abi3-win_amd64.whl'
    Sha256 = 'F9C3D431144D143D5EAF6830F0B85042C34517F63B2CF0E83AAA5E2BA4B636DD'
}

function Find-VisualStudioTool([string]$Pattern) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere)) { return $null }
    & $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find $Pattern | Select-Object -First 1
}

function Find-MSBuild {
    $msbuild = Find-VisualStudioTool 'MSBuild\**\Bin\MSBuild.exe'
    if (-not $msbuild) {
        $fallback = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe'
        if (Test-Path -LiteralPath $fallback) { $msbuild = $fallback }
    }
    if (-not $msbuild) { throw 'MSBuild was not found. Install the Visual Studio .NET desktop build tools.' }
    $msbuild
}

function Find-Roslyn {
    # 辅助工具用到较新的 C# 语法，系统自带的 .NET Framework csc 只到 C# 5，必须用 VS 里的 Roslyn。
    $csc = Find-VisualStudioTool 'MSBuild\**\Bin\Roslyn\csc.exe'
    if (-not $csc) { throw 'The Roslyn C# compiler was not found. Install the Visual Studio .NET desktop build tools.' }
    $csc
}

function Get-FrameworkPath {
    Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
}

# 下载到 build-cache，已有且哈希一致就直接用；哈希不符一律报错，不使用。
function Get-PinnedFile([hashtable]$Pin) {
    New-Item -ItemType Directory -Path $script:BuildCache -Force | Out-Null
    $target = Join-Path $script:BuildCache ([IO.Path]::GetFileName(([Uri]$Pin.Url).AbsolutePath))
    if (-not (Test-Path -LiteralPath $target) -or (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $Pin.Sha256) {
        $partial = $target + '.download'
        Invoke-WebRequest -Uri $Pin.Url -OutFile $partial -UseBasicParsing
        $actual = (Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash
        if ($actual -ne $Pin.Sha256) {
            Remove-Item -LiteralPath $partial -Force
            throw "Downloaded $($Pin.Url) has SHA-256 $actual, expected $($Pin.Sha256)."
        }
        Move-Item -LiteralPath $partial -Destination $target -Force
    }
    $target
}

function Expand-Zip([string]$Archive, [string]$Destination) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    [IO.Compression.ZipFile]::ExtractToDirectory($Archive, $Destination)
}

# 开发树里的恢复脚本从 tools\ 调用辅助程序、导入 frida（NativeCheckpointProbe.TOOLS），与 artifacts\ 里的本机证据分开。
$script:DevelopmentTools = Join-Path $script:RepoRoot 'tools'

# frida 没有就从固定的轮子解出来。
function Initialize-DevelopmentFrida {
    $deps = Join-Path $script:DevelopmentTools 'native-probe-deps'
    if (Test-Path -LiteralPath (Join-Path $deps 'frida\_frida.pyd')) { return $deps }
    Expand-Zip (Get-PinnedFile $script:PinnedFrida) $deps
    $deps
}

# 原生恢复用的四个 C# 辅助程序：与守护器共用 Core 源码，校验、抓取和撤销都走同一套规则。
function Build-NativeHelpers([string]$Destination) {
    $csc = Find-Roslyn
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    $coreSources = @(Get-ChildItem -LiteralPath (Join-Path $script:RepoRoot 'src\NoReturnGuardian.Core') -Filter '*.cs' | ForEach-Object FullName)
    foreach ($helper in @('VerifySnapshot', 'CaptureLiveSnapshot', 'CaptureDepartureSnapshot', 'UndoSnapshot')) {
        $source = Join-Path $script:RepoRoot "scripts\$helper.cs"
        & $csc /nologo /target:exe "/out:$(Join-Path $Destination ($helper + '.exe'))" /reference:System.Web.Extensions.dll /reference:System.Core.dll $source $coreSources
        if ($LASTEXITCODE -ne 0) { throw "Native recovery helper build failed: $helper" }
    }
}

function Find-InnoSetup {
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'))
    $iscc = $candidates | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
    if (-not $iscc) {
        $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
        if ($command) { $iscc = $command.Source }
    }
    if (-not $iscc) { throw 'Inno Setup 6 was not found. Install it with: winget install JRSoftware.InnoSetup --scope user' }
    $iscc
}

# 版本号只写在 AssemblyInfo.cs 一处；安装包、zip 和发布标签都从这里取。
function Get-ProductVersion {
    $info = Get-Content -LiteralPath (Join-Path $script:RepoRoot 'src\NoReturnGuardian.App\Properties\AssemblyInfo.cs') -Raw
    $match = [regex]::Match($info, 'AssemblyFileVersion\("(\d+)\.(\d+)\.(\d+)\.\d+"\)')
    if (-not $match.Success) { throw 'AssemblyFileVersion was not found in AssemblyInfo.cs.' }
    '{0}.{1}.{2}' -f $match.Groups[1].Value, $match.Groups[2].Value, $match.Groups[3].Value
}
