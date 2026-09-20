// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

namespace PaulTechGuy.CN.Abstractions;

/// <summary>
/// Gets work back onto the thread the UI lives on.
///
/// This exists because the view model previously reached for
/// TaskScheduler.FromCurrentSynchronizationContext(), which throws outright when there is no
/// synchronization context. That is a WinUI assumption buried in logic that has no business
/// knowing about WinUI, and it is what made the view model impossible to test: the first
/// test written against it failed on that call rather than on anything it was testing.
///
/// A debounced recompute needs to resume somewhere, and saying where is the caller's job.
/// </summary>
public interface IUiDispatcher
{
    /// <summary>
    /// Runs the action on the UI thread, later. Returns immediately.
    /// </summary>
    void Post(Action action);
}
