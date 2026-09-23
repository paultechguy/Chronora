// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace PaulTechGuy.CN.Metadata.Tests;

/// <summary>
/// A response whose body opens and then says nothing, which is the shape of a stalled
/// download. Writing it as a stream rather than a delayed SendAsync matters: the hang this
/// guards against is mid-body, after headers have arrived and the progress bar is on
/// screen, which is exactly when there is no way out of a pane that has no buttons.
/// </summary>
internal sealed class StallingStream : Stream
{
    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        // Never completes on its own. Only a cancelled token ends this, which is the whole
        // point: something other than the server has to decide the transfer is over.
        await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);

        return 0;
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

internal sealed class StallingHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new StallingStream()),
        });
}

/// <summary>
/// Getting out of a download.
///
/// The pane that shows this download held a TextBlock, a ProgressBar and no buttons, so
/// until the Cancel button landed there was no way out of it at all. These pin the two
/// halves of that fix below the UI: a user's cancel must not be dressed up as a network
/// fault, and a connection that has gone quiet must give up without being asked.
/// </summary>
public class ExifToolDownloadTests
{
    private static readonly ExifToolManifest Manifest =
        new("13.10", "https://exiftool.org/exiftool.zip", new string('a', 64), 12_000_000);

    private static ExifToolInstaller Stalling(TimeSpan stallTimeout) =>
        new(new HttpClient(new StallingHandler()), NullLogger<ExifToolInstaller>.Instance)
        {
            StallTimeout = stallTimeout,
        };

    /// <summary>
    /// Pressing Cancel is an answer, not a fault.
    ///
    /// It used to arrive as InstallResult.Failed saying the download "did not complete ...
    /// on a managed network this is often a proxy or a policy", which sends somebody who
    /// has just stopped it themselves off to argue with their network administrator. The
    /// catch filter named TaskCanceledException, which derives from the type a cancelled
    /// stream read actually throws, so it swallowed both.
    /// </summary>
    [Fact]
    public async Task A_cancelled_download_leaves_as_a_cancellation_not_a_failure()
    {
        using var temp = new TempDir();

        // Long enough that it cannot be the stall clock that ends this.
        ExifToolInstaller installer = Stalling(TimeSpan.FromMinutes(5));

        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();

        _ = await Should.ThrowAsync<OperationCanceledException>(
            () => installer.InstallAsync(Manifest, temp.Path, progress: null, cancel.Token));
    }

    /// <summary>
    /// A connection that has gone quiet ends itself, and says which kind of ending it was.
    ///
    /// Reported as a failure rather than a cancellation on purpose: nobody cancelled, and
    /// the sentence has to be one the user can act on - so it offers the retry and the
    /// install-it-yourself route rather than blaming a proxy.
    /// </summary>
    [Fact]
    public async Task A_stalled_download_gives_up_on_its_own()
    {
        using var temp = new TempDir();

        ExifToolInstaller installer = Stalling(TimeSpan.FromMilliseconds(200));

        InstallResult result = await installer.InstallAsync(
            Manifest, temp.Path, progress: null, TestContext.Current.CancellationToken);

        result.Succeeded.ShouldBeFalse();
        result.Detail.ShouldContain("stopped sending data");
        result.Detail.ShouldContain("install ExifTool yourself");
    }

    /// <summary>
    /// The scratch folder goes either way. It is the one thing both endings share, and the
    /// cancel path reaches it through a rethrow rather than a return, which is the easier
    /// of the two to leave behind.
    /// </summary>
    [Fact]
    public async Task Neither_ending_leaves_a_part_downloaded_file_behind()
    {
        using var temp = new TempDir();

        string scratch = Path.Combine(Path.GetTempPath(), "chronora-exiftool");
        int before = Directory.Exists(scratch) ? Directory.GetDirectories(scratch).Length : 0;

        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();

        _ = await Should.ThrowAsync<OperationCanceledException>(
            () => Stalling(TimeSpan.FromMinutes(5)).InstallAsync(Manifest, temp.Path, progress: null, cancel.Token));

        _ = await Stalling(TimeSpan.FromMilliseconds(200)).InstallAsync(
            Manifest, temp.Path, progress: null, TestContext.Current.CancellationToken);

        int after = Directory.Exists(scratch) ? Directory.GetDirectories(scratch).Length : 0;

        after.ShouldBe(before, "a stopped download left its scratch folder in the temp directory");
    }
}
