// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

namespace PaulTechGuy.CN.Abstractions;

/// <summary>
/// Where Chronora keeps everything the user owns.
///
/// All of it lives OUTSIDE the install directory, which is what makes an upgrade safe: the
/// installer replaces the app folder and touches nothing here, so templates, undo history and
/// the ExifTool consent all survive a new version.
/// </summary>
public interface IAppPaths
{
    string DataDirectory { get; }

    string SettingsFilePath { get; }

    string TemplatesFilePath { get; }

    /// <summary>The undo journal. Losing this silently would be worse than never having it.</summary>
    string JournalDatabasePath { get; }

    string LogDirectory { get; }

    /// <summary>Backups taken before a schema migration, kept per app version.</summary>
    string BackupDirectory { get; }

    /// <summary>
    /// Where a user-approved ExifTool is installed. Under LOCALAPPDATA rather than the app
    /// folder so the consent and the binary both survive an upgrade.
    /// </summary>
    string ExifToolDirectory { get; }

    void EnsureCreated();
}
