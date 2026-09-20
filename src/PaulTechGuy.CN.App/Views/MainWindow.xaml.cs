// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PaulTechGuy.CN.Presentation;
using PaulTechGuy.CN.Domain;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
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

        // handledEventsToo, which is the whole point. A ListViewItem marks tap events as
        // handled while doing its own selection, so a DoubleTapped hook declared on the
        // ListView in XAML never fires - which is why double-clicking a row did nothing
        // even after the DataContext bug was fixed. AddHandler with the flag set is the
        // only way to see an event a child has already claimed.
        this.FileList.AddHandler(
            UIElement.DoubleTappedEvent,
            new DoubleTappedEventHandler(this.OnRowDoubleTapped),
            handledEventsToo: true);

        // The same treatment for the row menu, and it needs it just as badly: a
        // ListViewItem claims the right-click on its way past.
        this.FileList.AddHandler(
            UIElement.RightTappedEvent,
            new RightTappedEventHandler(this.OnRowRightTapped),
            handledEventsToo: true);

        // Registered as well, not instead, and only for Shift+F10 and the menu key.
        // ContextRequested is the event the docs point you at for a context menu and it
        // did not fire once on a right-click here - the log from a whole session of them
        // has no handler entry and no exception. RightTapped above is what carries the
        // mouse; this stays for the keyboard, and the log says which one actually fires.
        this.FileList.AddHandler(
            UIElement.ContextRequestedEvent,
            new TypedEventHandler<UIElement, ContextRequestedEventArgs>(this.OnRowContextRequested),
            handledEventsToo: true);

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

        // The deferral covers reading the data view and NOTHING else.
        //
        // It has to exist: without it the package is disposed the moment this handler
        // returns and the read below fails. But a drop is an OLE transaction with a modal
        // message loop at both ends, and the deferral is what holds that transaction open.
        // Scanning the folder and reading every file's metadata inside it kept Explorer
        // and Chronora locked together for the whole job - which is what froze the app on
        // a drop of 24 files.
        List<string> paths;

        DragOperationDeferral deferral = e.GetDeferral();

        try
        {
            IReadOnlyList<Windows.Storage.IStorageItem> items = await e.DataView.GetStorageItemsAsync();

            paths = [.. items
                .Select(i => i.Path)
                .Where(p => !string.IsNullOrWhiteSpace(p))];
        }
        finally
        {
            // Released before any real work starts. The drag is over the instant its data
            // has been read; everything after this is Chronora's own business.
            deferral.Complete();
        }

        if (paths.Count > 0)
        {
            await this.Workbench.AddDroppedAsync(paths);
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


    /// <summary>
    /// Fills a row's thumbnail as its container is realised, in two phases.
    ///
    /// ContainerContentChanging rather than a binding, because the two tiers cannot be
    /// expressed as one value: the icon is available synchronously and belongs in the row
    /// during layout, and the real thumbnail arrives from a background queue afterwards.
    ///
    /// The measured reason for splitting them: a frame is 16 ms, a cached thumbnail costs
    /// 4-15 ms and an uncached one 13-203 ms. A screenful of twenty uncached rows measured
    /// 1.26 seconds, so fetching during layout would visibly freeze scrolling on exactly
    /// the folder this app is for - photos straight off a camera, which Explorer has never
    /// thumbnailed.
    /// </summary>
    private void OnRowRealised(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer.ContentTemplateRoot is not FrameworkElement root
            || root.FindName("RowThumbnail") is not Image image)
        {
            return;
        }

        // On its way to the recycle pool. Clearing matters: a container reused for another
        // file would otherwise show the previous one's picture until its own arrives.
        if (args.InRecycleQueue)
        {
            image.Source = null;
            image.Tag = null;
            return;
        }

        if (args.Item is not PlanRowViewModel row)
        {
            return;
        }

        if (args.Phase == 0)
        {
            // Which file this container is currently showing. Every later assignment checks
            // it, because a container is recycled while its thumbnail may still be in
            // flight - and a late result painted onto a reused row would be the control
            // lying about which file it is describing.
            image.Tag = row;

            // Lookups only. Nothing in phase 0 may wait for anything: this ran the icon
            // build here once and froze the app on the first row, because producing an
            // ImageSource ends in SetBitmapAsync and that completes ON the UI thread.
            bool haveThumbnail = ThumbnailProvider.TryGetThumbnail(row.File.FullPath, out ImageSource? ready);

            if (haveThumbnail)
            {
                image.Source = ready;
            }
            else
            {
                _ = ThumbnailProvider.TryGetIcon(row.File, out ImageSource? icon);
                image.Source = icon;

                args.RegisterUpdateCallback(OnRowRealised);
            }

            args.Handled = true;
            return;
        }

        args.Handled = true;
        UpgradeThumbnail(image, row);
    }

    /// <summary>
    /// Replaces the icon with the real thumbnail, if one turns up and the row still wants it.
    /// </summary>
    private static async void UpgradeThumbnail(Image image, PlanRowViewModel row)
    {
        try
        {
            // The icon first, so a row is never blank for longer than it takes to build
            // one icon per extension. Awaited rather than waited on - the difference
            // between this and the version that froze the app.
            if (!ThumbnailProvider.TryGetIcon(row.File, out ImageSource? _))
            {
                ImageSource? icon = await ThumbnailProvider.EnsureIconAsync(row.File, CancellationToken.None);

                if (icon is not null && ReferenceEquals(image.Tag, row) && image.Source is null)
                {
                    image.Source = icon;
                }
            }

            ImageSource? thumbnail = await ThumbnailProvider.LoadAsync(row.File, CancellationToken.None);

            // The container may have been recycled onto a different file while the Shell
            // was working. Assigning now would put this picture on that file's row.
            if (thumbnail is not null && ReferenceEquals(image.Tag, row))
            {
                image.Source = thumbnail;
            }
        }
        catch (OperationCanceledException)
        {
            // Scrolled away from. Normal, not a fault.
        }
        catch (Exception ex)
        {
            // Catch-all, and it has to be. This is async void, so anything escaping here
            // is rethrown on the UI thread during layout and kills the process outright -
            // which is how a thumbnail, the most cosmetic thing in the app, took Chronora
            // down. A row keeping its icon is not worth a crash.
            Serilog.Log.Warning(ex, "Could not load a thumbnail for {Path}.", row.File.FullPath);
        }
    }

    /// <summary>
    /// Which row a click landed on, found by walking up to the ListViewItem.
    ///
    /// Not DataContext, which is what this used and why double-clicking a row did nothing
    /// at all: a compiled x:Bind template does not set DataContext on the elements inside
    /// it, so the cast quietly failed and the handler returned. The container's Content is
    /// the item, always, whatever the template does.
    /// </summary>
    private PlanRowViewModel? FindRow(object? source) => this.RowOf(FindContainer(source));

    /// <summary>
    /// The item a container is showing, asked of the ListView rather than read off the
    /// container.
    ///
    /// ListViewItem.Content is empty here, and that is not a bug to fix: OnRowRealised
    /// sets args.Handled = true, which is the documented way to tell the framework you are
    /// filling the container yourself, and one of the things it then stops doing is
    /// setting Content. The null DataContext that broke double-click was the same
    /// behaviour seen from a different angle, and reading Content was the same mistake
    /// made twice.
    ///
    /// ItemFromContainer goes through the container-to-index map, which the list keeps
    /// either way.
    /// </summary>
    private PlanRowViewModel? RowOf(ListViewItem? container) =>
        container is null ? null : this.FileList.ItemFromContainer(container) as PlanRowViewModel;

    /// <summary>
    /// Every type between the clicked element and the visual root, for the log.
    ///
    /// Here because the walk below quietly fails and the reason is not guessable: the same
    /// gesture reports source=TextBlock whether the walk succeeds or not, so the type of
    /// the thing clicked tells you nothing. The chain does.
    /// </summary>
    private static string AncestorChain(object? source)
    {
        if (source is not DependencyObject node)
        {
            return source is null ? "null" : $"not-a-DependencyObject:{source.GetType().Name}";
        }

        var chain = new List<string>();

        // Bounded, because a runaway walk in a logging helper is not worth the risk.
        while (node is not null && chain.Count < 16)
        {
            chain.Add(node.GetType().Name);
            node = VisualTreeHelper.GetParent(node);
        }

        return string.Join(" < ", chain);
    }

    /// <summary>
    /// The container under a point, asked of the framework instead of walked to.
    ///
    /// The walk below is the obvious way and it does not work here - it reports no
    /// ListViewItem above a TextBlock that is plainly inside a row. This asks XAML's own
    /// hit-testing the same question and does not care what shape the tree is.
    /// </summary>
    private ListViewItem? HitTestContainer(Point hostPoint) =>
        VisualTreeHelper.FindElementsInHostCoordinates(hostPoint, this.FileList)
            .OfType<ListViewItem>()
            .FirstOrDefault();

    /// <summary>
    /// The container a click landed in, which is also the element a context menu should be
    /// positioned against.
    /// </summary>
    private static ListViewItem? FindContainer(object? source)
    {
        DependencyObject? node = source as DependencyObject;

        while (node is not null)
        {
            if (node is ListViewItem item)
            {
                return item;
            }

            node = VisualTreeHelper.GetParent(node);
        }

        return null;
    }

    /// <summary>
    /// The row the menu was opened on. Every item acts on this and nothing else.
    /// </summary>
    private PlanRowViewModel? _menuRow;

    /// <summary>
    /// The mouse path, and the one that actually works.
    ///
    /// ContextRequested was the obvious event for this and it never fired once - the log
    /// from a session full of right-clicks shows no handler entry and no exception. So the
    /// menu hangs off RightTapped instead, which is the mechanism already proven on this
    /// same list by the double-click fix.
    /// </summary>
    private void OnRowRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        // GetPosition(null) is relative to the window, which is what hit-testing wants.
        ListViewItem? container = FindContainer(e.OriginalSource)
            ?? this.HitTestContainer(e.GetPosition(null));

        if (this.ShowRowMenu("right-click", e.OriginalSource, container, c => e.GetPosition(c)))
        {
            e.Handled = true;
        }
    }

    /// <summary>
    /// The keyboard path - Shift+F10 and the menu key - kept separate because RightTapped
    /// cannot see them. Whether it ever fires is an open question; the log says which.
    /// </summary>
    private void OnRowContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        // No pointer to hit-test with, so the selected row is the right answer here - a
        // keyboard request IS a request about whatever is focused.
        ListViewItem? container = FindContainer(e.OriginalSource)
            ?? this.FileList.ContainerFromItem(this.FileList.SelectedItem) as ListViewItem;

        bool shown = this.ShowRowMenu(
            "keyboard",
            e.OriginalSource,
            container,
            c => e.TryGetPosition(c, out Point point) ? point : null);

        if (shown)
        {
            e.Handled = true;
        }
    }

    /// <summary>
    /// Opens the row menu, wherever the request came from.
    ///
    /// Selecting the row first is deliberate: the detail pane at the bottom follows the
    /// selection, so without this the menu would act on one file while the pane below it
    /// described another - which is exactly the mismatch that gets somebody to apply a
    /// change to the wrong photo.
    ///
    /// A request that resolves to no row is left alone rather than falling back to the
    /// selection. A menu that appears over empty space below the last row and acts on a
    /// file somewhere off screen is worse than no menu.
    /// </summary>
    /// <returns>False when there was no row under the request.</returns>
    private bool ShowRowMenu(string via, object? source, ListViewItem? container, Func<ListViewItem, Point?> position)
    {
        // The chain is logged, not just the type. Both a working and a broken walk report
        // source=TextBlock, so the type on its own says nothing about which one happened -
        // which is exactly why the first two attempts at this menu were guesswork.
        PlanRowViewModel? found = this.RowOf(container);

        Serilog.Log.Information(
            "Row menu requested. via={Via} resolved={Row} chain={Chain}",
            via,
            found?.Name ?? "none",
            AncestorChain(source));

        if (container is null || found is not { } row)
        {
            return false;
        }

        // Both paths can fire for one gesture. Whichever arrives first wins; the second
        // finds the menu already up and leaves it alone rather than reopening it.
        if (this.RowMenu.IsOpen)
        {
            return true;
        }

        this._menuRow = row;
        this.Workbench.SelectedRow = row;

        // Named for what the click will DO, not for the state it is in. "Include in the
        // run" sitting on an already-included row reads as a label rather than an action.
        if (this.RowMenuItem("tick") is { } tick)
        {
            tick.Text = row.IsIncluded ? "Take out of the run" : "Include in the run";
        }

        // Only offered when there is something to clear, because on every other row it
        // would be a menu item that does nothing.
        if (this.RowMenuItem("clear") is { } clear)
        {
            clear.Visibility = row.HasManualDate ? Visibility.Visible : Visibility.Collapsed;
        }

        // Positioned against the row rather than the list, so a keyboard request - which
        // carries no pointer position - still opens the menu on the row it belongs to
        // instead of at the top-left corner of a list scrolled a long way down.
        if (position(container) is { } point)
        {
            this.RowMenu.ShowAt(container, point);
        }
        else
        {
            this.RowMenu.ShowAt(container);
        }

        return true;
    }

    /// <summary>
    /// Finds a menu item by its tag.
    ///
    /// By tag rather than by name because these live in a ResourceDictionary, where the
    /// XAML compiler does not reliably generate fields for nested elements. The tag is
    /// stable and the lookup runs once per right-click on fourteen items.
    /// </summary>
    private MenuFlyoutItem? RowMenuItem(string tag) =>
        this.RowMenu.Items.OfType<MenuFlyoutItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, tag, StringComparison.Ordinal));

    private void OnRowMenuOpen(object sender, RoutedEventArgs e)
    {
        if (this._menuRow is { } row)
        {
            this.OpenWithShell(row);
        }
    }

    /// <summary>
    /// Opens the containing folder with the file already selected.
    ///
    /// The selection is the whole value: "where is this?" answered with a folder opened at
    /// the top of four thousand files is not an answer.
    /// </summary>
    private void OnRowMenuShowInExplorer(object sender, RoutedEventArgs e)
    {
        if (this._menuRow is not { } row)
        {
            return;
        }

        try
        {
            // Quoted because paths have spaces, and /select, takes exactly one argument.
            using var opening = Process.Start(new ProcessStartInfo("explorer.exe")
            {
                Arguments = string.Create(CultureInfo.InvariantCulture, $"/select,\"{row.File.FullPath}\""),
                UseShellExecute = true,
            });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            this.Workbench.ScanStatus = $"Could not show {row.Name} in File Explorer: {ex.Message}";
        }
    }

    private void OnRowMenuCopyPath(object sender, RoutedEventArgs e)
    {
        if (this._menuRow is not { } row)
        {
            return;
        }

        var package = new DataPackage();
        package.SetText(row.File.FullPath);
        Clipboard.SetContent(package);

        this.Workbench.ScanStatus = $"Copied the path to {row.Name}.";
    }

    /// <summary>
    /// Windows' own Properties dialog.
    ///
    /// It earns its place in a tool about dates by being the independent second opinion:
    /// it reads Created, Modified and Accessed straight from the filesystem, so it can
    /// confirm - or contradict - what Chronora says it just did.
    ///
    /// Process.Start with Verb = "properties" looks like it should do this and does not.
    /// The shell needs SEE_MASK_INVOKEIDLIST to have an item to raise a sheet for, and
    /// .NET never sets it, so that version fails silently.
    /// </summary>
    private void OnRowMenuProperties(object sender, RoutedEventArgs e)
    {
        if (this._menuRow is not { } row)
        {
            return;
        }

        var info = new SHELLEXECUTEINFO
        {
            cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
            fMask = SeeMaskInvokeIdList,
            hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this),
            lpVerb = "properties",
            lpFile = row.File.FullPath,
            nShow = SwShow,
        };

        if (!ShellExecuteEx(ref info))
        {
            this.Workbench.ScanStatus = $"Could not open properties for {row.Name}.";
        }
    }

    /// <summary>
    /// Duplicates the file beside itself, stamped with the time the copy was taken.
    ///
    /// The time it was COPIED rather than the date it holds, for two reasons: every copy
    /// is then unique, so taking two in a row cannot collide, and the name records when
    /// the safety net was put there. Naming it from the file's own date would be a first
    /// step into renaming files from dates, which this app deliberately does not do.
    ///
    /// Beside the original rather than in a backups folder, because this is a
    /// before-I-try-something copy and its value is being visible in the same place a
    /// moment later.
    /// </summary>
    private void OnRowMenuCreateCopy(object sender, RoutedEventArgs e)
    {
        if (this._menuRow is not { } row)
        {
            return;
        }

        string path = row.File.FullPath;

        if (Path.GetDirectoryName(path) is not { } folder)
        {
            return;
        }

        string stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture);
        string copy = Path.Combine(
            folder,
            $"{Path.GetFileNameWithoutExtension(path)}_{stamp}{Path.GetExtension(path)}");

        try
        {
            // Never overwrite. A copy that silently replaced an earlier copy would defeat
            // the only reason anybody makes one.
            File.Copy(path, copy, overwrite: false);

            this.Workbench.ScanStatus = $"Copied to {Path.GetFileName(copy)}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            this.Workbench.ScanStatus = $"Could not copy {row.Name}: {ex.Message}";
        }
    }

    private void OnRowMenuToggle(object sender, RoutedEventArgs e)
    {
        if (this._menuRow is { } row)
        {
            WorkbenchViewModel.ToggleRow(row);
        }
    }

    private void OnRowMenuSelectOnly(object sender, RoutedEventArgs e)
    {
        if (this._menuRow is { } row)
        {
            this.Workbench.SelectOnly(row);
        }
    }

    private void OnRowMenuRemove(object sender, RoutedEventArgs e)
    {
        if (this._menuRow is { } row)
        {
            this.Workbench.RemoveRow(row);
            this._menuRow = null;
        }
    }

    private void OnRowMenuUseDate(object sender, RoutedEventArgs e)
    {
        if (this._menuRow is { } row)
        {
            _ = this.Workbench.UseRowDateForRun(row);
        }
    }

    private void OnRowMenuClearDate(object sender, RoutedEventArgs e)
    {
        if (this._menuRow is { } row)
        {
            this.Workbench.SetManualDate(row, null);
        }
    }

    /// <summary>
    /// A date for this one file, overriding the run.
    ///
    /// In four thousand photos there are always three that need a date typed in, and
    /// without this the only way to handle them is a second run on a filtered list.
    ///
    /// Seeded with whatever the file already has, because the common edit is a correction
    /// of hours or minutes rather than a date built from nothing.
    /// </summary>
    private async void OnRowMenuSetDate(object sender, RoutedEventArgs e)
    {
        if (this._menuRow is not { } row)
        {
            return;
        }

        DateTimeOffset seed = (row.ManualDate ?? WorkbenchViewModel.BestDate(row) ?? DateTimeOffset.Now).ToLocalTime();

        var date = new CalendarDatePicker
        {
            Date = seed,
            Header = "Date",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        var time = new TimePicker
        {
            Time = seed.TimeOfDay,
            Header = "Time",
            ClockIdentifier = "24HourClock",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        // The same affordance as the options pane, for the same reason: "now" is a date
        // people genuinely want and typing today's date into two pickers to get it is
        // silly. Local, and there is deliberately no UTC twin - which frame the value is
        // stored in is per-format and the app already decides it.
        var now = new Button
        {
            Content = "Use the time now",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        ToolTipService.SetToolTip(
            now,
            "Fills in the current local date and time. Chronora works out for itself whether "
            + "each format stores that as local time or UTC.");

        now.Click += (_, _) =>
        {
            DateTimeOffset moment = DateTimeOffset.Now;

            date.Date = moment;
            time.Time = new TimeSpan(moment.Hour, moment.Minute, moment.Second);
        };

        // The same four fields the options pane offers, because the dialog is answering
        // the same two questions for one file - which date, and where it goes - and a
        // dialog that answered only the first would send the date into whatever the run
        // happened to be writing, which for a singled-out file is the thing most likely to
        // be wrong about it.
        IReadOnlySet<DateField> seeded = this.Workbench.DefaultTargetsFor(row);

        var boxes = new List<(DateField Field, CheckBox Box)>();

        foreach ((DateField field, string label, string? tip) in RowMenuTargets)
        {
            // A field this file cannot carry is left out rather than shown disabled. There
            // is no Taken date on a text file and never will be, so a greyed box only
            // raises a question with no answer.
            if (!WorkbenchViewModel.CanTarget(row, field))
            {
                continue;
            }

            var box = new CheckBox { Content = label, IsChecked = seeded.Contains(field) };

            if (tip is not null)
            {
                ToolTipService.SetToolTip(box, tip);
            }

            boxes.Add((field, box));
        }

        var targets = new StackPanel { Spacing = 2 };

        targets.Children.Add(new TextBlock
        {
            Text = "Write it to",
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
            Margin = new Thickness(0, 6, 0, 2),
        });

        foreach ((_, CheckBox box) in boxes)
        {
            targets.Children.Add(box);
        }

        var panel = new StackPanel { Spacing = 12 };

        panel.Children.Add(new TextBlock
        {
            Text = row.Name,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        panel.Children.Add(date);
        panel.Children.Add(time);
        panel.Children.Add(now);
        panel.Children.Add(targets);

        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
            Text = "This file alone uses this date and these fields. The rest of the run is "
                + "unchanged.",
        });

        var dialog = new ContentDialog
        {
            XamlRoot = this.Content.XamlRoot,
            Title = "Set this file's date",
            Content = panel,
            PrimaryButtonText = "Set",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary || date.Date is not { } picked)
        {
            return;
        }

        HashSet<DateField> chosen = [.. boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Field)];

        if (chosen.Count == 0)
        {
            // Nothing ticked means nothing to write, and saying so beats recording an
            // override that silently does nothing and then shows "by hand" on the row.
            this.Workbench.ScanStatus = $"{row.Name} was left alone - no fields were ticked.";
            return;
        }

        this.Workbench.SetManualDate(
            row,
            new DateTimeOffset(picked.Date.Add(time.Time), DateTimeOffset.Now.Offset),
            chosen);
    }

    /// <summary>
    /// The fields the row dialog offers, in the order and wording the options pane uses.
    ///
    /// Changed (NTFS) is deliberately absent, exactly as it is from the pane's main list:
    /// Explorer never shows it, so leaving it alone surprises nobody.
    /// </summary>
    private static readonly (DateField Field, string Label, string? Tip)[] RowMenuTargets =
    [
        (DateField.FileCreated, "Created", null),
        (DateField.FileModified, "Modified", null),
        (DateField.FileAccessed, "Accessed",
            "Explorer shows this next to Created and Modified. Windows usually stops updating "
            + "it, so a date set here tends to stay put."),
        (DateField.ExifDateTimeOriginal, "Taken (photo)",
            "The date the photo or video records as when it was taken. Photo libraries read "
            + "this and ignore the file dates, so on its own it is often exactly right."),
    ];

    /// <summary>
    /// Hands a file to whatever normally opens it, with one place to report a failure.
    ///
    /// UseShellExecute is the whole point: it resolves the user's own file association
    /// rather than trying to execute the file, which is what the default would do.
    /// </summary>
    private void OpenWithShell(PlanRowViewModel row)
    {
        try
        {
            using var opening = Process.Start(new ProcessStartInfo(row.File.FullPath)
            {
                UseShellExecute = true,
            });

            this.Workbench.ScanStatus = $"Opened {row.Name}.";
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            this.Workbench.ScanStatus = $"Could not open {row.Name}: {ex.Message}";
        }
    }

    /// <summary>SEE_MASK_INVOKEIDLIST - without it "properties" silently does nothing.</summary>
    private const uint SeeMaskInvokeIdList = 0x0000000C;

    /// <summary>SW_SHOWNORMAL.</summary>
    private const int SwShow = 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHELLEXECUTEINFO
    {
        public int cbSize;
        public uint fMask;
        public nint hwnd;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpVerb;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpParameters;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpDirectory;
        public int nShow;
        public nint hInstApp;
        public nint lpIDList;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpClass;
        public nint hkeyClass;
        public uint dwHotKey;
        public nint hIcon;
        public nint hProcess;
    }

    // DllImport rather than LibraryImport: the struct carries four marshalled strings, so
    // it is not blittable, and the source generator cannot marshal it without a hand-
    // written marshaller for no gain on a call made once per right-click.
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO info);

    /// <summary>
    /// Opens the double-clicked file with whatever normally opens it.
    ///
    /// Chronora is looking at dates, not at pictures, so the useful move is handing the
    /// file to something that IS a viewer rather than growing one. A double-click rather
    /// than a single, because a single already means "select this row" and a stray click
    /// must not launch another program.
    /// </summary>
    private void OnRowDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        // No fallback to the selection any more. There was one, and it is why this looked
        // fixed while the row lookup underneath it was broken: you select a row by
        // clicking it and then double-click the same row, so the fallback always had the
        // right answer and the lookup was never exercised. It cost hours on the context
        // menu, which had no selection to hide behind.
        PlanRowViewModel? row = this.FindRow(e.OriginalSource);

        // Logged because this handler has now failed three times for three different
        // reasons, and a double-click that does nothing leaves nothing behind to diagnose.
        Serilog.Log.Information(
            "Row double-clicked. resolved={Row} chain={Chain}",
            row?.Name ?? "none",
            AncestorChain(e.OriginalSource));

        if (row is null)
        {
            return;
        }

        try
        {
            using var opening = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(row.File.FullPath) { UseShellExecute = true });

            this.Workbench.ScanStatus = $"Opened {row.Name}.";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException
                                      or System.IO.FileNotFoundException)
        {
            // No association, the file has gone, or the shell refused it. Said in the status
            // bar rather than swallowed: a double-click that does nothing at all reads as
            // the app being broken.
            this.Workbench.ScanStatus = $"Could not open {row.Name}: {ex.Message}";
        }
    }

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
