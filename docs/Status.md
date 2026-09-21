# Where Chronora is

Last updated 2026-09-20. Branch `dev`, no remote. Build clean at `-warnaserror`,
450 tests passing.

```
dotnet build PaulTechGuy.CN.slnx -warnaserror
dotnet test PaulTechGuy.CN.slnx
```

---

## What needs testing

Everything below landed without anyone looking at it. **Nothing here has been seen
working.** Rough order of importance.

### 1. The row context menu — right-click a row

Eleven items, all acting on the right-clicked row only, whatever is ticked.

- **Properties** is the most likely to silently do nothing. It goes through
  `ShellExecuteEx` with `SEE_MASK_INVOKEIDLIST`, because `Process.Start` with
  `Verb = "properties"` looks like it should work and doesn't.
- **Create a copy** — should appear beside the original as
  `name_2026-09-20_094512.ext`. Run it twice; two files, no error.
- **Open**, **Show in File Explorer** (folder opens with the file selected, not at the
  top), **Copy full path**.
- **Run on this file only**, **Remove from the list**, **Include / Take out of the run**.
- Right-click *below* the last row should do nothing at all.

### 2. Set this file's date… (from the row menu)

- Four checkboxes — Created, Modified, Accessed, Taken — same as the options pane.
- On a `.txt` row, **Taken should be absent entirely**, not greyed out.
- Defaults come from what the run would already do, minus what the file cannot carry.
- **Use the time now** fills both pickers.
- After setting, line two of the row reads `by hand · …`, and
  **Use the run's date again** appears in the menu (hidden otherwise).
- Ticking nothing and pressing Set should record no override and say so.

### 3. Settings persistence

Resize and move the window, change options, close, reopen.

- Window position, size and maximized state come back — but not onto a monitor that is
  no longer there.
- Intent, source, ticked fields, copy-from field, shift, sort and view filters come back.
- **The date must NOT come back.** The app has to open with nothing to apply.

### 4. The chip builder

1. Drop in files whose names the built-ins don't recognise.
2. **Get the date from → The file name.** A new section appears under the radio buttons.
3. **Teach it this file name…** — opens on the selected row, or the first.
4. Each run of digits is a button. `20240315` → choose **Year**: takes `2024`, leaves
   `0315` beside it. Click that for **Month**, then the remaining `15` for **Day**.

Underneath: "Matches N of M files" with worked examples. **Use this pattern** stays
disabled while nothing matches. If the pattern has no time in it, it says each file keeps
its existing time of day.

### 5. About (top right, next to History)

- Version, **Check for updates** (will report it cannot reach the site until
  `docs/version.json` is actually published — that is the correct answer, not a failure).
- **Open data folder** and **Open logs**.
- **Add to Send to** — **this is unverified**. It compiles and the vtable order matches
  the documented `IShellLinkW`, but COM interop of that shape fails at runtime, not at
  build. Check `shell:sendto` after pressing it.

### 6. Template import / export

The `…` button beside Save as template / Delete.

---

## Open, unresolved

- **"All four dates changed, times did not sync."** Reported, and it does not reproduce.
  Verified identical at three levels: the pane with both pickers and all four boxes
  ticked, the row menu's own dialog, and the real apply path writing to disk —
  ChangeTime included. The journal shows the last real run (run 9) set only Created and
  Modified, both to exactly `2026-09-19 19:01:00.0000000Z`. **What is still needed:** the
  date and time picked, and what Explorer then showed for Created / Modified / Accessed.
  Two candidates if they turn out to be involved: a template still active (its source
  wins over the pickers), or Accessed being moved afterwards by anything that reads the
  file — including Chronora's own thumbnailing — on a volume where last-access updates
  are on.
- **Folder processing and recursion.** Paul deferred this on 2026-09-20 and wants to
  discuss it. Nothing is decided. Drops and Add-folder both pass `ScanFilter.Default`, so
  whatever recursion does today was never actually chosen — and dropping a folder is the
  most destructive gesture in the app. `ScanFilter` already carries `Recurse`,
  `IncludeFiles`, `IncludeDirectories` and `IncludeRootDirectory` as independent toggles,
  and the UI exposes none of them.
- **The app icon.** Start fresh — explicitly *not* FileTouch's `clock.ico`. Needs Paul.
- **An installer test failed once and never reproduced** in 4+ runs. Still unexplained.
- **The QuickTime local-time camera case is unverified.** Pixel writes UTC, so the other
  branch of the per-file inference has never been exercised against a real file.

---

## Done

**Milestones 1–7** complete: skeleton and grid spike, domain and rules, the filesystem
layer, the journal, the workbench UI, apply and undo, ExifTool.

**Milestone 8** is nearly done. Landed: templates and built-ins, History, the type
filter, empty states, row thumbnails, selection commands, the row context menu, per-row
date *and* field overrides, settings persistence, the chip-based filename pattern
builder, About, a user-initiated update check, template import/export, Send To, CSV
export of the preview.

**Remaining in 8:** the app icon, theming.

**Milestone 9** is barely started. `build/` has only `Get-ExifTool.ps1` and
`Test-ExifToolManifest.ps1`; `build/installer/` is empty. Needed: `ReleaseCommon.ps1`,
`Publish-Release.ps1`, `New-Release.ps1`, `New-DevBuild.ps1`, the per-user installer, and
the upgrade-over-previous smoke test — which cannot run even once until a previous
release exists.

**Milestone 10 (0.2.0):** Google Takeout importer, winget manifest.

CI (`.github/workflows/ci.yml`) runs build, test and publish on Windows, and asserts
three things about the publish output: no ExifTool binary, `exiftool.json` present,
`e_sqlite3.dll` present. All three were verified locally before the file was committed —
and the second one immediately found a real bug, which is why it is there.
