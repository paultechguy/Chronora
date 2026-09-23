# What Chronora owes

Last updated 2026-09-23. Branch `dev`. Four items, all known and all deferred on purpose —
none of them is a surprise waiting to be discovered.

`Status.md` says where the project *is* and what needs a human to look at it. This says what
is left to *do*. When an item lands, delete it from here and, if it needs testing, add it to
the list in `Status.md`.

Each item says what it is, why it was left, and what "done" looks like. Rough priority
order, but they are independent — take whichever suits the session.

---

## 1. `ScanStatus` carries about thirty-six unrelated messages

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

## 2. Two engineering smells in the row plumbing

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

## 3. High contrast has never been looked at

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

## 4. A flaky test — cause found 2026-09-23, fix not yet made

**What.** A test in `PaulTechGuy.CN.Presentation.Tests` fails inside `WorkbenchFixture`'s
constructor with a `SafeHandle.DangerousAddRef` crash in `sqlite3_changes`, under
`JournalSchema.ApplyPragmas`. It passes on every rerun, alone and across the full solution.

**Three sightings**, all on full-solution runs and never on the project alone: once before
2026-09-22 in `WorkbenchNotificationTests`, and twice on 2026-09-23 — the second of which
was caught with the whole log kept rather than piped through `tail`, which is the only
reason there is anything below. **The test name is different every time** — the third was
`WorkbenchSelectionTests.Setting_the_selection_to_what_it_already_is_changes_nothing` — and
that is itself the tell: nothing is wrong with any of these tests. It is the fixture.

**The exception, in full:**

```
System.ObjectDisposedException : Cannot access a disposed object.
Object name: 'SQLitePCL.sqlite3'.
   at System.Runtime.InteropServices.SafeHandle.DangerousAddRef(Boolean& success)
   at SQLitePCL.raw.sqlite3_changes(sqlite3 db)
   at Microsoft.Data.Sqlite.SqliteDataRecord.AddChanges()
   at Microsoft.Data.Sqlite.SqliteDataReader.Dispose(Boolean disposing)
   at Microsoft.Data.Sqlite.SqliteCommand.ExecuteNonQuery()
   at PaulTechGuy.CN.Journal.JournalSchema.ApplyPragmas(...) JournalSchema.cs:69
   at PaulTechGuy.CN.Journal.SqliteJournal.Open(...) SqliteJournal.cs:107
   at PaulTechGuy.CN.Presentation.Tests.WorkbenchFixture..ctor() WorkbenchFixture.cs:58
```

**The cause, and it is entirely in test code.** `SqliteJournal.Open` sets `Pooling = true`.
`WorkbenchFixture.Dispose` ends with `SqliteConnection.ClearAllPools()`
(`WorkbenchFixture.cs:126`), which is **process-wide and static** — it is not scoped to the
fixture that calls it. xUnit runs test classes in parallel, a `WorkbenchFixture` is built per
test, so one test's teardown disposes the pooled `sqlite3` handle another test's constructor
has just taken out of the pool and is running `PRAGMA` statements on. Hence a different
victim every time, only under parallelism, only in the assembly whose fixture calls it.
`ApplyServiceTests.cs:95` and `SqliteJournalTests.cs:28` make the same call, but those
assemblies run in their own processes, so they are not implicated in this one.

**Not a product bug.** Nothing in the app calls `ClearAllPools`, and nothing in the app
builds journals concurrently.

**Done looks like:** one of — drop the `ClearAllPools()` call (it is there so the temp folder
can be deleted, and that delete already tolerates failure); or serialise the fixture, by
putting these classes in one xUnit collection or turning parallelism off for the assembly.
Dropping the call is the smaller change and removes the race rather than hiding it; the
collection approach keeps the pool clearing but costs the assembly its parallelism, which is
currently about a second. **Worth deciding rather than guessing at** — it is a choice between
a slower suite and a slightly leakier temp directory.

---

## Housekeeping, needs an answer rather than work

- **`dev` reports being ahead of `origin/dev` by 11 commits**, which contradicts the
  standing rule that there is no remote. Either the tracking config is stale or the rule has
  moved on. Nothing has been pushed.
- **`feature/UI-Refactor` still points at `c01e428`**, merged into `dev` on 2026-09-22 as
  `cc2cd9d`. Keep as a marker or delete.
