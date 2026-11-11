// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// UI.Avalonia/ViewModels/MathPlane3DSectionViewModel.cs
//
// #1716 (Math Plane 3D T4, #1556) — the Control Center "Math Plane 3D" section and
// its on-canvas editor. Design: Docs/Technical/MathPlane3D-DesignPlan.md §3.6, §4.
// Like the 2D section, every edit is an ordinary param change: the scene text
// (FractalParameters.MathPlane3DScene), the map params (MathPlane3DMapOn / M11..M33 /
// T / Interpolation) and the camera (MathPlane3DCamera*), so regions, --batch and the
// Command builder follow. Edits are checked by re-parsing the scene they produce, so
// the errors are the scene parser's (shown in yellow, #FFCC00).
//
// On the render surface (IPointerEditHandler, behind the shell's handler chain):
// left-drag a handle (point, vector tip / tail, segment end, polyline vertex) to move
// it in the plane facing the camera (Shift snaps to the grid, Alt keeps the nearest
// axis); left-drag anywhere else orbits the camera; double-click selects a handle;
// Delete removes the selected object. The wheel zooms as in every 3D view.
//
// Owned by ShellViewModel so canvas editing works before the Control Center opens.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reactive;
using Avalonia.Threading;
using FracturingFog.Input;
using FracturingFog.Models;
using FracturingFog.ViewState;
using ReactiveUI;

namespace FracturingFog.UI.Avalonia.ViewModels;

public sealed class MathPlane3DSectionViewModel : ViewModelBase, IPointerEditHandler
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>The camera a new view (and Reset view) uses: the FractalParameters defaults.</summary>
    public const double DefaultDistance = 9.0, DefaultTheta = 0.9, DefaultPhi = 1.1;

    private readonly FractalViewState _view;
    private readonly Action _trigger;
    private string? _lastText;
    private MathPlane3DScene _scene = new();
    private bool _sceneValid = true;
    private bool _loadingSelection;

    /// <param name="view">The live view state (params, zoom, type).</param>
    /// <param name="trigger">Re-render after an edit.</param>
    /// <param name="switchTo">Switch the render to Math Plane 3D (the shell's type setter).</param>
    public MathPlane3DSectionViewModel(FractalViewState view, Action trigger, Action switchTo)
    {
        _view = view ?? throw new ArgumentNullException(nameof(view));
        _trigger = trigger ?? throw new ArgumentNullException(nameof(trigger));
        SwitchCommand = ReactiveCommand.Create(() => { switchTo(); Refresh(); });
        AddPointCommand = ReactiveCommand.Create(() => Add(MathPlane3DSceneEdit.AddKind.Point));
        AddVectorCommand = ReactiveCommand.Create(() => Add(MathPlane3DSceneEdit.AddKind.Vector));
        AddSegmentCommand = ReactiveCommand.Create(() => Add(MathPlane3DSceneEdit.AddKind.Segment));
        AddPolylineCommand = ReactiveCommand.Create(() => Add(MathPlane3DSceneEdit.AddKind.Polyline));
        AddCurveCommand = ReactiveCommand.Create(() => Add(MathPlane3DSceneEdit.AddKind.Curve));
        AddSurfaceCommand = ReactiveCommand.Create(() => Add(MathPlane3DSceneEdit.AddKind.Surface));
        DeleteCommand = ReactiveCommand.Create(DeleteSelected);
        MoveUpCommand = ReactiveCommand.Create(() => MoveSelected(-1));
        MoveDownCommand = ReactiveCommand.Create(() => MoveSelected(+1));
        ApplyPresetCommand = ReactiveCommand.Create<MathPlane3DSceneEdit.MapPreset>(ApplyPreset);
        PlayCommand = ReactiveCommand.Create(TogglePlay);
        ResetViewCommand = ReactiveCommand.Create(ResetView);
        Refresh();
    }

    public ReactiveCommand<Unit, Unit> SwitchCommand { get; }
    public ReactiveCommand<Unit, Unit> AddPointCommand { get; }
    public ReactiveCommand<Unit, Unit> AddVectorCommand { get; }
    public ReactiveCommand<Unit, Unit> AddSegmentCommand { get; }
    public ReactiveCommand<Unit, Unit> AddPolylineCommand { get; }
    public ReactiveCommand<Unit, Unit> AddCurveCommand { get; }
    public ReactiveCommand<Unit, Unit> AddSurfaceCommand { get; }
    public ReactiveCommand<Unit, Unit> DeleteCommand { get; }
    public ReactiveCommand<Unit, Unit> MoveUpCommand { get; }
    public ReactiveCommand<Unit, Unit> MoveDownCommand { get; }
    public ReactiveCommand<MathPlane3DSceneEdit.MapPreset, Unit> ApplyPresetCommand { get; }
    public ReactiveCommand<Unit, Unit> PlayCommand { get; }
    public ReactiveCommand<Unit, Unit> ResetViewCommand { get; }

    private FractalParameters P => _view.FractalParameters;

    /// <summary>True while the render is Math Plane 3D (the editor is live).</summary>
    public bool IsActive => _view.FractalType == FractalType.MathPlane3D;
    public bool IsInactive => !IsActive;

    private bool _canvasEditing = true;
    /// <summary>Handle dragging on the render (off: every left-drag orbits).</summary>
    public bool CanvasEditing { get => _canvasEditing; set => this.RaiseAndSetIfChanged(ref _canvasEditing, value); }

    /// <summary>The current scene (last successful parse).</summary>
    public MathPlane3DScene Scene => _scene;

    // ── Objects ──

    public ObservableCollection<string> Rows { get; } = new();

    private int _selectedIndex = -1;
    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            if (value < -1 || value >= _scene.Objects.Count) value = -1;
            if (_selectedIndex == value) return;
            this.RaiseAndSetIfChanged(ref _selectedIndex, value);
            LoadSelection();
        }
    }
    public bool HasSelection => _selectedIndex >= 0;

    private string _selKind = "", _selName = "", _selColor = "", _selCoords = "", _selCoordsHint = "";
    private string _selExpr1 = "", _selExpr2 = "", _selExpr3 = "";
    private int _selExprCount, _selMesh;
    private bool _selVisible, _selLabel, _selIsSurface;
    private string? _editError;

    public string SelectedKind { get => _selKind; private set => this.RaiseAndSetIfChanged(ref _selKind, value); }
    public string? EditError { get => _editError; private set => this.RaiseAndSetIfChanged(ref _editError, value); }
    /// <summary>What the Numbers box holds for the selected kind.</summary>
    public string SelectedCoordsHint { get => _selCoordsHint; private set => this.RaiseAndSetIfChanged(ref _selCoordsHint, value); }

    public string SelectedName
    {
        get => _selName;
        set
        {
            this.RaiseAndSetIfChanged(ref _selName, value ?? "");
            if (_loadingSelection || !HasSelection) return;
            if (!MathPlane3DSceneEdit.IsFreeName(_scene, _selName, _selectedIndex))
            { EditError = $"'{_selName}' is not a free name (letters, digits, '_', optional trailing ')."; return; }
            ApplySelected(o => o with { Name = _selName });
        }
    }

    /// <summary>#RRGGBB, or #RRGGBBAA for a see-through object.</summary>
    public string SelectedColor
    {
        get => _selColor;
        set
        {
            this.RaiseAndSetIfChanged(ref _selColor, value ?? "");
            if (_loadingSelection || !HasSelection) return;
            string t = _selColor.Trim().TrimStart('#');
            if ((t.Length != 6 && t.Length != 8) || !uint.TryParse(t, NumberStyles.HexNumber, Inv, out uint v))
            { EditError = $"'{_selColor}' is not a #RRGGBB or #RRGGBBAA colour."; return; }
            uint argb = t.Length == 6 ? 0xFF000000u | v : ((v & 0xFF) << 24) | (v >> 8);
            ApplySelected(o => o with { Color = argb });
        }
    }

    /// <summary>The selected object's numbers: coordinates, or a curve / surface's range.</summary>
    public string SelectedCoords
    {
        get => _selCoords;
        set
        {
            this.RaiseAndSetIfChanged(ref _selCoords, value ?? "");
            if (_loadingSelection || !HasSelection) return;
            var parts = _selCoords.Split(new[] { ' ', ',', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            var nums = new double[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                if (!double.TryParse(parts[i], NumberStyles.Float, Inv, out nums[i]) || !double.IsFinite(nums[i]))
                { EditError = $"'{parts[i]}' is not a number."; return; }
            var o = _scene.Objects[_selectedIndex];
            if (o.Kind == MathPlaneObjectKind.Vector && nums.Length is not (3 or 6))
            { EditError = "A vector takes dx dy dz, or dx dy dz x0 y0 z0 (drawn from x0, y0, z0)."; return; }
            ApplySelected(x => x with { Coords = nums, HasFrom = x.Kind == MathPlaneObjectKind.Vector && nums.Length == 6 });
        }
    }

    public int SelectedExpressionCount { get => _selExprCount; private set => this.RaiseAndSetIfChanged(ref _selExprCount, value); }
    public bool HasExpression1 => _selExprCount >= 1;
    public bool HasExpression3 => _selExprCount >= 3;
    public bool SelectedIsSurface { get => _selIsSurface; private set => this.RaiseAndSetIfChanged(ref _selIsSurface, value); }

    public string SelectedExpression1 { get => _selExpr1; set => SetExpression(0, value, ref _selExpr1); }
    public string SelectedExpression2 { get => _selExpr2; set => SetExpression(1, value, ref _selExpr2); }
    public string SelectedExpression3 { get => _selExpr3; set => SetExpression(2, value, ref _selExpr3); }

    /// <summary>The expression boxes' labels for the selected kind.</summary>
    public string Expression1Label => _selExprCount == 1 ? "z = f(x, y)" : SelectedIsSurface ? "x(u, v)" : "x(t)";
    public string Expression2Label => SelectedIsSurface ? "y(u, v)" : "y(t)";
    public string Expression3Label => SelectedIsSurface ? "z(u, v)" : "z(t)";

    private void SetExpression(int k, string? value, ref string field)
    {
        string v = value ?? "";
        if (field == v) return;
        field = v;
        this.RaisePropertyChanged(k == 0 ? nameof(SelectedExpression1) : k == 1 ? nameof(SelectedExpression2) : nameof(SelectedExpression3));
        if (_loadingSelection || !HasSelection) return;
        var o = _scene.Objects[_selectedIndex];
        if (k >= o.Expressions.Count) return;
        var sources = o.Expressions.ToArray();
        sources[k] = v.Trim();
        ApplySelected(x => x with { Sources = sources });
    }

    /// <summary>A surface's cells a side (0 = the default, 64).</summary>
    public int SelectedMesh
    {
        get => _selMesh;
        set
        {
            if (_selMesh == value) return;
            this.RaiseAndSetIfChanged(ref _selMesh, value);
            if (_loadingSelection || !HasSelection) return;
            ApplySelected(o => o with { Mesh = value == MathPlane3DScene.DefaultMesh ? 0 : value });
        }
    }

    public bool SelectedVisible
    {
        get => _selVisible;
        set
        {
            this.RaiseAndSetIfChanged(ref _selVisible, value);
            if (!_loadingSelection && HasSelection) ApplySelected(o => o with { Visible = value });
        }
    }

    public bool SelectedShowLabel
    {
        get => _selLabel;
        set
        {
            this.RaiseAndSetIfChanged(ref _selLabel, value);
            if (!_loadingSelection && HasSelection) ApplySelected(o => o with { ShowLabel = value });
        }
    }

    // ── Display (scene statements) ──

    public bool ShowAxes { get => _scene.ShowAxes; set => SetScene(_scene with { ShowAxes = value }, value != _scene.ShowAxes); }
    public bool ShowTicks { get => _scene.ShowTicks; set => SetScene(_scene with { ShowTicks = value }, value != _scene.ShowTicks); }
    public bool FlatShading { get => _scene.FlatShading; set => SetScene(_scene with { FlatShading = value }, value != _scene.FlatShading); }
    public bool ShowDet { get => _scene.ShowDet; set => SetScene(_scene with { ShowDet = value }, value != _scene.ShowDet); }
    public bool ShowEigen { get => _scene.ShowEigen; set => SetScene(_scene with { ShowEigen = value }, value != _scene.ShowEigen); }
    public bool ShowLengths { get => _scene.ShowLengths; set => SetScene(_scene with { ShowLengths = value }, value != _scene.ShowLengths); }
    public bool GridXY { get => Has(_scene.Grid, MathPlane3DGrid.XY); set => SetGrid(MathPlane3DGrid.XY, value); }
    public bool GridXZ { get => Has(_scene.Grid, MathPlane3DGrid.XZ); set => SetGrid(MathPlane3DGrid.XZ, value); }
    public bool GridYZ { get => Has(_scene.Grid, MathPlane3DGrid.YZ); set => SetGrid(MathPlane3DGrid.YZ, value); }
    public bool FillXY { get => Has(_scene.Fill, MathPlane3DGrid.XY); set => SetFill(MathPlane3DGrid.XY, value); }

    private static bool Has(MathPlane3DGrid g, MathPlane3DGrid flag) => (g & flag) != 0;
    private void SetGrid(MathPlane3DGrid flag, bool on)
        => SetScene(_scene with { Grid = on ? _scene.Grid | flag : _scene.Grid & ~flag }, on != Has(_scene.Grid, flag));
    private void SetFill(MathPlane3DGrid flag, bool on)
        => SetScene(_scene with { Fill = on ? _scene.Fill | flag : _scene.Fill & ~flag }, on != Has(_scene.Fill, flag));

    private void SetScene(MathPlane3DScene scene, bool changed)
    {
        if (changed && _sceneValid) Commit(scene, keepSelection: true);
    }

    private void RaiseDisplay()
    {
        foreach (var n in new[] { nameof(ShowAxes), nameof(ShowTicks), nameof(FlatShading), nameof(ShowDet), nameof(ShowEigen),
                                  nameof(ShowLengths), nameof(GridXY), nameof(GridXZ), nameof(GridYZ), nameof(FillXY), nameof(Scene) })
            this.RaisePropertyChanged(n);
    }

    // ── Map (params) ──

    public IReadOnlyList<MathPlane3DSceneEdit.MapPreset> MapPresets => MathPlane3DSceneEdit.Presets;
    public IReadOnlyList<MathPlaneInterpolation> Interpolations { get; } =
        (MathPlaneInterpolation[])Enum.GetValues(typeof(MathPlaneInterpolation));

    private void SetParams(Action<FractalParameters> change)
    {
        change(P);
        RaiseParams();
        _trigger();
    }

    public bool MapEnabled { get => P.MathPlane3DMapOn; set { if (value != P.MathPlane3DMapOn) SetParams(p => p.MathPlane3DMapOn = value); } }
    public double M11 { get => P.MathPlane3DM11; set => SetEntry(value, v => P.MathPlane3DM11 = v); }
    public double M12 { get => P.MathPlane3DM12; set => SetEntry(value, v => P.MathPlane3DM12 = v); }
    public double M13 { get => P.MathPlane3DM13; set => SetEntry(value, v => P.MathPlane3DM13 = v); }
    public double M21 { get => P.MathPlane3DM21; set => SetEntry(value, v => P.MathPlane3DM21 = v); }
    public double M22 { get => P.MathPlane3DM22; set => SetEntry(value, v => P.MathPlane3DM22 = v); }
    public double M23 { get => P.MathPlane3DM23; set => SetEntry(value, v => P.MathPlane3DM23 = v); }
    public double M31 { get => P.MathPlane3DM31; set => SetEntry(value, v => P.MathPlane3DM31 = v); }
    public double M32 { get => P.MathPlane3DM32; set => SetEntry(value, v => P.MathPlane3DM32 = v); }
    public double M33 { get => P.MathPlane3DM33; set => SetEntry(value, v => P.MathPlane3DM33 = v); }

    private void SetEntry(double value, Action<double> set)
    {
        if (double.IsFinite(value)) SetParams(_ => set(value));
    }

    /// <summary>Map progress: 0 = identity, 1 = the full matrix.</summary>
    public double T
    {
        get => P.MathPlane3DT;
        set
        {
            double t = double.IsFinite(value) ? Math.Clamp(value, 0.0, 1.0) : 1.0;
            if (t != P.MathPlane3DT) SetParams(p => p.MathPlane3DT = t);
        }
    }

    public MathPlaneInterpolation Interpolation
    {
        get => P.MathPlane3DInterpolation;
        set { if (value != P.MathPlane3DInterpolation) SetParams(p => p.MathPlane3DInterpolation = value); }
    }

    /// <summary>The frame now drawn and its det; notes polar's fallback.</summary>
    public string MapReadout
    {
        get
        {
            var m = MathPlane3DMap.Current(P);
            string F(double v) => MathPlaneSectionViewModel.Num(Math.Abs(v) < 5e-7 ? 0 : v);
            string s = $"now [[{F(m.M11)}, {F(m.M12)}, {F(m.M13)}], [{F(m.M21)}, {F(m.M22)}, {F(m.M23)}], [{F(m.M31)}, {F(m.M32)}, {F(m.M33)}]]  det {F(m.Det)}";
            if (MathPlane3DMap.PolarFallsBack(P)) s += "  (polar needs det > 0: using linear)";
            return s;
        }
    }

    private void ApplyPreset(MathPlane3DSceneEdit.MapPreset preset)
    {
        var m = preset.M;
        SetParams(p =>
        {
            p.MathPlane3DMapOn = true;
            (p.MathPlane3DM11, p.MathPlane3DM12, p.MathPlane3DM13) = (m.M11, m.M12, m.M13);
            (p.MathPlane3DM21, p.MathPlane3DM22, p.MathPlane3DM23) = (m.M21, m.M22, m.M23);
            (p.MathPlane3DM31, p.MathPlane3DM32, p.MathPlane3DM33) = (m.M31, m.M32, m.M33);
        });
    }

    // Play / Loop, as 2D: t runs 0 -> 1 in PlaySeconds; Loop ping-pongs.
    public const double PlaySeconds = 2.0;
    private DispatcherTimer? _playTimer;
    private readonly Stopwatch _playClock = new();
    private bool _loop;
    public bool Loop { get => _loop; set => this.RaiseAndSetIfChanged(ref _loop, value); }
    public bool IsPlaying => _playTimer?.IsEnabled == true;
    public string PlayLabel => IsPlaying ? "Stop" : "Play";

    private void TogglePlay()
    {
        if (IsPlaying) { StopPlay(); return; }
        if (!MapEnabled) MapEnabled = true;
        _playClock.Restart();
        _playTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Background, (_, _) => PlayTick());
        _playTimer.Start();
        this.RaisePropertyChanged(nameof(IsPlaying));
        this.RaisePropertyChanged(nameof(PlayLabel));
    }

    private void PlayTick()
    {
        double u = _playClock.Elapsed.TotalSeconds / PlaySeconds;
        if (!Loop && u >= 1) { T = 1; StopPlay(); return; }
        double phase = u % 2;
        T = phase <= 1 ? phase : 2 - phase;
    }

    private void StopPlay()
    {
        _playTimer?.Stop();
        this.RaisePropertyChanged(nameof(IsPlaying));
        this.RaisePropertyChanged(nameof(PlayLabel));
    }

    // ── Camera ──

    /// <summary>"distance 9, θ 51.6°, φ 63°, target (0, 0, 0), zoom 1".</summary>
    public string CameraReadout
    {
        get
        {
            string N(double v) => MathPlaneSectionViewModel.Num(v);
            return $"distance {N(P.MathPlane3DCameraDistance / Math.Max(1e-9, _view.Zoom))}, θ {N(P.MathPlane3DCameraTheta * 180 / Math.PI)}°, "
                 + $"φ {N(P.MathPlane3DCameraPhi * 180 / Math.PI)}°, target ({N(P.MathPlane3DTargetX)}, {N(P.MathPlane3DTargetY)}, {N(P.MathPlane3DTargetZ)})";
        }
    }

    private void ResetView()
    {
        _view.Zoom = 1;
        SetParams(p =>
        {
            (p.MathPlane3DCameraDistance, p.MathPlane3DCameraTheta, p.MathPlane3DCameraPhi) = (DefaultDistance, DefaultTheta, DefaultPhi);
            (p.MathPlane3DTargetX, p.MathPlane3DTargetY, p.MathPlane3DTargetZ) = (0, 0, 0);
        });
    }

    private void RaiseParams()
    {
        foreach (var n in new[] { nameof(MapEnabled), nameof(M11), nameof(M12), nameof(M13), nameof(M21), nameof(M22), nameof(M23),
                                  nameof(M31), nameof(M32), nameof(M33), nameof(T), nameof(Interpolation), nameof(MapReadout), nameof(CameraReadout) })
            this.RaisePropertyChanged(n);
    }

    // ── Scene text ──

    private string _sceneText = "";
    private string? _sceneError;
    public string SceneText
    {
        get => _sceneText;
        set
        {
            this.RaiseAndSetIfChanged(ref _sceneText, value ?? "");
            P.MathPlane3DScene = _sceneText;
            _trigger();
            Refresh();
        }
    }
    public string? SceneError { get => _sceneError; private set => this.RaiseAndSetIfChanged(ref _sceneError, value); }

    private string _pointerReadout = "";
    public string PointerReadout { get => _pointerReadout; private set => this.RaiseAndSetIfChanged(ref _pointerReadout, value); }

    // ── Sync with the view state ──

    /// <summary>Re-read the params (region recall, the params window, a type switch, a
    /// keyframed camera). Cheap when the scene text did not change.</summary>
    public void Refresh()
    {
        this.RaisePropertyChanged(nameof(IsActive));
        this.RaisePropertyChanged(nameof(IsInactive));
        RaiseParams();
        string text = P.MathPlane3DScene ?? "";
        if (text == _lastText) return;
        _lastText = text;
        if (_sceneText != text) { _sceneText = text; this.RaisePropertyChanged(nameof(SceneText)); }
        _sceneValid = MathPlane3DScene.TryParse(text, out var scene, out var err);
        SceneError = _sceneValid ? null : err!.ToString();
        if (_sceneValid) LoadScene(scene, keepSelectionByName: true);
    }

    private void LoadScene(MathPlane3DScene scene, bool keepSelectionByName)
    {
        string? selName = HasSelection && _selectedIndex < _scene.Objects.Count ? _scene.Objects[_selectedIndex].Name : null;
        _scene = scene;
        Rows.Clear();
        foreach (var o in scene.Objects) Rows.Add(Describe(o));
        int idx = keepSelectionByName && selName != null ? scene.Objects.ToList().FindIndex(o => o.Name == selName) : -1;
        _selectedIndex = -2;   // force the setter to reload
        SelectedIndex = idx;
        RaiseDisplay();
    }

    /// <summary>Write <paramref name="scene"/> if it re-parses (the parser's error otherwise).</summary>
    private bool Commit(MathPlane3DScene scene, int? select = null, bool keepSelection = false)
    {
        string text = scene.Serialize();
        if (!MathPlane3DScene.TryParse(text, out var parsed, out var err))
        {
            EditError = err!.Message;
            return false;
        }
        EditError = null;
        P.MathPlane3DScene = text;
        _lastText = text;
        _sceneText = text;
        this.RaisePropertyChanged(nameof(SceneText));
        SceneError = null;
        _sceneValid = true;
        LoadScene(parsed, keepSelectionByName: keepSelection || select == null);
        if (select is int si) SelectedIndex = si;
        _trigger();
        return true;
    }

    // A light commit during a drag: param + one row.
    private void CommitDrag(MathPlane3DScene scene, int index)
    {
        string text = scene.Serialize();
        P.MathPlane3DScene = text;
        _lastText = text;
        _sceneText = text;
        this.RaisePropertyChanged(nameof(SceneText));
        _scene = scene;
        Rows[index] = Describe(scene.Objects[index]);
        if (index == _selectedIndex) LoadSelection();
        _trigger();
    }

    private void ApplySelected(Func<MathPlaneObject, MathPlaneObject> change)
    {
        if (!_sceneValid || !HasSelection) return;
        int i = _selectedIndex;
        Commit(MathPlane3DSceneEdit.Replace(_scene, i, change(_scene.Objects[i])), i);
    }

    private void LoadSelection()
    {
        _loadingSelection = true;
        try
        {
            this.RaisePropertyChanged(nameof(HasSelection));
            EditError = null;
            if (!HasSelection)
            {
                SelectedKind = ""; SelectedName = ""; SelectedColor = ""; SelectedCoords = ""; SelectedCoordsHint = "";
                SelectedVisible = false; SelectedShowLabel = false; SelectedIsSurface = false; SelectedExpressionCount = 0;
                _selExpr1 = _selExpr2 = _selExpr3 = ""; _selMesh = 0;
                RaiseExpressions();
                return;
            }
            var o = _scene.Objects[_selectedIndex];
            SelectedKind = o.Kind switch
            {
                MathPlaneObjectKind.Surface => o.CurveKind == MathPlaneCurveKind.Parametric ? "Parametric surface" : "Surface z = f(x, y)",
                MathPlaneObjectKind.Curve => "Space curve",
                _ => o.Kind.ToString(),
            };
            SelectedName = o.Name;
            uint a = o.Color >> 24;
            SelectedColor = "#" + (o.Color & 0xFFFFFF).ToString("X6", Inv) + (a != 0xFF ? a.ToString("X2", Inv) : "");
            int n = o.Kind == MathPlaneObjectKind.Vector ? (o.HasFrom ? 6 : 3) : o.Coords.Count;
            SelectedCoords = string.Join(" ", o.Coords.Take(n).Select(MathPlaneSectionViewModel.Num));
            SelectedCoordsHint = CoordsHint(o);
            SelectedVisible = o.Visible;
            SelectedShowLabel = o.ShowLabel;
            SelectedIsSurface = o.Kind == MathPlaneObjectKind.Surface;
            SelectedExpressionCount = o.Expressions.Count;
            _selExpr1 = o.Expressions.Count > 0 ? o.Expressions[0] : "";
            _selExpr2 = o.Expressions.Count > 1 ? o.Expressions[1] : "";
            _selExpr3 = o.Expressions.Count > 2 ? o.Expressions[2] : "";
            _selMesh = SelectedIsSurface ? MathPlane3DScene.MeshOf(o) : 0;
            RaiseExpressions();
        }
        finally { _loadingSelection = false; }
    }

    private void RaiseExpressions()
    {
        foreach (var n in new[] { nameof(SelectedExpression1), nameof(SelectedExpression2), nameof(SelectedExpression3), nameof(SelectedMesh),
                                  nameof(HasExpression1), nameof(HasExpression3), nameof(Expression1Label), nameof(Expression2Label), nameof(Expression3Label) })
            this.RaisePropertyChanged(n);
    }

    private static string CoordsHint(MathPlaneObject o) => o.Kind switch
    {
        MathPlaneObjectKind.Point => "x y z",
        MathPlaneObjectKind.Vector => "dx dy dz [x0 y0 z0]",
        MathPlaneObjectKind.Segment => "x1 y1 z1 x2 y2 z2",
        MathPlaneObjectKind.Polyline => "x y z for each point",
        MathPlaneObjectKind.Curve => "tmin tmax",
        MathPlaneObjectKind.Surface when o.CurveKind == MathPlaneCurveKind.Parametric => "umin umax vmin vmax",
        _ => "xmin xmax ymin ymax (empty: -3 3 -3 3)",
    };

    // ── List commands ──

    private void Add(MathPlane3DSceneEdit.AddKind kind)
    {
        if (!_sceneValid) return;
        var at = (P.MathPlane3DTargetX, P.MathPlane3DTargetY, P.MathPlane3DTargetZ);
        if (kind == MathPlane3DSceneEdit.AddKind.Point && at == (0, 0, 0)) at = (1, 1, 1);
        var (scene, idx) = MathPlane3DSceneEdit.Add(_scene, kind, at, 1);
        Commit(scene, idx);
    }

    private void DeleteSelected()
    {
        if (!_sceneValid || !HasSelection) return;
        int i = _selectedIndex;
        _selectedIndex = -1;
        Commit(MathPlane3DSceneEdit.Remove(_scene, i));
        SelectedIndex = Math.Min(i, _scene.Objects.Count - 1);
    }

    private void MoveSelected(int delta)
    {
        if (!_sceneValid || !HasSelection) return;
        int to = Math.Clamp(_selectedIndex + delta, 0, _scene.Objects.Count - 1);
        Commit(MathPlane3DSceneEdit.Reorder(_scene, _selectedIndex, delta), to);
    }

    // ── IPointerEditHandler (the render surface) ──

    private MathPlane3DHandle? _drag;
    private (double X, double Y, double Z) _dragStart;
    private bool _orbiting;
    private (int X, int Y) _orbitFrom;
    private (double Theta, double Phi) _orbitStart;

    private bool Editing => IsActive && CanvasEditing && _sceneValid;

    private MathPlane3DCamera Camera(PointerInput e)
        => MathPlane3DCamera.For(P, _view.Zoom, Math.Max(1, e.ClientWidth), Math.Max(1, e.ClientHeight));

    private Mat3 CurrentMap => MathPlane3DMap.Current(P);

    public bool OnPointerDown(PointerInput e)
    {
        if (!IsActive) return false;
        Refresh();
        if (Editing && MathPlane3DSceneEdit.HitTest(_scene, Camera(e), CurrentMap, e.X + 0.5, e.Y + 0.5) is { } h)
        {
            _drag = h;
            _dragStart = MathPlane3DSceneEdit.HandlePosition(_scene.Objects[h.ObjectIndex], h.Point);
            SelectedIndex = h.ObjectIndex;
            return true;
        }
        // Empty space: orbit (the 3D families' right-drag, on the left button here).
        _orbiting = true;
        _orbitFrom = (e.X, e.Y);
        _orbitStart = (P.MathPlane3DCameraTheta, P.MathPlane3DCameraPhi);
        return true;
    }

    public void OnPointerMove(PointerInput e)
    {
        if (_orbiting)
        {
            var (th, ph) = MathPlane3DSceneEdit.Orbit(_orbitStart.Theta, _orbitStart.Phi, e.X - _orbitFrom.X, e.Y - _orbitFrom.Y,
                                                      e.ClientWidth, e.ClientHeight);
            SetParams(p => { p.MathPlane3DCameraTheta = th; p.MathPlane3DCameraPhi = ph; });
            return;
        }
        if (_drag is not { } h || !Editing || h.ObjectIndex >= _scene.Objects.Count) return;
        bool snap = (e.Modifiers & InputModifiers.Shift) != 0, axis = (e.Modifiers & InputModifiers.Alt) != 0;
        if (MathPlane3DSceneEdit.DragTo(Camera(e), CurrentMap, _dragStart, e.X + 0.5, e.Y + 0.5, snap, axis) is not { } at) return;
        CommitDrag(MathPlane3DSceneEdit.MoveHandle(_scene, h, at.X, at.Y, at.Z), h.ObjectIndex);
        string N(double v) => MathPlaneSectionViewModel.Num(v);
        PointerReadout = $"{_scene.Objects[h.ObjectIndex].Name}: ({N(at.X)}, {N(at.Y)}, {N(at.Z)})";
    }

    public void OnPointerUp(PointerInput e) { _drag = null; _orbiting = false; }

    public bool OnDoubleClick(PointerInput e)
    {
        if (!IsActive) return false;
        Refresh();
        if (Editing && MathPlane3DSceneEdit.HitTest(_scene, Camera(e), CurrentMap, e.X + 0.5, e.Y + 0.5) is { } h)
            SelectedIndex = h.ObjectIndex;
        return true;   // no 2D recentre in a 3D view
    }

    public void OnHover(PointerInput e)
    {
        if (!IsActive || !_sceneValid) return;
        PointerReadout = MathPlane3DSceneEdit.HitTest(_scene, Camera(e), CurrentMap, e.X + 0.5, e.Y + 0.5) is { } h
            ? Describe(_scene.Objects[h.ObjectIndex]) : "";
    }

    public bool OnKey(KeyInput e)
    {
        if (!Editing || e.Key != InputKey.Delete || !HasSelection) return false;
        DeleteSelected();
        return true;
    }

    // ── Formatting ──

    public static string Describe(MathPlaneObject o)
    {
        string N(double v) => MathPlaneSectionViewModel.Num(v);
        string P3(int i) => $"({N(o.Coords[3 * i])}, {N(o.Coords[3 * i + 1])}, {N(o.Coords[3 * i + 2])})";
        string body = o.Kind switch
        {
            MathPlaneObjectKind.Point => $"point {o.Name} {P3(0)}",
            MathPlaneObjectKind.Vector => o.HasFrom ? $"vector {o.Name} {P3(0)} from {P3(1)}" : $"vector {o.Name} {P3(0)}",
            MathPlaneObjectKind.Segment => $"segment {o.Name} {P3(0)} to {P3(1)}",
            MathPlaneObjectKind.Polyline => $"polyline {o.Name} ({o.Coords.Count / 3} points)",
            MathPlaneObjectKind.Curve => $"curve {o.Name}: ({string.Join(", ", o.Expressions)})",
            MathPlaneObjectKind.Surface when o.CurveKind == MathPlaneCurveKind.Parametric => $"surface {o.Name}: ({string.Join(", ", o.Expressions)})",
            MathPlaneObjectKind.Surface => $"surface {o.Name}: z = {(o.Expressions.Count > 0 ? o.Expressions[0] : "")}",
            _ => o.Name,
        };
        return o.Visible ? body : body + "  [hidden]";
    }
}
