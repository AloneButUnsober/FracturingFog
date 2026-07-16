// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FracturingFog.UI.Avalonia.Views;

/// <summary>
/// Avalonia Scene Editor. Modeless floating editor for SceneData assets (Scene
/// Engine Roadmap Phase S5). Hybrid-shell: a UserControl hosted modeless by
/// MainWindow.SyncSceneEditor; the host + shell flag own chrome + close => hide,
/// and ShellViewModel wires the VM events (SceneSavedToLibrary,
/// SceneDeletedFromLibrary, PreviewShotRequested, StopPreviewRequested,
/// CloseRequested, MessageRequested).
/// </summary>
public sealed partial class SceneEditorView : UserControl
{
    public SceneEditorView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
