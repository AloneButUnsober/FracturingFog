// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FracturingFog.UI.Avalonia.Views;

/// <summary>
/// Avalonia port of <c>FloatingHelp</c>. Tab-based help window. Help text comes
/// from the host's <see cref="FracturingFog.Help.IHelpContentProvider"/>; the
/// host also handles <see cref="ViewModels.FloatingHelpViewModel.LinkRequested"/>
/// to launch URLs in the system browser. Hybrid-shell: a UserControl hosted
/// modeless by MainWindow.SyncHelp; the host + shell flag own chrome + close =>
/// hide (VM CloseRequested -> IsHelpVisible=false, wired in ShellViewModel).
/// </summary>
public sealed partial class FloatingHelpView : UserControl
{
    public FloatingHelpView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
