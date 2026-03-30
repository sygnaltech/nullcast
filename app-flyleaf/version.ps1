param(
    [string]$NewVersion
)

$csproj = Join-Path $PSScriptRoot "VideoPlayer.csproj"

if (-not $NewVersion) {
    $content = [System.IO.File]::ReadAllText($csproj)
    if ($content -match '<Version>([^<]+)</Version>') {
        Write-Host $Matches[1]
    } else {
        Write-Error "No <Version> tag found in $csproj"
        exit 1
    }
}
else {
    if ($NewVersion -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') {
        Write-Error "Invalid version '$NewVersion'. Expected Major.Minor.Patch or Major.Minor.Patch.Revision"
        exit 1
    }

    $content = [System.IO.File]::ReadAllText($csproj)

    if ($content -notmatch '<Version>[^<]+</Version>') {
        Write-Error "No <Version> tag found in $csproj"
        exit 1
    }

    $updated = $content -replace '<Version>[^<]+</Version>', "<Version>$NewVersion</Version>"
    $encoding = [System.Text.UTF8Encoding]::new($false)  # UTF-8 without BOM
    [System.IO.File]::WriteAllText($csproj, $updated, $encoding)
    Write-Host "Version updated to $NewVersion"
}
