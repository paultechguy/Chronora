// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace PaulTechGuy.CN.App.Views;

/// <summary>
/// The branding artwork that ships beside the executable.
///
/// Paths are built from the base directory rather than ms-appx: URIs. Chronora is deployed
/// unpackaged, so there is no package for ms-appx to resolve against - and the failure mode is
/// silent, because artwork that will not resolve simply draws nothing.
/// </summary>
internal static class AppImages
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "Assets");

    /// <summary>
    /// The multi-size icon, for the window and the taskbar.
    ///
    /// This is a SECOND path to the same artwork, and both are needed for different reasons.
    /// The csproj's ApplicationIcon embeds the .ico in Chronora.exe, which is what Explorer,
    /// the Start menu, Add/Remove Programs and the Send To entry all read - they resolve
    /// "&lt;exe&gt;,0". AppWindow.SetIcon needs the file itself, and is what puts the icon in
    /// the title bar and alt-tab. Embedding alone leaves the running window generic; shipping
    /// the file alone leaves every shortcut generic.
    /// </summary>
    public static string IconPath { get; } = Path.Combine(Root, "ChronoraLogo.ico");

    /// <summary>
    /// Checked rather than assumed. A build that lost the Content item still runs; it simply
    /// looks unbranded, which is a cosmetic problem and must never be a fatal one.
    /// </summary>
    public static bool HasIcon => File.Exists(IconPath);

    private static readonly string LogoPath = Path.Combine(Root, "ChronoraLogo.png");

    /// <summary>
    /// The logo, decoded at the size it will actually be drawn.
    ///
    /// A bitmap rather than a vector, and that is a decision rather than laziness:
    /// SvgImageSource is backed by Direct2D, whose SVG support covers neither CSS class
    /// selectors nor text elements, so an export using either draws the wrong thing with
    /// nothing written to the log. The vector master stays in docs\assets\ and the PNG ships.
    ///
    /// DecodePixelType.Logical matters. The size asked for is in logical pixels, so the
    /// decode follows display scaling - at 200% a 96 here decodes 192 real pixels instead of
    /// stretching 96 across them, which is the difference between a crisp logo and a soft one
    /// on exactly the high-DPI screens most likely to be looking at it.
    ///
    /// Null when the file is missing, which the caller draws as nothing.
    /// </summary>
    public static ImageSource? Logo(int size)
    {
        if (!File.Exists(LogoPath))
        {
            return null;
        }

        // The decode properties are only read on the way in: setting UriSource starts the
        // decode, so it has to be assigned last.
        var logo = new BitmapImage
        {
            DecodePixelType = DecodePixelType.Logical,
            DecodePixelWidth = size,
            DecodePixelHeight = size,
        };

        logo.UriSource = new Uri(LogoPath);

        return logo;
    }
}
