// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.UI.Dispatching;
using PaulTechGuy.CN.Abstractions;
using PaulTechGuy.CN.App.ViewModels;
using PaulTechGuy.CN.App.Views;
using PaulTechGuy.CN.Repositories;
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

            IHost host = BuildHost(paths, args);

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
            Log.Information("Chronora exiting.");
            Log.CloseAndFlush();
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

        builder.Services.AddSingleton<MainViewModel>();
        builder.Services.AddSingleton<MainWindow>();

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
