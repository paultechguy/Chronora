// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using PaulTechGuy.CN.Abstractions;

namespace PaulTechGuy.CN.Presentation.Tests;

/// <summary>
/// Runs the work where it stands. There is no UI thread in a test, and the thing under
/// test is what the view model does, not which thread it does it on.
/// </summary>
internal sealed class ImmediateDispatcher : IUiDispatcher
{
    public void Post(Action action) => action();
}
