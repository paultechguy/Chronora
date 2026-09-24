# Feasibility study: advanced folder filters

2026-09-23. Investigation only — nothing here is approved or scheduled.

**The idea.** A modal in the style of Beyond Compare's *Session Settings* filter page: four
pattern lists (include files, exclude files, include folders, exclude folders), a Clear
button, and a *Use for this session only / Also update application preferences* choice
moved up to sit directly under the four lists. It is opened from the Source card's
existing *How folders are read* dropdown.

**Verdict.** Useful to a real subset of users, and nothing in the product stands in the
way. Every layer it touches already has a slot for it. The work is one moderate UI piece
and three small traps, listed below, that must not be rediscovered.

---

## Decisions already taken (2026-09-23)

| Question | Answer |
|---|---|
| How it relates to the list header's Type filter | **Both stay, with different jobs.** The modal decides what is *read from disk*. The Type filter narrows a list already loaded, instantly, with no rescan. |
| Entry point | **The flyout stays** with its three checkboxes, and gains a **More filters…** button that opens the modal. One-click toggles remain one click. |
| Pattern language | **Name wildcards only**, matched against the file or folder *name*, using the matcher the app already has. No relative paths, no regex. |

---

## 1. Would advanced users actually want it?

**Yes, mostly for the exclude half.** The Type filter already answers "only the JPEGs", and
it answers it better, because it needs no rescan. What nothing in the app can express today
is *don't even walk into that*. That is exactly the case for someone who points Chronora at
a whole library, a NAS share or a drive:

- **Folders that are never photos:** `@eaDir` (Synology thumbnails, one per folder, often
  thousands), `.thumbnails`, `*.lrdata` (Lightroom previews), `.git`, `node_modules`,
  `$RECYCLE.BIN`. The recycle bin is hidden/system, so the existing checkbox mostly covers
  it. The rest are ordinary folders and currently get scanned.
- **Files that ride along with photos and should not be restamped:** `*.aae` (iPhone edit
  sidecars), `*.xmp`, `.picasa.ini`, `*.tmp`. Some of these the user may *want* dated with
  their photo. That is a matter of preference, which is exactly why it belongs in a filter
  and not in a hard-coded rule.
- **Scan cost.** An excluded folder that is never walked costs nothing. The Type filter
  cannot give that saving: by the time it applies, the files have been read, including
  their ExifTool metadata pass.

**Who it is not for.** Someone who drops ten photos never opens it, and must never be
tripped up by it. The real product risk is not complexity but a **forgotten filter**:
a sticky exclude set six months ago silently hides files from a new drop. The mitigation
already has a home. The card label (`ScanSettingLabel`, "New drops: subfolders · hidden
files") must name active patterns, e.g. `New drops: subfolders · 3 excludes`, so the filter
is never only visible inside the modal. This is the same rule already written in
`SettingsStore.cs`: *a sticky setting only visible inside a flyout is how somebody
recursively scans a drive by accident.*

**Beyond Compare parity is not the goal.** BC users compare trees, and folder includes are
central to that. For dating photos, **include folders** is the weakest of the four panes,
because "only folders named `2019*`" is rare and would hide everything else. It is worth
considering whether that pane ships at all, or starts as a plain `*` that people rarely
touch.

---

## 2. Is there any technical reason it couldn't be built?

**No.** Layer by layer:

| Layer | Today | What it would need |
|---|---|---|
| Domain — `ScanFilter` (`Recipe.cs`) | Already has `Patterns` (include files). | Add `ExcludeFiles`, `IncludeFolders`, `ExcludeFolders`. A record, so this is additive. |
| FileSystem — `FileScanner` | Hand-built `FileSystemEnumerable` with `ShouldIncludePredicate` (file patterns) and `ShouldRecursePredicate` (the junction guard). | Folder excludes go into `ShouldRecursePredicate`, alongside the junction check, so an excluded tree is never walked. File excludes go into `ShouldIncludePredicate`. Both use `FileSystemName.MatchesSimpleExpression`, which is already in use. |
| Presentation — `BuildScanFilter()` | Single builder used by all five entry points (drop, Add folder, Send To, command line, rescan after apply), hard-codes `["*"]`. | Fill the new fields. Because there is only one builder, all five entry points pick them up at once; this was designed in on 2026-09-22 for exactly this kind of change. |
| Repositories — `AppSettings` | `ScanRecurse`, `ScanIncludeHidden`, `ScanIncludeFolders`, always persisted. | Four string lists, `set` not `init` (the STJ trap in CLAUDE.md). |
| Journal | `Recipe.Filter` is part of the recipe JSON recorded per run. | Nothing — the filters are recorded automatically, so History can show what a run excluded. |
| UI | Flyout in `MainWindow.xaml`; `ContentDialog` already used for the filename pattern builder and ExifTool consent. | One new `ContentDialog`: four multi-line `TextBox`es in a 2×2 grid, Clear, the session/preferences `ComboBox`, OK / Cancel. Nothing exotic for WinUI 3. |

### The three traps

These are the only parts that could go wrong quietly. Each was checked against the code,
not assumed.

1. **`*.*` does not mean "everything" here.** BC's include box defaults to `*.*`. Under
   `MatchesSimpleExpression`, which the scanner deliberately uses instead of Win32 matching,
   `*.*` requires a dot in the name. **Measured 2026-09-23:** `README` → no match, `a.jpg` →
   match, `.git` → match. A user who types BC's default would silently lose every
   extensionless file. The fix is either to default to `*` or to normalise `*.*` to `*`, and
   a test should pin whichever is chosen. Switching the matcher to Win32 is **not** the fix:
   the scanner's comment says that would change what `IMG_????.CR2` means.

2. **The scanner walks the tree once per include pattern.** `Enumerate` loops over
   `filter.Patterns` and builds a new `FileSystemEnumerable` for each, de-duplicating with
   a `HashSet`. This was harmless while the list was always `["*"]`. Five include patterns
   on a NAS would be five full walks. Any implementation should first change this to one
   walk that matches *any* pattern inside the predicate. Doing so is simpler, not harder.

3. **`SameScan` ignores `Patterns`, on purpose.** Its comment explains why: patterns were
   always `["*"]`, and records compare lists by reference. Once patterns are user-editable,
   `SameScan` must compare them by *content*. Otherwise changing an exclude never lights
   **Read the folders again**, and the card goes back to describing settings that do not
   match the list on screen, which is the exact problem `CanRescanWithOptions` exists to
   prevent.

### Behaviour to settle, not problems

- **Files dropped individually bypass the filter.** `ScanPathsAsync` does this
  deliberately ("the user pointed at this one"). This should stay: an exclude of `*.xmp` should
  not refuse an `.xmp` somebody dragged in by hand. The modal's help text should say so.
- **Exclude beats include**, as in BC. Worth stating in one line in the modal.
- **The root of a drop is never excluded by a folder pattern.** Dropping a folder named
  `@eaDir` on purpose means it.
- **Session only vs preferences** is a new idea for this app: every scan setting today is
  sticky. Supporting it means keeping a session copy that overrides the saved settings until
  the app closes. That is small, but it raises a question the study does not answer: does
  *session only* apply to the whole flyout (the three checkboxes as well) or only to the
  patterns? Having two persistence rules in one flyout would be confusing. Applying it to the
  patterns only is the simpler start.
- **Templates** currently build recipes with `ScanFilter.Default`. Whether a template should
  carry its own excludes ("my Synology template") is a later question. Nothing prevents it.

---

## Rough size

One `ContentDialog`, three new record fields and their settings, the predicate changes and
the single-walk rewrite, a content-comparing `SameScan`, and the card label. Tests should be
few and aimed at the traps: `*.*` handling, a folder exclude that is never descended into
(measured with a counter in the predicate, not inferred), one walk for many patterns, and
`CanRescanWithOptions` lighting when only a pattern changes. No new dependency, no network,
no ExifTool involvement.
