// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using Microsoft.UI.Xaml.Controls;

namespace PaulTechGuy.CN.App.Views;

/// <summary>
/// Turns the view model's plain bool into the InfoBar severity the run notice shows.
///
/// It lives here rather than on the view model because <c>InfoBarSeverity</c> is a WinUI
/// type, and the layering rule says the Presentation assembly does not know what a WinUI
/// control is. The view model reports whether the run had failures; what colour that is
/// belongs to the thing doing the drawing.
/// </summary>
internal static class RunNoticeVisual
{
    /// <summary>
    /// Informational rather than Success for a clean run.
    ///
    /// A green bar for "Done. 12 changed, 0 failed" reads as congratulation for something
    /// the user asked for and expected. Warning is reserved for the run that actually needs
    /// a second look, so that colour keeps meaning something when it appears.
    /// </summary>
    public static InfoBarSeverity SeverityFor(bool hadFailures) =>
        hadFailures ? InfoBarSeverity.Warning : InfoBarSeverity.Informational;
}
