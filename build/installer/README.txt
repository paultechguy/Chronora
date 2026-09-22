Chronora {{VERSION}}
Sets file and photo dates on Windows. Preview every change, then undo it.

===========================================================================
INSTALL
===========================================================================

  1. Extract this whole zip into a folder of its own. Keep the folder
     structure.
  2. Double-click Install.cmd.
  3. That's it. Chronora is in your Start menu and on your desktop.

Nothing is installed for other users, nothing needs an administrator, and
you will not be asked for a password. The app goes in your own profile at:

  %LOCALAPPDATA%\Programs\Chronora

You can delete this extracted folder once the install has finished.


---------------------------------------------------------------------------
Photo and video dates need ExifTool
---------------------------------------------------------------------------

File dates - Created and Modified - work out of the box. Nothing else is
needed for them.

The date a photo or video records INSIDE itself is different. Reading and
writing that needs ExifTool, by Phil Harvey, which Chronora does not ship
and never will: ExifTool is GPL/Artistic and Chronora is Apache-2.0, so no
part of it is in this archive or in the repository.

Chronora can fetch it for you, once, if you ask. It downloads only the exact
version named in its manifest and refuses anything whose SHA-256 does not
match. If you already have ExifTool - on PATH, from winget or Chocolatey -
Chronora will find and use that instead, and will not manage, update or
remove a copy that is yours.

Without ExifTool, every file-date feature works exactly as it does with it.
The photo and video fields simply say the helper is not available.

Note that Windows Explorer does not display a "Date taken" for PNG files,
whatever wrote it. Chronora writes the tag correctly and photo applications
read it; Explorer's Details tab just does not show it for that format.


---------------------------------------------------------------------------
Requirements
---------------------------------------------------------------------------

  * Windows 10 1809 (build 17763) or later. Windows 11 recommended.
  * 64-bit (x64).
You do NOT need .NET installed. This build carries its own copy.


---------------------------------------------------------------------------
About the security warnings
---------------------------------------------------------------------------

Chronora is not code-signed - a certificate costs several hundred dollars a
year, which is hard to justify for a free tool. Windows therefore treats it
as it treats anything else it has not seen before:

  * Explorer may show "Open File - Security Warning" when you run
    Install.cmd. Click Run.

  * If you run Chronora.exe straight out of this folder without installing,
    SmartScreen shows a blue "Windows protected your PC" panel. Click
    "More info", then "Run anyway".

The installer removes the downloaded-file marker from the installed copy, so
once Chronora is installed it starts normally with no warnings at all. Every
file here came out of the build in the project's repository, and the app
contacts nothing on its own at runtime.


---------------------------------------------------------------------------
Checking that the download arrived intact
---------------------------------------------------------------------------

The build writes a SHA256 checksum beside the zip, in a file named

  Chronora-{{VERSION}}-win-x64.zip.sha256

It is not inside the zip - a checksum of a zip cannot live within the zip it
describes - so you have it only if it was sent along with the download. If
you do, open PowerShell in the folder holding the zip and run:

  Get-FileHash Chronora-{{VERSION}}-win-x64.zip -Algorithm SHA256

The Hash it prints should match the one in the .sha256 file, ignoring upper
and lower case.

A mismatch means the copy is damaged rather than dangerous. 84 MB across a
USB stick, a network share or a mail server does occasionally arrive short,
and a partly corrupt zip will often extract far enough to look right and
then fail somewhere later, which is a miserable thing to diagnose. Copy it
across again rather than installing what you have.

What this proves is that the file arrived exactly as it was built. It is not
a signature and says nothing about who built it: anyone able to alter the
zip could rewrite the checksum sitting beside it. Only a code-signing
certificate answers that question, and Chronora does not have one.


===========================================================================
UNINSTALL
===========================================================================

Either of these:

  * Settings > Apps > Installed apps > Chronora > Uninstall
  * Double-click Uninstall.cmd in this folder

Both remove the app, the shortcuts and the Settings entry. That includes the
"Send to > Chronora" entry, if you added one from the About window.

If Chronora downloaded a copy of ExifTool for you, that is removed too - it
is not your data, it is software Chronora installed. An ExifTool you manage
yourself is somewhere else and is left alone.

Your settings, saved templates and run history are KEPT, so that installing
a newer version does not lose them. They live in:

  %LOCALAPPDATA%\PaulTechGuy\Chronora

To remove those too, either delete that folder afterwards, or run:

  Uninstall.cmd -RemoveUserData


===========================================================================
OPTIONS
===========================================================================

Install.cmd and Uninstall.cmd pass anything you give them to the scripts in
install\, so from a command prompt:

  Install.cmd -NoDesktopShortcut        skip the desktop shortcut
  Install.cmd -NoStartMenuShortcut      skip the Start menu shortcut
  Install.cmd -InstallDir "D:\Apps\Chronora"
                                        install somewhere else
  Install.cmd -Force                    close Chronora if it is running
  Install.cmd -Quiet                    no progress output

  Uninstall.cmd -RemoveUserData         also delete settings, templates
                                        and run history
  Uninstall.cmd -Force                  close Chronora if it is running

An upgrade is just an install: run Install.cmd from the newer release and it
replaces what is there, keeping your settings.


===========================================================================
Copyright (c) Paul Carver
===========================================================================
