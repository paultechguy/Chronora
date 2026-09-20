// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using PaulTechGuy.CN.App.ViewModels;
using Windows.Graphics;

namespace PaulTechGuy.CN.App.Views;

public sealed partial class MainWindow : Window
{
    // The window is freely resizable because the primary content is a file listing: more
    // screen means more rows, which is the biggest usability lever in a bulk tool. The
    // minimum only stops the three regions collapsing into nonsense.
    private const int MinimumWidth = 900;
    private const int MinimumHeight = 600;

    public MainWindow(MainViewModel viewModel)
    {
        this.ViewModel = viewModel;

        this.InitializeComponent();

        this.Title = "Chronora";
        this.SystemBackdrop = new MicaBackdrop { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt };
        this.ExtendsContentIntoTitleBar = true;
        this.SetTitleBar(this.AppTitleBar);

        this.AppWindow.Resize(new SizeInt32(1280, 820));
        this.AppWindow.Changed += this.OnAppWindowChanged;
    }

    public MainViewModel ViewModel { get; }

    /// <summary>
    /// WinUI has no MinWidth on a Window, so the clamp is applied on resize.
    /// </summary>
    private void OnAppWindowChanged(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowChangedEventArgs args)
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
}
