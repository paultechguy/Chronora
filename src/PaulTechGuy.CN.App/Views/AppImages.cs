// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

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
}
