// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;
using PaulTechGuy.CN.Domain;
using PaulTechGuy.CN.FileSystem;
using PaulTechGuy.CN.Journal;
using PaulTechGuy.CN.Metadata;

namespace PaulTechGuy.CN.Services;

/// <summary>How far a run has got.</summary>
/// <param name="Done">Files finished.</param>
/// <param name="Total">Files in the run.</param>
/// <param name="Written">Files actually changed.</param>
/// <param name="Failed">Files that could not be changed.</param>
/// <param name="CurrentPath">What is being worked on, for the status line.</param>
public readonly record struct ApplyProgress(int Done, int Total, int Written, int Failed, string CurrentPath);

/// <summary>What a finished run amounted to.</summary>
/// <param name="RunId">The journal run, so History and undo can find it.</param>
/// <param name="Written">Files changed.</param>
/// <param name="Failed">Files that failed.</param>
/// <param name="Skipped">Files with nothing to do.</param>
/// <param name="Status">How it ended.</param>
public sealed record ApplyOutcome(long RunId, int Written, int Failed, int Skipped, RunStatus Status);

/// <summary>
/// Writes a plan to disk, and puts it back again.
///
/// Two rules govern everything here. Prior state is committed to the journal BEFORE any file
/// in a batch is touched, because the crash the journal exists to survive is the mid-write
/// one. And metadata is written before filesystem timestamps, because ExifTool rewrites the
/// file and moves the very times the user just set - that ordering is the difference between
/// the preview being true and being a lie.
/// </summary>
public sealed class ApplyService(
    FileTimeWriter writer,
    VolumeProbe volumes,
    SqliteJournal journal,
    ILogger<ApplyService> logger,
    IMetadataWriteGateway? metadata = null)
{
    private readonly FileTimeWriter _writer = writer;
    private readonly VolumeProbe _volumes = volumes;
    private readonly SqliteJournal _journal = journal;
    private readonly ILogger<ApplyService> _logger = logger;

    /// <summary>
    /// Optional, and null in every test that is only about filesystem dates. A run that
    /// needs it and does not have it reports the field as blocked rather than failing.
    /// </summary>
    private readonly IMetadataWriteGateway? _metadata = metadata;

    /// <summary>
    /// Keep a copy of a file before rewriting its bytes.
    ///
    /// On by default, and the reason is in the journal's own design: it records field
    /// values, so it can put a date back but cannot repair a container a write corrupted.
    /// Those are two different guarantees and only one of them is free.
    /// </summary>
    public bool KeepBackups { get; set; } = true;

    /// <summary>
    /// Applies a set of plans. Runs off the UI thread; progress is reported through
    /// <paramref name="progress" />, which the caller creates on the UI thread so the
    /// marshalling is handled for us.
    /// </summary>
    public async Task<ApplyOutcome> ApplyAsync(
        IReadOnlyList<FilePlan> plans,
        RunHeader header,
        IProgress<ApplyProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plans);
        ArgumentNullException.ThrowIfNull(header);

        return await Task.Run(
            () => this.ApplyCoreAsync(plans, header, progress, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<ApplyOutcome> ApplyCoreAsync(
        IReadOnlyList<FilePlan> plans,
        RunHeader header,
        IProgress<ApplyProgress>? progress,
        CancellationToken cancellationToken)
    {
        long runId = this._journal.BeginRun(header, DateTimeOffset.UtcNow);

        int written = 0;
        int failed = 0;
        int skipped = 0;
        int done = 0;
        var status = RunStatus.Completed;

        try
        {
            foreach (FilePlan[] batch in Batch(plans, SqliteJournal.BatchSize))
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Everything this batch is about to destroy, recorded and committed first.
                IReadOnlyList<long> rowIds = this._journal.RecordPriorState(
                    runId,
                    [.. batch.Select(ToPriorState)]);

                var results = new List<FileResult>(batch.Length);

                for (int i = 0; i < batch.Length; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    FilePlan plan = batch[i];
                    FileResult result = await this.ApplyOneAsync(plan, rowIds[i], cancellationToken).ConfigureAwait(false);
                    results.Add(result);

                    switch (result.Outcome)
                    {
                        case FileOutcome.Applied:
                            written++;
                            break;
                        case FileOutcome.Failed:
                            failed++;
                            break;
                        default:
                            skipped++;
                            break;
                    }

                    done++;

                    // 10 Hz is plenty for a progress bar and avoids posting 50,000 messages
                    // to the dispatcher.
                    if (done % 64 == 0 || done == plans.Count)
                    {
                        progress?.Report(new ApplyProgress(done, plans.Count, written, failed, plan.File.FullPath));
                    }
                }

                this._journal.RecordResults(results);
            }
        }
        catch (OperationCanceledException)
        {
            status = RunStatus.Cancelled;
            this._logger.LogInformation("Run {RunId} was cancelled after {Done} of {Total} files.", runId, done, plans.Count);
        }
        catch (Exception ex)
        {
            status = RunStatus.Failed;
            this._logger.LogError(ex, "Run {RunId} failed after {Done} of {Total} files.", runId, done, plans.Count);
            throw;
        }
        finally
        {
            this._journal.CompleteRun(runId, status, DateTimeOffset.UtcNow);
        }

        this._logger.LogInformation(
            "Run {RunId} finished: {Written} written, {Failed} failed, {Skipped} skipped.",
            runId, written, failed, skipped);

        return new ApplyOutcome(runId, written, failed, skipped, status);
    }

    /// <summary>
    /// One file, in the one order that is correct.
    ///
    /// Metadata first, then all four timestamps and the attributes in a single
    /// SetFileInformationByHandle call. There is no "ChangeTime last" step because a single
    /// call has no sequence to get wrong.
    ///
    /// The order is not a preference. ExifTool rewrites the file, which moves the very
    /// timestamps the user is here to set, so metadata written afterwards would silently
    /// undo half the run and the preview would have been a lie.
    /// </summary>
    private async Task<FileResult> ApplyOneAsync(FilePlan plan, long rowId, CancellationToken cancellationToken)
    {
        IReadOnlyList<PlannedChange> writes = [.. plan.Changes.Where(c => c.WillWrite)];

        if (writes.Count == 0)
        {
            return new FileResult(rowId, FileOutcome.Skipped, null);
        }

        List<PlannedChange> metadataWrites = [.. writes.Where(IsMetadata)];
        bool rewroteBytes = false;

        if (metadataWrites.Count > 0)
        {
            if (this._metadata is null || !this._metadata.Available)
            {
                return new FileResult(rowId, FileOutcome.Failed, "ExifTool is not available, so the photo date was not written.");
            }

            MetadataWriteResult written = await this._metadata
                .WriteAsync(BuildRequest(plan, metadataWrites), this.KeepBackups, cancellationToken)
                .ConfigureAwait(false);

            if (!written.Succeeded)
            {
                // The filesystem write is abandoned too. Half-applying a file the user
                // asked to change in two ways, and then reporting a failure, leaves them
                // with no idea which half landed.
                return new FileResult(rowId, FileOutcome.Failed, written.Detail);
            }

            rewroteBytes = written.Destination == WriteDestination.Embedded;
        }

        IReadOnlyList<PlannedChange> fileWrites = [.. writes.Where(w => !IsMetadata(w))];

        if (fileWrites.Count == 0 && !rewroteBytes)
        {
            // A sidecar write touched nothing else, so there is nothing to put back.
            return new FileResult(rowId, FileOutcome.Applied, null);
        }

        // Anything the rewrite disturbed but the user did not ask to change is put back to
        // what it was. Otherwise "set the photo's Taken date" silently moves the file's
        // Modified date as well, which is precisely the surprise this app exists to stop.
        TimestampSet times = rewroteBytes
            ? ToTimestampSet(fileWrites, restoreFrom: plan.File.Times)
            : ToTimestampSet(fileWrites);

        bool wantsChangeTime = fileWrites.Any(w => w.Target is ChangeTarget.Field { Which: DateField.FileChanged });

        WriteResult result = this._writer.Write(
            plan.File.FullPath,
            times,
            plan.File.IsDirectory,
            attributes: null,
            verify: wantsChangeTime);

        if (result.Succeeded)
        {
            return new FileResult(rowId, FileOutcome.Applied, null);
        }

        // A proven ChangeTime rejection turns the capability off for the whole volume, so
        // the rest of the run stops attempting something this drive cannot do rather than
        // rediscovering it file by file.
        if (result.Problem == ProblemCode.ChangeTimeUnsupported)
        {
            this._volumes.RecordChangeTimeUnsupported(plan.File.FullPath);
        }

        return new FileResult(rowId, FileOutcome.Failed, result.Detail ?? result.Problem.ToString());
    }

    /// <summary>
    /// Builds a revert plan and pushes it through the same write path.
    ///
    /// A revert is an ordinary run, which is deliberate: it is journaled, it shows up in
    /// History, and it can itself be reverted.
    /// </summary>
    public async Task<ApplyOutcome> RevertAsync(
        long sourceRunId,
        RunHeader header,
        bool force = false,
        IProgress<ApplyProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(header);

        return await Task.Run(
            () => this.Revert(sourceRunId, header, force, progress, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    private ApplyOutcome Revert(
        long sourceRunId,
        RunHeader header,
        bool force,
        IProgress<ApplyProgress>? progress,
        CancellationToken cancellationToken)
    {
        var recorded = this._journal.ReadRevertable(sourceRunId);
        long runId = this._journal.BeginRun(header with { Kind = RunKind.Revert, RevertsRunId = sourceRunId }, DateTimeOffset.UtcNow);

        int restored = 0;
        int refused = 0;
        int done = 0;
        var status = RunStatus.Completed;

        try
        {
            foreach ((JournalFile file, IReadOnlyList<JournalFieldChange> changes) in recorded)
            {
                cancellationToken.ThrowIfCancellationRequested();

                RevertOutcome outcome = this.RevertOne(file, changes, force);
                this._journal.RecordRevert(file.FileRowId, outcome, DateTimeOffset.UtcNow);

                if (outcome == RevertOutcome.Reverted)
                {
                    restored++;
                }
                else
                {
                    refused++;
                }

                done++;

                if (done % 64 == 0 || done == recorded.Count)
                {
                    progress?.Report(new ApplyProgress(done, recorded.Count, restored, refused, file.Path));
                }
            }
        }
        catch (OperationCanceledException)
        {
            status = RunStatus.Cancelled;
        }
        finally
        {
            this._journal.CompleteRun(runId, status, DateTimeOffset.UtcNow);
            this._journal.RefreshRevertStatus(sourceRunId);
        }

        this._logger.LogInformation(
            "Revert of run {Source} finished: {Restored} restored, {Refused} left alone.",
            sourceRunId, restored, refused);

        return new ApplyOutcome(runId, restored, refused, 0, status);
    }

    /// <summary>
    /// The drift ladder, in order: does the file still exist, is it still the same file,
    /// and does it still hold what the run left there?
    ///
    /// Anything that fails is SKIPPED by default. Silently stomping a value the user
    /// deliberately changed last Tuesday is the one unforgivable bug in an undo feature, so
    /// forcing it has to be an explicit choice.
    /// </summary>
    private RevertOutcome RevertOne(JournalFile file, IReadOnlyList<JournalFieldChange> changes, bool force)
    {
        if (!this._writer.TryRead(file.Path, file.IsDirectory, out TimestampSet current, out _))
        {
            return RevertOutcome.Missing;
        }

        if (!force && HasDrifted(current, changes))
        {
            this._logger.LogInformation("{Path} changed since the run; leaving it alone.", file.Path);
            return RevertOutcome.Drifted;
        }

        TimestampSet restore = ToRestoreSet(changes);

        WriteResult result = this._writer.Write(file.Path, restore, file.IsDirectory);

        return result.Succeeded
            ? RevertOutcome.Reverted
            : result.Problem == ProblemCode.FileLocked ? RevertOutcome.Locked : RevertOutcome.Failed;
    }

    /// <summary>
    /// True when the file no longer holds what the run wrote, which means something else
    /// edited it in the meantime.
    /// </summary>
    private static bool HasDrifted(TimestampSet current, IReadOnlyList<JournalFieldChange> changes)
    {
        foreach (JournalFieldChange change in changes)
        {
            if (change.AfterTicks is not { } expected)
            {
                continue;
            }

            DateTimeOffset? actual = current.Get(change.Field);
            if (actual is null)
            {
                return true;
            }

            // A second of slack for filesystem granularity: FAT rounds, and a few ticks of
            // difference is storage behaviour rather than somebody editing the file.
            if ((actual.Value - new DateTimeOffset(expected, TimeSpan.Zero)).Duration() > TimeSpan.FromSeconds(2))
            {
                return true;
            }
        }

        return false;
    }

    private static PriorState ToPriorState(FilePlan plan)
    {
        List<PriorField> fields = [.. plan.Changes
            .Where(c => c.WillWrite && c.Target is ChangeTarget.Field)
            .Select(c =>
            {
                var field = (ChangeTarget.Field)c.Target;
                DateFieldSpec spec = DateFieldCatalog.Get(field.Which);

                return new PriorField(
                    field.Which,
                    spec.Tag,
                    BeforePresent: c.Before is not null,
                    BeforeTicks: c.BeforeDate?.UtcTicks,
                    BeforeRaw: plan.File.MetadataFor(field.Which).Raw,
                    AfterTicks: c.AfterDate?.UtcTicks,
                    AfterRaw: null);
            })];

        return new PriorState(
            plan.File.FullPath,
            VolumeSerial: null,
            FileId: null,
            plan.File.Length,
            (int)plan.File.Attributes,
            plan.File.IsDirectory,
            fields);
    }

    private static bool IsMetadata(PlannedChange change) =>
        change.Target is ChangeTarget.Field field
        && DateFieldCatalog.GenreOf(field.Which) == FieldGenre.Metadata;

    /// <summary>
    /// Turns the metadata half of a plan into the tags ExifTool will be given.
    ///
    /// One target expands to several tags: the date, the offset that EXIF has no room for,
    /// and the sub-second companion that has to be cleared rather than left behind
    /// describing a moment that never existed.
    /// </summary>
    private static MetadataWriteRequest BuildRequest(FilePlan plan, List<PlannedChange> writes)
    {
        var assignments = new List<TagAssignment>(writes.Count * 3);

        // Which frame this file's QuickTime atoms are in, as the scan inferred it.
        // Written in the wrong one a video date looks entirely plausible and is hours out.
        bool quickTimeAsUtc = plan.File.Traits.HasFlag(FileTraits.QuickTimeReadAsUtc);

        foreach (PlannedChange write in writes)
        {
            if (write.Target is not ChangeTarget.Field field)
            {
                continue;
            }

            switch (write.After)
            {
                case FieldWrite.SetDate set:
                    assignments.AddRange(TagWritePlanner.Plan(
                        field.Which, set.Value, set.Precision, writeOffset: true, quickTimeAsUtc));
                    break;

                case FieldWrite.SetRaw raw when DateFieldCatalog.Get(field.Which).Tag is { } tag:
                    assignments.Add(new TagAssignment(tag, raw.Raw));
                    break;

                case FieldWrite.Delete when DateFieldCatalog.Get(field.Which).Tag is { } tag:
                    assignments.Add(new TagAssignment(tag, string.Empty));
                    break;

                default:
                    break;
            }
        }

        return new MetadataWriteRequest(plan.File.FullPath, plan.File.Kind, assignments);
    }

    /// <summary>
    /// Only the fields a plan actually writes; the rest stay null, meaning "leave alone".
    /// </summary>
    /// <param name="writes">The planned filesystem changes.</param>
    /// <param name="restoreFrom">
    /// The file's original timestamps, supplied only when the bytes have just been
    /// rewritten. Every field the plan does not set is then written back to what it was,
    /// because the rewrite moved it and nobody asked for that.
    /// </param>
    private static TimestampSet ToTimestampSet(IReadOnlyList<PlannedChange> writes, TimestampSet? restoreFrom = null)
    {
        DateTimeOffset? created = restoreFrom?.Created;
        DateTimeOffset? modified = restoreFrom?.Modified;
        DateTimeOffset? accessed = restoreFrom?.Accessed;
        DateTimeOffset? changed = restoreFrom?.Changed;

        foreach (PlannedChange write in writes)
        {
            if (write.Target is not ChangeTarget.Field field || write.AfterDate is not { } value)
            {
                continue;
            }

            switch (field.Which)
            {
                case DateField.FileCreated: created = value; break;
                case DateField.FileModified: modified = value; break;
                case DateField.FileAccessed: accessed = value; break;
                case DateField.FileChanged: changed = value; break;
                default: break;
            }
        }

        return new TimestampSet(created, modified, accessed, changed);
    }

    private static TimestampSet ToRestoreSet(IReadOnlyList<JournalFieldChange> changes)
    {
        DateTimeOffset? created = null;
        DateTimeOffset? modified = null;
        DateTimeOffset? accessed = null;
        DateTimeOffset? changed = null;

        foreach (JournalFieldChange change in changes)
        {
            // A null BeforeTicks on a filesystem field means there was nothing there, which
            // cannot happen for a real file, so it is left alone rather than zeroed.
            if (change.BeforeTicks is not { } ticks)
            {
                continue;
            }

            var value = new DateTimeOffset(ticks, TimeSpan.Zero);

            switch (change.Field)
            {
                case DateField.FileCreated: created = value; break;
                case DateField.FileModified: modified = value; break;
                case DateField.FileAccessed: accessed = value; break;
                case DateField.FileChanged: changed = value; break;
                default: break;
            }
        }

        return new TimestampSet(created, modified, accessed, changed);
    }

    private static IEnumerable<T[]> Batch<T>(IReadOnlyList<T> source, int size)
    {
        for (int i = 0; i < source.Count; i += size)
        {
            yield return [.. source.Skip(i).Take(size)];
        }
    }
}
