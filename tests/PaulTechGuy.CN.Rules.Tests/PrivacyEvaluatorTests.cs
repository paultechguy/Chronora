// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Frozen;
using PaulTechGuy.CN.Domain;
using PaulTechGuy.CN.Rules;
using Shouldly;

namespace PaulTechGuy.CN.Rules.Tests;

public class PrivacyEvaluatorTests
{
    private static readonly EvaluationContext Ready = new(ClockContext.Local, DateTimeOffset.UtcNow, MetadataEngineAvailable: true);

    private static readonly HashSet<PrivacyCategory> Everything = [.. PrivacyCategoryNames.All];

    private static PrivacyFindings Carrying(params PrivacyCategory[] categories) =>
        new(categories.ToFrozenDictionary(c => c, c => (IReadOnlyList<string>)[$"X:{c}"]), HasEmbeddedMedia: false);

    private static ScannedFile File(MediaKind kind, PrivacyFindings? privacy, FileTraits traits = FileTraits.None) =>
        new("C:/p/a.jpg", 1, kind, FileAttributes.Normal, default, FrozenDictionary<DateField, MetadataValue>.Empty, traits)
        {
            Privacy = privacy,
        };

    private static PrivacyCategory[] Removed(FilePlan plan) =>
        [.. plan.Changes.Where(c => c.WillWrite).Select(c => ((ChangeTarget.Privacy)c.Target).Which)];

    [Fact]
    public void Only_what_the_file_carries_and_the_user_ticked_is_planned()
    {
        FilePlan plan = PrivacyEvaluator.Evaluate(
            File(MediaKind.Jpeg, Carrying(PrivacyCategory.Location, PrivacyCategory.Thumbnail)),
            new HashSet<PrivacyCategory> { PrivacyCategory.Location, PrivacyCategory.CameraOwner },
            Ready);

        Removed(plan).ShouldBe([PrivacyCategory.Location]);
    }

    [Fact]
    public void A_file_with_nothing_to_remove_is_not_rewritten()
    {
        FilePlan plan = PrivacyEvaluator.Evaluate(File(MediaKind.Jpeg, PrivacyFindings.None), Everything, Ready);

        plan.WillWrite.ShouldBeFalse();
        plan.HasProblem.ShouldBeFalse();
    }

    [Fact]
    public void A_file_not_yet_read_never_counts_as_changing()
    {
        PrivacyEvaluator.Evaluate(File(MediaKind.Jpeg, privacy: null), Everything, Ready).Changes.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(MediaKind.RawProprietary)]
    [InlineData(MediaKind.Dng)]
    public void Raw_is_refused(MediaKind kind)
    {
        FilePlan plan = PrivacyEvaluator.Evaluate(File(kind, Carrying(PrivacyCategory.Location)), Everything, Ready);

        plan.WillWrite.ShouldBeFalse();
        plan.Changes.ShouldAllBe(c => c.Problem == ProblemCode.RawNotSupported);
    }

    [Fact]
    public void A_motion_photo_is_refused_rather_than_reported_clean()
    {
        PrivacyFindings motion = Carrying(PrivacyCategory.Location) with { HasEmbeddedMedia = true };

        FilePlan plan = PrivacyEvaluator.Evaluate(File(MediaKind.Jpeg, motion), Everything, Ready);

        plan.WillWrite.ShouldBeFalse();
        plan.Changes.ShouldAllBe(c => c.Problem == ProblemCode.EmbeddedMediaNotSupported);
    }

    [Fact]
    public void Video_is_location_only()
    {
        FilePlan plan = PrivacyEvaluator.Evaluate(
            File(MediaKind.Video, Carrying([.. PrivacyCategoryNames.All])), Everything, Ready);

        Removed(plan).ShouldBe([PrivacyCategory.Location]);
    }

    [Fact]
    public void Without_ExifTool_every_ticked_category_is_blocked()
    {
        FilePlan plan = PrivacyEvaluator.Evaluate(
            File(MediaKind.Jpeg, privacy: null), Everything, Ready with { MetadataEngineAvailable = false });

        plan.HasProblem.ShouldBeTrue();
        plan.Changes.ShouldAllBe(c => c.Problem == ProblemCode.MetadataEngineUnavailable);
    }
}
