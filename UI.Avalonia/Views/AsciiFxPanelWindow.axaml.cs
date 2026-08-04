// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FracturingFog.UI.Avalonia.Views;

/// <summary>Floating window hosting the full ASCII FX panel (#229). A separate
/// top-level window (not an in-render overlay) so the native GPU swap-chain HWND
/// can't occlude it. Bound to the shell's AsciiFxPanelViewModel.</summary>
public sealed partial class AsciiFxPanelWindow : Window
{
    public AsciiFxPanelWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
