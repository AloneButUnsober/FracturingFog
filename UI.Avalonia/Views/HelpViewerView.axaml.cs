// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FracturingFog.UI.Avalonia.Views;

/// <summary>
/// Markdown-backed contextual Help viewer. Hybrid-shell: a UserControl hosted
/// modeless by <see cref="HelpViewerLauncher"/>, which owns the window chrome
/// and wires the VM's <c>CloseRequested</c> to the host window's close.
/// </summary>
public sealed partial class HelpViewerView : UserControl
{
    public HelpViewerView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
