// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace PaulTechGuy.CN.Presentation;

/// <summary>
/// The rows the grid shows, as one collection instance for the life of the window.
///
/// The instance never changing is the whole point. ItemsSource is bound to it, and handing
/// a list control a DIFFERENT list makes it throw away every container and start again:
/// scroll position back to the top, every thumbnail re-requested, selection re-applied. A
/// recompute that did not move a single row should cost none of that, and before this it
/// cost all of it - every keystroke in the date box, every option change, every time a row
/// handed its date to the run.
///
/// ObservableCollection has no way to replace its contents in one go; Clear() followed by
/// Add() per item raises a notification each time. <see cref="ResetTo"/> is the missing
/// operation.
/// </summary>
public sealed class RowCollection : ObservableCollection<PlanRowViewModel>
{
    /// <summary>
    /// Replaces everything with a single Reset notification instead of one per row.
    /// </summary>
    public void ResetTo(IReadOnlyList<PlanRowViewModel> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        // Items is the raw backing list, so nothing is raised while it is being rebuilt.
        this.Items.Clear();

        foreach (PlanRowViewModel row in rows)
        {
            this.Items.Add(row);
        }

        this.OnPropertyChanged(new PropertyChangedEventArgs(nameof(this.Count)));

        // The indexer's conventional name. Bindings to Rows[n] watch for this exact string.
        this.OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));

        this.OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
