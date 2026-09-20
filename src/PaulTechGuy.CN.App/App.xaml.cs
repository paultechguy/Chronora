// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.UI.Xaml;
using PaulTechGuy.CN.App.Views;
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

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWindow window = this._host.Services.GetRequiredService<MainWindow>();
        window.Activate();
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
