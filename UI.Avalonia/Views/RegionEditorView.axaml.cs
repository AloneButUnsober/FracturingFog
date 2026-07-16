// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FracturingFog.UI.Avalonia.Views;

/// <summary>
/// Avalonia Region Editor. Modeless floating editor for a saved region's
/// metadata (Animation Roadmap Sub-goal B, Phase R1). Hybrid-shell: a
/// UserControl hosted modeless by MainWindow.SyncRegionEditor; the host + shell
/// flag own chrome + close => hide, and ShellViewModel wires the VM events
/// (RegionSavedToLibrary, CloseRequested, MessageRequested). Geometry is
/// read-only here — Save Region from the live view handles re-framing.
/// </summary>
public sealed partial class RegionEditorView : UserControl
{
    public RegionEditorView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
