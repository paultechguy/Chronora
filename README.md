# Chronora

Every date, every file.

Chronora bulk-changes the dates on Windows files **and** the capture dates recorded inside
photos and videos — behind a preview you can sort, summarise and export before anything is
written, and an undo that still works a week later.

Apache-2.0. Work in progress.

## Why it exists

Changing a file's date on Windows is split across tools that each do half the job.
BulkFileChanger handles the three visible file timestamps and knows nothing about photos.
ExifToolGUI and XnViewMP expose total metadata power and overwhelm anyone who just wants
their holiday photos to sort correctly.

The gap is not that nobody does either half. It is that nobody does **both** — on Windows,
under one preview, with a date free to cross between them:

- *"Explorer shows the wrong date"* — copy each photo's Taken date onto its file dates.
- *"The taken date is missing"* — the reverse, for scans, downloads and exports that never
  had one, which is why they all land on today in a photo library.

## What it does

| | |
|---|---|
| File dates | Created, Modified, Accessed, and the hidden NTFS ChangeTime |
| Photo and video dates | EXIF, XMP and QuickTime capture dates, via ExifTool |
| Sources | A date you pick · shift the existing one · another date the file already has · the file name |
| Preview | Every planned change, sortable, summarised, exportable as CSV — before anything is written |
| Undo | A SQLite journal. Revert a run days later, and the revert is itself revertible |

## Things it deliberately does not do

Renaming files from dates, moving them into date folders, and editing non-date metadata.
Each would need its own pattern language and its own undo semantics, and moving files is a
great deal scarier than editing a timestamp.

## ExifTool

Photo and video dates are read and written by [ExifTool](https://exiftool.org), by Phil
Harvey. **Chronora does not include it and does not redistribute it.**

On first use it looks for a copy you already have — on `PATH`, or installed by winget or
Chocolatey. Only if there is none does it offer to download one, with your permission,
into its own folder, pinned to a version and SHA-256 published in
[`docs/exiftool.json`](docs/exiftool.json).

Every file-date feature works with no ExifTool installed at all. "Not installed" is a
supported state, not an error.

## Network

Chronora makes exactly two kinds of network call, and both happen only because you asked:

1. Fetching ExifTool, after you agree to it.
2. Checking for a newer Chronora, when you press the button in About.

It never checks on its own, never downloads an update by itself, and sends nothing
anywhere.

## Your data

Templates, settings and the undo journal live in `%LOCALAPPDATA%\PaulTechGuy\Chronora\`,
outside the install folder, so updating or reinstalling leaves them alone. Every file
carries a schema version, and one written by a newer build opens read-only rather than
being rewritten in an older shape.

About has buttons that open that folder and the log folder.

## Building

Requires the .NET 10 SDK and Windows.

```
dotnet build PaulTechGuy.CN.slnx -warnaserror
dotnet test PaulTechGuy.CN.slnx
```

The repository is kept at zero warnings; `-warnaserror` is what CI runs.

The metadata integration tests need a real ExifTool and skip themselves without one.
`build/Get-ExifTool.ps1` fetches a developer copy into the gitignored `tools/exiftool/`.
That copy is for tests; it is never packaged.

## Licence

Apache-2.0. Copyright © 2026 Paul Carver.

ExifTool is separately licensed and separately obtained — see
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
