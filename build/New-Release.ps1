#Requires -Version 7.0

<#
.SYNOPSIS
    Builds the Chronora release zip: one command, one file, ready to hand to someone else.

.DESCRIPTION
    Restores the third-party web assets if they are missing, publishes the app, stages it
    alongside the installer scripts, and compresses the lot into

        build\artifacts\Chronora-<version>-win-x64.zip

    What comes out is self-contained in every sense. The .NET runtime and the Windows App
    SDK are inside it, so the target machine needs neither. The installer is per-user and
    writes nothing outside HKCU and the user's profile, so it needs no administrator. And
    the app contacts nothing on its own at runtime, so it needs no connection.

    The zip has no wrapper folder. Its contents sit at the root, so extracting it into a
    folder you already made does not bury everything a level deeper, and Explorer's Extract
    All still proposes a destination named after the zip:

        README.txt          what to do, in plain text
        Install.cmd         double-click to install
        Uninstall.cmd       double-click to remove
        install\            the scripts both wrappers call
        app\                the published application

    Tests are opt-in via -Test. Repackaging is something you do repeatedly while getting
    the installer right, and paying for the full suite on every iteration only teaches you
    to stop running the script.

.PARAMETER Configuration
    Build configuration. Release by default, and there is rarely a reason to change it:
    only Release is self-contained and precompiled, so a Debug zip would need the .NET
    runtime already present on the target machine.

.PARAMETER Test
    Runs the test suite before publishing and stops if anything fails. Worth it for a
    release you are actually going to send someone.

.PARAMETER OutputDirectory
    Where the zip is written. Defaults to build\artifacts, which is git-ignored.

.PARAMETER KeepStaging
    Leaves the staged folder in place next to the zip, which is the quickest way to inspect
    exactly what shipped without unzipping it again.

.PARAMETER ShowBuildOutput
    Streams the dotnet output instead of capturing it. The output is shown automatically
    when a step fails; this is for when a step succeeds and you still want to see it.

.EXAMPLE
    pwsh .\build\New-Release.ps1

    The usual invocation.

.EXAMPLE
    pwsh .\build\New-Release.ps1 -Test

    The same, gated on a green test run.

.NOTES
    Exit codes: 0 success, 1 failure.
#>

[CmdletBinding()]
param(
    [string] $Configuration = 'Release',

    [switch] $Test,

    [string] $OutputDirectory,

    [switch] $KeepStaging,

    [switch] $ShowBuildOutput
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Progress output, sizes and the dotnet wrapper are shared with New-ReleaseNotes.ps1 and
# Publish-Release.ps1, so they live in one file rather than three.
. (Join-Path $PSScriptRoot 'ReleaseCommon.ps1')

$repoRoot = Split-Path -Parent $PSScriptRoot
$appProject = Join-Path $repoRoot 'src\PaulTechGuy.CN.App\PaulTechGuy.CN.App.csproj'
$solution = Join-Path $repoRoot 'PaulTechGuy.CN.slnx'
$buildProps = Join-Path $repoRoot 'Directory.Build.props'
$installerSource = Join-Path $PSScriptRoot 'installer'
$manifestScript = Join-Path $PSScriptRoot 'Test-ExifToolManifest.ps1'

$artifacts = if ($OutputDirectory) { [System.IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $PSScriptRoot 'artifacts' }
$staging = Join-Path $artifacts '.stage'
$runtimeIdentifier = 'win-x64'

Initialize-TaskList -Total $(if ($Test) { 5 } else { 4 })

try {
    Write-Host ''
    Write-Host 'Chronora release' -ForegroundColor White
    Write-Host ''

    # ---- exiftool manifest
    #
    # The full -Verify form here, unlike the dev build's offline check. docs\Releasing.md
    # calls this "the release gate": it is the only check that proves the manifest describes
    # the file actually sitting at that URL rather than one that used to. A manifest that has
    # gone stale refuses EVERY ExifTool install, for everyone, from the moment this ships.
    Write-Task 'exiftool manifest'
    $manifestOutput = & pwsh -NoProfile -File $manifestScript -Verify 2>&1

    if ($LASTEXITCODE -ne 0) {
        Write-Failed
        Write-CapturedOutput $manifestOutput
        throw 'The ExifTool manifest is not releasable. Run: pwsh .\build\Test-ExifToolManifest.ps1 -Verify'
    }

    Write-Done 'verified against the URL'

    # ---- tests
    if ($Test) {
        Write-Task 'tests'
        Invoke-Dotnet -Arguments @('test', $solution, '-c', $Configuration, '-warnaserror', '--nologo') -FailureMessage 'Tests failed.' -Stream:$ShowBuildOutput
        Write-Done 'passed'
    }

    # ---- publish
    Write-Task "publish $Configuration"

    if (Test-Path -LiteralPath $staging) {
        Remove-Item -LiteralPath $staging -Recurse -Force
    }

    New-Item -ItemType Directory -Path $staging -Force | Out-Null
    $publishDir = Join-Path $staging 'app'

    Invoke-Dotnet `
        -Arguments @('publish', $appProject, '-c', $Configuration, '-o', $publishDir, '--nologo') `
        -FailureMessage 'The publish failed.' `
        -Stream:$ShowBuildOutput

    $publishedExe = Join-Path $publishDir 'Chronora.exe'

    if (-not (Test-Path -LiteralPath $publishedExe -PathType Leaf)) {
        Write-Failed
        throw "The publish completed but produced no Chronora.exe in '$publishDir'."
    }

    # The app cannot start without these two, and neither is copied by the plain publish
    # targets - see PublishWinUIResources in the app project. Checking here turns a zip
    # that fails on the user's machine with an unresolvable ms-appx:/// URI into a build
    # that fails on this one.
    foreach ($required in @('Chronora.pri', 'App.xbf', 'e_sqlite3.dll', 'exiftool.json')) {
        if (-not (Test-Path -LiteralPath (Join-Path $publishDir $required))) {
            Write-Failed
            throw "The publish is missing '$required'. The app would install but not start."
        }
    }

    # The licensing gate, on the artifact people actually receive. CI runs the same scan, but
    # against its own publish on a GitHub runner - the zip is built here and never passes
    # through CI, so without this the Apache-2.0/GPL rule is enforced only on an artifact
    # nobody ships. exiftool.json is excluded by name because it is ours.
    $stowaways = @(Get-ChildItem -LiteralPath $publishDir -Recurse -Filter 'exiftool*' -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -ne 'exiftool.json' })

    if ($stowaways.Count -gt 0) {
        Write-Failed
        $stowaways | ForEach-Object { Write-Host "    $($_.FullName)" -ForegroundColor Red }
        throw 'ExifTool must never ship with Chronora. Remove it from the publish output.'
    }

    $publishSize = Get-DirectorySize -Path $publishDir
    Write-Done (Format-Size $publishSize)

    # ---- stage
    Write-Task 'stage installer'
    $version = Get-PublishedVersion -Exe $publishedExe -PropsPath $buildProps
    $stagedRoot = New-StagedRelease `
        -PublishDir $publishDir `
        -Version $version `
        -StagingRoot $staging `
        -InstallerSource $installerSource `
        -RepoRoot $repoRoot `
        -RuntimeIdentifier $runtimeIdentifier
    Write-Done "v$version"

    # ---- zip
    Write-Task 'zip'
    New-Item -ItemType Directory -Path $artifacts -Force | Out-Null
    $zipPath = Join-Path $artifacts ("Chronora-$version-$runtimeIdentifier.zip")
    New-ReleaseArchive -StagedRoot $stagedRoot -ZipPath $zipPath

    $zipSize = (Get-Item -LiteralPath $zipPath).Length
    Write-Done (Format-Size $zipSize)

    # A checksum beside the zip, so whoever receives it can confirm it arrived intact.
    # Cheap to produce and the only thing this build can offer in place of a signature.
    $hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
    "$hash  $(Split-Path -Leaf $zipPath)" | Set-Content -LiteralPath "$zipPath.sha256" -Encoding ASCII

    if (-not $KeepStaging) {
        Remove-Item -LiteralPath $staging -Recurse -Force
    }

    Write-Host ''
    Write-Host "  $zipPath" -ForegroundColor White
    Write-Host "  SHA256 $hash" -ForegroundColor DarkGray
    Write-Host ''
    Write-Host '  Copy the zip to the other machine, extract it into a folder, run Install.cmd.' `
        -ForegroundColor DarkGray
    Write-Host ''

    exit 0
}
catch {
    Write-Failure $_.Exception.Message
    exit 1
}
