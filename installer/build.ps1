<#
.SYNOPSIS
    Builds the shippable Nullcast Windows installer.

.DESCRIPTION
    Publishes app-flyleaf as a self-contained x64 build (the .NET 8 runtime is
    baked in, so target machines need no prerequisite), then compiles
    nullcast.iss into a single setup executable.

    The version is read from VideoPlayer.csproj and is the only source of truth
    for it — bump it there (or with app-flyleaf\version.ps1), never here.

.PARAMETER Configuration
    Build configuration to publish. Defaults to Release.

.PARAMETER SkipPublish
    Reuse the existing publish tree and only recompile the installer. Useful
    when iterating on nullcast.iss.

.EXAMPLE
    .\build.ps1
    .\build.ps1 -SkipPublish
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [switch] $SkipPublish
)

$ErrorActionPreference = 'Stop'

$InstallerDir = $PSScriptRoot
$RepoRoot     = Split-Path $InstallerDir -Parent
$AppDir       = Join-Path $RepoRoot 'app-flyleaf'
$Csproj       = Join-Path $AppDir 'VideoPlayer.csproj'
$PublishDir   = Join-Path $AppDir 'bin\publish\win-x64'
$OutputDir    = Join-Path $InstallerDir 'Output'

# ── Version ────────────────────────────────────────────────────────────────
$csprojText = [System.IO.File]::ReadAllText($Csproj)
if ($csprojText -notmatch '<Version>([^<]+)</Version>') {
    throw "No <Version> element found in $Csproj"
}
$Version = $Matches[1].Trim()
Write-Host "Nullcast $Version" -ForegroundColor Cyan

# ── Locate ISCC ────────────────────────────────────────────────────────────
# Inno Setup does not put itself on PATH; probe the usual homes for 7 then 6.
$IsccCandidates = @(
    'C:\Program Files\Inno Setup 7\ISCC.exe'
    'C:\Program Files (x86)\Inno Setup 7\ISCC.exe'
    'C:\Program Files\Inno Setup 6\ISCC.exe'
    'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'
)
$Iscc = $IsccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $Iscc) {
    $onPath = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($onPath) { $Iscc = $onPath.Source }
}
if (-not $Iscc) {
    throw "ISCC.exe not found. Install Inno Setup from https://jrsoftware.org/isdl.php"
}
Write-Host "Using $Iscc"

# ── Publish ────────────────────────────────────────────────────────────────
if ($SkipPublish) {
    if (-not (Test-Path (Join-Path $PublishDir 'VideoPlayer.exe'))) {
        throw "-SkipPublish was given but no publish tree exists at $PublishDir"
    }
    Write-Host "Skipping publish; reusing $PublishDir" -ForegroundColor Yellow
}
else {
    # Publish into a clean directory. Incremental publishes accumulate files
    # from earlier builds — a stale 200 MB libvlc\ folder from a long-removed
    # package reference is exactly how bin\Release grew to 372 MB.
    if (Test-Path $PublishDir) {
        Write-Host "Cleaning $PublishDir"
        Remove-Item $PublishDir -Recurse -Force
    }

    Write-Host "Publishing self-contained win-x64 ($Configuration)..." -ForegroundColor Cyan
    dotnet publish $Csproj `
        -c $Configuration `
        -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=false `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -p:SatelliteResourceLanguages=en `
        -o $PublishDir `
        --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
}

# ── Sanity-check the payload ───────────────────────────────────────────────
# A publish that "succeeds" but drops FFmpeg produces an installer whose app
# cannot play anything, so fail here rather than at the user's first launch.
$required = @(
    'VideoPlayer.exe'
    'VideoPlayer.dll'
    'FFmpeg\avcodec-62.dll'
    'FFmpeg\avformat-62.dll'
    'FFmpeg\avutil-60.dll'
    'telemetry.json'
)
foreach ($rel in $required) {
    if (-not (Test-Path (Join-Path $PublishDir $rel))) {
        throw "Publish output is missing $rel — refusing to build an installer from it."
    }
}
$payloadMb = [math]::Round((Get-ChildItem $PublishDir -Recurse -File |
                            Measure-Object -Property Length -Sum).Sum / 1MB, 1)
Write-Host "Payload: $payloadMb MB" -ForegroundColor Green

# ── Compile the installer ──────────────────────────────────────────────────
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

Write-Host "Compiling installer..." -ForegroundColor Cyan
& $Iscc `
    "/DAppVersion=$Version" `
    "/DPublishDir=$PublishDir" `
    "/DOutputDir=$OutputDir" `
    (Join-Path $InstallerDir 'nullcast.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE" }

$Setup = Join-Path $OutputDir "nullcast-setup-$Version.exe"
$setupMb = [math]::Round((Get-Item $Setup).Length / 1MB, 1)

Write-Host ""
Write-Host "Installer ready: $Setup ($setupMb MB)" -ForegroundColor Green
Write-Host "NOTE: unsigned — SmartScreen will warn on first run for end users." -ForegroundColor Yellow
