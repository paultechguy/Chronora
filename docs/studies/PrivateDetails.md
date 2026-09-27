# Study: removing private details

2026-09-27. **Built the same day**; see Status section 12. This file records what was
decided and what the adversarial review changed, which are the parts worth keeping.

**The idea.** A small-footprint way to clear personal metadata from photos before sharing
them, without turning Chronora into a metadata editor.

---

## Decisions (Paul, 2026-09-27)

| Question | Answer |
|---|---|
| The job | **Privacy before sharing.** Remove location, camera serial numbers and owner, software and edit history, and embedded thumbnails. Keep dates, which are this app's whole purpose. |
| Where it lives | **A fourth intent**, run over the ticked rows through Apply. Tick, filter, preview, confirm, report and History all come for free. |
| Output | **Always in place.** No cleaned copies, no backups. |
| Undo | **None.** The confirmation says so. It carries a *Don't warn me about this again* box, which is reset from the options pane. |
| History | Runs are recorded and marked **Cannot be undone**. Tag names only, never values. |
| Viewing | **Read-only, grouped, every tag**, for one file at a time. |
| Categories | **Four fixed ones, each tickable.** |

**Out of scope:** editing tags, copies, backups, raw files, a People and captions category,
and templates.

---

## Why these tag lists, and not `-all=`

`-all=` deletes the Taken date, which this app exists to fix. It also deletes Orientation, so
portraits turn sideways, and the ICC profile, so colours shift. Chronora deletes tags by
name instead, so what it keeps is kept by construction.

The lists were **measured, not reasoned** (ExifTool 13.59, a JPEG and a PNG):

- **Unqualified and wildcard deletes reach every group.** For example, `-Artist=` also takes
  PNG's own Artist text chunk, and `-*GPS*=` also takes maker-note GPS. A group-qualified
  list would have missed PNG entirely.
- **`MakerNotes:all=` is never used.** On an iPhone it takes Live Photo pairing and HDR data
  with it. Serial numbers and owner are removed by name instead.
- **Lightroom's `XMP-crs` is kept.** Removing it would silently undo somebody's edit.
- **Copyright is kept.** It is the photographer's claim, not a leak.
- **`CameraOwnerName` and `ThumbnailTIFF` were dropped.** The first is not a defined tag; the
  second is not writable.

---

## The adversarial review, and what it changed

There were two discriminators: one on safety and privacy, one on integration and UX.

**Adopted:**

- **Success is decided by re-reading the file, not by ExifTool's status.** With `-m`, a
  warning rides on a successful write, and an undeletable tag is silently left behind. The
  PNG IPTC write proves the first: it warns every time.
- **Results are journaled one file at a time**, and **a strip is never cancelled mid-file.**
  The cancel token only stops the *wait*; ExifTool carries on regardless. Recording a file
  that was really changed as untouched is the worst report an irreversible run can give.
- **The run is actually stamped `PrivacyStrip`.** The code had always stamped `Apply`, so
  every undo guard would have let a strip through.
- **The run report's own Undo button had no `CanExecute`**, so it would have been clickable
  on a strip. It is now shown only when `CanUndo` is true.
- **The journal's revert read excludes strips**, and `RevertAsync` refuses one before it
  creates an empty undo run.
- **Motion photos are refused**, because their embedded video carries its own location.
- **A detection read** is broad, with an exact classifier, so a leftover is *reported*
  rather than missed.
- **The intent is not restored at launch.**
- **Templates step out of the intent.**
- **The privacy read runs inside the scan**, so Cancel reaches it, and it matches rows by
  identity.
- **The viewer has no Copy all.** Clipboard history would keep the coordinates.

**Corrected during the build:**

- **"Refuse `Kind != Apply`" was wrong.** Reverts are revertible by design, so the guard is
  `Kind == PrivacyStrip`.
- **Refusing every file with an MPF image was dropped.** iPhone HDR JPEGs carry their gain
  map that way, so it would have refused most camera rolls. Only embedded *video* is refused.

**Rejected:**

- "There is no CSV export." There is, and it already exports privacy rows by category name
  with no values.
- A temporary crash-safety copy. It contradicts the no-backups decision.

**Accepted risk:** `-overwrite_original_in_place` writes to a temporary file and then copies
it over the original. A power loss *during that copy* can truncate the photo. The warning
tells people to keep their own copy.
