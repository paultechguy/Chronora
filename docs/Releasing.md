# Releasing Chronora

> **This document is a stub.** The release pipeline itself — `build/ReleaseCommon.ps1`,
> `New-ReleaseNotes.ps1`, `Publish-Release.ps1`, the per-user installer — is adopted from
> Marqora in milestone 9 and is not here yet. What follows is the part that exists now and
> the part that must not be forgotten when the rest arrives.

---

## Before anything else: the ExifTool manifest

Chronora never bundles ExifTool. It offers to fetch the version named in
`docs/exiftool.json`, and it **refuses to install anything whose SHA256 does not match**.

That makes the manifest load-bearing in a way an ordinary config file is not.

> **A placeholder hash breaks every install at once.** Not for some users, not
> intermittently — for everyone, from the moment the release goes out, until a new release
> corrects it. And it does not fail in a way anybody can diagnose: the app reports that the
> download did not match its checksum, which reads like a security problem rather than an
> unfinished file.

The file ships with a zero hash deliberately, so that forgetting to fill it in fails a gate
rather than failing in front of a user.

```powershell
pwsh .\build\Test-ExifToolManifest.ps1            # offline: catches the placeholder
pwsh .\build\Test-ExifToolManifest.ps1 -Verify    # downloads and confirms the hash
```

The offline form is cheap enough to run on every build. **The `-Verify` form is the release
gate**, because it is the only check that proves the manifest describes the file actually
sitting at that URL rather than one that used to.

To fill it in:

```powershell
$url = (Get-Content docs/exiftool.json -Raw | ConvertFrom-Json).url
Invoke-WebRequest $url -OutFile $env:TEMP\exiftool.zip
(Get-FileHash $env:TEMP\exiftool.zip -Algorithm SHA256).Hash
```

### Why the manifest is fetched rather than compiled in

A hash baked into the binary would go stale the first time Phil Harvey ships a release: the
URL 404s or serves different bytes, the check fails, and every user who installs after that
day is stuck until Chronora itself is re-released. Serving the manifest from this repository's
GitHub Pages site means a stale pin is fixed with a commit.

It also means **the published manifest can break a release that already shipped**. Bumping
the version there is a live change to every installed copy, so run `-Verify` against it after
any edit, not only at release time.

---

## Gates carried over from Marqora

When the pipeline is adopted in milestone 9, these come with it: tooling, branch and tree,
`master` ancestor of `dev`, tag free, version match, release notes ready, tests,
licence headers, and build with no warnings.

**Do not carry over `Test-NetworkClaim.ps1`.** It guards Marqora's claim to be offline by
default, and Chronora does not make that claim: it fetches the ExifTool manifest and checks
for updates. Inheriting the gate would fail the build for telling the truth.

### Gates added for Chronora

| Gate | Refuses when |
|---|---|
| ExifTool manifest | `docs/exiftool.json` is missing, malformed, not https, or still carries the placeholder hash. With `-Verify`, when the archive at that URL does not match |
| No bundled ExifTool | The publish output contains any `exiftool*` file. The release artifact is Apache-2.0 and must stay free of GPL code |
| Native assets present | `Chronora.pri`, `App.xbf` or `e_sqlite3.dll` are missing from the publish. Any one of them means the app installs but does not start |
| Upgrade over previous | Installing over the previous release loses templates, history, settings or the ExifTool consent |

The last one is a manual step and the easiest to skip, which is exactly why it is written
down: install the previous release, create a template and apply a run so the journal is not
empty, install the new version over it, and confirm all four survive. A checksum will not
catch this, and the damage only appears on somebody's second install.
