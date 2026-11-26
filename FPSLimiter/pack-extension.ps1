# Builds FPS Limiter and packs a .pext using Playnite's Toolbox.exe.
# Usage (from this folder):
#   .\pack.ps1
#   .\pack.ps1 -Configuration Release
#   .\pack.ps1 -PlaynitePath "D:\Playnite"
#   .\pack.ps1 -SkipBuild   # pack existing bin\Release only

param(
    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Release",

    [string] $PlaynitePath = "",

    [switch] $SkipBuild,

    [string] $OutputDir = ""
)

$ErrorActionPreference = "Stop"
$Root = $PSScriptRoot
$ProjectDir = $Root
$Csproj = Join-Path $ProjectDir "FPSLimiter.csproj"
$BuildOut = Join-Path $ProjectDir "bin\$Configuration"

function Find-PlaynitePath {
    param([string] $Hint)

    if ($Hint -and (Test-Path (Join-Path $Hint "Toolbox.exe"))) {
        return (Resolve-Path $Hint).Path
    }

    $candidates = @(
        $env:PLAYNITE_PATH,
        "$env:LOCALAPPDATA\Playnite",
        "$env:ProgramFiles\Playnite",
        "${env:ProgramFiles(x86)}\Playnite",
        "C:\Playnite"
    ) | Where-Object { $_ }

    foreach ($c in $candidates) {
        if (Test-Path (Join-Path $c "Toolbox.exe")) {
            return (Resolve-Path $c).Path
        }
    }

    return $null
}

function Find-MSBuild {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $vswhere) {
        $found = & $vswhere -latest -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe" 2>$null
        if ($found) {
            return ($found | Select-Object -First 1)
        }
    }

    foreach ($ver in @("18", "2026", "2022", "17.0", "16.0", "15.0")) {
        $p = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\$ver\*\MSBuild\$ver\Bin\MSBuild.exe"
        $hit = Get-Item $p -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($hit) { return $hit.FullName }
    }

    $cmd = Get-Command msbuild -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }

    return $null
}

Write-Host "=== FPS Limiter pack ===" -ForegroundColor Cyan
Write-Host "Root: $Root"

if (-not (Test-Path $Csproj)) {
    throw "Project not found: $Csproj"
}

# --- Build ---
if (-not $SkipBuild) {
    $msbuild = Find-MSBuild
    if (-not $msbuild) {
        throw "MSBuild not found. Install Visual Studio Build Tools or run from a Developer PowerShell, or pass -SkipBuild if binaries already exist."
    }

    Write-Host "MSBuild: $msbuild"
    Write-Host "Building $Configuration..."

    $packagesConfig = Join-Path $ProjectDir "packages.config"
    $sdkDll = Join-Path $ProjectDir "packages\PlayniteSDK.6.16.0\lib\net462\Playnite.SDK.dll"
    if ((Test-Path $packagesConfig) -and -not (Test-Path $sdkDll)) {
        # The packages folder is not committed. Use nuget.exe from PATH, or download it once.
        $nugetExe = $null
        $nugetCmd = Get-Command nuget -ErrorAction SilentlyContinue
        if ($nugetCmd) { $nugetExe = $nugetCmd.Source }
        else {
            $nugetExe = Join-Path $env:TEMP "nuget.exe"
            if (-not (Test-Path $nugetExe)) {
                Write-Host "Downloading nuget.exe..."
                [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
                Invoke-WebRequest -Uri "https://dist.nuget.org/win-x86-commandline/latest/nuget.exe" -OutFile $nugetExe
            }
        }
        Write-Host "Restoring NuGet packages..."
        & $nugetExe restore $Csproj -PackagesDirectory (Join-Path $ProjectDir "packages") | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "NuGet restore failed with exit code $LASTEXITCODE" }
    }

    & $msbuild $Csproj /t:Build /p:Configuration=$Configuration /p:Platform=AnyCPU /v:m /nologo
    if ($LASTEXITCODE -ne 0) {
        throw "MSBuild failed with exit code $LASTEXITCODE"
    }
} else {
    Write-Host "Skipping build (-SkipBuild)."
}

if (-not (Test-Path (Join-Path $BuildOut "FPSLimiter.dll"))) {
    throw "Build output missing FPSLimiter.dll under $BuildOut. Build first or check -Configuration."
}

if (-not (Test-Path (Join-Path $BuildOut "extension.yaml"))) {
    throw "Build output missing extension.yaml under $BuildOut."
}

# The FreeSync helper is mandatory for this version of FPS Limiter.
$FreeSyncExe = Join-Path $BuildOut "FreeSyncControl.exe"
if (-not (Test-Path -LiteralPath $FreeSyncExe -PathType Leaf)) {
    throw "Missing FreeSyncControl.exe in $BuildOut. Build in Release mode first; the FreeSync toggle cannot work without this executable."
}
$RtssIcon = Join-Path $BuildOut "RTSS.ico"
if (-not (Test-Path -LiteralPath $RtssIcon -PathType Leaf)) {
    throw "Missing RTSS.ico in $BuildOut. The supplied RTSS icon is required for the top-bar button."
}
$SkifIcon = Join-Path $BuildOut "SKIF.ico"
if (-not (Test-Path -LiteralPath $SkifIcon -PathType Leaf)) {
    throw "Missing SKIF.ico in $BuildOut. The extracted SKIF icon is required for the Special K top-bar button."
}

# --- Pack ---
$playnite = Find-PlaynitePath -Hint $PlaynitePath
if (-not $playnite) {
    throw "Playnite not found (Toolbox.exe). Install Playnite or pass -PlaynitePath 'C:\Path\To\Playnite'."
}

$toolbox = Join-Path $playnite "Toolbox.exe"
Write-Host "Toolbox: $toolbox"

if (-not $OutputDir) {
    $OutputDir = Join-Path $Root "dist"
}
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

# Pack into a new empty staging directory, so an old .pext can never be
# mistaken for this build. Only copy the package to dist after validation.
$StageDir = Join-Path ([IO.Path]::GetTempPath()) ("FPSLimiter-pack-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $StageDir | Out-Null
Write-Host "Packing $BuildOut"
& $toolbox pack $BuildOut $StageDir
if ($LASTEXITCODE -ne 0) {
    throw "Toolbox pack failed with exit code $LASTEXITCODE"
}

# Toolbox may omit extra files even when they're in the input folder.
# Find the just-produced archive, then verify/insert required files at the root.
$pext = Get-ChildItem -LiteralPath $StageDir -Filter "*.pext" -File |
    Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
if (-not $pext) {
    throw "Toolbox finished but no new .pext was produced."
}

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$helperBytes = [System.IO.File]::ReadAllBytes($FreeSyncExe)
$rtssIconBytes = [System.IO.File]::ReadAllBytes($RtssIcon)
$skifIconBytes = [System.IO.File]::ReadAllBytes($SkifIcon)
$helperHash = [System.Security.Cryptography.SHA256]::Create()
try {
    $expectedHash = [BitConverter]::ToString($helperHash.ComputeHash($helperBytes)).Replace('-', '')
} finally { $helperHash.Dispose() }
$rtssIconHash = [System.Security.Cryptography.SHA256]::Create()
try {
    $expectedRtssIconHash = [BitConverter]::ToString($rtssIconHash.ComputeHash($rtssIconBytes)).Replace('-', '')
} finally { $rtssIconHash.Dispose() }
$skifIconHash = [System.Security.Cryptography.SHA256]::Create()
try {
    $expectedSkifIconHash = [BitConverter]::ToString($skifIconHash.ComputeHash($skifIconBytes)).Replace('-', '')
} finally { $skifIconHash.Dispose() }

$archive = [System.IO.Compression.ZipFile]::Open($pext.FullName, [System.IO.Compression.ZipArchiveMode]::Update)
try {
    # Avoid nested / duplicate helper copies in the installer.
    $existing = @($archive.Entries | Where-Object { $_.FullName -ieq 'FreeSyncControl.exe' })
    foreach ($entry in $existing) { $entry.Delete() }
    $entry = $archive.CreateEntry('FreeSyncControl.exe', [System.IO.Compression.CompressionLevel]::Optimal)
    $stream = $entry.Open()
    try { $stream.Write($helperBytes, 0, $helperBytes.Length) } finally { $stream.Dispose() }

    $existingIcons = @($archive.Entries | Where-Object { $_.FullName -ieq 'RTSS.ico' })
    foreach ($entry in $existingIcons) { $entry.Delete() }
    $iconEntry = $archive.CreateEntry('RTSS.ico', [System.IO.Compression.CompressionLevel]::Optimal)
    $iconStream = $iconEntry.Open()
    try { $iconStream.Write($rtssIconBytes, 0, $rtssIconBytes.Length) } finally { $iconStream.Dispose() }

    $existingSkifIcons = @($archive.Entries | Where-Object { $_.FullName -ieq 'SKIF.ico' })
    foreach ($entry in $existingSkifIcons) { $entry.Delete() }
    $skifIconEntry = $archive.CreateEntry('SKIF.ico', [System.IO.Compression.CompressionLevel]::Optimal)
    $skifIconStream = $skifIconEntry.Open()
    try { $skifIconStream.Write($skifIconBytes, 0, $skifIconBytes.Length) } finally { $skifIconStream.Dispose() }
} finally { $archive.Dispose() }

# Validate the final installer, not merely bin\Release.
$archive = [System.IO.Compression.ZipFile]::OpenRead($pext.FullName)
try {
    $dll = $archive.GetEntry('FPSLimiter.dll')
    $metadata = $archive.GetEntry('extension.yaml')
    $helper = $archive.GetEntry('FreeSyncControl.exe')
    $icon = $archive.GetEntry('RTSS.ico')
    $skifIcon = $archive.GetEntry('SKIF.ico')
    if ($null -eq $dll -or $null -eq $metadata -or $null -eq $helper -or $null -eq $icon -or $null -eq $skifIcon) {
        throw "INVALID .pext: FPSLimiter.dll, extension.yaml, FreeSyncControl.exe, RTSS.ico, or SKIF.ico missing at archive root."
    }
    $stream = $dll.Open()
    $shaDll = [System.Security.Cryptography.SHA256]::Create()
    try { $packageDllHash = [BitConverter]::ToString($shaDll.ComputeHash($stream)).Replace('-', '') }
    finally { $stream.Dispose(); $shaDll.Dispose() }
    $shaDll = [System.Security.Cryptography.SHA256]::Create()
    $stream = [System.IO.File]::OpenRead((Join-Path $BuildOut 'FPSLimiter.dll'))
    try { $builtDllHash = [BitConverter]::ToString($shaDll.ComputeHash($stream)).Replace('-', '') }
    finally { $stream.Dispose(); $shaDll.Dispose() }
    if ($packageDllHash -ne $builtDllHash) { throw "INVALID .pext: packaged DLL does not match this build." }
    $stream = $helper.Open()
    $memory = New-Object System.IO.MemoryStream
    try { $stream.CopyTo($memory); $includedBytes = $memory.ToArray() }
    finally { $stream.Dispose(); $memory.Dispose() }
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { $actualHash = [BitConverter]::ToString($sha.ComputeHash($includedBytes)).Replace('-', '') }
    finally { $sha.Dispose() }
    if ($actualHash -ne $expectedHash) { throw "INVALID .pext: embedded FreeSyncControl.exe differs from build output." }
    $stream = $icon.Open()
    $shaIcon = [System.Security.Cryptography.SHA256]::Create()
    try { $packageIconHash = [BitConverter]::ToString($shaIcon.ComputeHash($stream)).Replace('-', '') }
    finally { $stream.Dispose(); $shaIcon.Dispose() }
    if ($packageIconHash -ne $expectedRtssIconHash) { throw "INVALID .pext: packaged RTSS.ico differs from the supplied icon." }
    $stream = $skifIcon.Open()
    $shaSkifIcon = [System.Security.Cryptography.SHA256]::Create()
    try { $packageSkifIconHash = [BitConverter]::ToString($shaSkifIcon.ComputeHash($stream)).Replace('-', '') }
    finally { $stream.Dispose(); $shaSkifIcon.Dispose() }
    if ($packageSkifIconHash -ne $expectedSkifIconHash) { throw "INVALID .pext: packaged SKIF.ico differs from the extracted icon." }
} finally { $archive.Dispose() }

$finalPackage = Join-Path $OutputDir $pext.Name
Copy-Item -LiteralPath $pext.FullName -Destination $finalPackage -Force
Remove-Item -LiteralPath $StageDir -Recurse -Force
Write-Host ""
Write-Host "Validated .pext: FPSLimiter.dll, extension.yaml, FreeSyncControl.exe, RTSS.ico and SKIF.ico present." -ForegroundColor Green
Write-Host "FreeSyncControl.exe SHA256: $expectedHash"
Write-Host "Packed: $finalPackage" -ForegroundColor Green
Write-Host "Drag this .pext into Playnite (Desktop mode) to install/update."
