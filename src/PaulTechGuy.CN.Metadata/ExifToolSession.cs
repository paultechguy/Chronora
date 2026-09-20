// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PaulTechGuy.CN.Metadata;

/// <summary>What one ExifTool command produced.</summary>
/// <param name="StandardOutput">Everything before the ready sentinel.</param>
/// <param name="StandardError">Warnings and errors, which ExifTool emits per file.</param>
/// <param name="Status">
/// ExifTool's own exit status for the command, recovered through -echo4. A batch can
/// partially fail, so this is checked per command rather than per process.
/// </param>
public sealed record ExifToolResult(string StandardOutput, string StandardError, int Status)
{
    public bool Succeeded => this.Status == 0;
}

/// <summary>
/// A long-lived ExifTool driven over its -stay_open protocol.
///
/// Starting a process per file would dominate the runtime - ExifTool takes roughly a second
/// to boot its Perl runtime - so one process handles the whole run and commands are framed
/// by sentinels.
///
/// Commands are serialised. The protocol is a single pair of pipes with no request ids in
/// the payload, so two commands in flight would interleave their output irrecoverably.
/// </summary>
public sealed class ExifToolSession : IAsyncDisposable
{
    /// <summary>
    /// Arguments that go on every command, and each one is load-bearing.
    /// </summary>
    private static readonly string[] CommonArguments =
    [
        // Without this ExifTool uses the ANSI code page and simply cannot see a file named
        // with characters outside it. Silent "file not found" on exactly the files someone
        // cares most about.
        "-charset",
        "filename=utf8",

        // Group-qualified tag names. The same tag name exists in several groups and an
        // unqualified read cannot tell ExifIFD:CreateDate from QuickTime:CreateDate.
        "-G1",

        // Ignore minor errors. Without it one malformed tag fails the entire batch, and a
        // real photo library is full of malformed tags.
        "-m",
    ];

    private readonly Process _process;
    private readonly Channel<string> _stdout = Channel.CreateUnbounded<string>();
    private readonly Channel<string> _stderr = Channel.CreateUnbounded<string>();
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private readonly ILogger _logger;

    private int _commandNumber;
    private bool _disposed;

    private ExifToolSession(Process process, ILogger logger)
    {
        this._process = process;
        this._logger = logger;

        _ = Task.Run(() => PumpAsync(process.StandardOutput, this._stdout));
        _ = Task.Run(() => PumpAsync(process.StandardError, this._stderr));
    }

    /// <summary>Starts the process. The caller has already validated the executable.</summary>
    public static ExifToolSession Start(string executablePath, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(executablePath);

        var info = new ProcessStartInfo(executablePath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,

            // No BOM on any stream. A BOM on stdin corrupts the first argument of the
            // first command, which then fails in a way that looks like a bad file.
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };

        info.ArgumentList.Add("-stay_open");
        info.ArgumentList.Add("True");
        info.ArgumentList.Add("-@");
        info.ArgumentList.Add("-");

        var process = new Process { StartInfo = info };
        _ = process.Start();

        return new ExifToolSession(process, logger ?? NullLogger.Instance);
    }

    /// <summary>
    /// Runs one command and waits for its sentinel.
    /// </summary>
    /// <param name="arguments">
    /// Arguments WITHOUT the common ones and without -execute, which are added here. Each
    /// goes on its own line: the argfile format is strictly one argument per line, and a
    /// space inside a filename would otherwise split it in two.
    /// </param>
    /// <param name="timeout">Per-command ceiling. A video rewrite legitimately takes minutes.</param>
    /// <param name="cancellationToken">Cancels the wait, not the process.</param>
    public async Task<ExifToolResult> ExecuteAsync(
        IReadOnlyList<string> arguments,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ObjectDisposedException.ThrowIf(this._disposed, this);

        await this._oneAtATime.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            int number = ++this._commandNumber;
            string readySentinel = string.Create(CultureInfo.InvariantCulture, $"{{ready{number}}}");
            string statusPrefix = string.Create(CultureInfo.InvariantCulture, $"##cn{number}:");

            var block = new StringBuilder();

            foreach (string argument in CommonArguments)
            {
                _ = block.Append(argument).Append('\n');
            }

            foreach (string argument in arguments)
            {
                // A newline inside an argument would be read as an argument boundary, so a
                // path containing one is rejected rather than silently mangled.
                if (argument.Contains('\n', StringComparison.Ordinal) || argument.Contains('\r', StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        $"An ExifTool argument cannot contain a line break: '{argument}'.", nameof(arguments));
                }

                _ = block.Append(argument).Append('\n');
            }

            // -echo4 writes to stderr after the command finishes, and ${status} carries
            // ExifTool's own result. That is the only way to tell a partially failed batch
            // from a successful one, because the process itself never exits.
            _ = block.Append("-echo4\n").Append(statusPrefix).Append("${status}\n");
            _ = block.Append("-execute").Append(number.ToString(CultureInfo.InvariantCulture)).Append('\n');

            await this._process.StandardInput.WriteAsync(block.ToString()).ConfigureAwait(false);
            await this._process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);

            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(timeout ?? TimeSpan.FromMinutes(5));

            string output = await ReadUntilAsync(this._stdout, line => line.StartsWith(readySentinel, StringComparison.Ordinal), limit.Token)
                .ConfigureAwait(false);

            (string errors, int status) = await ReadStatusAsync(this._stderr, statusPrefix, limit.Token).ConfigureAwait(false);

            return new ExifToolResult(output, errors, status);
        }
        finally
        {
            _ = this._oneAtATime.Release();
        }
    }

    /// <summary>
    /// Reads lines through a stateful decoder rather than raw bytes, so a UTF-8 sequence
    /// split across two pipe reads is reassembled instead of corrupted.
    /// </summary>
    private static async Task PumpAsync(StreamReader reader, Channel<string> channel)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                await channel.Writer.WriteAsync(line).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The process went away; the waiting command will time out and report it.
        }
        finally
        {
            channel.Writer.TryComplete();
        }
    }

    private static async Task<string> ReadUntilAsync(Channel<string> channel, Func<string, bool> isSentinel, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();

        while (true)
        {
            string line = await channel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);

            if (isSentinel(line))
            {
                return text.ToString();
            }

            _ = text.Append(line).Append('\n');
        }
    }

    private static async Task<(string Errors, int Status)> ReadStatusAsync(Channel<string> channel, string prefix, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();

        while (true)
        {
            string line = await channel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);

            if (line.StartsWith(prefix, StringComparison.Ordinal))
            {
                string value = line[prefix.Length..].Trim();
                return (text.ToString(), int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int status) ? status : 0);
            }

            _ = text.Append(line).Append('\n');
        }
    }

    /// <summary>
    /// Asks the process to stop, and kills it if it will not. The protocol's own shutdown
    /// is two lines on stdin; a process that ignores them is already wedged.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (this._disposed)
        {
            return;
        }

        this._disposed = true;

        try
        {
            if (!this._process.HasExited)
            {
                await this._process.StandardInput.WriteAsync("-stay_open\nFalse\n").ConfigureAwait(false);
                await this._process.StandardInput.FlushAsync().ConfigureAwait(false);

                using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await this._process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
        {
            this._logger.LogDebug(ex, "ExifTool did not shut down cleanly; killing it.");

            try
            {
                this._process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }
        }
        finally
        {
            this._oneAtATime.Dispose();
            this._process.Dispose();
        }
    }
}
