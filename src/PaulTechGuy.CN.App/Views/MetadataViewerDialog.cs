// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PaulTechGuy.CN.Domain;
using PaulTechGuy.CN.Metadata;
using PaulTechGuy.CN.Presentation;

namespace PaulTechGuy.CN.App.Views;

/// <summary>
/// "Show all private data…": the personal details in one file, read-only, grouped by what
/// they mean rather than by where they are stored. Read live from the file each time, never
/// from the scan snapshot.
///
/// The groups are Private details' own categories, each marked Removed or Kept under the
/// current ticks, so looking at one file is also a preview of what the run would do to it.
///
/// Three things it deliberately does not have. No editing - Chronora is not a metadata editor,
/// and a half one is worse than none. No "Copy all" - it would put coordinates and serial
/// numbers into clipboard history, which Windows keeps and can sync. No map link - opening a
/// map is a network request with somebody's location in it, and Chronora makes none unasked.
/// </summary>
internal static class MetadataViewerDialog
{
    private const string Dates = "Dates";
    private const string Image = "Image";
    private const string FromWindows = "From Windows";
    private const string Other = "Everything else";

    public static async Task ShowAsync(XamlRoot root, WorkbenchViewModel workbench, PlanRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(workbench);
        ArgumentNullException.ThrowIfNull(row);

        IReadOnlyList<MetadataTag>? tags = await workbench.ReadAllMetadataAsync(row.FullPath);

        if (tags is null)
        {
            workbench.ReportProblem(string.Create(CultureInfo.CurrentCulture, $"Could not read the metadata of {row.Name}."));
            return;
        }

        List<(string Title, string Badge, List<MetadataTag> Tags)> sections = Group(tags, workbench, row.File.Kind);

        var list = new StackPanel { Spacing = 14 };

        var search = new TextBox { PlaceholderText = "Find a tag", HorizontalAlignment = HorizontalAlignment.Stretch };
        search.TextChanged += (_, _) => Fill(list, sections, search.Text);
        Fill(list, sections, string.Empty);

        var panel = new StackPanel { Spacing = 12, MinWidth = 640 };
        panel.Children.Add(new TextBlock
        {
            Text = sections.Count == 0
                ? "This file carries no private data."
                : string.Create(CultureInfo.CurrentCulture, $"{sections.Sum(s => s.Tags.Count):N0} private tags, marked with what Remove private details would do with the boxes ticked now. Nothing is removed until you run it."),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
        });
        panel.Children.Add(search);

        // No scroller of our own. The dialog already scrolls its content, and a fixed-height one
        // nested inside it was clipped by the dialog, hiding the bottom of the list.
        panel.Children.Add(list);

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = row.Name,
            Content = panel,
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
        };

        // The default ContentDialog tops out around 548 epx, which cuts a group-qualified tag
        // name and its value to a word each.
        dialog.Resources["ContentDialogMaxWidth"] = 760.0;

        _ = await dialog.ShowAsync();
    }

    private static List<(string Title, string Badge, List<MetadataTag> Tags)> Group(
        IReadOnlyList<MetadataTag> tags,
        WorkbenchViewModel workbench,
        MediaKind kind)
    {
        var byTitle = new Dictionary<string, (string Badge, List<MetadataTag> Tags)>(StringComparer.Ordinal);

        // Private data only. It showed every tag, and after a clean the camera model, the
        // exposure and the dates still filled the dialog - which read as "the details are all
        // still there". What is not personal is not this dialog's business.
        foreach (MetadataTag tag in tags.Where(t => PrivacyTagCatalog.CategoryOf($"{t.Group}:{t.Name}") is not null))
        {
            (string title, string badge) = SectionOf(tag, workbench, kind);

            if (!byTitle.TryGetValue(title, out (string Badge, List<MetadataTag> Tags) section))
            {
                section = (badge, []);
                byTitle[title] = section;
            }

            section.Tags.Add(tag);
        }

        // Personal details first, in the options pane's order, because they are why anybody
        // opened this; the rest after.
        string[] order =
        [
            .. PrivacyCategoryNames.All.Select(PrivacyCategoryNames.Title),
            Dates, Image, Other, FromWindows,
        ];

        return [.. order
            .Where(byTitle.ContainsKey)
            .Select(title => (title, byTitle[title].Badge, byTitle[title].Tags))];
    }

    private static (string Title, string Badge) SectionOf(MetadataTag tag, WorkbenchViewModel workbench, MediaKind kind)
    {
        if (PrivacyTagCatalog.CategoryOf($"{tag.Group}:{tag.Name}") is { } category)
        {
            return (PrivacyCategoryNames.Title(category), workbench.WouldRemove(category, kind) ? "will be removed" : "will be kept");
        }

        // What ExifTool reports about the file on disk rather than from inside it.
        if (tag.Group is "System" or "File" or "ExifTool" or "Composite")
        {
            return (FromWindows, string.Empty);
        }

        if (tag.Name.Contains("Date", StringComparison.OrdinalIgnoreCase)
            || tag.Name.StartsWith("OffsetTime", StringComparison.OrdinalIgnoreCase)
            || tag.Name.StartsWith("SubSecTime", StringComparison.OrdinalIgnoreCase))
        {
            return (Dates, "Kept");
        }

        if (tag.Group.StartsWith("ICC", StringComparison.OrdinalIgnoreCase)
            || tag.Name is "Orientation" or "ColorSpace" or "ImageWidth" or "ImageHeight" or "ExifImageWidth" or "ExifImageHeight")
        {
            return (Image, "Kept");
        }

        return (Other, "Kept");
    }

    /// <summary>Rebuilt on each keystroke. A few hundred text blocks is nothing to redraw.</summary>
    private static void Fill(StackPanel list, List<(string Title, string Badge, List<MetadataTag> Tags)> sections, string filter)
    {
        list.Children.Clear();

        var mono = new FontFamily("Consolas");
        var caption = (Style)Application.Current.Resources["CaptionTextBlockStyle"];
        var strong = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"];

        foreach ((string title, string badge, List<MetadataTag> tags) in sections)
        {
            MetadataTag[] shown = [.. tags.Where(t => filter.Length == 0
                || t.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || t.Group.Contains(filter, StringComparison.OrdinalIgnoreCase))];

            if (shown.Length == 0)
            {
                continue;
            }

            string heading = badge.Length == 0
                ? string.Create(CultureInfo.CurrentCulture, $"{title}  ({shown.Length:N0})")
                : string.Create(CultureInfo.CurrentCulture, $"{title}  ({shown.Length:N0}) · {badge}");

            list.Children.Add(new TextBlock { Text = heading, Style = strong });

            var grid = new Grid { ColumnSpacing = 12, RowSpacing = 2 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            for (int i = 0; i < shown.Length; i++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var name = new TextBlock
                {
                    Text = $"{shown[i].Group}:{shown[i].Name}",
                    FontFamily = mono,
                    Style = caption,
                    Opacity = 0.75,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    IsTextSelectionEnabled = true,
                };

                var value = new TextBlock
                {
                    Text = shown[i].Value,
                    FontFamily = mono,
                    Style = caption,
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                };

                Grid.SetRow(name, i);
                Grid.SetRow(value, i);
                Grid.SetColumn(value, 1);
                grid.Children.Add(name);
                grid.Children.Add(value);
            }

            list.Children.Add(grid);
        }
    }
}
