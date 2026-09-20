// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using PaulTechGuy.CN.Services;
using Shouldly;

namespace PaulTechGuy.CN.Services.Tests;

/// <summary>
/// Answers one canned response, so the check can be tested without the network.
/// </summary>
internal sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
}

/// <summary>
/// Whether a newer Chronora exists.
///
/// The case worth the most here is the unreachable one. "Could not check" and "up to date"
/// have to stay different answers: collapsing them reports success when nothing was read,
/// which keeps somebody on a version whose bug has already been fixed and tells them, with
/// confidence, that there is nothing to get.
/// </summary>
public class UpdateCheckerTests
{
    private static UpdateChecker With(HttpStatusCode status, string body) =>
        new(new HttpClient(new StubHandler(status, body)));

    [Fact]
    public async Task A_newer_published_version_is_offered()
    {
        UpdateChecker checker = With(
            HttpStatusCode.OK,
            """{"version":"0.2.0","url":"https://example.invalid/r","notes":"Takeout importer."}""");

        UpdateStatus status = await checker.CheckAsync(new Version(0, 1, 0), cancellationToken: TestContext.Current.CancellationToken);

        status.Outcome.ShouldBe(UpdateOutcome.UpdateAvailable);
        status.Latest.ShouldBe(new Version(0, 2, 0));
        status.Notes.ShouldBe("Takeout importer.");
    }

    [Fact]
    public async Task The_same_version_is_up_to_date()
    {
        UpdateChecker checker = With(
            HttpStatusCode.OK,
            """{"version":"0.1.0","url":"https://example.invalid/r"}""");

        UpdateStatus status = await checker.CheckAsync(new Version(0, 1, 0), cancellationToken: TestContext.Current.CancellationToken);

        status.Outcome.ShouldBe(UpdateOutcome.UpToDate);
    }

    /// <summary>
    /// A local build ahead of what is published is not an invitation to "upgrade"
    /// backwards, which is exactly what a plain inequality would do.
    /// </summary>
    [Fact]
    public async Task A_build_ahead_of_the_published_one_is_not_offered_a_downgrade()
    {
        UpdateChecker checker = With(
            HttpStatusCode.OK,
            """{"version":"0.1.0","url":"https://example.invalid/r"}""");

        UpdateStatus status = await checker.CheckAsync(new Version(0, 2, 0), cancellationToken: TestContext.Current.CancellationToken);

        status.Outcome.ShouldBe(UpdateOutcome.UpToDate);
    }

    [Fact]
    public async Task An_unreachable_site_is_not_reported_as_up_to_date()
    {
        UpdateChecker checker = With(HttpStatusCode.NotFound, "no");

        UpdateStatus status = await checker.CheckAsync(new Version(0, 1, 0), cancellationToken: TestContext.Current.CancellationToken);

        status.Outcome.ShouldBe(UpdateOutcome.CouldNotCheck);
        status.Problem.ShouldNotBeNullOrWhiteSpace("the failure has to be sayable to a person");
    }

    [Fact]
    public async Task A_manifest_that_makes_no_sense_is_not_reported_as_up_to_date()
    {
        UpdateChecker checker = With(HttpStatusCode.OK, """{"version":"banana","url":"x"}""");

        UpdateStatus status = await checker.CheckAsync(new Version(0, 1, 0), cancellationToken: TestContext.Current.CancellationToken);

        status.Outcome.ShouldBe(UpdateOutcome.CouldNotCheck);
    }

    /// <summary>
    /// The file Chronora actually publishes has to parse with the running version, or the
    /// whole feature is broken in exactly the way nobody notices until a release is out.
    /// </summary>
    [Fact]
    public async Task The_manifest_in_the_docs_folder_is_readable()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "docs", "version.json");

        Assert.SkipUnless(File.Exists(path), "Running outside the repository.");

        UpdateChecker checker = With(
            HttpStatusCode.OK,
            await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));

        UpdateStatus status = await checker.CheckAsync(new Version(0, 1, 0), cancellationToken: TestContext.Current.CancellationToken);

        status.Outcome.ShouldNotBe(UpdateOutcome.CouldNotCheck, "the published manifest must be readable");
    }
}
