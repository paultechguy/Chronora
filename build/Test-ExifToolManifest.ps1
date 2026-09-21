<#
.SYNOPSIS
    Checks that docs/exiftool.json names a real, verifiable ExifTool download.

.DESCRIPTION
    Chronora never bundles ExifTool. It offers to fetch the version named in
    docs/exiftool.json and refuses to install anything whose SHA256 does not match. That
    makes the manifest load-bearing in a way a normal config file is not:

      A placeholder hash means EVERY download is refused, for every user, from the moment
      the release goes out. The app is not broken in a way anyone can diagnose - it just
      reports that the download did not match its checksum, which reads like a security
      problem rather than an unfinished file.

    The file ships with a zero hash on purpose, so that forgetting to fill it in fails
    here rather than in front of a user.

    Offline by default, so it is cheap enough to run on every build. -Verify additionally
    downloads the archive and hashes it, which is the only check that proves the manifest
    describes the file actually sitting at that URL.

.PARAMETER Check
    Report only and exit non-zero on failure. The CI and release-gate form.

.PARAMETER Verify
    Also download the archive and confirm its hash. Needs network.
#>
[CmdletBinding()]
param(
    [switch] $Check,
    [switch] $Verify
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$manifestPath = Join-Path $repoRoot 'docs/exiftool.json'

function Fail([string] $message) {
    Write-Host "  FAIL  $message" -ForegroundColor Red
    exit 1
}

Write-Host "ExifTool manifest: $manifestPath"

if (-not (Test-Path -LiteralPath $manifestPath)) {
    Fail "docs/exiftool.json is missing. The app reads it to know what it may offer to install."
}

try {
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
}
catch {
    Fail "docs/exiftool.json is not valid JSON: $($_.Exception.Message)"
}

foreach ($field in @('version', 'url', 'sha256')) {
    if (-not $manifest.PSObject.Properties.Name.Contains($field)) {
        Fail "docs/exiftool.json has no '$field'."
    }
}

$sha = "$($manifest.sha256)".Trim()

if ($sha -notmatch '^[0-9a-fA-F]{64}$') {
    Fail "sha256 is not 64 hex characters. Found: '$sha'"
}

# The placeholder the file ships with. This is the check that exists to catch the one
# mistake that breaks every install at once.
if ($sha -match '^0{64}$') {
    Fail @'
sha256 is still the placeholder (all zeros).

Every download would be refused, for every user, with a checksum error that reads like a
security problem rather than an unfinished release.

To fix it:
    $url = (Get-Content docs/exiftool.json -Raw | ConvertFrom-Json).url
    Invoke-WebRequest $url -OutFile $env:TEMP\exiftool.zip
    (Get-FileHash $env:TEMP\exiftool.zip -Algorithm SHA256).Hash

Put that hash in docs/exiftool.json, then run this script with -Verify.
'@
}

if ("$($manifest.url)" -notmatch '^https://') {
    Fail "url must be https. The hash is only as trustworthy as the channel that names it."
}

Write-Host "  OK    version $($manifest.version)"
Write-Host "  OK    $($manifest.url)"
Write-Host "  OK    sha256 is present and is not the placeholder"

if (-not $Verify) {
    Write-Host ""
    Write-Host "Offline check only. Run with -Verify before a release to confirm the hash" -ForegroundColor Yellow
    Write-Host "matches the file actually sitting at that URL." -ForegroundColor Yellow
    exit 0
}

$temp = Join-Path ([System.IO.Path]::GetTempPath()) "chronora-manifest-$([guid]::NewGuid().ToString('N')).zip"

try {
    Write-Host "  ...   downloading to verify"

    # curl.exe, not Invoke-WebRequest, and for the reason already written down in
    # Get-ExifTool.ps1: SourceForge answers some client shapes with an HTML interstitial
    # instead of the file. This script used Invoke-WebRequest and so hashed the
    # interstitial - measured at 139,123 bytes beginning "<!doctype html" - and reported a
    # checksum mismatch against a manifest that was perfectly correct.
    #
    # That is the exact failure the header of this file warns about: it reads like
    # tampering rather than a wrong URL, and it would have blocked the first release.
    curl.exe --silent --show-error --location --max-time 300 --output $temp $manifest.url

    if ($LASTEXITCODE -ne 0) {
        Fail "The download failed (curl exit $LASTEXITCODE)."
    }

    # Checked before the hash, so a moved URL is reported as a moved URL. A hash mismatch
    # is a much more alarming sentence and should be reserved for a real one.
    $magic = [System.IO.File]::ReadAllBytes($temp)[0..1]

    if ($magic[0] -ne 0x50 -or $magic[1] -ne 0x4B) {
        Fail @"
What came back from the manifest URL is not a zip.

  url  $($manifest.url)

The URL has probably moved. This is not a hash problem - nothing was tampered with.
"@
    }

    $actual = (Get-FileHash -LiteralPath $temp -Algorithm SHA256).Hash

    if ($actual -ne $sha.ToUpperInvariant()) {
        Fail @"
The archive at the manifest URL does not match the recorded hash.

  expected  $($sha.ToUpperInvariant())
  actual    $actual

Either the upstream file changed, or the manifest was not updated after the version was
bumped. Both mean every install would be refused.
"@
    }

    Write-Host "  OK    the archive at that URL matches the recorded hash"
}
finally {
    if (Test-Path -LiteralPath $temp) {
        Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue
    }
}

Write-Host ""
Write-Host "Manifest is releasable." -ForegroundColor Green
exit 0
