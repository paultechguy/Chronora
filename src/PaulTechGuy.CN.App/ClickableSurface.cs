// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace PaulTechGuy.CN.App;

/// <summary>
/// A wrapper that shows a hand cursor over whatever it contains.
///
/// It exists at all because WinUI makes UIElement.ProtectedCursor protected: there is no
/// way to set a cursor from XAML or from outside the element, so something has to derive.
/// It derives from ContentControl rather than Border because Border - along with Grid and
/// most other WinUI panels - is sealed.
///
/// It draws nothing itself. The thing inside keeps its own background, corners and border,
/// so this adds a cursor and an event surface and changes no visuals.
/// </summary>
internal sealed partial class ClickableSurface : ContentControl
{
    public ClickableSurface()
    {
        this.ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);

        // Without these the content is centred in whatever space the control takes and
        // stops lining up with the text beside it.
        this.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        this.VerticalContentAlignment = VerticalAlignment.Stretch;

        // Not a focus stop: it is a shortcut to an external viewer, not a control anyone
        // needs to reach by keyboard, and tabbing onto a picture that looks inert is worse
        // than not offering it.
        this.IsTabStop = false;
    }
}
