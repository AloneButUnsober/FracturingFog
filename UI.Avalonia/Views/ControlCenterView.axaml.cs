// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System.Collections.Generic;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

using FracturingFog.UI.Avalonia.Services;
using FracturingFog.UI.Avalonia.ViewModels;
using FracturingFog.UI.Avalonia.Views.ControlCenterSections;

namespace FracturingFog.UI.Avalonia.Views;

/// <summary>
/// Phase S1 Control Center shell — a SplitView nav-rail + sectioned content
/// that re-presents <see cref="ViewModels.FloatingMenuViewModel"/> /
/// <see cref="ViewModels.ShellViewModel"/>. Hybrid-shell: a UserControl hosted
/// modeless by MainWindow.SyncControlCenter (PanelHostWindow), so it can dock
/// or pop out and is 2nd-monitor aware.
///
/// S2 — each section is its own UserControl. The docked view hosts one instance
/// inline; <see cref="OnDetachRequested"/> pops a second instance into its own
/// PanelHostWindow bound to the same VM, so docked + detached stay in lock-step.
/// </summary>
public sealed partial class ControlCenterView : UserControl
{
    private ControlCenterViewModel? _wired;

    public ControlCenterView()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += (_, _) => Rewire();
    }

    private void Rewire()
    {
        if (_wired != null) _wired.DetachRequested -= OnDetachRequested;
        _wired = DataContext as ControlCenterViewModel;
        if (_wired != null) _wired.DetachRequested += OnDetachRequested;
    }

    private void OnDetachRequested(object? sender, ControlCenterSection section)
    {
        if (DataContext is ControlCenterViewModel vm)
            ControlCenterDetachService.Open(section, vm);
    }
}
