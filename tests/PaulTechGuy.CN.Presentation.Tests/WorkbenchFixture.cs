// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using System.ComponentModel;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using PaulTechGuy.CN.Abstractions;
using PaulTechGuy.CN.FileSystem;
using PaulTechGuy.CN.Journal;
using PaulTechGuy.CN.Metadata;
using PaulTechGuy.CN.Rules;
using PaulTechGuy.CN.Services;

namespace PaulTechGuy.CN.Presentation.Tests;

/// <summary>Points the app's data directory at a temporary folder.</summary>
internal sealed class TempPaths(string root) : IAppPaths
{
    public string DataDirectory { get; } = root;

    public string SettingsFilePath => Path.Combine(this.DataDirectory, "settings.json");

    public string TemplatesFilePath => Path.Combine(this.DataDirectory, "templates.json");

    public string JournalDatabasePath => Path.Combine(this.DataDirectory, "journal.db");

    public string LogDirectory => Path.Combine(this.DataDirectory, "logs");

    public string BackupDirectory => Path.Combine(this.DataDirectory, "backups");

    public string ExifToolDirectory => Path.Combine(this.DataDirectory, "exiftool");

    public void EnsureCreated() => Directory.CreateDirectory(this.DataDirectory);
}

/// <summary>
/// A real WorkbenchViewModel against temporary folders.
///
/// Everything is the genuine article rather than a mock: none of these constructors touch
/// the network or the user's disk on their own, and a view model wired to fakes would not
/// have caught any of the bugs this suite exists for - they were all in how the real
/// pieces notify each other.
/// </summary>
internal sealed class WorkbenchFixture : IDisposable
{
    private readonly string _root;

    public WorkbenchFixture()
    {
        this._root = Path.Combine(Path.GetTempPath(), "chronora-vm-tests", Guid.NewGuid().ToString("N"));
        this.Files = Path.Combine(this._root, "files");

        _ = Directory.CreateDirectory(this.Files);

        var paths = new TempPaths(Path.Combine(this._root, "data"));
        paths.EnsureCreated();

        this.Journal = SqliteJournal.Open(paths.JournalDatabasePath);

        var writer = new FileTimeWriter();
        var volumes = new VolumeProbe();
        var http = new HttpClient();

        var exifTool = new ExifToolService(
            new ExifToolLocator(),
            new ExifToolValidator(),
            new ExifToolManifestSource(http) { LocalDirectory = paths.DataDirectory },
            new ExifToolInstaller(http),
            NullLogger<ExifToolService>.Instance);

        // No consent has been recorded in this temp folder, so the engine reports
        // unavailable and the gateway never starts a process. That is the state most of
        // these tests are about: everything still has to work with no ExifTool.
        this.Metadata = new MetadataGateway(
            exifTool, new MetadataReader(), new MetadataWriter(), NullLogger<MetadataGateway>.Instance);

        this.ViewModel = new WorkbenchViewModel(
            new FileScanner(writer, volumes),
            new RuleEvaluator(),
            new ApplyService(writer, volumes, this.Journal, NullLogger<ApplyService>.Instance, this.Metadata),
            this.Journal,
            exifTool,
            this.Metadata,
            paths,
            new ImmediateDispatcher(),
            NullLogger<WorkbenchViewModel>.Instance);
    }

    public string Files { get; }

    public SqliteJournal Journal { get; }

    public MetadataGateway Metadata { get; }

    public WorkbenchViewModel ViewModel { get; }

    public string CreateFile(string name)
    {
        string path = Path.Combine(this.Files, name);
        File.WriteAllText(path, "x");
        return path;
    }

    public Task LoadAsync(params string[] names)
    {
        foreach (string name in names)
        {
            _ = this.CreateFile(name);
        }

        return this.ViewModel.AddFolderAsync(this.Files, Domain.ScanFilter.Default);
    }

    public void Dispose()
    {
        this.ViewModel.Dispose();
        this.Metadata.DisposeAsync().AsTask().GetAwaiter().GetResult();
        this.Journal.Dispose();
        SqliteConnection.ClearAllPools();

        try
        {
            if (Directory.Exists(this._root))
            {
                Directory.Delete(this._root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp folder is not worth failing a test over.
        }
    }
}

/// <summary>
/// Watches a view model and answers the only question that matters for this bug class:
/// did every property whose VALUE changed also get announced?
/// </summary>
internal sealed class NotificationWatcher : IDisposable
{
    private static readonly string[] Ignored = ["Rows", "Summary", "History"];

    private readonly INotifyPropertyChanged _source;
    private readonly HashSet<string> _raised = new(StringComparer.Ordinal);
    private readonly Dictionary<string, object?> _before;

    public NotificationWatcher(INotifyPropertyChanged source)
    {
        this._source = source;
        this._before = Snapshot(source);
        source.PropertyChanged += this.OnPropertyChanged;
    }

    /// <summary>
    /// Properties whose value moved but which nobody announced. A binding to any of these
    /// is showing a stale value right now, and nothing about it will look broken in code.
    /// </summary>
    public IReadOnlyList<string> SilentChanges()
    {
        Dictionary<string, object?> after = Snapshot(this._source);

        return [.. after
            .Where(kv => !Equals(this._before[kv.Key], kv.Value))
            .Where(kv => !this._raised.Contains(kv.Key))
            .Select(kv => kv.Key)
            .OrderBy(name => name, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Every public, parameterless, readable property. Get-only computed properties are
    /// the point: those are the ones with no setter to hang a notification off, which is
    /// exactly why they get forgotten.
    /// </summary>
    private static Dictionary<string, object?> Snapshot(object source)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (PropertyInfo property in source.GetType()
                     .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
                     .Where(p => !Ignored.Contains(p.Name, StringComparer.Ordinal)))
        {
            try
            {
                values[property.Name] = property.GetValue(source);
            }
            catch (TargetInvocationException)
            {
                // A property that throws in some state is not this test's business.
            }
        }

        return values;
    }

    private void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is { } name)
        {
            _ = this._raised.Add(name);
        }
    }

    public void Dispose() => this._source.PropertyChanged -= this.OnPropertyChanged;
}
