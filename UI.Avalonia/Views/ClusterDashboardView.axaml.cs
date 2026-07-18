// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Views/ClusterDashboardView.axaml.cs
// Hybrid-shell: a UserControl hosted modeless by MainWindow.SyncClusterDashboard.
// The VM poll lifecycle (start on host Opened, stop on host Closed) and the
// close => hide behavior (VM CloseRequested -> IsClusterDashboardVisible=false,
// wired in ShellViewModel) are owned by the host + shell flag, so this view
// carries no window chrome or lifecycle code.

using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FracturingFog.UI.Avalonia.Views;

public partial class ClusterDashboardView : UserControl
{
    public ClusterDashboardView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
