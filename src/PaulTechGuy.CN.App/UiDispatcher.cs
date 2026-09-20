// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using Microsoft.UI.Dispatching;
using PaulTechGuy.CN.Abstractions;

namespace PaulTechGuy.CN.App;

/// <summary>
/// The WinUI end of <see cref="IUiDispatcher" />.
///
/// This is the only place that knows work has to go back through a DispatcherQueue, which
/// is the point: the view model states that it needs the UI thread and this says how.
/// </summary>
public sealed class UiDispatcher(DispatcherQueue queue) : IUiDispatcher
{
    private readonly DispatcherQueue _queue = queue;

    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        // Already on the UI thread: run it now rather than queueing a frame's delay onto
        // something the user is waiting to see.
        if (this._queue.HasThreadAccess)
        {
            action();
            return;
        }

        _ = this._queue.TryEnqueue(() => action());
    }
}
