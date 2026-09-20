// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PaulTechGuy.CN.Repositories;

/// <summary>
/// Anything the app keeps in a JSON file, read and written the same careful way.
///
/// Three rules, and all three exist because the alternative is the user losing work they
/// cannot get back.
///
/// Writes are atomic: a neighbouring temp file, then a replace. A crash or a full disk
/// halfway through a direct write leaves a truncated file, which on the next start reads
/// as "you have no templates".
///
/// A file that will not parse is moved aside rather than deleted or overwritten. It might
/// be recoverable by hand, and it is certainly not the app's to throw away.
///
/// A file written by a NEWER version opens read-only. That is the one that bites during a
/// rollback: an older build that cheerfully rewrites a newer file in the old shape has
/// silently destroyed whatever the new fields held.
/// </summary>
/// <typeparam name="T">The document type. Its own record carries the schema version.</typeparam>
public sealed class JsonFileStore<T>(
    string path,
    JsonTypeInfo<T> typeInfo,
    int currentSchemaVersion,
    Func<T, int> versionOf,
    ILogger? logger = null)
    where T : class
{
    private readonly string _path = path;
    private readonly JsonTypeInfo<T> _typeInfo = typeInfo;
    private readonly int _currentSchemaVersion = currentSchemaVersion;
    private readonly Func<T, int> _versionOf = versionOf;
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    /// <summary>
    /// True when the file on disk came from a later version of Chronora. The caller must
    /// not save over it.
    /// </summary>
    public bool IsReadOnly { get; private set; }

    /// <summary>Why it is read-only, ready to show someone.</summary>
    public string? ReadOnlyReason { get; private set; }

    /// <summary>
    /// Reads the file, or returns null when there is nothing usable there.
    ///
    /// Null means "start fresh", which is always a valid state: no file yet, an unreadable
    /// one that has been set aside, or a permissions problem. None of them is worth
    /// refusing to start over.
    /// </summary>
    public T? Read()
    {
        this.IsReadOnly = false;
        this.ReadOnlyReason = null;

        try
        {
            if (!File.Exists(this._path))
            {
                return null;
            }

            using FileStream stream = File.OpenRead(this._path);
            T? document = JsonSerializer.Deserialize(stream, this._typeInfo);

            if (document is null)
            {
                return null;
            }

            int version = this._versionOf(document);

            if (version > this._currentSchemaVersion)
            {
                this.IsReadOnly = true;
                this.ReadOnlyReason =
                    $"{Path.GetFileName(this._path)} was written by a newer version of Chronora "
                    + $"(format {version}, this build understands {this._currentSchemaVersion}). "
                    + "It is being used as-is and will not be overwritten.";

                this._logger.LogWarning("{Reason}", this.ReadOnlyReason);
            }

            return document;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            this._logger.LogWarning(ex, "Could not read {Path}; setting it aside and starting fresh.", this._path);
            this.SetAside();
            return null;
        }
    }

    /// <summary>
    /// Writes the file, unless the copy on disk came from a newer build.
    /// </summary>
    /// <returns>True when it was written.</returns>
    public bool Write(T document)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (this.IsReadOnly)
        {
            this._logger.LogWarning("Not saving {Path}: {Reason}", this._path, this.ReadOnlyReason);
            return false;
        }

        try
        {
            string? folder = Path.GetDirectoryName(this._path);

            if (folder is not null)
            {
                _ = Directory.CreateDirectory(folder);
            }

            // Same directory as the target, so the replace is a rename within one volume
            // rather than a copy that can half-finish.
            string temp = this._path + ".tmp";

            using (FileStream stream = File.Create(temp))
            {
                JsonSerializer.Serialize(stream, document, this._typeInfo);
            }

            if (File.Exists(this._path))
            {
                File.Replace(temp, this._path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temp, this._path);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            this._logger.LogError(ex, "Could not save {Path}.", this._path);
            return false;
        }
    }

    /// <summary>
    /// Moves a damaged file out of the way, keeping it under a dated name.
    ///
    /// Deleting would be easier and is the wrong call: the file is the user's, it may hold
    /// templates they spent time on, and a corrupt JSON file is often one bad character
    /// away from being fine.
    /// </summary>
    private void SetAside()
    {
        try
        {
            if (!File.Exists(this._path))
            {
                return;
            }

            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            string quarantine = $"{this._path}.unreadable-{stamp}";

            File.Move(this._path, quarantine, overwrite: true);
            this._logger.LogWarning("Moved the unreadable file to {Path}.", quarantine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            this._logger.LogWarning(ex, "Could not move the unreadable file aside.");
        }
    }
}
