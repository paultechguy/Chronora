// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PaulTechGuy.CN.Abstractions;

namespace PaulTechGuy.CN.Repositories;

/// <summary>
/// What Chronora remembers between sessions.
///
/// Two things are deliberately NOT here.
///
/// The chosen date is the first. Restoring it would mean the app opens with a plan already
/// loaded, and the moment files are added it proposes writing that date over all of them
/// before anybody has decided anything. An app that edits irreplaceable files must arrive
/// with nothing to apply. The SHAPE of the last run - which fields, which kind of source,
/// how things were sorted - is safe to restore precisely because on its own it does
/// nothing.
///
/// The file list is the second. Reopening yesterday's files would put a destructive button
/// in front of somebody who has not looked at what it covers.
///
/// Settable properties rather than init-only, and that is not a style choice: the
/// System.Text.Json source generator treats init-only members as constructor parameters
/// and silently writes the type default over any property missing from the JSON, which
/// turns "an older file lacks this field" into "this field is now false".
/// </summary>
public sealed record AppSettings
{
    public int SchemaVersion { get; set; } = SettingsStore.SchemaVersion;

    // ---- Window placement ---------------------------------------------------------

    /// <summary>Restored bounds, never the maximized ones. Null until a window has run.</summary>
    public int? WindowX { get; set; }

    public int? WindowY { get; set; }

    public int? WindowWidth { get; set; }

    public int? WindowHeight { get; set; }

    public bool WindowMaximized { get; set; }

    // ---- The shape of the last run -------------------------------------------------

    /// <summary>The name of a <c>WorkIntent</c>, or null when none was chosen.</summary>
    public string? Intent { get; set; }

    /// <summary>The name of a <c>SourceChoice</c>.</summary>
    public string? Source { get; set; }

    public bool WriteCreated { get; set; } = true;

    public bool WriteModified { get; set; } = true;

    public bool WriteChanged { get; set; }

    public bool WriteTaken { get; set; }

    /// <summary>The name of a <c>DateField</c> for the copy-from source.</summary>
    public string? CopyFromField { get; set; }

    public double ShiftHours { get; set; }

    /// <summary>The name of a <c>SortChoice</c>.</summary>
    public string? Sort { get; set; }

    public bool ShowOnlyChanging { get; set; }

    public bool ShowOnlyProblems { get; set; }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;

/// <summary>
/// Reads and writes <see cref="AppSettings" />, and never gets in the way of starting.
///
/// Every failure here resolves to "use the defaults". A missing file is the first run, an
/// unreadable one has been set aside by the store, and a file from a newer build opens
/// read-only so a rollback cannot quietly rewrite it in the old shape. None of those is
/// worth refusing to open the app over - the worst case is a window the wrong size.
/// </summary>
public sealed class SettingsStore
{
    /// <summary>The format this build writes.</summary>
    public const int SchemaVersion = 1;

    private readonly JsonFileStore<AppSettings> _file;
    private readonly ILogger _logger;

    public SettingsStore(IAppPaths paths, ILogger<SettingsStore>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(paths);

        this._logger = logger ?? NullLogger<SettingsStore>.Instance;
        this._file = new JsonFileStore<AppSettings>(
            paths.SettingsFilePath,
            SettingsJsonContext.Default.AppSettings,
            SchemaVersion,
            s => s.SchemaVersion,
            this._logger);

        this.Current = this._file.Read() ?? new AppSettings();
    }

    /// <summary>
    /// The settings in force. One instance, mutated in place and written on the way out,
    /// so nothing has to thread a settings object through the app to read one value.
    /// </summary>
    public AppSettings Current { get; }

    /// <summary>True when the file came from a newer build and must not be overwritten.</summary>
    public bool IsReadOnly => this._file.IsReadOnly;

    public string? ReadOnlyReason => this._file.ReadOnlyReason;

    /// <summary>
    /// Writes the current settings.
    ///
    /// Failure is logged and swallowed. This runs while the app is closing, and a dialog
    /// nobody can act on - about a window size, at the moment they have already decided to
    /// leave - would be worse than losing the setting.
    /// </summary>
    public bool Save()
    {
        this.Current.SchemaVersion = SchemaVersion;

        bool saved = this._file.Write(this.Current);

        if (!saved)
        {
            this._logger.LogWarning("Settings were not saved.");
        }

        return saved;
    }
}
