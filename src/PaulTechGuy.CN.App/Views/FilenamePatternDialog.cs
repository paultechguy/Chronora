// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using PaulTechGuy.CN.Domain;
using PaulTechGuy.CN.Presentation;
using PaulTechGuy.CN.Rules;

namespace PaulTechGuy.CN.App.Views;

/// <summary>
/// "Click the year. Click the month. Click the day."
///
/// The escape hatch for a filename none of the built-in patterns recognise, and the reason
/// it is not a regex box: the person who needs an escape hatch is the one the built-ins
/// failed, and knowing that your camera writes odd filenames has nothing to do with being
/// able to write a named capture group.
///
/// The match count underneath is what makes it safe to press Use. Having clicked three
/// numbers on one file, "matches 1,284 of 1,284" is the reassurance - and "matches 1 of
/// 1,284" is what stops somebody applying a pattern that only ever fitted the one file
/// they built it from.
/// </summary>
internal static class FilenamePatternDialog
{
    /// <summary>The roles a run of digits can be given, in the order people read a date.</summary>
    private static readonly (ChipRole Role, string Label)[] Roles =
    [
        (ChipRole.Year, "Year (4 digits)"),
        (ChipRole.ShortYear, "Year (2 digits)"),
        (ChipRole.Month, "Month"),
        (ChipRole.Day, "Day"),
        (ChipRole.Hour, "Hour"),
        (ChipRole.Minute, "Minute"),
        (ChipRole.Second, "Second"),
    ];

    /// <summary>
    /// Shows the builder for one filename and applies the result if the user says so.
    /// </summary>
    public static async Task ShowAsync(XamlRoot root, WorkbenchViewModel workbench, string fileName)
    {
        ArgumentNullException.ThrowIfNull(workbench);

        var builder = new FilenameChipBuilder(fileName);

        var chips = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
        };

        var tokens = new TextBlock
        {
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.8,
        };

        var status = new InfoBar { IsOpen = true, IsClosable = false };

        var samples = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
        };

        var panel = new StackPanel { Spacing = 12, MinWidth = 460 };

        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = "Click a number, then say what it is.",
        });

        // A worked example, because the splitting behaviour is the part nobody guesses:
        // clicking Year on one long run takes the first four digits and hands the rest
        // back, which is what makes a camera filename workable at all.
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
            Text = "For IMG_20240315_142530, click 20240315 and choose Year: it takes 2024 and "
                + "leaves 0315 beside it. Click 0315 and choose Month to take 03, then the "
                + "remaining 15 as Day. Same again on 142530 for the time, if you want one.",
        });

        var scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Auto,
            Content = chips,
        };

        panel.Children.Add(scroller);
        panel.Children.Add(tokens);
        panel.Children.Add(status);
        panel.Children.Add(samples);

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "Teach Chronora this file name",
            Content = panel,
            PrimaryButtonText = "Use this pattern",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
        };

        // Declared before Redraw so the chips' click handlers can call it.
        void Redraw()
        {
            chips.Children.Clear();

            for (int i = 0; i < builder.Chips.Count; i++)
            {
                chips.Children.Add(ChipButton(builder, i, Redraw));
            }

            tokens.Text = builder.ToTokens();

            if (builder.Problem is { } problem)
            {
                status.Severity = InfoBarSeverity.Informational;
                status.Message = problem;
                samples.Text = string.Empty;
                dialog.IsPrimaryButtonEnabled = false;
                return;
            }

            FilenamePatternPreview preview =
                workbench.PreviewFilenamePattern(builder.ToTokens(), builder.Precision);

            // Matching nothing is a failure worth saying out loud rather than leaving
            // somebody to press Use and find every row unchanged.
            status.Severity = preview.Matched == 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success;
            status.Message = string.Create(
                CultureInfo.CurrentCulture,
                $"Matches {preview.Matched:N0} of {preview.Total:N0} files.");

            // Said out loud, because otherwise it looks exactly like a bug. A pattern with
            // no hour or minute sets the date and leaves each field's existing time of day
            // alone rather than inventing midnight - so afterwards the dates all match and
            // the times do not, which is a reasonable thing to be alarmed by.
            if (builder.Precision == DatePrecision.Day)
            {
                status.Message += " This pattern has no time in it, so each file keeps the "
                    + "time of day it already has.";
            }

            samples.Text = string.Join(Environment.NewLine, preview.Samples);
            dialog.IsPrimaryButtonEnabled = preview.Matched > 0;
        }

        Redraw();

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            workbench.UseFilenamePattern(builder.ToTokens(), builder.Precision);
        }
    }

    /// <summary>
    /// One piece of the filename. Digits are clickable; everything else is just there so
    /// the name still reads as the name.
    /// </summary>
    private static UIElement ChipButton(FilenameChipBuilder builder, int index, Action redraw)
    {
        FilenameChip chip = builder.Chips[index];

        if (!chip.IsDigits)
        {
            return new TextBlock
            {
                Text = chip.Text,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0.65,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            };
        }

        var label = new StackPanel { Spacing = 0 };

        label.Children.Add(new TextBlock
        {
            Text = chip.Text,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        // The role sits under the digits rather than replacing them, so the filename can
        // still be read while it is being taken apart.
        if (chip.Role != ChipRole.None)
        {
            label.Children.Add(new TextBlock
            {
                Text = chip.Role.ToString().ToLowerInvariant(),
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"],
            });
        }

        var menu = new MenuFlyout();

        foreach ((ChipRole role, string text) in Roles)
        {
            // Roles that do not fit are left out rather than shown disabled: a two-digit
            // run cannot hold a four-digit year and never will, so a greyed item only
            // raises a question with no answer.
            if (chip.Text.Length < FilenameChipBuilder.WidthOf(role))
            {
                continue;
            }

            var item = new MenuFlyoutItem { Text = text };

            item.Click += (_, _) =>
            {
                _ = builder.Assign(index, role);
                redraw();
            };

            menu.Items.Add(item);
        }

        if (chip.Role != ChipRole.None)
        {
            menu.Items.Add(new MenuFlyoutSeparator());

            var clear = new MenuFlyoutItem { Text = "Not part of the date" };

            clear.Click += (_, _) =>
            {
                builder.Clear(index);
                redraw();
            };

            menu.Items.Add(clear);
        }

        return new Button
        {
            Content = label,
            Padding = new Thickness(6, 2, 6, 2),
            Flyout = menu,
        };
    }
}
