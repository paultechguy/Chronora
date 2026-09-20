// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PaulTechGuy.CN.Abstractions;
using PaulTechGuy.CN.Services;
using Windows.Graphics;

namespace PaulTechGuy.CN.App.Views;

/// <summary>
/// What this is, what version, where it keeps things, and whether there is a newer one.
///
/// The folder buttons are the part that earns its keep. Everything the user owns lives
/// under %LOCALAPPDATA%, which is correct and completely unfindable - and the one time
/// somebody needs the log is the one time they are least inclined to go hunting for it.
/// </summary>
public sealed partial class AboutWindow : Window
{
    private readonly IAppPaths _paths;
    private readonly UpdateChecker _updates;

    public AboutWindow(IAppPaths paths, UpdateChecker updates)
    {
        this._paths = paths;
        this._updates = updates;

        this.InitializeComponent();

        this.Title = "Chronora — About";

        this.SystemBackdrop = new MicaBackdrop { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt };
        this.ExtendsContentIntoTitleBar = true;
        this.SetTitleBar(this.AppTitleBar);

        this.AppWindow.Resize(new SizeInt32(560, 700));

        this.VersionLine.Text = string.Create(CultureInfo.CurrentCulture, $"Version {CurrentVersion().ToString(3)}");
        this.DataFolderLine.Text = paths.DataDirectory;

        this.RefreshSendTo();

        this.ExifToolLine.Text =
            "Photo and video dates are read and written by ExifTool, by Phil Harvey, which "
            + "Chronora does not include and does not redistribute. With your permission it "
            + "is downloaded into Chronora's own folder, and you can remove it at any time.";
    }

    /// <summary>
    /// The three-part product version. Build and revision are not shown: they say nothing
    /// a person can act on and turn a version into something nobody can read back over the
    /// phone.
    /// </summary>
    private static Version CurrentVersion() =>
        typeof(AboutWindow).Assembly.GetName().Version ?? new Version(0, 0, 0);

    private async void OnCheckForUpdates(object sender, RoutedEventArgs e)
    {
        this.CheckButton.IsEnabled = false;
        this.Checking.IsActive = true;
        this.UpdateResult.IsOpen = false;
        this.DownloadLink.Visibility = Visibility.Collapsed;

        try
        {
            UpdateStatus status = await this._updates.CheckAsync(CurrentVersion());

            // Built before the switch: string.Create takes its interpolated argument by
            // ref, so trimming the result inline turns it back into a plain string and
            // the overload no longer binds.
            string available = string.Create(
                CultureInfo.CurrentCulture,
                $"Chronora {status.Latest?.ToString(3)} is available. {status.Notes}");

            (InfoBarSeverity severity, string message) = status.Outcome switch
            {
                UpdateOutcome.UpdateAvailable => (InfoBarSeverity.Success, available.TrimEnd()),

                UpdateOutcome.UpToDate => (InfoBarSeverity.Informational, "This is the newest version."),

                // Never "up to date". Saying that when nothing was read keeps somebody on
                // a version whose bug is already fixed, and tells them so confidently.
                _ => (InfoBarSeverity.Warning, status.Problem ?? "Chronora could not check."),
            };

            this.UpdateResult.Severity = severity;
            this.UpdateResult.Message = message;
            this.UpdateResult.IsOpen = true;

            if (status is { Outcome: UpdateOutcome.UpdateAvailable, Url: { } url })
            {
                this.DownloadLink.NavigateUri = new Uri(url);
                this.DownloadLink.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            // async void. Anything escaping here is rethrown on the UI thread during
            // layout, and a version check is not worth taking the app down for.
            Serilog.Log.Warning(ex, "The update check failed.");

            this.UpdateResult.Severity = InfoBarSeverity.Warning;
            this.UpdateResult.Message = "Chronora could not check for updates.";
            this.UpdateResult.IsOpen = true;
        }
        finally
        {
            this.Checking.IsActive = false;
            this.CheckButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Names what the click will do, not what is true now. The button is read as an
    /// action, so "Add to Send to" sitting on a machine that already has it reads as a
    /// statement and gets pressed by mistake.
    /// </summary>
    private void RefreshSendTo() =>
        this.SendToButton.Content = SendToShortcut.Exists
            ? "Remove from Send to"
            : "Add to Send to";

    private void OnToggleSendTo(object sender, RoutedEventArgs e)
    {
        bool worked = SendToShortcut.Exists ? SendToShortcut.Remove() : SendToShortcut.Create();

        this.RefreshSendTo();

        if (!worked)
        {
            this.UpdateResult.Severity = InfoBarSeverity.Warning;
            this.UpdateResult.Message = "Chronora could not change the Send to menu.";
            this.UpdateResult.IsOpen = true;
        }
    }

    private void OnOpenDataFolder(object sender, RoutedEventArgs e) => Reveal(this._paths.DataDirectory);

    private void OnOpenLogFolder(object sender, RoutedEventArgs e)
    {
        // Created on demand, because the folder may not exist yet on a first run and
        // Explorer's answer to a missing path is an error dialog with no next step.
        _ = Directory.CreateDirectory(this._paths.LogDirectory);

        Reveal(this._paths.LogDirectory);
    }

    private static void Reveal(string folder)
    {
        try
        {
            using var opening = Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Serilog.Log.Warning(ex, "Could not open {Folder}.", folder);
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => this.Close();
}
