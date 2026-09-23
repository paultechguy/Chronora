// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PaulTechGuy.CN.Presentation;
using PaulTechGuy.CN.Metadata;

namespace PaulTechGuy.CN.App.Views;

/// <summary>
/// Asks, once, before Chronora obtains ExifTool.
///
/// The ordering is the design. It leads with what is needed and what will happen, and puts
/// the licence, the URL and the checksum one disclosure away. Revision 1 led with
/// "GPL v1+ / Artistic" and a SHA256, which to someone who has just clicked past a
/// SmartScreen warning does not read as transparency - it reads as a program that wants to
/// install another program and is being oddly legalistic about it, which is the shape of a
/// bundled-adware prompt. The facts are all still here; only the order changed.
///
/// This is one of two modal surfaces in the app, and it earns it: a non-modal consent pane
/// is a thing people click away from and then cannot find again.
/// </summary>
internal static class ExifToolConsent
{
    /// <summary>
    /// Shows the pane and carries out whatever was chosen. Returns the resulting status,
    /// which may still be unavailable - declining is a supported answer, not a failure.
    /// </summary>
    public static async Task<EngineStatus> ShowAsync(XamlRoot root, WorkbenchViewModel workbench)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(workbench);

        // Loops so "Check again" can re-probe and rebuild. The probe runs when the pane is
        // built, so without this it cannot see an ExifTool installed WHILE the pane is
        // open - which is exactly what happens when someone follows the winget advice.
        //
        // A cancelled download now goes round the same way, which is why the loop carries a
        // notice: the pane it comes back to is otherwise identical to the one just left,
        // and a pane that looks untouched reads as the Cancel having failed.
        string? notice = null;

        while (true)
        {
            (EngineStatus? result, notice) = await ShowOnceAsync(root, workbench, notice).ConfigureAwait(true);

            if (result is { } status)
            {
                return status;
            }
        }
    }

    /// <summary>
    /// One pass.
    /// </summary>
    /// <param name="notice">
    /// A line to show above the options, carried over from the pass that asked to go round
    /// again. Null on the first pass and after "Check again", which needs no explanation.
    /// </param>
    /// <returns>
    /// The settled status, or null to go round again — paired with the notice the next pass
    /// should carry.
    /// </returns>
    private static async Task<(EngineStatus? Status, string? Notice)> ShowOnceAsync(
        XamlRoot root,
        WorkbenchViewModel workbench,
        string? notice)
    {
        // Probed before the pane is built, so someone who already has ExifTool is offered
        // their own copy rather than a download they do not need.
        IReadOnlyList<ExifToolCandidate> existing = workbench.FindExistingExifTool();
        ExifToolManifest? offer = await workbench.GetExifToolOfferAsync().ConfigureAwait(true);

        var body = new StackPanel { Spacing = 8, MinWidth = 460 };

        // Above the intro rather than below it: on this pass the user has already read what
        // ExifTool is, and what they want to know is what became of the download they
        // stopped. "Nothing was installed" is the half worth saying - a part-finished
        // download is the thing people worry about having left behind.
        if (notice is not null)
        {
            body.Children.Add(new InfoBar
            {
                IsOpen = true,
                IsClosable = false,
                Severity = InfoBarSeverity.Informational,
                Title = notice,
                Message = "Nothing was installed.",
                Margin = new Thickness(0, 0, 0, 4),
            });
        }

        body.Children.Add(new TextBlock
        {
            Text = "ExifTool, by Phil Harvey, is the standard tool for reading and writing photo dates. "
                 + "Chronora does not include it, and will not fetch it without you saying so.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 4),
        });

        // One button per option, each naming its own action, rather than a radio group and
        // a Continue. A radio list with a single entry reads as "pick one of one", and the
        // tick-then-confirm step invents a question - do I have to select something? - that
        // clicking the thing you want does not raise at all.
        string? chosen = null;
        ContentDialog dialog = null!;

        Button Option(string title, string description, string tag)
        {
            var content = new StackPanel { Spacing = 2 };
            content.Children.Add(new TextBlock { Text = title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            content.Children.Add(new TextBlock
            {
                Text = description,
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.75,
                FontSize = 12,
            });

            var button = new Button
            {
                Content = content,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(12, 10, 12, 10),
            };

            button.Click += (_, _) =>
            {
                chosen = tag;
                dialog.Hide();
            };

            return button;
        }

        // The existing install comes first when there is one: adopting it costs nothing
        // and respects a deliberate setup.
        if (existing.Count > 0)
        {
            body.Children.Add(Option(
                "Use the copy already on this PC",
                $"{existing[0].ExecutablePath}\n{existing[0].Origin}. Upgrading or uninstalling it will affect Chronora too.",
                "existing"));
        }

        if (offer is not null)
        {
            body.Children.Add(Option(
                "Download a private copy for Chronora",
                // Future tense: nothing has happened yet at the point this is read, and
                // "is installed" states as fact something the user has not yet agreed to.
                $"ExifTool {offer.Version}, {offer.SizeText}. A private copy will be installed in Chronora's "
                + "own folder, where nothing else can change it.",
                "download"));
        }

        body.Children.Add(Option(
            "Choose the file myself…",
            "If you have ExifTool somewhere Chronora did not look.",
            "browse"));

        // Why an option is absent, said where the options are rather than hidden in the
        // details. Without this the pane silently offers less than it should and looks
        // broken rather than blocked.
        if (offer is null)
        {
            var recheck = new Button { Content = "Check again" };
            recheck.Click += (_, _) =>
            {
                chosen = "recheck";
                dialog.Hide();
            };

            body.Children.Add(new InfoBar
            {
                IsOpen = true,
                IsClosable = false,
                Severity = InfoBarSeverity.Informational,
                Title = "Downloading is not available right now",

                // winget INSTALLS it, into a folder Chronora already looks in. An earlier
                // version of this told people to install it and then browse for the file,
                // which is nonsense: once winget has finished there is nothing to find by
                // hand. All that is missing is a second look, because the probe ran when
                // this pane was built.
                Message = "Chronora could not reach the list of available versions, so it cannot offer to "
                        + "fetch ExifTool. You can install it yourself:\n\n"
                        + "    winget install OliverBetz.ExifTool\n\n"
                        + "Chronora will find it on its own once that finishes — just choose Check again.",
                ActionButton = recheck,
                Margin = new Thickness(0, 4, 0, 0),
            });
        }

        body.Children.Add(BuildDetails(existing, offer));

        dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "Chronora needs a free helper to read photo dates",
            Content = body,

            // No primary button: every action is one of the buttons above, so a confirm
            // step here would only ask which of two ways of saying yes was meant.
            CloseButtonText = "Not now",
        };

        _ = await dialog.ShowAsync();

        switch (chosen)
        {
            // Declining is a real answer. Every file-date feature keeps working, so there
            // is nothing to say beyond letting them get on with it.
            case null:
                return (workbench.EngineStatus, null);

            // A null status means "go round again": re-probe and rebuild, so an ExifTool
            // installed while this pane was open is found rather than missed. No notice —
            // the user asked for another look and is about to get one.
            case "recheck":
                return (null, null);

            case "existing":
                return (await workbench.UseExistingExifToolAsync(existing[0].ExecutablePath).ConfigureAwait(true), null);

            case "browse":
                return (await BrowseAsync(root, workbench).ConfigureAwait(true), null);

            default:
                // Null here means the download was cancelled, which lands back on this pane
                // rather than closing the flow: they chose one of three ways in and changed
                // their mind about that one, not about all of them.
                EngineStatus? installed = await InstallAsync(root, workbench).ConfigureAwait(true);

                return installed is { } settled
                    ? (settled, null)
                    : (null, "The download was stopped");
        }
    }

    /// <summary>
    /// Licence, source and checksum. Present and complete, but folded away: we are not
    /// distributing GPL code, so there is no obligation to put it before the decision, and
    /// leading with it costs more in abandoned installs than it buys in candour.
    /// </summary>
    private static Expander BuildDetails(IReadOnlyList<ExifToolCandidate> existing, ExifToolManifest? offer)
    {
        var details = new StackPanel { Spacing = 4, Margin = new Thickness(0, 4, 0, 0) };

        void Line(string label, string value) =>
            details.Children.Add(new TextBlock
            {
                Text = $"{label,-10} {value}",
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
            });

        Line("Licence", "GPL v1 or later, or the Artistic Licence");
        Line("Author", "Phil Harvey");

        if (offer is not null)
        {
            Line("Version", offer.Version);
            Line("From", offer.Url);
            Line("Size", offer.SizeText);
            Line("SHA256", offer.Sha256);

            details.Children.Add(new TextBlock
            {
                Text = "The download is refused if its checksum does not match.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.75,
                FontSize = 12,
                Margin = new Thickness(0, 4, 0, 0),
            });
        }
        foreach (ExifToolCandidate candidate in existing)
        {
            Line("Found", $"{candidate.ExecutablePath}  ({candidate.Origin})");
        }

        return new Expander
        {
            Header = "Licence, source and checksum",
            Content = details,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
    }

    private static async Task<EngineStatus> BrowseAsync(XamlRoot root, WorkbenchViewModel workbench)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        picker.FileTypeFilter.Add(".exe");

        nint handle = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindowHandle);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, handle);

        Windows.Storage.StorageFile? file = await picker.PickSingleFileAsync();

        if (file is null)
        {
            return workbench.EngineStatus;
        }

        EngineStatus status = await workbench.UseExistingExifToolAsync(file.Path).ConfigureAwait(true);

        if (!status.Available)
        {
            await ReportAsync(root, "That file could not be used", status.Detail).ConfigureAwait(true);
        }

        return status;
    }

    /// <summary>
    /// Downloads and installs, with a way out.
    ///
    /// This pane used to hold a TextBlock, a ProgressBar and no buttons at all, which made
    /// it the one modal in the app with no way to dismiss it. It is also the first thing a
    /// new user meets: they have asked for photo dates, been told a helper is needed and
    /// said yes. A dead modal at that moment is the worst first impression available.
    /// </summary>
    /// <returns>The resulting status, or null when the user stopped the download.</returns>
    private static async Task<EngineStatus?> InstallAsync(XamlRoot root, WorkbenchViewModel workbench)
    {
        var bar = new ProgressBar { Minimum = 0, Maximum = 100, Width = 380 };
        var text = new TextBlock { Text = "Downloading…" };

        using var cancel = new CancellationTokenSource();

        var progressDialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "Installing ExifTool",
            Content = new StackPanel { Spacing = 12, Children = { text, bar } },

            // The close button, which Esc also invokes. The token was threaded the whole
            // way down to the read loop long before anything created one to pass.
            CloseButtonText = "Cancel",
        };

        progressDialog.CloseButtonClick += (_, _) => cancel.Cancel();

        var progress = new Progress<double>(p =>
        {
            bar.Value = p;
            text.Text = string.Create(CultureInfo.CurrentCulture, $"Downloading… {p:N0}%");
        });

        Task<EngineStatus> install = workbench.InstallExifToolAsync(progress, cancel.Token);

        // Shown and dismissed around the work rather than blocking on it, so the progress
        // is visible without the dialog owning the operation.
        _ = progressDialog.ShowAsync();

        EngineStatus status;

        try
        {
            status = await install.ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            // In a finally, not on the line after the await. Hide() used to sit outside any
            // guard, so anything thrown by the install left this pane on screen for good -
            // the same dead modal the Cancel button exists to prevent, reached by the one
            // door a Cancel button cannot cover. Hiding an already-hidden dialog is a no-op,
            // so the cancel path costs nothing for passing through here.
            progressDialog.Hide();
        }

        if (!status.Available)
        {
            await ReportAsync(root, "ExifTool was not installed", status.Detail).ConfigureAwait(true);
        }

        return status;
    }

    /// <summary>
    /// Every failure ends somewhere useful rather than in a dead end. On a managed network
    /// the download is often simply blocked, and the answer is to install it another way.
    /// </summary>
    private static async Task ReportAsync(XamlRoot root, string title, string detail)
    {
        var body = new StackPanel { Spacing = 12, MinWidth = 420 };

        body.Children.Add(new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap });

        body.Children.Add(new TextBlock
        {
            Text = "You can install it yourself and point Chronora at it:\n\n"
                 + "    winget install OliverBetz.ExifTool\n\n"
                 + "or download it from exiftool.org on another machine and copy it across. "
                 + "Chronora's own file-date features keep working either way.",
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            FontSize = 12,
            Opacity = 0.85,
        });

        await new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = body,
            CloseButtonText = "Close",
        }.ShowAsync();
    }
}
