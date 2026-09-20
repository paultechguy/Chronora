// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using PaulTechGuy.CN.Abstractions;

namespace PaulTechGuy.CN.Repositories;

/// <inheritdoc cref="IAppPaths" />
public sealed class AppPaths : IAppPaths
{
    private const string CompanyFolder = "PaulTechGuy";
    private const string ProductFolder = "Chronora";

    public AppPaths()
        : this(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
    {
    }

    /// <summary>Test seam: point the data root at a temporary directory.</summary>
    public AppPaths(string localAppDataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localAppDataRoot);

        this.DataDirectory = Path.Combine(localAppDataRoot, CompanyFolder, ProductFolder);
    }

    public string DataDirectory { get; }

    public string SettingsFilePath => Path.Combine(this.DataDirectory, "settings.json");

    public string TemplatesFilePath => Path.Combine(this.DataDirectory, "templates.json");

    public string JournalDatabasePath => Path.Combine(this.DataDirectory, "journal.db");

    public string LogDirectory => Path.Combine(this.DataDirectory, "logs");

    public string BackupDirectory => Path.Combine(this.DataDirectory, "backups");

    public string ExifToolDirectory => Path.Combine(this.DataDirectory, "exiftool");

    public void EnsureCreated()
    {
        _ = Directory.CreateDirectory(this.DataDirectory);
        _ = Directory.CreateDirectory(this.LogDirectory);
        _ = Directory.CreateDirectory(this.BackupDirectory);
    }
}
