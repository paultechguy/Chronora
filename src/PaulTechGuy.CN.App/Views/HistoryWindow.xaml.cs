// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
        this.AppWindow.Resize(new SizeInt32(760, 620));

        // Re-read on open rather than trusting whatever the main window last loaded. A run
        // may have finished since, and a stale History is one someone would act on.
        workbench.RefreshHistory();
    }

    public WorkbenchViewModel Workbench { get; }

    private void OnRefresh(object sender, RoutedEventArgs e) => this.Workbench.RefreshHistory();

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
