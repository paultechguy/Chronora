// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.UI.Xaml;
using PaulTechGuy.CN.App.Views;
using PaulTechGuy.CN.Journal;
using PaulTechGuy.CN.Repositories;
using Serilog;

namespace PaulTechGuy.CN.App;

/// <summary>
/// Owns the host lifetime and the main window. It deliberately does not own the container:
/// that is built in <see cref="Program" /> before any UI exists.
/// </summary>
public partial class App : Application
{
    private readonly IHost _host;

    public App(IHost host)
    {
        this._host = host;
        Services = host.Services;

        this.InitializeComponent();

        this.UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    /// <summary>
    /// XAML-constructed types cannot take constructor injection, so a small number of them
    /// reach the container through here. Everything else is injected.
    /// </summary>
    public static IServiceProvider Services { get; private set; } = null!;

    /// <summary>
    /// The main window, so an unpackaged file picker has something to parent to. An
    /// unpackaged app has no implicit window and the dialog simply never appears without it.
    /// </summary>
    public static Window MainWindowHandle { get; private set; } = null!;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // A run still marked Running did not finish. Resolving that here, before any UI
        // exists, means History never shows a run that is pretending to be in progress.
        int interrupted = this._host.Services.GetRequiredService<SqliteJournal>().RecoverInterruptedRuns();
        if (interrupted > 0)
        {
            Log.Warning("{Count} run(s) from a previous session did not finish.", interrupted);
        }

        SettingsStore settings = this._host.Services.GetRequiredService<SettingsStore>();

        MainWindow window = this._host.Services.GetRequiredService<MainWindow>();

        // After the window exists, because restoring the options runs a recompute and the
        // dispatcher it posts to belongs to the UI thread the window set up. Nothing is
        // loaded yet, so this recomputes an empty list - which is the point: the shape of
        // the last run comes back, a plan does not.
        window.Workbench.ApplySettings(settings.Current);

        if (settings.IsReadOnly)
        {
            Log.Warning("Settings are read-only and will not be saved: {Reason}", settings.ReadOnlyReason);
        }

        MainWindowHandle = window;
        window.Activate();

        // Revalidated at every launch rather than trusted. A copy the user manages can
        // have been upgraded, uninstalled or quarantined since the last session, and a
        // remembered path is a starting point, never a promise.
        _ = window.Workbench.RefreshEngineAsync();

        _ = LoadCommandLinePathsAsync(window);
    }

    /// <summary>
    /// Files and folders named on the command line are loaded at startup.
    ///
    /// This is what a Send To shortcut and an Explorer "open with" will both need, so it
    /// is a real feature rather than a test hook - but it is also the only way to get files
    /// into the app without a person driving it, which matters: a freeze that only appears
    /// once rows exist cannot otherwise be reproduced except by hand.
    /// </summary>
    private static async Task LoadCommandLinePathsAsync(MainWindow window)
    {
        string[] paths =
        [
            .. Environment.GetCommandLineArgs()
                .Skip(1)
                .Where(a => !a.StartsWith('-') && !a.StartsWith('/'))
                .Where(a => File.Exists(a) || Directory.Exists(a)),
        ];

        if (paths.Length == 0)
        {
            return;
        }

        Log.Information("Loading {Count} path(s) named on the command line.", paths.Length);

        await window.Workbench.AddDroppedAsync(paths);
    }

    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unhandled exception on the UI thread.");

        // Keep the window alive. Losing an unapplied plan to a rendering glitch would be a
        // worse outcome for the user than one broken interaction.
        e.Handled = true;
    }

    private static void OnDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs e) =>
        Log.Fatal(e.ExceptionObject as Exception, "Unhandled exception on a background thread.");

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unobserved task exception.");
        e.SetObserved();
    }
}
