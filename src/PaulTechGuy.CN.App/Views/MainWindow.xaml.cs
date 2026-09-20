// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PaulTechGuy.CN.Presentation;
using PaulTechGuy.CN.Domain;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage.Pickers;

namespace PaulTechGuy.CN.App.Views;

public sealed partial class MainWindow : Window
{
    // Freely resizable because the primary content is a file listing: more screen means
    // more rows, which is the biggest usability lever in a bulk tool. The minimum only
    // stops the three regions collapsing into nonsense.
    private const int MinimumWidth = 980;
    private const int MinimumHeight = 640;

    public MainWindow(MainViewModel viewModel, WorkbenchViewModel workbench)
    {
        this.ViewModel = viewModel;
        this.Workbench = workbench;

        this.InitializeComponent();

        this.Title = "Chronora";
        this.SystemBackdrop = new MicaBackdrop { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt };
        this.ExtendsContentIntoTitleBar = true;
        this.SetTitleBar(this.AppTitleBar);

        this.AppWindow.Resize(new SizeInt32(1360, 880));
        this.AppWindow.Changed += OnAppWindowChanged;

        // WinUI does not close a second window when the main one goes, and the process
        // stays alive while ANY window is open. Left alone, closing Chronora with History
        // open leaves an orphaned window and a running process behind - the app looks like
        // it did not shut down, because it did not.
        this.Closed += (_, _) =>
        {
            this._history?.Close();
            this._history = null;
        };
    }

    public MainViewModel ViewModel { get; }

    public WorkbenchViewModel Workbench { get; }

    /// <summary>WinUI has no MinWidth on a Window, so the clamp is applied on resize.</summary>
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

    private async void OnAddFolder(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");

        // An unpackaged app has no implicit window for a picker to parent to, so the
        // handle has to be supplied by hand or the dialog never appears at all.
        nint handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, handle);

        Windows.Storage.StorageFolder? folder = await picker.PickSingleFolderAsync();
        if (folder is null)
        {
            return;
        }

        await this.Workbench.AddFolderAsync(folder.Path, ScanFilter.Default);
    }

    /// <summary>
    /// Accepts folders and files, including a mixed selection. Windows hands a drop over
    /// as one list with no guarantee the items are all the same kind, so nothing here
    /// assumes they are.
    /// </summary>
    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "Add to the list";
        e.DragUIOverride.IsGlyphVisible = true;
        e.DragUIOverride.IsCaptionVisible = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        // Nothing to undo visually yet; the handler exists so the state stays symmetrical
        // when a drop overlay is added.
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        // The deferral matters: without it the data view is disposed the moment this
        // handler returns, and the await below would read from a closed package.
        DragOperationDeferral deferral = e.GetDeferral();

        try
        {
            IReadOnlyList<Windows.Storage.IStorageItem> items = await e.DataView.GetStorageItemsAsync();

            List<string> paths = [.. items
                .Select(i => i.Path)
                .Where(p => !string.IsNullOrWhiteSpace(p))];

            if (paths.Count > 0)
            {
                await this.Workbench.AddDroppedAsync(paths);
            }
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void OnDismissActionNotice(InfoBar sender, object args) => this.Workbench.DismissActionNotice();

    private void OnDismissNudge(InfoBar sender, object args) => this.Workbench.DismissNudge();

    /// <summary>
    /// Opens the consent pane. Only ever reached from this button, which appears only
    /// once the user has asked for something that needs ExifTool - so the question is
    /// never put to somebody who has not shown they want the answer.
    /// </summary>
    private async void OnSetUpExifTool(object sender, RoutedEventArgs e) =>
        _ = await ExifToolConsent.ShowAsync(this.Content.XamlRoot, this.Workbench);

    /// <summary>
    /// Opens History, or brings the open one forward.
    ///
    /// One window rather than one per click: a second copy of a list that offers to write
    /// to files is a way to undo the same run twice, and the second attempt would look
    /// like the app corrupting things rather than like a duplicate.
    /// </summary>
    private void OnOpenHistory(object sender, RoutedEventArgs e)
    {
        if (this._history is not null)
        {
            this._history.Activate();
            return;
        }

        this._history = new HistoryWindow(this.Workbench);
        this._history.Closed += (_, _) => this._history = null;
        this._history.Activate();
    }

    private HistoryWindow? _history;

    private void OnTemplateChosen(object sender, SelectionChangedEventArgs e)
    {
        if (this.Workbench is not null && sender is ComboBox { SelectedItem: DateTemplate template })
        {
            this.Workbench.UseTemplate(template);
        }
    }

    /// <summary>
    /// Saves the current options under a name.
    ///
    /// Modal, because it is a decide-now question with a consequence, and because a
    /// non-modal name prompt is a thing people click away from and then cannot find.
    /// </summary>
    private async void OnSaveTemplate(object sender, RoutedEventArgs e)
    {
        if (this.Workbench is null)
        {
            return;
        }

        var name = new TextBox { PlaceholderText = "Name", Header = "Template name" };
        var description = new TextBox
        {
            PlaceholderText = "What is this for?",
            Header = "Description (optional)",
            AcceptsReturn = true,
            Height = 72,
            TextWrapping = TextWrapping.Wrap,
        };

        var error = new InfoBar { IsOpen = false, Severity = InfoBarSeverity.Error, IsClosable = false };

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(name);
        panel.Children.Add(description);
        panel.Children.Add(error);

        var dialog = new ContentDialog
        {
            XamlRoot = this.Content.XamlRoot,
            Title = "Save as template",
            Content = panel,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        // Held open on a bad name rather than closing and reporting the problem somewhere
        // else, because the fix belongs in the box the name was typed into.
        dialog.PrimaryButtonClick += (_, args) =>
        {
            string? problem = this.Workbench.SaveCurrentAsTemplate(name.Text, description.Text);

            if (problem is not null)
            {
                args.Cancel = true;
                error.Message = problem;
                error.IsOpen = true;
            }
        };

        _ = await dialog.ShowAsync();
    }

    /// <summary>
    /// Deleting is confirmed. It is irreversible, and a template someone built by hand is
    /// not something they can reasonably reconstruct from memory.
    /// </summary>
    private async void OnDeleteTemplate(object sender, RoutedEventArgs e)
    {
        if (this.Workbench?.ActiveTemplate is not { IsBuiltIn: false } template)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = this.Content.XamlRoot,
            Title = "Delete this template?",
            Content = $"“{template.Name}” will be removed. The files in your list are not affected.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Keep it",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            this.Workbench.DeleteActiveTemplate();
        }
    }

    private void OnSortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (this.Workbench is not null
            && sender is ComboBox { SelectedItem: ComboBoxItem { Tag: string tag } }
            && Enum.TryParse(tag, out SortChoice choice))
        {
            this.Workbench.Sort = choice;
        }
    }

    /// <summary>
    /// Apply, behind a confirmation.
    ///
    /// This is one of only two modal surfaces in the app. Marqora's house rule is that
    /// nothing is modal, and that rule came from a text editor where modality interrupts
    /// flow; here the user is about to rewrite dates on files they cannot easily replace,
    /// which is precisely a "decide now" moment. Inheriting the convention without
    /// re-deriving it would have been the wrong call.
    /// </summary>
    private async void OnApply(object sender, RoutedEventArgs e)
    {
        ChangeSummary summary = this.Workbench.Summary;

        if (summary.FilesToWrite == 0)
        {
            return;
        }

        var body = new StringBuilder();
        _ = body.AppendLine(CultureInfo.CurrentCulture, $"{summary.FilesToWrite:N0} files will be changed.");

        foreach (SummaryLine line in summary.Lines)
        {
            _ = body.AppendLine(CultureInfo.CurrentCulture, $"  {line.FieldName}: {line.Detail}");
        }

        // Anything asked for that will NOT happen, stated here rather than left out. A
        // confirmation that lists only the good news is how someone applies 4,000 files and
        // discovers afterwards that the one field they actually wanted was never written.
        // The type filter narrows the RUN, so the confirmation has to name it. A filter
        // that quietly shrinks what a destructive button does is the surprise this whole
        // dialog exists to prevent.
        if (summary.HasTypeFilter)
        {
            _ = body.AppendLine();
            _ = body.AppendLine(
                CultureInfo.CurrentCulture,
                $"Only files matching {summary.TypeFilter} are included.");

            if (summary.FilesHiddenByTypeFilter > 0)
            {
                _ = body.AppendLine(
                    CultureInfo.CurrentCulture,
                    $"{summary.FilesHiddenByTypeFilter:N0} other file(s) in the list are NOT being changed.");
            }
        }

        if (summary.HasBlocked || summary.HasUntouched)
        {
            _ = body.AppendLine();
            _ = body.AppendLine("Will NOT be changed:");

            foreach (BlockedLine line in summary.BlockedLines)
            {
                _ = body.AppendLine(CultureInfo.CurrentCulture, $"  {line.FieldName}: {line.Detail}");
            }

            // File dates nobody asked for, named alongside the ones that are blocked.
            // Explorer shows Created, Modified and Accessed together, so a run that moves
            // two of them leaves the third sitting there looking untouched - and without
            // this, nothing anywhere says that was the intention.
            foreach (DateField field in summary.UntouchedFileDates)
            {
                _ = body.AppendLine(
                    CultureInfo.CurrentCulture,
                    $"  {DateFieldCatalog.Get(field).DisplayName}: not selected");
            }
        }

        if (summary.FilesSuspicious > 0)
        {
            _ = body.AppendLine();
            _ = body.AppendLine(CultureInfo.CurrentCulture,
                $"⚠ {summary.FilesSuspicious:N0} results look wrong. Sort by biggest change to see them first.");
        }

        _ = body.AppendLine();
        _ = body.Append("This can be undone afterwards.");

        var dialog = new ContentDialog
        {
            XamlRoot = this.Content.XamlRoot,
            Title = "Apply these changes?",
            Content = body.ToString(),
            PrimaryButtonText = "Apply",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await this.Workbench.ApplyAsync();
        }
    }

    /// <summary>
    /// Answers "how do I check 5,000 rows" by handing them to a spreadsheet, and doubles as
    /// a record of what a run was about to do. It replaced a Dry run button, which sitting
    /// beside a live preview only suggests the preview might not be real.
    /// </summary>
    private async void OnExportPreview(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker();
        picker.FileTypeChoices.Add("CSV", [".csv"]);
        picker.SuggestedFileName = "chronora-preview";

        nint handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, handle);

        Windows.Storage.StorageFile? file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return;
        }

        var csv = new StringBuilder();
        _ = csv.AppendLine("Path,Field,Before,After,Status,Problem");

        foreach (PlanRowViewModel row in this.Workbench.Rows)
        {
            if (row.Plan is null)
            {
                continue;
            }

            foreach (PlannedChange change in row.Plan.Changes)
            {
                _ = csv.Append(Quote(row.FullPath)).Append(',')
                       .Append(Quote(change.Target.DisplayName)).Append(',')
                       .Append(Quote(Stamp(change.BeforeDate))).Append(',')
                       .Append(Quote(Stamp(change.AfterDate))).Append(',')
                       .Append(Quote(change.Status.ToString())).Append(',')
                       .Append(Quote(PlanRowViewModel.Describe(change.Problem)))
                       .AppendLine();
            }
        }

        await Windows.Storage.FileIO.WriteTextAsync(file, csv.ToString());
    }

    private static string Stamp(DateTimeOffset? value) =>
        value is { } v ? v.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : string.Empty;

    private static string Quote(string value) =>
        "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
