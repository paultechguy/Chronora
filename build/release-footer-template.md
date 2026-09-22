---

## Download

**[{{ZIP}}]({{DOWNLOAD_URL}})** — Windows 10 1809 (build 17763) or later, 64-bit.

The .NET runtime and the Windows App SDK are inside the zip, so the target machine needs
neither. The installer is per-user and writes nothing outside `HKCU` and your own profile, so
it needs no administrator.

## Install

1. Extract the zip into a folder of its own.
2. Double-click `Install.cmd`.
3. Chronora is in your Start menu and on your desktop.

The app installs to `%LOCALAPPDATA%\Programs\Chronora`.

To remove it: **Settings → Apps → Installed apps → Chronora → Uninstall**, or `Uninstall.cmd`
from the extracted folder. Your settings, saved templates and run history under
`%LOCALAPPDATA%\PaulTechGuy\Chronora` are kept either way, so installing a newer version does
not lose them. `Uninstall.cmd -RemoveUserData` removes those too.

## Photo and video dates need ExifTool

File dates — Created and Modified — work with nothing else installed.

The date a photo or video records *inside itself* is a different thing, and reading or writing
it needs **ExifTool** by Phil Harvey. Chronora does not ship it and never will: ExifTool is
GPL/Artistic and Chronora is Apache-2.0, so no part of it is in this archive.

Chronora can fetch it for you, once, if you ask. It downloads only the version named in its
manifest and refuses anything whose SHA-256 does not match. If you already have ExifTool — on
`PATH`, from winget or Chocolatey — Chronora finds and uses that instead, and will not manage,
update or remove a copy that is yours.

Without it, every file-date feature behaves exactly the same; the photo and video fields simply
say the helper is not available.

One thing that surprises people: **Windows Explorer does not show a "Date taken" for PNG
files**, whatever wrote it. Chronora writes the tag correctly and photo applications read it —
Explorer's Details tab just does not surface it for that format.

## What Chronora does on the network

Nothing on its own, and nothing on a timer. Two things reach the internet, both only when you
ask: fetching ExifTool, and the **Check for updates** button in About. Everything else —
scanning, previewing and writing dates — is entirely local.

## About the security warnings

Chronora is not code-signed — a certificate costs several hundred dollars a year, which is hard
to justify for a free tool. Windows therefore treats it the way it treats anything else it has
not seen before:

- Explorer may show **Open File - Security Warning** when you run `Install.cmd`. Click **Run**.
- Running `Chronora.exe` straight out of the folder without installing shows the blue
  **Windows protected your PC** panel. Click **More info**, then **Run anyway**.

The installer strips the downloaded-file marker from the installed copy, so once Chronora is
installed it starts normally with no warnings at all.

## Verify your download

```
SHA256  {{SHA256}}
```

```powershell
Get-FileHash {{ZIP}} -Algorithm SHA256
```

A mismatch means the copy is damaged rather than dangerous — 80-odd MB across a network or a
USB stick does occasionally arrive short, and a partly corrupt zip will often extract far
enough to look right and then fail somewhere later, which is a miserable thing to diagnose.
Copy it across again rather than installing what you have.

What this proves is that the file arrived exactly as it was built. It is not a signature and
says nothing about who built it: anyone able to alter the zip could rewrite the checksum beside
it. Only a code-signing certificate answers that question, and Chronora does not have one.
