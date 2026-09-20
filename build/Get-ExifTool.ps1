<#
.SYNOPSIS
    Fetches a local ExifTool for the integration tests. Developer machines only.

.DESCRIPTION
    Chronora never ships ExifTool. This puts a copy in tools/exiftool/ - which is
    gitignored and never reaches the build output - so the integration tests have something
    real to run against.

    Those tests matter more than most. Four behaviours the app depends on are, by the
    upstream documentation's own account, either unstated or contradicted by what the API
    looks like it does:

      * -overwrite_original renames a temp file over the original and gives it a NEW
        creation time. Chronora uses -overwrite_original_in_place instead, and the only
        proof that works is a real file on a real NTFS volume.
      * A time zone written into an EXIF date tag is silently discarded.
      * Writing a date does NOT clear an existing sub-second or offset tag. This one was
        read out of Exif.pm rather than any documentation, so it needs checking against a
        real JPEG more than anything else here.
      * QuickTime date atoms hold local time about as often as they hold UTC.

    It downloads the version named in docs/exiftool.json and refuses anything whose hash
    does not match, exactly as the app does.

.PARAMETER Force
    Re-download even if a copy is already there.
#>
[CmdletBinding()]
param(
    [switch] $Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$manifestPath = Join-Path $repoRoot 'docs/exiftool.json'
$toolsDir = Join-Path $repoRoot 'tools/exiftool'
$exePath = Join-Path $toolsDir 'exiftool.exe'

if ((Test-Path -LiteralPath $exePath) -and -not $Force) {
    $version = (& $exePath -ver) 2>$null
    Write-Host "ExifTool $version is already at $exePath" -ForegroundColor Green
    Write-Host "Run with -Force to replace it."
    exit 0
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json

Write-Host "Fetching ExifTool $($manifest.version)"
Write-Host "  from $($manifest.url)"

$temp = Join-Path ([System.IO.Path]::GetTempPath()) "chronora-exiftool-$([guid]::NewGuid().ToString('N')).zip"

try {
    # Invoke-WebRequest is deliberately not used here. SourceForge, which is where
    # exiftool.org now sends its downloads, answers some client shapes with an HTML
    # interstitial rather than the file - which would then fail the hash check and look
    # like tampering rather than a wrong URL.
    curl.exe --silent --show-error --location --max-time 300 --output $temp $manifest.url

    if ($LASTEXITCODE -ne 0) {
        throw "The download failed (curl exit $LASTEXITCODE)."
    }

    $magic = [System.IO.File]::ReadAllBytes($temp)[0..1]

    if ($magic[0] -ne 0x50 -or $magic[1] -ne 0x4B) {
        throw "What came back was not a zip. The URL in docs/exiftool.json has probably moved."
    }

    $actual = (Get-FileHash -LiteralPath $temp -Algorithm SHA256).Hash

    if ($actual -ne "$($manifest.sha256)".ToUpperInvariant()) {
        throw @"
The archive does not match the hash in docs/exiftool.json.

  expected  $("$($manifest.sha256)".ToUpperInvariant())
  actual    $actual
"@
    }

    Write-Host "  hash matches the manifest" -ForegroundColor Green

    if (Test-Path -LiteralPath $toolsDir) {
        Remove-Item -LiteralPath $toolsDir -Recurse -Force
    }

    New-Item -ItemType Directory -Force -Path $toolsDir | Out-Null

    $staging = Join-Path ([System.IO.Path]::GetTempPath()) "chronora-unpack-$([guid]::NewGuid().ToString('N'))"
    Expand-Archive -LiteralPath $temp -DestinationPath $staging -Force

    # The archive holds a single versioned folder; its contents are what we want.
    $inner = Get-ChildItem -LiteralPath $staging -Directory | Select-Object -First 1
    $source = if ($inner) { $inner.FullName } else { $staging }

    Get-ChildItem -LiteralPath $source -Force | Move-Item -Destination $toolsDir -Force

    # The Windows build ships as "exiftool(-k).exe". The -k means "pause before exiting",
    # so run under that name it waits for a keypress that never comes and the stay-open
    # protocol hangs on the first command.
    $shipped = Join-Path $toolsDir 'exiftool(-k).exe'

    if (Test-Path -LiteralPath $shipped) {
        Move-Item -LiteralPath $shipped -Destination $exePath -Force
    }

    if (-not (Test-Path -LiteralPath $exePath)) {
        throw "No exiftool.exe after unpacking. The archive layout has changed."
    }

    # It is useless without exiftool_files beside it: that folder is the Perl runtime, and
    # the exe on its own starts and dies immediately.
    if (-not (Test-Path -LiteralPath (Join-Path $toolsDir 'exiftool_files'))) {
        throw "exiftool_files is missing. The exe cannot run without it."
    }

    Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue

    $version = (& $exePath -ver)
    Write-Host ""
    Write-Host "ExifTool $version is ready at $exePath" -ForegroundColor Green
    Write-Host "The integration tests find it there automatically."
}
finally {
    if (Test-Path -LiteralPath $temp) {
        Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue
    }
}
