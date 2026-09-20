// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PaulTechGuy.CN.App.ViewModels;
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

        // Probed before the pane is built, so someone who already has ExifTool is offered
        // their own copy rather than a download they do not need.
        IReadOnlyList<ExifToolCandidate> existing = workbench.FindExistingExifTool();
        ExifToolManifest? offer = await workbench.GetExifToolOfferAsync().ConfigureAwait(true);

        var body = new StackPanel { Spacing = 8, MinWidth = 460 };

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
            body.Children.Add(new InfoBar
            {
                IsOpen = true,
                IsClosable = false,
                Severity = InfoBarSeverity.Informational,
                Title = "Downloading is not available right now",
                Message = "Chronora could not reach the list of available versions, so it cannot offer to "
                        + "fetch ExifTool. You can install it yourself with:\n\n"
                        + "    winget install OliverBetz.ExifTool\n\n"
                        + "then use “Choose the file myself” above.",
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

        return chosen switch
        {
            // Declining is a real answer. Every file-date feature keeps working, so there
            // is nothing to say beyond letting them get on with it.
            null => workbench.EngineStatus,
            "existing" => await workbench.UseExistingExifToolAsync(existing[0].ExecutablePath).ConfigureAwait(true),
            "browse" => await BrowseAsync(root, workbench).ConfigureAwait(true),
            _ => await InstallAsync(root, workbench).ConfigureAwait(true),
        };
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

    private static async Task<EngineStatus> InstallAsync(XamlRoot root, WorkbenchViewModel workbench)
    {
        var bar = new ProgressBar { Minimum = 0, Maximum = 100, Width = 380 };
        var text = new TextBlock { Text = "Downloading…" };

        var progressDialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "Installing ExifTool",
            Content = new StackPanel { Spacing = 12, Children = { text, bar } },
        };

        var progress = new Progress<double>(p =>
        {
            bar.Value = p;
            text.Text = string.Create(CultureInfo.CurrentCulture, $"Downloading… {p:N0}%");
        });

        Task<EngineStatus> install = workbench.InstallExifToolAsync(progress);

        // Shown and dismissed around the work rather than blocking on it, so the progress
        // is visible without the dialog owning the operation.
        _ = progressDialog.ShowAsync();

        EngineStatus status = await install.ConfigureAwait(true);
        progressDialog.Hide();

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
