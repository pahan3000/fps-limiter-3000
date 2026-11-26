param(
    [string]$OutputDirectory = "",
    [string]$SdkCacheDirectory = ""
)
$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = (Resolve-Path (Join-Path $here '..\..')).Path
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $projectRoot 'bin\Release' }
if (-not $SdkCacheDirectory) { $SdkCacheDirectory = Join-Path $env:LOCALAPPDATA 'FPSLimiter\adlx-build' }
# Guard against a terminal slash escaping the closing quote of an MSBuild Exec argument.
$OutputDirectory = $OutputDirectory.Trim().Trim('"').TrimEnd('\', '/')
$SdkCacheDirectory = $SdkCacheDirectory.Trim().Trim('"').TrimEnd('\', '/')
New-Item -ItemType Directory -Force -Path $OutputDirectory,$SdkCacheDirectory | Out-Null
$sdkRoot = Join-Path $SdkCacheDirectory 'ADLX-main'
$buildLog = Join-Path $SdkCacheDirectory 'freesync-build.log'
$helper = Join-Path $sdkRoot 'SDK\ADLXHelper\Windows\Cpp\ADLXHelper.cpp'
$platform = Join-Path $sdkRoot 'SDK\Platform\Windows\WinAPIs.cpp'
if (-not ((Test-Path $helper) -and (Test-Path $platform))) {
    $archive = Join-Path $SdkCacheDirectory 'adlx-main.zip'
    Write-Host 'Downloading AMD ADLX source automatically from GPUOpen...'
    Invoke-WebRequest -UseBasicParsing -Uri 'https://github.com/GPUOpen-LibrariesAndSDKs/ADLX/archive/refs/heads/main.zip' -OutFile $archive
    Expand-Archive -Path $archive -DestinationPath $SdkCacheDirectory -Force
    if (-not ((Test-Path $helper) -and (Test-Path $platform))) { throw 'AMD ADLX SDK archive was incomplete.' }
}
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) { throw 'Visual Studio Installer is required; install Desktop development with C++ workload.' }
$vc = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vc) { throw 'Visual Studio C++ x64 tools missing. Install Desktop development with C++ workload using Visual Studio Installer.' }
$vcvars = Join-Path $vc 'VC\Auxiliary\Build\vcvars64.bat'
if (-not (Test-Path $vcvars)) { throw "Missing vcvars64.bat: $vcvars" }
$exe = Join-Path $OutputDirectory 'FreeSyncControl.exe'
$work = Join-Path $SdkCacheDirectory 'obj'
New-Item -ItemType Directory -Force -Path $work | Out-Null
$main = (Join-Path $here 'main.cpp')
$bat = Join-Path $work 'build-adlx.cmd'
# /Fo and /Fe force all build artifacts into temporary cache, not the extension directory.
$cmdText = '@echo off' + "`r`n" + 'call "' + $vcvars + '" >nul' + "`r`n" + 'cd /d "' + $work + '"' + "`r`n" + 'cl /nologo /std:c++17 /EHsc /MD /O2 /I"' + $sdkRoot + '" "' + $main + '" "' + $helper + '" "' + $platform + '" /Fe:"' + $exe + '" /link /MACHINE:X64' + "`r`n" + 'exit /b %errorlevel%' + "`r`n"
[IO.File]::WriteAllText($bat, $cmdText, [Text.Encoding]::ASCII)
Write-Host 'Building AMD FreeSync companion (x64)...'
& cmd.exe /d /c "`"$bat`"" 2>&1 | Tee-Object -FilePath $buildLog
$compilerExit = $LASTEXITCODE
if ($compilerExit -ne 0 -or -not (Test-Path $exe)) { throw "FreeSyncControl.exe compilation failed (exit $compilerExit). Compiler output: $buildLog" }
Write-Host "AMD FreeSync companion ready: $exe"
