# What Chronora owes

Last updated 2026-09-22. Branch `dev`. Six items, all known and all deferred on purpose —
none of them is a surprise waiting to be discovered.

`Status.md` says where the project *is* and what needs a human to look at it. This says what
is left to *do*. When an item lands, delete it from here and, if it needs testing, add it to
the list in `Status.md`.

Each item says what it is, why it was left, and what "done" looks like. Rough priority
order, but they are independent — take whichever suits the session.

---

## 1. A large drop has no brake

**What.** Dropping a folder expands it with no confirmation, no depth limit and no file cap.
Recursion is a checkbox now and the scan can be cancelled, but nothing warns before a drop
turns into forty thousand rows.

**Why it is still here.** This is the part of the folder-processing question that was always
a product decision rather than a bug. The bugs around it — junction traversal, hidden files,
subfolders becoming rows — were fixed on 2026-09-22. This was deliberately not bundled with
them, because the answer is a judgement about what people mean by dropping a folder.

**Still the most destructive gesture in the app.**

**Decisions needed before any code:**
- Does a drop that resolves to more than N files confirm first, or just report afterwards?
  There is already an undo notice on every add, which may be enough.
- Is there a depth limit, and is it a setting or a constant?
- A file cap at all? A cap that silently truncates would be worse than no cap.

**Done looks like:** dropping a deep tree either states what it is about to do before doing
it, or is demonstrably safe to let run. Either way the answer is written down in
`Status.md`.

---

## 2. The ExifTool download has no Cancel

**What.** `ExifToolConsent.cs` (around the install progress pane) shows a `ContentDialog`
holding a `TextBlock` and a `ProgressBar` and **no buttons at all**. A stalled or very slow
download leaves a modal on screen with no way out.

**Why it matters more than its size suggests.** This is the first thing a new user meets:
they have asked for photo dates, the app has said it needs a helper, and they have said yes.
A dead modal at that moment is the worst possible first impression.

**Why it is still here.** Found during the adversarial review of the chrome refactor, and
kept out of it because it is a different part of the app.

**Done looks like:** `CloseButtonText = "Cancel"`, a `CancellationTokenSource` threaded into
the download, and the pane returning to its previous state rather than closing the whole
consent flow. The manifest fetch already got a bounded timeout on 2026-09-22; this is the
other half.

---

## 3. `ScanStatus` carries about thirty-six unrelated messages

**What.** One `TextBlock` in the footer is the channel for a transient toast ("Copied the
path to x.jpg"), a mode statement ("Using 'template'"), a progress meter ("Read 45,000
files…") and a run report ("Done. 0 changed, 5 failed… ExifTool is not available") — roughly
thirty-six assignment sites in `WorkbenchViewModel`.

**Why it matters.** The 2026-09-22 fix that made failure reasons visible put the app's most
important sentence into a channel where the next mouse gesture overwrites it. A run that
partly failed can say so and be gone before it is read.

**Why it is still here.** Untangling it touches every one of those sites and wants the
notice region settled first — see the note below.

**Done looks like:** progress stays in the footer; a run outcome becomes a notice that
persists until dismissed, with a link to History; incidental confirmations become the toast
that already exists.

**Related, decide together:** two `InfoBar`s still stack above the list (ExifTool missing,
intent nudge) and both can be open at once. The action notice already left that stack and
became a floating toast on 2026-09-22. Whether the remaining two become one notice region is
the same decision as where a run outcome goes.

---

## 4. Two engineering smells in the row plumbing

Cheap to fix and cheapest while the code is fresh. Both were found by the review of the
chrome refactor and neither was in its scope.

**`TrackRow` never unsubscribes.** Every row's `PropertyChanged` is hooked to call
`RefreshSummary`, and `_allRows.Clear()` happens in five places without ever detaching them.
A leak today; a correctness hazard the moment a cleared row's `IsIncluded` can still fire.

**`SelectAllShown` is N × O(N).** Every `IsIncluded` change triggers a full `RefreshSummary`,
which walks all of `_allRows` **and** calls `BuildRecipe()` — and `SelectAllShown` sets
`IsIncluded` in a loop. Survivable only because nobody has clicked it on 50,000 files.

**Done looks like:** rows detach when they leave the list, and the two selection commands
suppress the per-row storm and refresh once at the end. The codebase already uses
`_applyingIntent` / `_applyingTemplate` flags for exactly this shape of problem.

---

## 5. High contrast has never been looked at

**What.** The chrome refactor added a command deck, chips, column headers, a floating toast
and accent-coloured focus visuals. None of it has been seen in a high contrast theme.

**Why it matters here specifically.** `App.xaml` defines `CnAccentBrush` and
`FocusVisualPrimaryBrush` per theme, with a `HighContrast` dictionary that points both at
`SystemColorHighlightColor` — written to the rule that high contrast takes its colours from
the user's scheme and nothing may override them. That is the intent; it is unverified.

**Done looks like:** switch Windows to a high contrast theme and walk the window. Particular
suspicion: the deck cards' accent left edge, the chips' background, the toast's card fill,
and the title bar, which behaves differently under high contrast and has a custom drag
region and a button in it.

---

## 6. A flaky test

**What.** `WorkbenchNotificationTests` failed once inside `WorkbenchFixture`'s constructor
with a `SafeHandle.DangerousAddRef` crash in `sqlite3_changes`, under
`JournalSchema.ApplyPragmas`. It passed on every rerun, alone and across the full solution.

**Reading.** A SQLitePCL native handle race during parallel fixture construction, not
anything the refactor touched.

**Why it is still here.** One occurrence is not a pattern, and chasing it on one sighting
would be guesswork.

**Done looks like:** either it recurs and there is enough evidence to fix it, or it is
written off. Worth watching if CI goes red in `PaulTechGuy.CN.Presentation.Tests` for no
apparent reason — that is the signature.

---

## Housekeeping, needs an answer rather than work

- **`dev` reports being ahead of `origin/dev` by 11 commits**, which contradicts the
  standing rule that there is no remote. Either the tracking config is stale or the rule has
  moved on. Nothing has been pushed.
- **`feature/UI-Refactor` still points at `c01e428`**, merged into `dev` on 2026-09-22 as
  `cc2cd9d`. Keep as a marker or delete.
