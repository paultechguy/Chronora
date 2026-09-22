# Where Chronora is

Last updated 2026-09-20. Branch `dev`, no remote. Build clean at `-warnaserror`,
458 tests passing.

```
dotnet build PaulTechGuy.CN.slnx -warnaserror
dotnet test PaulTechGuy.CN.slnx
```

---

## What needs testing

Everything below landed without anyone looking at it, and is unseen unless it says
otherwise. Rough order of importance.

### 1. The row context menu — right-click a row

Eleven items, all acting on the right-clicked row only, whatever is ticked.

- **Properties** works — **confirmed 2026-09-20**, and it was the item here most likely to
  fail silently. `ShellExecuteEx` with `SEE_MASK_INVOKEIDLIST` is what makes it work.
  `Process.Start` with `Verb = "properties"` looks like it should and doesn't, so don't
  "simplify" this one back.
- **Create a copy** — should appear beside the original as
  `name_2026-09-20_094512.ext`. Run it twice; two files, no error.
- **Open**, **Show in File Explorer** (folder opens with the file selected, not at the
  top), **Copy full path**.
- **Run on this file only**, **Remove from the list**, **Include / Take out of the run**.
- Right-click *below* the last row does nothing — **fixed and confirmed 2026-09-20**. It
  used to open the menu on whatever row was selected, and the log settled it:
  `ContextRequested` fires on every right-click after all, a millisecond behind
  `RightTapped` and with an identical source chain. So when `RightTapped` correctly
  declined the empty space, the handler meant for the keyboard ran for the same gesture,
  fell back to `SelectedItem`, and opened the menu on a file nowhere near the pointer.
  The fallback is now reached only when `TryGetPosition` fails, which is what tells a
  keyboard request from a pointer one. The log shows both halves working:
  `via=context/pointer resolved=none` over empty space, and the row resolved on a row.
- **Shift+F10 and the menu key are still unexercised.** No log, before the fix or after,
  contains a single `via=context/keyboard` line — nobody has pressed the keys. This is
  the one path into the row menu with no evidence at all, and the fix narrowed what
  reaches it, so it is worth one press when convenient.

### 2. Set this file's date… (from the row menu)

**The override itself is confirmed working end to end (2026-09-21, run 16):** the row read
`by hand · Created · Modified · Taken → 09:32`, the plan carried it, and x.png landed on
its own value while the other four took the run's. Note the confirmation dialog shows the
range across the whole run — "5 files → 08:00 ... 09:32" is min…max, four files at one
value and one at another, not one value for all five. That reads like the override has
been ignored, and it is the reason the override was reported broken when it was not.
The rest of this section is still unseen.

- Three checkboxes — Created, Modified, Taken — same as the options pane. Accessed was
  removed on 2026-09-21; see below.
- On a `.txt` row, **Taken should be absent entirely**, not greyed out.
- Defaults come from what the run would already do, minus what the file cannot carry.
- **Use the time now** fills both pickers.
- After setting, line two of the row reads `by hand · …`, and
  **Use the run's date again** appears in the menu (hidden otherwise).
- Ticking nothing and pressing Set should record no override and say so.

### 3. Settings persistence

Resize and move the window, change options, close, reopen.

- Window position and size come back — **confirmed working 2026-09-20**. Still unseen:
  maximized state, and the refusal to restore onto a monitor that is no longer there.
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
- **Add to Send to** — **works in both directions, confirmed 2026-09-20.** The worry about
  COM interop of that shape failing at runtime was unfounded; the hand-declared
  `IShellLinkW` vtable is right and the shortcut is both created and removed. The button
  is its own undo: About reads the Send To folder when it opens and again after every
  press, so it says **Add to Send to** or **Remove from Send to** to match what is
  actually in the folder. The entire footprint either way is one file, `Chronora.lnk`, in
  the per-user Send To folder — no registry, no installer step.

---

## Open, unresolved

- **`AppPaths.BackupDirectory` is dead code.** `<data>/backups` is created at every startup
  and nothing has ever written to it — `MetadataWriter` is not even given `IAppPaths`. It
  should be deleted or used; as it stands it is a signpost to the wrong place, which cost
  time during the backup bug below.
- **A failed run says how many, never why.** The status bar read "Done. 0 changed, 5
  failed, 0 skipped" while the journal held the exact reason on every file. The reason is
  recorded and was never shown, which is what turned a one-line wiring bug into a long hunt.
  Surfacing the first distinct error — or making the count a link to History — is the
  smallest thing that would have prevented it.
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

**Seen working 2026-09-20:** template import, export and delete; window size and position
across a restart; Send To, adding and removing; the row menu declining empty space, and
Properties from it.

**Fixed and verified 2026-09-21 — the "all four dates changed, times did not sync"
report.** It was never a date or time bug, which is why every look at the date logic came
back clean. `IMetadataWriteGateway` was never registered in the composition root — only
the concrete `MetadataGateway` — and `ApplyService` takes it as an *optional* parameter,
so the container supplied `null` instead of throwing. Every run that included **Taken**
failed all its files with "ExifTool is not available" while ExifTool was running and had
reported 109 writable formats in the same log; and because a failed metadata write
deliberately abandons the filesystem write too, Created and Modified were dropped with it.
Runs 9–13 only looked healthy because they targeted Created and Modified alone and never
consulted the gateway. Runs 14 and 15 are the journalled proof of the bug — `change_count
15`, `0 written`, `5 failed`. Run 16, after the one-line registration fix, is the proof of
the cure: 5 files, 15 changes, **0 errors**, `ExifIFD:DateTimeOriginal` written on all
five including the four PNGs, file dates landing with it, and x.png's by-hand override
(15:45 UTC) staying distinct from the run's (14:00 UTC).

**Fixed 2026-09-21 — backup copies left in the user's folders.** Reported as "the app
created 5 new files without extensions". They were Chronora's own pre-write backups:
`MetadataWriter` copies the file to `<path>_original` before an embedded write, because
`-overwrite_original_in_place` and ExifTool's own backup are mutually exclusive. Three
faults, and the first was the serious one. The copy is taken with `overwrite: true` and
was never removed, so the **second** successful run replaced the pristine backup with the
already-modified file — all five backups were verified byte-identical by SHA-256 to the
live files they were supposedly protecting, carrying the same written
`DateTimeOriginal`. The pristine bytes were gone. Second, nothing ever deleted them,
though `MetadataWriteResult.BackupPath` is documented as being carried "so it can be
offered for deletion later". Third, `_original` is exactly ExifTool's own naming, making
Chronora's backups indistinguishable from ExifTool's. `ApplyService.DiscardBackup` now
removes the copy on success in both the apply and revert paths, and keeps it on every
failure; two tests pin both halves. The five stray files were verified as duplicates and
deleted. **Still to verify:** a real run leaves no `_original` files behind.

**Removed 2026-09-21 — Accessed, everywhere.** Paul's call, and the right one: it is not a
field any application can set reliably. Chronora wrote it correctly on every run measured;
anything that then reads the file moves it, and Windows defaults `DisableLastAccess` to 2
("System Managed", i.e. updates ON). Proved independently: set all three dates on a scratch
file, then merely read it — Created and Modified hold, Accessed jumps to now. On run 19 the
three PNGs shared an Accessed time to within 4ms, 96 seconds after the run, with no
Chronora activity in the log: Explorer's thumbnailer. The chokepoint is
`DateFieldCatalog.AppliesTo` returning false, so nothing can plan it, preview it or write
it — a stale template naming Accessed now produces no line rather than a promise that is
silently broken. `ToRestoreSet` still restores it, so undo of runs 9–19 still works.
Validated end to end outside the test suite: a harness drove the real evaluator, real
`ApplyService` and real `FileTimeWriter` over real .txt/.jpg/.png files with a recipe that
deliberately asked for Accessed; the planner emitted `[FileCreated, FileModified]` only, and
PowerShell — not Chronora's own reader — confirmed Created and Modified exactly on target
with Accessed still holding its original value.

**Also settled 2026-09-21 — "Taken" on a PNG.** Not a bug, and now explained in the app
rather than left to be rediscovered. Chronora writes `ExifIFD:DateTimeOriginal` and
`ExifIFD:OffsetTimeOriginal` into PNG correctly; ExifTool and third-party metadata viewers
read them back. Windows Explorer does not surface Date taken for PNG at all.

The format table was **measured, not assumed** — the identical tag written with plain
ExifTool to one file of each format, then Explorer's own "Date taken" column read back:
JPEG shows it, TIFF shows it, PNG blank, GIF blank (GIF never reaches this path, it is
`MediaKind.Other`; BMP is not EXIF-writable at all and is blocked earlier by `-listwf`).
**HEIC and DNG are deliberately not in the table** — there was no genuine file of either to
test, and warning wrongly is worse than not warning. The `.heic` in Downloads is a misnamed
JPEG. Measure one before adding it.

The write is *not* blocked, deliberately: the date works everywhere except Explorer, and
refusing it would throw away a functioning Taken date. Instead it is stated in the two
places somebody checks — a line in the Apply confirmation ("Taken will be written to N PNG
file(s)…") and a note in the row detail pane for a selected PNG. Both appear only when the
run actually writes Taken to such a file. A test pins the measured table, and a second test
pins that PNG stays writable, so a later "fix" that blocks it fails.

**Added 2026-09-21 — getting back out of a per-row override.** Reported from testing: having
set a date on one row, there was no obvious way to hand that file back to the run. The item
existed — "Use the run's date again" — but it is hidden unless the row already has an
override, and it was named for where the file lands rather than for what the click removes,
so it was not what anyone scanned for. Renamed to **"Remove this file's own date"**, with
the effect in its tooltip. Added **"Remove every by-hand date (N)"** beside it, shown when
any exist and hidden when the only one is the row already offering to clear itself. That
item is the only place the number of by-hand rows appears anywhere in the app — an override
is otherwise visible solely as a marker on its own row, so several in a long list can only
be found by scrolling. It is reversible through the same notice as Clear list and Start
over. **Confirmed 2026-09-21:** the original item *was* visible on the row — so this was
purely a naming and discoverability failure, not a visibility bug. Worth remembering: a
correctly-working, correctly-placed menu item was invisible in practice because it was named
for its outcome instead of its action.

**Remaining in 8:** the app icon, theming.

**Milestone 9** is built, and half of it is verified by use rather than by reading.
`ReleaseCommon.ps1`, `New-DevBuild.ps1`, `New-Release.ps1`, `New-ReleaseNotes.ps1`,
`Publish-Release.ps1`, the two note templates and `build/installer/` are all in place, and
`docs/Releasing.md` is no longer a stub. Adapted from Marqora, with two of its gates
deliberately dropped and three added that Chronora needs — see Releasing.md.

**Seen working 2026-09-21/22:** `New-DevBuild.ps1` produces an 82 MB zip with the right seven
root entries and no ExifTool binary; that zip installs, the app runs, Send To adds and removes
correctly, and Add/Remove Programs uninstalls cleanly — removing the managed ExifTool and
keeping settings, templates and history. The gate machinery is live: `New-ReleaseNotes -Check`
correctly refused a release while `dev` was ahead of `origin/dev`.

**Not yet exercised:** `Publish-Release.ps1`. It cannot be until there is something to
release, and its `-Verify` step — which writes `docs/version.json` — has never run. The
upgrade-over-previous test is structurally impossible for 0.1.0 and is in force from 0.2.0.

**Milestone 10 (0.2.0):** Google Takeout importer, winget manifest.

CI (`.github/workflows/ci.yml`) runs build, test and publish on Windows, and asserts
three things about the publish output: no ExifTool binary, `exiftool.json` present,
`e_sqlite3.dll` present. All three were verified locally before the file was committed —
and the second one immediately found a real bug, which is why it is there.
