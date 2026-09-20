// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Media;
using PaulTechGuy.CN.Presentation;
using Windows.Graphics;

namespace PaulTechGuy.CN.App.Views;

/// <summary>
/// What Chronora has changed, and the way back.
///
/// Non-modal, following the rule the rest of the app uses: only the two genuinely
/// "decide now" surfaces - the ExifTool consent and the Apply confirmation - are modal.
/// History is something people want open BESIDE the main window while they compare what a
/// run did against what the files look like now.
///
/// The undo itself is confirmed, because it writes to files.
/// </summary>
public sealed partial class HistoryWindow : Window
{
    public HistoryWindow(WorkbenchViewModel workbench)
    {
        this.Workbench = workbench;

        this.InitializeComponent();

        this.Title = "Chronora — History";

        // Everything the main window does, not just the backdrop. Setting SystemBackdrop
        // alone was not enough and this window still came out flat black: a standard title
        // bar composites Mica differently from an extended one, so the two windows only
        // match once the title bar treatment matches too.
        this.SystemBackdrop = new MicaBackdrop { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt };
        this.ExtendsContentIntoTitleBar = true;
        this.SetTitleBar(this.AppTitleBar);

        this.AppWindow.Resize(new SizeInt32(760, 620));
        this.AppWindow.Changed += OnAppWindowChanged;

        // Re-read on open rather than trusting whatever the main window last loaded. A run
        // may have finished since, and a stale History is one someone would act on.
        workbench.RefreshHistory();
    }

    public WorkbenchViewModel Workbench { get; }

    // Enough to keep the list and the buttons from collapsing into each other. Same
    // approach as the main window, since WinUI has no MinWidth on a Window.
    private const int MinimumWidth = 520;
    private const int MinimumHeight = 360;

    private static void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidSizeChange)
        {
            return;
        }

        int width = Math.Max(sender.Size.Width, MinimumWidth);
        int height = Math.Max(sender.Size.Height, MinimumHeight);

        if (width != sender.Size.Width || height != sender.Size.Height)
        {
            sender.Resize(new SizeInt32(width, height));
        }
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => this.Workbench.RefreshHistory();

    /// <summary>
    /// Deletes every run, behind a typed confirmation.
    ///
    /// Typed rather than clicked because of what it costs. The files on disk are untouched
    /// - what goes is the record of what they used to look like, so everything the app has
    /// ever done stops being undoable at once, and nothing can rebuild that. A dialog you
    /// can dismiss with the space bar is the wrong shape for it.
    ///
    /// The Delete button stays disabled until the word matches exactly, so the dialog
    /// cannot be completed by reflex.
    /// </summary>
    private async void OnClearHistory(object sender, RoutedEventArgs e)
    {
        var typed = new TextBox
        {
            PlaceholderText = WorkbenchViewModel.ClearHistoryConfirmation,
            Header = $"Type {WorkbenchViewModel.ClearHistoryConfirmation} to confirm",
        };

        var panel = new StackPanel { Spacing = 12 };

        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = "This deletes every run Chronora has recorded, including pinned ones.\n\n"
                + "Your files are not touched. What goes is the record of what their dates used to be, "
                + "so nothing Chronora has already done can be undone afterwards. This cannot be reversed.",
        });

        panel.Children.Add(typed);

        var dialog = new ContentDialog
        {
            XamlRoot = this.Content.XamlRoot,
            Title = "Clear all history?",
            Content = panel,
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            IsPrimaryButtonEnabled = false,
        };

        typed.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled =
            string.Equals(typed.Text, WorkbenchViewModel.ClearHistoryConfirmation, StringComparison.Ordinal);

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            _ = this.Workbench.ClearHistory(typed.Text);
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => this.Close();

    /// <summary>
    /// Puts one run back, after asking.
    ///
    /// Drift is handled by the apply path rather than here: anything that has changed
    /// since the run is skipped and reported, never silently overwritten. Stomping a value
    /// somebody deliberately set last Tuesday is the one unforgivable bug in an undo
    /// feature, so the confirmation says that is what will happen.
    /// </summary>
    private async void OnUndoRun(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: long runId })
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = this.Content.XamlRoot,
            Title = "Undo this run?",
            Content = "Chronora will put the dates back to what they were before this run.\n\n"
                + "Any file that has changed since will be left alone rather than overwritten, "
                + "and it will tell you how many.",
            PrimaryButtonText = "Undo it",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await this.Workbench.RevertAsync(runId, force: false);
        this.Workbench.RefreshHistory();
    }
}
