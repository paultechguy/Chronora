#Requires -Version 5.1

<#
.SYNOPSIS
    Installs Chronora for the current user.

.DESCRIPTION
    Copies the app out of the extracted release folder into %LOCALAPPDATA%\Programs\Chronora,
    then wires up the things that make it feel installed rather than merely copied: a Start
    menu shortcut, an optional desktop shortcut, and an entry in Settings > Apps with a
    working Uninstall button.

    No file associations. Chronora does not own a file type, and the one shell entry it does
    offer - Send to > Chronora - is written by the app itself from the About window, because
    adding something to somebody's shell menus without asking is what installers get disliked
    for. The uninstaller removes it if it is there.

    Everything is per-user. Nothing is written outside HKCU and the user's own profile, so no
    elevation is needed and a locked-down machine is no obstacle. The app already keeps its
    state in %LOCALAPPDATA%\PaulTechGuy\Chronora, so a per-user install is not a compromise
    here - it is the shape the app was already built for.

    Two details are worth knowing about, because both are invisible when they work:

    - Mark of the Web. Files extracted from a downloaded zip carry a zone marker, and an
      unsigned executable carrying one is exactly what SmartScreen blocks with the full-screen
      "Windows protected your PC" panel. Chronora has no code-signing certificate, so instead
      the marker is stripped from the installed copy (see Unblock-Payload). The user vets the
      download once, at the zip; they are not asked again every launch.

    - Stale files. An upgrade removes the old install directory rather than copying over it.
      Overlaying a new build on an old one leaves behind assemblies nothing references any
      more, and a self-contained .NET app that finds two versions of the same assembly fails
      in ways that look nothing like the actual cause. User data lives elsewhere and is never
      touched by this.

    The script is Windows PowerShell 5.1 compatible on purpose. pwsh 7 is not present on a
    stock Windows install, and requiring the user to install a shell before they can install
    the app defeats the point.

.PARAMETER InstallDir
    Where to install. Defaults to %LOCALAPPDATA%\Programs\Chronora. A path outside the user's
    profile will generally need elevation, which is not what this installer is for.

.PARAMETER NoStartMenuShortcut
    Skips the Start menu shortcut.

.PARAMETER NoDesktopShortcut
    Skips the desktop shortcut.

.PARAMETER Force
    Proceeds when the target directory holds something that does not look like a previous
    Chronora install, and closes a running Chronora that has not responded to a polite
    request to shut down. A running Chronora is always asked to close first, with or
    without this.

.PARAMETER Quiet
    Suppresses progress output. Warnings and errors are still written and the exit code still
    reports them.

.EXAMPLE
    .\Install.cmd

    The normal path: double-click, or run from a prompt. Installs with every default.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\install\Install.ps1 -NoDesktopShortcut

    Installs without putting a shortcut on the desktop.

.NOTES
    Exit codes: 0 success, 1 failure.
#>

[CmdletBinding()]
param(
    [string] $InstallDir,

    [switch] $NoStartMenuShortcut,

    [switch] $NoDesktopShortcut,

    [switch] $Force,

    [switch] $Quiet
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$appName = 'Chronora'
$exeName = 'Chronora.exe'
$processName = 'Chronora'
$publisher = 'PaulTechGuy'

# The ARP key name is the identity Windows Settings stores this install under. Renaming it
# later would strand the entry already written on a user's machine.
$arpKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Chronora'

# Matches AppPaths.cs. Kept here as a literal rather than read from the app, because the
# uninstaller has to know it too and neither script can run the app to ask.
$dataDirectory = Join-Path $env:LOCALAPPDATA 'PaulTechGuy\Chronora'

# Where a managed ExifTool is unpacked - AppPaths.ExifToolDirectory. It is under the DATA
# directory, not the install directory: the two trees are disjoint, which is what makes an
# upgrade leave everything alone. It matters here because a -stay_open ExifTool left running
# holds this folder locked, and a locked folder is what turns an uninstall into a half-done
# one.
$exifToolDirectory = Join-Path $dataDirectory 'exiftool'

$manifestName = 'install-manifest.json'
$uninstallFolder = 'uninstall'

function Write-Step {
    param([string] $Message)

    if (-not $Quiet) {
        Write-Host $Message -ForegroundColor Cyan
    }
}

function Write-Detail {
    param([string] $Message)

    if (-not $Quiet) {
        Write-Host "  $Message" -ForegroundColor DarkGray
    }
}

function Write-Plain {
    param([string] $Message = '')

    if (-not $Quiet) {
        Write-Host $Message
    }
}

function Get-PayloadRoot {
    # Install.ps1 lives in install\ inside the extracted release; the app sits beside it
    # in app\. Deriving both from $PSScriptRoot means the release folder can be extracted
    # or renamed anywhere without the installer caring.
    $releaseRoot = Split-Path -Parent $PSScriptRoot
    $payload = Join-Path $releaseRoot 'app'
    $exe = Join-Path $payload $exeName

    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
        throw "This installer expects to find $exeName in '$payload'. Extract the whole release zip, keeping its folder structure, and run Install.cmd from the top of it."
    }

    return $payload
}

function Get-PayloadVersion {
    param([string] $Exe)

    $productVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($Exe).ProductVersion

    if (-not $productVersion) {
        return '0.0.0'
    }

    # A ProductVersion can carry build metadata after a '+'. Settings > Apps shows this
    # string verbatim, and a commit hash in the version column helps nobody.
    return $productVersion.Split('+')[0].Trim()
}

# There is no WebView2 check here, deliberately. Marqora needed one because it renders every
# preview through WebView2; Chronora references it nowhere, and a prerequisite warning about
# a runtime the app never touches is worse than no warning at all.

# ExifTool runs as a -stay_open child and blocks on stdin, so one that has been orphaned has
# no parent to notice and will sit there for ever holding its own folder locked. Chronora now
# puts every child in a job object so this cannot happen again, but a machine upgrading FROM a
# build that did not will still have them - and this installer is exactly what runs on such a
# machine.
#
# They are not named Chronora, so Stop-RunningChronora below cannot see them. They are matched
# by image path instead, so an ExifTool the user runs themselves from somewhere else is left
# entirely alone.
function Stop-OrphanedExifTool {
    $orphans = @(
        Get-Process -Name 'exiftool' -ErrorAction SilentlyContinue |
            Where-Object {
                try { $_.Path -and $_.Path.StartsWith($exifToolDirectory, [StringComparison]::OrdinalIgnoreCase) }
                catch { $false }
            })

    if ($orphans.Count -eq 0) {
        return
    }

    Write-Detail "stopping $($orphans.Count) leftover ExifTool process(es)"

    foreach ($orphan in $orphans) {
        try {
            $orphan.Kill()
        }
        catch {
            # Already gone, or not ours to stop. The copy below reports the real problem if
            # the folder is still locked.
        }
    }
}

function Stop-RunningChronora {
    $running = @(Get-Process -Name $processName -ErrorAction SilentlyContinue)

    if ($running.Count -eq 0) {
        return
    }

    Write-Detail "asking $($running.Count) running instance(s) to close"

    # CloseMainWindow, not Stop-Process. This is the same request the window's close button
    # sends, so the app runs its own shutdown and gets to ask about unsaved documents.
    # Killing it outright would skip that, and an installer that throws away someone's
    # unsaved work to save them one click has made a bad trade on their behalf.
    foreach ($process in $running) {
        try {
            $process.CloseMainWindow() | Out-Null
        }
        catch {
            # No main window, or it exited between the enumeration and here. The wait
            # below settles either case.
        }
    }

    $deadline = (Get-Date).AddSeconds(15)

    while ((Get-Date) -lt $deadline -and @(Get-Process -Name $processName -ErrorAction SilentlyContinue).Count -gt 0) {
        Start-Sleep -Milliseconds 250
    }

    $remaining = @(Get-Process -Name $processName -ErrorAction SilentlyContinue)

    if ($remaining.Count -eq 0) {
        return
    }

    # Still up after fifteen seconds: either a save prompt is sitting there waiting for an
    # answer, or the process is wedged. Which of those it is, and what to do about it, is
    # the user's call - so only -Force makes it.
    if (-not $Force) {
        throw "$appName is still running, and may be asking about unsaved changes. Finish closing it and run this installer again, or pass -Force to close it regardless."
    }

    $remaining | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
}

function Resolve-InstallDirectory {
    param([string] $Candidate)

    if (-not $Candidate) {
        $Candidate = Join-Path $env:LOCALAPPDATA "Programs\$appName"
    }

    $full = [System.IO.Path]::GetFullPath($Candidate)

    if (-not (Test-Path -LiteralPath $full)) {
        return $full
    }

    # About to delete this directory, so be sure it is ours. An empty folder is fine, and
    # so is one holding a previous install. Anything else is a typo in -InstallDir, and
    # deleting it would be an unpleasant way to find that out.
    $hasExe = Test-Path -LiteralPath (Join-Path $full $exeName) -PathType Leaf
    $hasManifest = Test-Path -LiteralPath (Join-Path $full "$uninstallFolder\$manifestName") -PathType Leaf
    $isEmpty = -not (Get-ChildItem -LiteralPath $full -Force -ErrorAction SilentlyContinue | Select-Object -First 1)

    if ($hasExe -or $hasManifest -or $isEmpty) {
        return $full
    }

    if ($Force) {
        Write-Warning "'$full' does not look like a $appName install; -Force was given, so its contents will be replaced."
        return $full
    }

    throw "'$full' already exists and does not look like a $appName install. Choose another -InstallDir, or pass -Force to replace its contents."
}

function Copy-Payload {
    param(
        [string] $Source,
        [string] $Destination
    )

    if (Test-Path -LiteralPath $Destination) {
        Remove-Item -LiteralPath $Destination -Recurse -Force
    }

    New-Item -ItemType Directory -Path $Destination -Force | Out-Null

    # robocopy rather than Copy-Item: the payload is several thousand files and robocopy
    # moves them several times faster with multiple threads. It ships in System32 on every
    # supported Windows, so this is not a new dependency.
    $roboArgs = @($Source, $Destination, '/E', '/MT:8', '/R:2', '/W:1', '/NFL', '/NDL', '/NJH', '/NJS', '/NP')
    $output = & robocopy.exe @roboArgs 2>&1

    # robocopy speaks in a bit field: 0-7 are success, 8 and above are real failures.
    # $LASTEXITCODE is left non-zero on a perfectly good copy, which trips up anything
    # downstream that checks it, so normalize it here.
    $roboExit = $LASTEXITCODE
    $global:LASTEXITCODE = 0

    if ($roboExit -ge 8) {
        throw "Copying the app failed (robocopy exit code $roboExit).`n$($output -join [Environment]::NewLine)"
    }
}

function Unblock-Payload {
    param([string] $Directory)

    # Explorer stamps a Zone.Identifier stream on every file extracted from a downloaded
    # zip, and Copy-Item and robocopy both carry that stream to the destination. Left in
    # place on an unsigned executable it produces the SmartScreen panel on every launch,
    # and on the scripts it defeats the execution policy bypass.
    #
    # Removing it is the whole reason the download is vetted once instead of forever. It
    # is not a security bypass the user did not ask for: they chose to run this installer.
    Get-ChildItem -LiteralPath $Directory -Recurse -File -Force -ErrorAction SilentlyContinue |
        Unblock-File -ErrorAction SilentlyContinue
}

function New-Shortcut {
    param(
        [string] $Path,
        [string] $Target,
        [string] $Description
    )

    $shell = New-Object -ComObject WScript.Shell

    try {
        $shortcut = $shell.CreateShortcut($Path)
        $shortcut.TargetPath = $Target
        $shortcut.WorkingDirectory = Split-Path -Parent $Target
        $shortcut.Description = $Description
        $shortcut.IconLocation = "$Target,0"
        $shortcut.Save()
    }
    finally {
        [System.Runtime.InteropServices.Marshal]::ReleaseComObject($shell) | Out-Null
    }
}

function Write-ArpEntry {
    param(
        [string] $Directory,
        [string] $Version,
        [string] $Exe
    )

    $psExe = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $uninstallScript = Join-Path $Directory "$uninstallFolder\Uninstall.ps1"
    $uninstallString = '"{0}" -NoProfile -ExecutionPolicy Bypass -File "{1}"' -f $psExe, $uninstallScript

    $sizeKb = [int](((Get-ChildItem -LiteralPath $Directory -Recurse -File -Force |
        Measure-Object -Property Length -Sum).Sum) / 1KB)

    if (-not (Test-Path -LiteralPath $arpKey)) {
        New-Item -Path $arpKey -Force | Out-Null
    }

    $strings = [ordered] @{
        DisplayName     = $appName
        DisplayVersion  = $Version
        DisplayIcon     = "$Exe,0"
        Publisher       = $publisher
        InstallLocation = $Directory
        InstallDate     = (Get-Date).ToString('yyyyMMdd')

        # QuietUninstallString is what Windows prefers when it can; without it, some paths
        # fall back to UninstallString and the user gets a console they did not ask for.
        UninstallString      = $uninstallString
        QuietUninstallString = "$uninstallString -Quiet"
    }

    foreach ($name in $strings.Keys) {
        New-ItemProperty -LiteralPath $arpKey -Name $name -Value $strings[$name] -PropertyType String -Force | Out-Null
    }

    # EstimatedSize is in KB and drives the size column in Settings > Apps. NoModify and
    # NoRepair remove the two buttons this installer does not implement, rather than
    # leaving them there to fail.
    $dwords = [ordered] @{
        EstimatedSize = $sizeKb
        NoModify      = 1
        NoRepair      = 1
    }

    foreach ($name in $dwords.Keys) {
        New-ItemProperty -LiteralPath $arpKey -Name $name -Value $dwords[$name] -PropertyType DWord -Force | Out-Null
    }
}

function Write-Manifest {
    param(
        [string] $Directory,
        [string] $Version,
        [string[]] $Shortcuts
    )

    # The uninstaller removes what this install actually created, not what a default
    # install would have created. Without the manifest, an install run with
    # -NoDesktopShortcut and uninstalled later would have the uninstaller hunting for a
    # desktop shortcut that was never there - and, worse, an install into a non-default
    # -InstallDir would leave the uninstaller looking in the wrong place entirely.
    $manifest = [ordered] @{
        schema            = 1
        product           = $appName
        version           = $Version
        installedUtc      = (Get-Date).ToUniversalTime().ToString('o')
        installDir        = $Directory
        shortcuts         = @($Shortcuts)
        arpKey            = $arpKey
        dataDirectory     = $dataDirectory
    }

    $path = Join-Path $Directory "$uninstallFolder\$manifestName"
    $manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $path -Encoding UTF8
}

try {
    $payload = Get-PayloadRoot
    $sourceExe = Join-Path $payload $exeName
    $version = Get-PayloadVersion -Exe $sourceExe

    Write-Plain
    Write-Plain "$appName $version"
    Write-Plain

    $target = Resolve-InstallDirectory -Candidate $InstallDir
    $installedExe = Join-Path $target $exeName
    $upgrading = Test-Path -LiteralPath $installedExe -PathType Leaf

    Write-Step $(if ($upgrading) { "[1/5] Replacing the previous install" } else { "[1/5] Preparing" })
    Write-Detail $target
    Stop-RunningChronora

    # After the app, because a polite shutdown stops its ExifTool for us. This catches the
    # ones left behind by a build that leaked them, which is exactly what an upgrade meets.
    Stop-OrphanedExifTool

    Write-Step '[2/5] Copying files'
    Copy-Payload -Source $payload -Destination $target
    $fileCount = @(Get-ChildItem -LiteralPath $target -Recurse -File -Force).Count
    Write-Detail "$fileCount files"

    Write-Step '[3/5] Removing the download marker'
    Unblock-Payload -Directory $target
    Write-Detail 'the app will start without a SmartScreen prompt'

    # The uninstaller ships inside the install so Settings > Apps has something durable to
    # point at, long after the extracted release folder has been deleted.
    $uninstallDir = Join-Path $target $uninstallFolder
    New-Item -ItemType Directory -Path $uninstallDir -Force | Out-Null

    foreach ($file in @('Uninstall.ps1')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination $uninstallDir -Force
    }

    Write-Step '[4/5] Creating shortcuts'
    $shortcuts = New-Object System.Collections.Generic.List[string]

    if (-not $NoStartMenuShortcut) {
        $startMenu = Join-Path ([Environment]::GetFolderPath('Programs')) "$appName.lnk"
        New-Shortcut -Path $startMenu -Target $installedExe -Description 'Set file and photo dates'
        $shortcuts.Add($startMenu)
        Write-Detail 'Start menu'
    }

    if (-not $NoDesktopShortcut) {
        $desktop = Join-Path ([Environment]::GetFolderPath('Desktop')) "$appName.lnk"
        New-Shortcut -Path $desktop -Target $installedExe -Description 'Set file and photo dates'
        $shortcuts.Add($desktop)
        Write-Detail 'Desktop'
    }

    Write-Step '[5/5] Registering with Windows'
    Write-ArpEntry -Directory $target -Version $version -Exe $installedExe
    Write-Manifest -Directory $target -Version $version -Shortcuts $shortcuts.ToArray()
    Write-Detail 'Settings > Apps > Chronora'

    Write-Plain
    Write-Plain "Installed to $target"
    Write-Plain

    Write-Plain "To remove Chronora later: Settings > Apps > Installed apps > Chronora > Uninstall,"
    Write-Plain "or run Uninstall.cmd from this folder."
    Write-Plain

    exit 0
}
catch {
    # See the note in Uninstall.ps1: a plain sentence in red, not an error record, because
    # the reader is someone installing an app rather than debugging a script.
    Write-Host ''
    Write-Host $_.Exception.Message -ForegroundColor Red
    Write-Host ''
    exit 1
}
