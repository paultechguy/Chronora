// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using PaulTechGuy.CN.Domain;

namespace PaulTechGuy.CN.Rules;

/// <summary>
/// What the Private details intent would do to one file: (file, chosen categories) becomes a
/// plan, with no I/O, exactly as <see cref="RuleEvaluator" /> does for dates.
///
/// It produces ordinary <see cref="FilePlan" />s on purpose. Ticking, the type filter, the
/// "can change" and "need a look" filters, the RESULT counts and the Apply gate all read
/// FilePlan, so a privacy plan gets every one of them without a second implementation.
///
/// A file only ever gets a change for a category it actually carries. Nothing is written
/// "just in case": a file with no location in it is not rewritten to remove the location it
/// does not have, which would cost a rewrite and move its Modified date for nothing.
/// </summary>
public static class PrivacyEvaluator
{
    public static FilePlan Evaluate(ScannedFile file, IReadOnlySet<PrivacyCategory> chosen, EvaluationContext context)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(chosen);
        ArgumentNullException.ThrowIfNull(context);

        // Folders and files ExifTool is never asked about have nothing to remove, and are not
        // a problem either - a folder in the list is not something the user needs to look at.
        if (file.IsDirectory || file.Kind == MediaKind.Other)
        {
            return new FilePlan(file, []);
        }

        PrivacyCategory[] applicable = [.. PrivacyCategoryNames.All.Where(c => chosen.Contains(c) && AppliesTo(c, file.Kind))];

        if (applicable.Length == 0)
        {
            return new FilePlan(file, []);
        }

        ProblemCode refusal =
            file.Kind is MediaKind.RawProprietary or MediaKind.Dng ? ProblemCode.RawNotSupported
            : !context.MetadataEngineAvailable ? ProblemCode.MetadataEngineUnavailable
            : file.Traits.HasFlag(FileTraits.CloudDehydrated) ? ProblemCode.CloudPlaceholderWouldHydrate
            : file.Privacy?.HasEmbeddedMedia == true ? ProblemCode.EmbeddedMediaNotSupported
            : ProblemCode.None;

        if (refusal != ProblemCode.None)
        {
            return new FilePlan(file, [.. applicable.Select(c => Change(c, ChangeStatus.Blocked, refusal))]);
        }

        // Not read yet. No plan rather than a guess: the row says it is reading, and a file
        // that has not been looked at must never count towards "will change".
        if (file.Privacy is not { } found)
        {
            return new FilePlan(file, []);
        }

        return new FilePlan(file, [.. applicable.Where(found.Has).Select(c => Change(c, ChangeStatus.WillChange, ProblemCode.None))]);
    }

    /// <summary>
    /// Whether a category means anything for this kind of file.
    ///
    /// Video is location only. QuickTime's location atoms can be removed; its other metadata
    /// is not organised into these categories, and a GPS TRACK recorded into the video stream
    /// itself (GoPro, DJI) is not metadata ExifTool can remove at all - the row says so.
    /// </summary>
    public static bool AppliesTo(PrivacyCategory category, MediaKind kind) =>
        kind != MediaKind.Video || category == PrivacyCategory.Location;

    private static PlannedChange Change(PrivacyCategory category, ChangeStatus status, ProblemCode problem) =>
        new(new ChangeTarget.Privacy(category), Before: null, After: status == ChangeStatus.WillChange ? new FieldWrite.Delete() : null, status, problem);
}
