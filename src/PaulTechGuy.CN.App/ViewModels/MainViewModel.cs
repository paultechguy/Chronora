// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using CommunityToolkit.Mvvm.ComponentModel;
using PaulTechGuy.CN.Abstractions;

namespace PaulTechGuy.CN.App.ViewModels;

/// <summary>
/// Skeleton for milestone 1. The real one arrives with the workbench in milestone 5.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    public MainViewModel(IAppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        this.DataDirectory = paths.DataDirectory;
    }

    [ObservableProperty]
    public partial string DataDirectory { get; set; }
}
