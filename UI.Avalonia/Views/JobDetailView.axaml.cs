// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Views/JobDetailView.axaml.cs
// Hybrid-shell: UserControl hosted modeless by MainWindow.SyncJobDetail. Poll
// lifecycle (host Opened/Closed) + close => hide (VM CloseRequested ->
// IsJobDetailVisible=false in ShellViewModel) are owned by the host + shell
// flag. Single-instance VM: the JobId setter clears state + immediate-polls,
// so a target swap needs no window/lifecycle churn here.

using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FracturingFog.UI.Avalonia.Views;

public partial class JobDetailView : UserControl
{
    public JobDetailView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
