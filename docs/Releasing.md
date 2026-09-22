# Releasing Chronora

Everything is PowerShell 7 (`pwsh`), run from the repository root. Every script exits `0` on
success and `1` on failure, so any of them can gate a commit or a CI step.

```
build/
    ReleaseCommon.ps1             the library; dot-sourced, never invoked
    New-DevBuild.ps1              a zip under a version you name. No git side effects
    New-Release.ps1               the shippable zip, versioned from Directory.Build.props
    New-ReleaseNotes.ps1          step 1: bump the version, scaffold the notes
    Publish-Release.ps1           step 2: promote, tag, draft. Then -Verify
    Get-ExifTool.ps1              a local copy for integration tests. Never shipped
    Test-ExifToolManifest.ps1     the manifest gate
    release-notes-template.md     scaffolded into docs/releases/v<version>.md
    release-footer-template.md    appended to the GitHub release body
    installer/                    Install.cmd, Uninstall.cmd, the .ps1 pair, README.txt
    artifacts/                    git-ignored output
```

---

## Before anything else: the ExifTool manifest

Chronora never bundles ExifTool. It offers to fetch the version named in
`docs/exiftool.json`, and it **refuses to install anything whose SHA256 does not match**.

That makes the manifest load-bearing in a way an ordinary config file is not.

> **A placeholder or stale hash breaks every install at once.** Not for some users, not
> intermittently — for everyone, from the moment the release goes out, until a new release
> corrects it. And it does not fail in a way anybody can diagnose: the app reports that the
> download did not match its checksum, which reads like a security problem rather than an
> unfinished file.

```powershell
pwsh .\build\Test-ExifToolManifest.ps1            # offline: catches the placeholder
pwsh .\build\Test-ExifToolManifest.ps1 -Verify    # downloads and confirms the hash
```

The `-Verify` form is the release gate, and `New-Release.ps1` runs it. It is the only check
that proves the manifest describes the file actually sitting at that URL rather than one that
used to.

It fetches with `curl.exe`, not `Invoke-WebRequest`. SourceForge answers some client shapes
with an HTML interstitial instead of the file — measured at 139,123 bytes beginning
`<!doctype html` — and hashing that reports a checksum mismatch against a manifest that is
perfectly correct. `Get-ExifTool.ps1` had already solved this; the verifier had not, and would
have blocked the first release.

To fill the hash in:

```powershell
$url = (Get-Content docs/exiftool.json -Raw | ConvertFrom-Json).url
curl.exe -sL $url -o $env:TEMP\exiftool.zip
(Get-FileHash $env:TEMP\exiftool.zip -Algorithm SHA256).Hash
```

### Why the manifest is fetched rather than compiled in

A hash baked into the binary would go stale the first time Phil Harvey ships a release: the
URL 404s or serves different bytes, the check fails, and every user who installs after that
day is stuck until Chronora itself is re-released. Serving the manifest from this repository's
GitHub Pages site means a stale pin is fixed with a commit.

It also means **the published manifest can break a release that already shipped**. Bumping the
version there is a live change to every installed copy, so run `-Verify` against it after any
edit, not only at release time.

---

## Releasing, in two steps

The split is deliberate: nothing reaches GitHub until the notes are written and committed.

### Step 1 — the notes

```powershell
pwsh .\build\New-ReleaseNotes.ps1 -Version 0.2.0 -Check   # could a release start?
pwsh .\build\New-ReleaseNotes.ps1 -Version 0.2.0
```

Bumps `<Version>` in `Directory.Build.props` and scaffolds `docs/releases/v0.2.0.md` from the
template. Write the notes, then commit both as an ordinary change.

`Publish-Release.ps1` refuses to run while a `TODO` is left behind, or while the file still
matches the template it came from.

### Step 2 — publish

```powershell
pwsh .\build\Publish-Release.ps1 -Version 0.2.0 -WhatIf   # every gate for real, then stop
pwsh .\build\Publish-Release.ps1 -Version 0.2.0
```

Runs the gates, promotes `master`, tags, builds through `New-Release.ps1 -Test`, and leaves a
**draft** release on GitHub. Nothing it does is unrecoverable, and it prints how to undo each
part.

Then, by hand:

1. Smoke-test the draft. `gh release download` the assets, extract, run `Install.cmd`, launch
   it, and confirm About reports the version. This is the only way to test the bytes GitHub
   will actually serve.
2. Click **Publish release**.
3. `pwsh .\build\Publish-Release.ps1 -Version 0.2.0 -Verify`

### Step 3 — announce it

`-Verify` downloads the published assets, checks the zip against its published checksum,
checks the archive layout, and **then** writes `docs/version.json`. It does not commit it; it
prints the commands.

The ordering is the point, and it is easy to get backwards. GitHub Pages serves `docs/` from
`master`, and `Publish-Release.ps1` promotes `master` as its *first* action. A manifest written
then would announce the release while it was still a private draft, sending every running copy
to a `releases/latest` URL that 404s — or, worse, serving them the previous release as though
it were the new one.

So `docs/version.json` is written last, and the gate before publishing asserts it is still
*behind*. A manifest that already names the version being released means somebody jumped the
gun, and the release stops.

Until that commit is pushed, the update check keeps reporting the previous version. That is the
safe way round: an unannounced release is an inconvenience, an announced one that does not
exist is a broken download for everybody.

---

## The gates

| Gate | Refuses when |
|---|---|
| tooling | `git`, `gh` or `dotnet` is missing, or `gh` is not authenticated |
| branch and tree | Not on `dev`, working tree dirty, or `dev` and `origin/dev` disagree |
| master ancestor of dev | `origin/master` is not an ancestor of `origin/dev`, so the fast-forward would fail |
| tag free | `v<version>` already exists locally, on origin, or as a release. `-Republish` overrides |
| version match | `Directory.Build.props` does not say the version being released |
| release notes ready | The notes file is missing, still matches the template, or still holds a `TODO` |
| build | Anything at all at `-warnaserror` |
| tests | Any test fails |
| exiftool manifest | The manifest is missing, malformed, not https, carries the placeholder hash, or — with `-Verify` — does not match the archive at its URL |
| release manifest | `docs/version.json` already names the version being released, before it exists |
| upgrade over previous | Installing over the previous release loses templates, history, settings or the ExifTool consent |

### Two gates deliberately absent

**`Test-NetworkClaim.ps1` is not ported.** It guards Marqora's claim to be offline by default.
Chronora does not make that claim — it fetches the ExifTool manifest and checks for updates,
both only when asked — so inheriting the gate would fail the build for telling the truth.

**`Test-GateHeaders` is not ported.** It shelled `Add-FileHeaders.ps1`, which this repository
does not have, and it is redundant: `.editorconfig` sets `file_header_template` with `IDE0073`
as a warning, `Directory.Build.props` sets `EnforceCodeStyleInBuild`, and the build gate runs
`-warnaserror`. A missing SPDX header is a build failure before it could ever be a gate
failure.

### The upgrade test is manual, and 0.1.0 cannot run it

Install the previous release, create a template and apply a run so the journal is not empty,
install the new version over it, and confirm all four survive: templates, history, settings,
and the ExifTool consent. A checksum will not catch this, and the damage only appears on
somebody's second install.

**It is not applicable to 0.1.0**, because there is no previous release to install. It is in
force from 0.2.0. A gate that is structurally unsatisfiable on the release it is introduced for
is a gate that gets ticked without being run, so it is written down here rather than left to
memory.

---

## A build for somebody else, without releasing

```powershell
pwsh .\build\New-DevBuild.ps1 -Version 0.2.0-test4
```

The same publish, staging and installer a real release produces, built from the working tree
as it stands, under a version you name. It writes no commits, moves no branches, creates no
tags, and does not touch `Directory.Build.props`.

`-Version` is mandatory on purpose: without it the build would inherit the number in
`Directory.Build.props` and land on the other machine indistinguishable from the real release
of the same name. A suffix is worth using for the same reason.

---

## What ships in the archive

Seven entries at the root, and `Publish-Release.ps1 -Verify` checks for exactly these:

```
app/                      the self-contained publish
install/                  Install.ps1, Uninstall.ps1
Install.cmd
Uninstall.cmd
README.txt                carries the version
LICENSE
THIRD-PARTY-NOTICES.md
```

`LICENSE` and the notices are in there because `app/` is a self-contained publish full of
third-party binaries — the .NET runtime, the Windows App SDK, SQLitePCLRaw, Serilog — and
Apache-2.0 asks for both to travel with the distribution.

The only `exiftool*` entry is `app/exiftool.json`, which is Chronora's own manifest.
`New-Release.ps1` scans the publish for anything else by that name and refuses to build if it
finds one. CI runs the same scan, but against its own publish on a GitHub runner — the zip is
built locally and never passes through CI, so without the scan in the release script the
Apache-2.0/GPL rule would be enforced only on an artifact nobody receives.
