// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PaulTechGuy.CN.Domain;
using PaulTechGuy.CN.Presentation;

namespace PaulTechGuy.CN.App.Views;

/// <summary>
/// "More filters…": read or skip files and folders by name, in the shape of Beyond
/// Compare's session filters.
///
/// This decides what is READ FROM DISK. The Type filter on the list header is the other
/// half and stays: it narrows a list already loaded, instantly, and scopes the run. The
/// panes are titled "Include" and "Skip". They were "Read" and "Skip" until Paul chose
/// Include on 2026-09-24; the help line's pointer to Type is what keeps the two apart.
///
/// Pressing OK reads nothing, exactly like ticking one of the flyout's boxes; the card
/// offers "Read the folders again" instead.
/// </summary>
internal static class ScanFiltersDialog
{
    private const int SessionOnly = 0;
    private const int AlsoSave = 1;

    /// <param name="saveNow">Writes the saved filters to the settings file straight away.</param>
    public static async Task ShowAsync(XamlRoot root, WorkbenchViewModel workbench, Action saveNow)
    {
        ArgumentNullException.ThrowIfNull(workbench);
        ArgumentNullException.ThrowIfNull(saveNow);

        ScanPatterns current = workbench.ScanPatternsInEffect;

        TextBox readFiles = Pane("Include files named");
        TextBox skipFiles = Pane("Skip files named");
        TextBox readFolders = Pane("Include folders named");
        TextBox skipFolders = Pane("Skip folders named");

        void Fill(ScanPatterns patterns)
        {
            readFiles.Text = Lines(patterns.IncludeFiles);
            skipFiles.Text = Lines(patterns.ExcludeFiles);
            readFolders.Text = Lines(patterns.IncludeFolders);
            skipFolders.Text = Lines(patterns.ExcludeFolders);
        }

        Fill(current);

        var grid = new Grid { ColumnSpacing = 12, RowSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition());
        Place(grid, readFiles, 0, 0);
        Place(grid, skipFiles, 0, 1);
        Place(grid, readFolders, 1, 0);
        Place(grid, skipFolders, 1, 1);

        var clear = new Button { Content = "Clear" };
        clear.Click += (_, _) => Fill(ScanPatterns.None);

        // Merged, never replaced: somebody's own excludes stay where they are.
        var suggest = new Button
        {
            Content = "Suggest",
        };
        ToolTipService.SetToolTip(
            suggest,
            "Adds what photo libraries often carry and nobody wants dated: Synology @eaDir, "
            + ".thumbnails and Lightroom previews, and iPhone .aae edit files.");
        suggest.Click += (_, _) =>
        {
            skipFiles.Text = Lines(Merge(skipFiles.Text, ScanPatterns.SuggestedExcludeFiles, files: true));
            skipFolders.Text = Lines(Merge(skipFolders.Text, ScanPatterns.SuggestedExcludeFolders, files: false));
        };

        // The way back from a session-only experiment without retyping what was saved.
        var resetToSaved = new HyperlinkButton
        {
            Content = "Reset to saved",
            Visibility = workbench.ScanPatternsAreSessionOnly ? Visibility.Visible : Visibility.Collapsed,
        };
        resetToSaved.Click += (_, _) => Fill(workbench.ScanPatternsSaved);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(clear);
        buttons.Children.Add(suggest);
        buttons.Children.Add(resetToSaved);

        // Opens on "this session only" unless preferences already hold filters and those
        // are what is in effect. On a fresh install both copies are empty and therefore
        // equal, and defaulting to "update preferences" there would make a one-off NAS
        // exclude sticky without anyone choosing it - the forgotten filter that hides files
        // from a drop months later.
        var scope = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            SelectedIndex = workbench.ScanPatternsSaved.IsNarrowed(recurse: true) && !workbench.ScanPatternsAreSessionOnly
                ? AlsoSave
                : SessionOnly,
        };
        scope.Items.Add("Use for this session only");
        scope.Items.Add("Also update application preferences");
        AutomationProperties.SetName(scope, "Where these filters apply");

        var help = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
            Text = "One name per line, or separated by ;. Use * and ? as wildcards; \"xmp\" means *.xmp. "
                + "Skip wins over Include. Include folders takes everything beneath a match, at any depth. "
                + "Hidden and system files are already skipped. Files you drop one at a time are always read. "
                + "To narrow a list that is already loaded, use Type on the list instead.",
        };

        var panel = new StackPanel { Spacing = 12, MinWidth = 480 };
        panel.Children.Add(grid);
        panel.Children.Add(buttons);
        panel.Children.Add(scope);
        panel.Children.Add(help);

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "Include or skip by name",
            Content = new ScrollViewer { Content = panel },
            PrimaryButtonText = "OK",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        ScanPatterns chosen = ScanPatterns.FromText(readFiles.Text, skipFiles.Text, readFolders.Text, skipFolders.Text);
        bool save = scope.SelectedIndex == AlsoSave;

        workbench.ApplyScanPatterns(chosen, save);

        if (save)
        {
            saveNow();
        }

        Serilog.Log.Information(
            "Name filters set ({Scope}): {Filters}",
            save ? "saved" : "session only",
            chosen.Describe().Replace(Environment.NewLine, "; ", StringComparison.Ordinal));
    }

    /// <summary>
    /// A multi-line pane. AcceptsReturn means Enter makes a new line rather than pressing
    /// OK, which is what somebody typing a list expects.
    /// </summary>
    private static TextBox Pane(string header)
    {
        var box = new TextBox
        {
            Header = header,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            Height = 130,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            IsSpellCheckEnabled = false,
        };
        AutomationProperties.SetName(box, header);
        ScrollViewer.SetVerticalScrollBarVisibility(box, ScrollBarVisibility.Auto);

        return box;
    }

    private static void Place(Grid grid, FrameworkElement element, int row, int column)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }

    /// <summary>One per line in the panes; TextBox uses a bare carriage return.</summary>
    private static string Lines(IReadOnlyList<string> patterns) => string.Join("\r", patterns);

    private static List<string> Merge(string existing, IReadOnlyList<string> additions, bool files)
    {
        var merged = NamePatterns.Parse(existing, files).ToList();

        foreach (string addition in additions)
        {
            if (!merged.Contains(addition, StringComparer.OrdinalIgnoreCase))
            {
                merged.Add(addition);
            }
        }

        return merged;
    }
}
