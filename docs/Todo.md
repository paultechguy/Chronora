# What Chronora owes

Last updated 2026-09-23. Branch `dev`. Two items. One of them — the flaky suite — **is** a
surprise that was discovered, and it is bigger than it was written up as.

`Status.md` says where the project *is* and what needs a human to look at it. This says what
is left to *do*. When an item lands, delete it from here and, if it needs testing, add it to
the list in `Status.md`.

Each item says what it is, why it was left, and what "done" looks like. Rough priority
order, but they are independent — take whichever suits the session.

---

## 1. High contrast has never been looked at

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

## 2. A flaky suite — two separate causes, both found 2026-09-23

**This item was "a flaky test". It is not.** `PaulTechGuy.CN.Presentation.Tests` has **two
unrelated intermittent failures**, and between them they put the assembly red roughly one
run in four. Neither ever reproduces in isolation. Measured across ~15 full runs on
2026-09-23, including five at a commit with no local changes, so neither is anything a
recent change introduced.

---

### 2a. A SQLite handle race in the fixture constructor

**What.** A test fails inside `WorkbenchFixture`'s constructor with a
`SafeHandle.DangerousAddRef` crash in `sqlite3_changes`, under `JournalSchema.ApplyPragmas`.
It passes on every rerun, alone and across the full solution.

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

### 2b. Tests race the recompute debounce

**Found 2026-09-23** while checking whether the row-plumbing change had broken anything. It
had not; this is what was failing.

**What.** A *different* test fails each run with an ordinary assertion failure — a stale
value, not a crash. Three seen, all in tests that set an option and then assert:

- `RunNoticeTests.A_run_report_survives_the_rescan_that_follows_it` — footer still read
  `Writing 2 of 2…` instead of the rescan's sentence.
- `TypeFilterTests.A_bare_extension_works_as_well_as_a_wildcard(typed: "  png  ")`
- `ExplorerPatternTests.Explorer_style_patterns_select_what_they_should(pattern: "beach*")`

The last of those failed at `a1465c0` with **no local changes**, which is how this was
separated from the work in flight.

**The cause.** `QueueRecompute` (`WorkbenchViewModel.cs`, around line 3570) is a real
timer-based debounce: `Task.Delay(RecomputeDebounce)` and then a dispatcher post. It runs in
tests exactly as it runs in the app. A test that changes an option and asserts immediately
is racing that timer — it passes when the machine is idle and fails when six test classes
are running at once. It also means a debounce queued by one step can land *during* a later
one and overwrite what it just set up.

**Not a product bug.** The debounce is right for the app; it is the tests that assume it is
not there.

**Done looks like:** the fixture can settle the debounce deterministically. Options, roughly
in order of how much they change: give `WorkbenchFixture` a way to flush it (await the
pending recompute, or drive `RecomputeDebounce` to zero for tests, the way
`ExifToolInstaller.StallTimeout` is settable); or have the affected tests call `Recompute()`
explicitly instead of relying on the queue. **Worth checking first whether any test is
relying on the debounce deliberately** — one that tests the debouncing itself would need
the opposite treatment.

---

## Housekeeping, needs an answer rather than work

- **`dev` reports being ahead of `origin/dev` by 11 commits**, which contradicts the
  standing rule that there is no remote. Either the tracking config is stale or the rule has
  moved on. Nothing has been pushed.
- **`feature/UI-Refactor` still points at `c01e428`**, merged into `dev` on 2026-09-22 as
  `cc2cd9d`. Keep as a marker or delete.
