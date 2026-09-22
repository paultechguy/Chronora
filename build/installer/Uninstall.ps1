#Requires -Version 5.1

<#
.SYNOPSIS
    Removes a per-user Chronora install.

.DESCRIPTION
    Undoes everything Install.ps1 created: the shortcuts, the Settings > Apps entry, and the
    install directory itself. What it removes is read from the manifest the installer wrote,
    so an install that skipped the desktop shortcut or went to a non-default directory
    uninstalls correctly rather than by guesswork.

    Two things are removed that the installer never created, because nothing else can.

    Send to > Chronora is written by the app itself, from About, so it is not in the manifest.
    Left behind, it sits in every Explorer context menu pointing at an executable that no
    longer exists - and the only thing that could ever have removed it was the About button
    in the app this script just deleted.

    A managed ExifTool, under %LOCALAPPDATA%\PaulTechGuy\Chronora\exiftool, goes whether or
    not -RemoveUserData was asked for. It is not the user's work: it is software Chronora
    downloaded and unpacked, and 33 MB of a third-party Perl distribution left behind after an
    uninstall is littering rather than caution. An ExifTool the user manages themselves is
    somewhere else entirely and is never touched.

    User data - settings, templates, the run journal and logs under
    %LOCALAPPDATA%\PaulTechGuy\Chronora - is deliberately left alone unless -RemoveUserData
    is given. An upgrade is an uninstall followed by an install often enough that silently
    deleting somebody's templates and history would be a bad default, and the script prints
    where the data is so removing it stays a one-line follow-up.

    The script can be started from inside the directory it is about to delete, which is what
    the Uninstall button in Settings > Apps does. It handles that by copying itself to the
    temp directory and re-running from there - see the relaunch below. A .ps1 is read into
    memory before it executes, so deleting the script file mid-run is harmless; the real
    problem is any process holding the directory open as its working directory, and moving
    out of the tree entirely sidesteps every variant of that.

.PARAMETER InstallDir
    The directory to remove. Found from the manifest, then the Settings > Apps entry, then
    the default location, so this is rarely needed.

.PARAMETER RemoveUserData
    Also deletes %LOCALAPPDATA%\PaulTechGuy\Chronora. This is not recoverable: saved
    templates, the run journal that Undo reads, and settings all go with it.

.PARAMETER Force
    Closes a running Chronora that has not responded to a polite request to shut down. A
    running Chronora is always asked to close first, with or without this.

.PARAMETER Quiet
    Suppresses progress output and never pauses. This is what the Settings > Apps entry
    uses for its quiet uninstall path.

.PARAMETER NoPause
    Returns immediately instead of waiting for a key. Uninstall.cmd passes this because it
    does its own pause; the Uninstall button in Settings has no wrapper, so the default is
    to wait and let the user read what happened.

.PARAMETER FromTemp
    Internal. Set on the relaunched copy to stop it relaunching again.

.EXAMPLE
    .\Uninstall.cmd

    Removes the app, keeping settings, templates and run history.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\install\Uninstall.ps1 -RemoveUserData

    Removes the app and everything it ever wrote.

.NOTES
    Exit codes: 0 success, 1 failure.
#>

[CmdletBinding()]
param(
    [string] $InstallDir,

    [switch] $RemoveUserData,

    [switch] $Force,

    [switch] $Quiet,

    [switch] $NoPause,

    [switch] $FromTemp
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$appName = 'Chronora'
$exeName = 'Chronora.exe'
$processName = 'Chronora'
$arpKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Chronora'
$dataDirectory = Join-Path $env:LOCALAPPDATA 'PaulTechGuy\Chronora'

# A managed ExifTool, unpacked here by the app after the user consented to it. It is removed
# whether or not -RemoveUserData was asked for, because it is not user data: it is software
# Chronora installed, and leaving 33 MB of a third-party Perl distribution behind after an
# uninstall is not keeping someone's work safe, it is littering.
#
# An ExifTool the user manages themselves - on PATH, from winget or Chocolatey, or pointed at
# by hand with Browse - is never in this folder and is never touched.
$exifToolDirectory = Join-Path $dataDirectory 'exiftool'

# Written by the app itself, from About, not by the installer - so it is not in the manifest
# and has to be named here. Without this an uninstall leaves "Send to > Chronora" in every
# Explorer context menu, pointing at an executable that no longer exists, and the only thing
# that could ever have removed it was the About button in the app just deleted.
$sendToShortcut = Join-Path ([Environment]::GetFolderPath('SendTo')) "$appName.lnk"
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

function Get-ManifestValue {
    param(
        $Manifest,
        [string] $Name,
        $Default = $null
    )

    # ConvertFrom-Json produces a PSCustomObject, and under Set-StrictMode reading a
    # property that is not there is an error rather than $null. A manifest written by an
    # older installer is a normal thing to meet, so every read goes through here.
    if ($null -ne $Manifest -and $Manifest.PSObject.Properties.Match($Name).Count -gt 0) {
        return $Manifest.$Name
    }

    return $Default
}

function Find-InstallDirectory {
    param([string] $Candidate)

    if ($Candidate) {
        return [System.IO.Path]::GetFullPath($Candidate)
    }

    # This script may be sitting inside the install (the Settings > Apps path) or beside
    # the release payload (the extracted-zip path). The first case answers itself.
    $parent = Split-Path -Parent $PSScriptRoot

    if ($parent -and (Test-Path -LiteralPath (Join-Path $parent $exeName) -PathType Leaf)) {
        return $parent
    }

    try {
        $recorded = (Get-ItemProperty -LiteralPath $arpKey -Name 'InstallLocation' -ErrorAction Stop).InstallLocation

        if ($recorded) {
            return $recorded
        }
    }
    catch {
        # No entry in Settings > Apps. Fall through to the default location.
    }

    return (Join-Path $env:LOCALAPPDATA "Programs\$appName")
}

function Read-Manifest {
    param([string] $Directory)

    $path = Join-Path $Directory "$uninstallFolder\$manifestName"

    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        return $null
    }

    try {
        return Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    }
    catch {
        Write-Warning "The install manifest could not be read; falling back to the default locations. ($($_.Exception.Message))"
        return $null
    }
}

function Invoke-Relaunch {
    param([string] $Directory)

    $stage = Join-Path $env:TEMP ("Chronora-uninstall-" + [Guid]::NewGuid().ToString('N'))
    Copy-Item -LiteralPath $PSScriptRoot -Destination $stage -Recurse -Force

    $psExe = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'

    $arguments = @(
        '-NoProfile'
        '-ExecutionPolicy', 'Bypass'
        '-File', (Join-Path $stage 'Uninstall.ps1')
        '-FromTemp'
        '-InstallDir', $Directory
    )

    if ($RemoveUserData) { $arguments += '-RemoveUserData' }
    if ($Force) { $arguments += '-Force' }
    if ($Quiet) { $arguments += '-Quiet' }
    if ($NoPause) { $arguments += '-NoPause' }

    # Same console, and waited on, so the caller - Settings > Apps, or a shell - sees one
    # window and one exit code rather than a process that returns before it has done
    # anything.
    $process = Start-Process -FilePath $psExe -ArgumentList $arguments -NoNewWindow -Wait -PassThru

    # The staged copy cannot delete itself while it is the running script's home, so the
    # parent cleans it up now that the child has exited.
    Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue

    return $process.ExitCode
}

# Same reasoning as the installer's copy: an orphaned -stay_open ExifTool holds its own folder
# locked, and here that is fatal rather than untidy. The old order removed the Settings entry
# and the program folder BEFORE deleting the data folder, so a lock threw at the last step and
# left the machine with no app, no Settings entry, no uninstaller and a half-deleted data
# folder - with no supported way back.
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
        try { $orphan.Kill() } catch { }
    }
}

function Stop-RunningChronora {
    $running = @(Get-Process -Name $processName -ErrorAction SilentlyContinue)

    if ($running.Count -eq 0) {
        return
    }

    Write-Detail "asking $($running.Count) running instance(s) to close"

    # CloseMainWindow, not Stop-Process - the same request the window's close button sends,
    # so the app shuts down its own way and gets to ask about unsaved documents.
    #
    # This deliberately does not depend on -Quiet. Windows prefers QuietUninstallString
    # where it can, and letting that path hard-kill the app would mean an uninstall
    # started from Settings could discard unsaved work with nothing on screen to say so.
    foreach ($process in $running) {
        try {
            $process.CloseMainWindow() | Out-Null
        }
        catch {
            # Already gone, or no window to close.
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

    if (-not $Force) {
        throw "$appName is still running, and may be asking about unsaved changes. Finish closing it and try again, or pass -Force to close it regardless."
    }

    $remaining | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
}

function Remove-Shortcuts {
    param([string[]] $Paths)

    foreach ($path in $Paths) {
        if ($path -and (Test-Path -LiteralPath $path)) {
            Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
            Write-Detail (Split-Path -Leaf $path)
        }
    }
}

function Remove-InstallDirectory {
    param([string] $Directory)

    if (-not (Test-Path -LiteralPath $Directory)) {
        Write-Detail 'already gone'
        return
    }

    # One retry: a virus scanner or an Explorer window that was looking at the folder a
    # moment ago can hold a handle for a second or two after the app has exited.
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        try {
            Remove-Item -LiteralPath $Directory -Recurse -Force
            return
        }
        catch {
            if ($attempt -eq 3) {
                throw "Could not remove '$Directory'. Close anything using it and try again. ($($_.Exception.Message))"
            }

            Start-Sleep -Seconds 1
        }
    }
}

try {
    $target = Find-InstallDirectory -Candidate $InstallDir

    # Running from inside the directory about to be deleted: restart from the temp copy and
    # let that one do the work.
    if (-not $FromTemp -and $PSScriptRoot.StartsWith($target, [StringComparison]::OrdinalIgnoreCase)) {
        exit (Invoke-Relaunch -Directory $target)
    }

    $manifest = Read-Manifest -Directory $target
    $version = Get-ManifestValue -Manifest $manifest -Name 'version' -Default ''
    $dataDirectory = Get-ManifestValue -Manifest $manifest -Name 'dataDirectory' -Default $dataDirectory

    Write-Plain
    Write-Plain ("Uninstalling $appName $version").TrimEnd()
    Write-Plain

    if (-not (Test-Path -LiteralPath $target) -and -not (Test-Path -LiteralPath $arpKey)) {
        Write-Plain "$appName does not appear to be installed."
        exit 0
    }

    Write-Step '[1/4] Closing the app'
    Stop-RunningChronora

    # Before anything is deleted. A locked ExifTool folder is what turns this from an
    # uninstall into a half-finished one.
    Stop-OrphanedExifTool

    Write-Step '[2/4] Removing shortcuts'

    # Default shortcut locations are removed alongside the recorded ones, so an entry the
    # manifest never captured - written by an older installer, say - does not survive.
    $recorded = @(Get-ManifestValue -Manifest $manifest -Name 'shortcuts' -Default @())
    $defaults = @(
        (Join-Path ([Environment]::GetFolderPath('Programs')) "$appName.lnk")
        (Join-Path ([Environment]::GetFolderPath('Desktop')) "$appName.lnk")
        $sendToShortcut
    )

    Remove-Shortcuts -Paths (@($recorded) + $defaults | Where-Object { $_ } | Select-Object -Unique)

    Write-Step '[3/4] Removing the Settings entry'

    if (Test-Path -LiteralPath $arpKey) {
        Remove-Item -LiteralPath $arpKey -Recurse -Force
    }

    Write-Step '[4/4] Removing the app'
    Remove-InstallDirectory -Directory $target
    Write-Detail $target

    Write-Plain

    # ExifTool goes either way. It is not the user's work, it is software Chronora put there.
    if (Test-Path -LiteralPath $exifToolDirectory) {
        $exifToolSize = (Get-ChildItem -LiteralPath $exifToolDirectory -Recurse -File -Force -ErrorAction SilentlyContinue |
            Measure-Object -Property Length -Sum).Sum

        try {
            Remove-Item -LiteralPath $exifToolDirectory -Recurse -Force
            Write-Plain ("Removed the copy of ExifTool Chronora downloaded ({0:N0} MB)." -f ($exifToolSize / 1MB))
        }
        catch {
            # Said, not thrown. Everything else is already gone and failing here would leave
            # the uninstall looking broken over a folder the user can delete by hand.
            Write-Warning "Could not remove $exifToolDirectory - something still has a file open there."
        }
    }

    if ($RemoveUserData) {
        if (Test-Path -LiteralPath $dataDirectory) {
            Remove-Item -LiteralPath $dataDirectory -Recurse -Force
            Write-Plain "Removed your settings, templates, run history and logs from $dataDirectory"
        }
    }
    elseif (Test-Path -LiteralPath $dataDirectory) {
        Write-Plain 'Your settings, templates and run history were kept, in case this is an upgrade:'
        Write-Plain "  $dataDirectory"
        Write-Plain 'Delete that folder, or re-run this uninstaller with -RemoveUserData, to remove them too.'
    }

    Write-Plain
    Write-Plain "$appName has been uninstalled."
    Write-Plain

    if (-not $Quiet -and -not $NoPause -and [Environment]::UserInteractive) {
        Read-Host 'Press Enter to close'
    }

    exit 0
}
catch {
    # Write-Host rather than Write-Error on purpose. This script is read by someone who
    # clicked Uninstall in Settings, and a PowerShell error record - CategoryInfo,
    # FullyQualifiedErrorId, a caret pointing at the line that rethrew - buries the one
    # sentence that tells them what to do. The exit code still reports the failure.
    Write-Host ''
    Write-Host $_.Exception.Message -ForegroundColor Red
    Write-Host ''

    if (-not $Quiet -and -not $NoPause -and [Environment]::UserInteractive) {
        Read-Host 'Press Enter to close' | Out-Null
    }

    exit 1
}
