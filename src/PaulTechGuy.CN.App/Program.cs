// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using PaulTechGuy.CN.Abstractions;
using PaulTechGuy.CN.Presentation;
using PaulTechGuy.CN.App.Views;
using PaulTechGuy.CN.Services;
using PaulTechGuy.CN.FileSystem;
using PaulTechGuy.CN.Journal;
using PaulTechGuy.CN.Metadata;
using PaulTechGuy.CN.Repositories;
using PaulTechGuy.CN.Rules;
using Serilog;

namespace PaulTechGuy.CN.App;

/// <summary>
/// Composition root.
///
/// The XAML compiler would normally generate this entry point; DISABLE_XAML_GENERATED_MAIN
/// hands it over so logging and the DI container are both live before any UI is created,
/// which means a failure during startup lands in the log rather than vanishing.
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        IHost? host = null;

        var paths = new AppPaths();
        paths.EnsureCreated();

        Log.Logger = CreateBootstrapLogger(paths);

        try
        {
            Log.Information("Chronora starting. Data directory: {DataDirectory}", paths.DataDirectory);

            // SQLitePCLRaw's provider is normally wired by a module initializer. Calling it
            // here instead turns a missing e_sqlite3.dll into a clear DllNotFoundException at
            // startup, rather than a confusing TypeInitializationException the first time the
            // user opens History. New-Release.ps1 also asserts the dll reached the publish.
            SQLitePCL.Batteries_V2.Init();

            // WinRT projections must be live before any Windows App SDK type is touched.
            WinRT.ComWrappersSupport.InitializeComWrappers();

            host = BuildHost(paths, args);

            Microsoft.UI.Xaml.Application.Start(_unusedInitParams =>
            {
                // WinUI runs on a DispatcherQueue rather than a classic message pump, so the
                // synchronization context has to be installed by hand for await to resume on
                // the UI thread.
                var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(context);

                _ = new App(host);
            });
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Chronora terminated unexpectedly during startup.");
            throw;
        }
        finally
        {
            ShutDown(host);

            Log.Information("Chronora exiting.");
            Log.CloseAndFlush();
        }
    }

    /// <summary>
    /// Disposes the host, which is the only thing that shuts ExifTool down.
    ///
    /// Nothing did this. The host was built, handed to App, and then the process simply
    /// ended - so the MetadataGateway singleton, which is IAsyncDisposable precisely so it
    /// can stop its ExifTool, was never disposed. One -stay_open child was left running per
    /// session: ten were found alive on one machine over two days, each holding the ExifTool
    /// folder locked, which breaks Repair and would make an uninstall unrecoverable.
    ///
    /// It also closes the journal, and that matters more than it looks: SQLite in WAL mode
    /// only checkpoints into the .db on a clean close. Without this, journal.db stays a 4 KB
    /// stub beside a 1 MB -wal, and anybody told to "back up journal.db" loses their history.
    ///
    /// On the thread pool deliberately. Application.Start leaves a
    /// DispatcherQueueSynchronizationContext installed on this thread, and its queue is gone
    /// by the time we get here, so blocking on a continuation that wants to post back to it
    /// would deadlock on the way out - a hang at exit, which looks exactly like the leak
    /// this is here to fix.
    /// </summary>
    private static void ShutDown(IHost? host)
    {
        if (host is null)
        {
            return;
        }

        try
        {
            // IHost is only IDisposable; the concrete host is also IAsyncDisposable, and the
            // async path is the one that matters. A SYNCHRONOUS provider dispose throws when
            // a singleton implements only IAsyncDisposable - which MetadataGateway does,
            // precisely so that it can stop ExifTool - so taking the sync route here would
            // fail to do the one thing this method exists for.
            bool finished = host is IAsyncDisposable disposable
                ? Task.Run(async () => await disposable.DisposeAsync().ConfigureAwait(false))
                    .Wait(TimeSpan.FromSeconds(15))
                : Task.Run(host.Dispose).Wait(TimeSpan.FromSeconds(15));

            if (!finished)
            {
                Log.Warning("Shutdown did not finish in time; ExifTool may not have been stopped.");
            }
        }
        catch (Exception ex) when (ex is AggregateException or ObjectDisposedException or InvalidOperationException)
        {
            // Never rethrow from here. The app is already leaving, and a failure to tidy up
            // must not turn a normal exit into a crash dialog.
            Log.Error(ex, "Something failed while shutting down.");
        }
    }

    private static IHost BuildHost(AppPaths paths, string[] args)
    {
        // appsettings.json ships next to the executable, which is not necessarily the
        // working directory the process was launched from.
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
            ApplicationName = "Chronora",
        });

        builder.Services.AddSerilog((_unusedProvider, configuration) =>
        {
            _ = configuration
                .ReadFrom.Configuration(builder.Configuration)
                .Enrich.FromLogContext()
                .WriteTo.Debug(formatProvider: CultureInfo.InvariantCulture)
                .WriteTo.Async(sink => sink.File(
                    Path.Combine(paths.LogDirectory, "chronora-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 7,
                    fileSizeLimitBytes: 16 * 1024 * 1024,
                    rollOnFileSizeLimit: true,
                    formatProvider: CultureInfo.InvariantCulture,
                    outputTemplate:
                        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}"));
        });

        // Each layer contributes its own registrations, so this stays a list of intents.
        builder.Services.AddSingleton<IAppPaths>(paths);


        // Captured on the UI thread, which is where BuildHost runs from inside

        // Application.Start. The view model asks for "the UI thread" and this supplies it.

        builder.Services.AddSingleton<IUiDispatcher>(

            _ => new UiDispatcher(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()));

        // One registration per layer, so the root reads as a list of intents.
        builder.Services.AddSingleton<VolumeProbe>();
        builder.Services.AddSingleton<FileTimeWriter>();
        builder.Services.AddSingleton<FileScanner>();
        builder.Services.AddSingleton<FilenameDateParser>();
        builder.Services.AddSingleton<RuleEvaluator>();

        // The journal is opened once and held: SQLite in WAL mode allows a single writer,
        // and a run that reopened it per batch would fight itself.

        builder.Services.AddSingleton(sp => SqliteJournal.Open(
            paths.JournalDatabasePath,
            sp.GetRequiredService<ILogger<SqliteJournal>>()));

        builder.Services.AddSingleton<ApplyService>();

        // ExifTool. Nothing here touches the network until the user asks for it: the
        // locator only reads the disk, and the manifest and installer are reached solely
        // from an explicit choice in the consent pane.
        builder.Services.AddSingleton(_ =>
        {
            var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            client.DefaultRequestHeaders.Add(
                "User-Agent",
                $"Chronora/{typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0"}");

            return client;
        });

        builder.Services.AddSingleton<ExifToolLocator>();
        builder.Services.AddSingleton<ExifToolValidator>();
        builder.Services.AddSingleton<ExifToolManifestSource>();
        builder.Services.AddSingleton<ExifToolInstaller>();
        builder.Services.AddSingleton<ExifToolService>();

        builder.Services.AddSingleton<MetadataReader>();
        builder.Services.AddSingleton<MetadataWriter>();

        // Owns the running ExifTool process. A singleton because starting one costs about
        // a second of Perl boot, so a session is kept alive across scans rather than
        // started per run.
        builder.Services.AddSingleton<MetadataGateway>();

        // The interface as well, forwarded to the SAME instance - and this line is load
        // bearing. ApplyService takes IMetadataWriteGateway as an OPTIONAL parameter so
        // that filesystem-only tests can leave it out, which means an unregistered
        // interface does not throw here: the container quietly hands it the default, null.
        // Without this registration every photo-date write failed with "ExifTool is not
        // available" while ExifTool was running and had already reported 109 writable
        // formats in the same session - and because a failed metadata write deliberately
        // abandons the file's timestamps too, Created and Modified silently went with it.
        //
        // Resolved through the concrete registration on purpose.
        // AddSingleton<IMetadataWriteGateway, MetadataGateway>() would compile, read the
        // same, and build a SECOND gateway with a second ExifTool process behind it.
        builder.Services.AddSingleton<IMetadataWriteGateway>(
            sp => sp.GetRequiredService<MetadataGateway>());

        builder.Services.AddSingleton<TemplateStore>();
        builder.Services.AddSingleton<SettingsStore>();
        builder.Services.AddSingleton<UpdateChecker>();

        builder.Services.AddSingleton<MainViewModel>();
        builder.Services.AddSingleton<WorkbenchViewModel>();
        builder.Services.AddSingleton<MainWindow>();

        // Transient: a closed WinUI Window cannot be reactivated, so a singleton would
        // open once and then silently do nothing on every later click.
        builder.Services.AddTransient<AboutWindow>();

        return builder.Build();
    }

    /// <summary>
    /// Covers process start through host construction, so a configuration error during
    /// startup is still recorded rather than lost.
    /// </summary>
    private static Serilog.Core.Logger CreateBootstrapLogger(AppPaths paths) =>
        new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Debug(formatProvider: CultureInfo.InvariantCulture)
            .WriteTo.File(
                Path.Combine(paths.LogDirectory, "chronora-.log"),
                rollingInterval: RollingInterval.Day,
                formatProvider: CultureInfo.InvariantCulture)
            .CreateLogger();
}
