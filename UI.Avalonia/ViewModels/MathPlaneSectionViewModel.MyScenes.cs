// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// UI.Avalonia/ViewModels/MathPlaneSectionViewModel.MyScenes.cs
//
// #1637 — My scenes: save the current Math Plane settings (scene, map, c,
// backdrop, view) under a name and load them back. Entries live in
// MathPlaneSceneStore and are MathPlanePreset records, so loading one is the
// same path as loading a built-in demo (LoadDemo). Built-in names are refused:
// the demos stay read-only.

using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using FracturingFog.Models;
using ReactiveUI;

namespace FracturingFog.UI.Avalonia.ViewModels;

public sealed partial class MathPlaneSectionViewModel
{
    private MathPlaneSceneStore? _sceneStore;
    private MathPlaneSceneStore SceneStore => _sceneStore ??= MathPlaneSceneStore.Instance;

    /// <summary>The user's saved scenes, in save order.</summary>
    public ObservableCollection<MathPlanePreset> MyScenes { get; } = new();

    public ReactiveCommand<Unit, Unit> SaveMySceneCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> LoadMySceneCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> RenameMySceneCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> DeleteMySceneCommand { get; private set; } = null!;
    // #1694 — share scenes as files.
    public ReactiveCommand<Unit, Unit> ExportMySceneCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ExportAllMyScenesCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ImportMyScenesCommand { get; private set; } = null!;

    /// <summary>#1694 — the host's save picker: (title, suggested file name) -> path or null.</summary>
    public Func<string, string, Task<string?>>? PickSaveFile { get; set; }

    /// <summary>#1694 — the host's open picker: title -> path or null.</summary>
    public Func<string, Task<string?>>? PickOpenFile { get; set; }

    private string? _mySceneStatus;
    /// <summary>#1694 — the last import / export result (neutral text).</summary>
    public string? MySceneStatus
    {
        get => _mySceneStatus;
        private set => this.RaiseAndSetIfChanged(ref _mySceneStatus, value);
    }

    private void InitMyScenes()
    {
        SaveMySceneCommand = ReactiveCommand.Create(() => { SaveMyScene(); });
        LoadMySceneCommand = ReactiveCommand.Create(() => { if (_selectedMyScene is { } s) LoadDemo(s); });
        RenameMySceneCommand = ReactiveCommand.Create(() => { RenameMyScene(); });
        DeleteMySceneCommand = ReactiveCommand.Create(() => { DeleteMyScene(); });
        ExportMySceneCommand = ReactiveCommand.CreateFromTask(() => ExportViaPickerAsync(all: false));
        ExportAllMyScenesCommand = ReactiveCommand.CreateFromTask(() => ExportViaPickerAsync(all: true));
        ImportMyScenesCommand = ReactiveCommand.CreateFromTask(ImportViaPickerAsync);
        ReloadMyScenes(null);
    }

    private MathPlanePreset? _selectedMyScene;
    public MathPlanePreset? SelectedMyScene
    {
        get => _selectedMyScene;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedMyScene, value);
            if (value != null) MySceneName = value.Name;
            this.RaisePropertyChanged(nameof(HasSelectedMyScene));
        }
    }

    public bool HasSelectedMyScene => _selectedMyScene != null;

    private string _mySceneName = "";
    /// <summary>The name to save under (Save) or rename to (Rename).</summary>
    public string MySceneName
    {
        get => _mySceneName;
        set => this.RaiseAndSetIfChanged(ref _mySceneName, value ?? "");
    }

    private string? _mySceneError;
    /// <summary>Why the last save / rename was refused (shown in yellow), or null.</summary>
    public string? MySceneError
    {
        get => _mySceneError;
        private set => this.RaiseAndSetIfChanged(ref _mySceneError, value);
    }

    /// <summary>Save the current settings under <see cref="MySceneName"/>. Saving under an
    /// existing name overwrites that scene. Returns the stored scene, or null when refused.</summary>
    public MathPlanePreset? SaveMyScene()
    {
        if (MathPlaneSceneStore.NameProblem(MySceneName) is { } why) { MySceneError = why; return null; }
        var p = _view.FractalParameters;
        var stored = SceneStore.SaveScene(MathPlanePreset.Capture(MySceneName, p, _view.CenterX, -_view.CenterY, _view.Zoom));
        MySceneError = null;
        ReloadMyScenes(stored?.Name);
        return stored;
    }

    /// <summary>Rename the selected scene to <see cref="MySceneName"/>.</summary>
    public bool RenameMyScene()
    {
        if (_selectedMyScene is not { } s) { MySceneError = "Pick a saved scene to rename."; return false; }
        if (SceneStore.Rename(s.Name, MySceneName) is { } why) { MySceneError = why; return false; }
        MySceneError = null;
        ReloadMyScenes(MySceneName.Trim());
        return true;
    }

    /// <summary>Delete the selected saved scene (built-ins are not in this list).</summary>
    public bool DeleteMyScene()
    {
        if (_selectedMyScene is not { } s) return false;
        bool removed = SceneStore.Remove(s.Name);
        MySceneError = null;
        ReloadMyScenes(null);
        return removed;
    }



    private async Task ExportViaPickerAsync(bool all)
    {
        if (PickSaveFile == null) return;
        if (!all && _selectedMyScene == null) { MySceneError = "Pick a saved scene to export."; return; }
        string suggested = all ? "mathplane-scenes.json" : SafeFileName(_selectedMyScene!.Name) + ".json";
        if (await PickSaveFile(all ? "Export all saved scenes" : "Export saved scene", suggested) is not { } path) return;
        ExportMyScenesTo(path, all);
    }

    private async Task ImportViaPickerAsync()
    {
        if (PickOpenFile == null) return;
        if (await PickOpenFile("Import Math Plane scenes") is not { } path) return;
        ImportMyScenesFrom(path);
    }

    /// <summary>#1694 — write the selected scene (or all of them) to <paramref name="path"/>.</summary>
    public bool ExportMyScenesTo(string path, bool all)
    {
        var scenes = all ? SceneStore.Scenes.ToList()
                   : _selectedMyScene is { } s ? new() { s } : new System.Collections.Generic.List<MathPlanePreset>();
        if (scenes.Count == 0) { MySceneError = all ? "There are no saved scenes to export." : "Pick a saved scene to export."; return false; }
        try
        {
            MathPlaneSceneStore.ExportFile(path, scenes);
        }
        catch (Exception ex)
        {
            MySceneError = "Could not write the file: " + ex.Message;
            return false;
        }
        MySceneError = null;
        MySceneStatus = $"Exported {scenes.Count} scene{(scenes.Count == 1 ? "" : "s")} to {System.IO.Path.GetFileName(path)}.";
        return true;
    }

    /// <summary>#1694 — add the scenes in <paramref name="path"/> to My scenes (clashing names get a
    /// numbered suffix; unreadable entries are skipped).</summary>
    public MathPlaneSceneStore.ImportResult ImportMyScenesFrom(string path)
    {
        var r = SceneStore.ImportFile(path);
        if (r.Error != null) { MySceneError = r.Error; MySceneStatus = null; return r; }
        MySceneError = null;
        MySceneStatus = r.Describe();
        ReloadMyScenes(r.Names.Count > 0 ? r.Names[^1] : null);
        return r;
    }

    private static string SafeFileName(string name)
    {
        var bad = System.IO.Path.GetInvalidFileNameChars();
        var s = new string(name.Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim();
        return s.Length == 0 ? "scene" : s;
    }

    private void ReloadMyScenes(string? select)
    {
        MyScenes.Clear();
        foreach (var s in SceneStore.Scenes) MyScenes.Add(s);
        SelectedMyScene = select == null ? null
            : MyScenes.FirstOrDefault(s => s.Name.Equals(select, System.StringComparison.OrdinalIgnoreCase));
    }
}
