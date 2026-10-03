// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;

using Avalonia.Controls;
using Avalonia.Markup.Xaml;

using FracturingFog.UI.Avalonia.ViewModels;

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
    private SceneEditorViewModel? _vm;

    public SceneEditorView()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += (_, _) =>
        {
            if (_vm != null) _vm.DrawCameraPathRequested -= OnDrawCameraPath;
            _vm = DataContext as SceneEditorViewModel;
            if (_vm != null) _vm.DrawCameraPathRequested += OnDrawCameraPath;
        };
    }

    // #1056 — modal "Draw camera path" dialog for one shot.
    private async void OnDrawCameraPath(object? sender, SceneShotRowViewModel row)
    {
        try
        {
            var vm = new CameraPathDrawViewModel(row.IsRelief3D, row.DurationSeconds);
            var win = new CameraPathDrawWindow { DataContext = vm };
            await Services.WindowService.ShowDialogAsync(win, TopLevel.GetTopLevel(this) as Window);
            if (vm.Result != null) row.ReplaceCameraTrack(vm.Result);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SceneEditorView] Draw camera path failed: {ex.Message}");
        }
    }
}
