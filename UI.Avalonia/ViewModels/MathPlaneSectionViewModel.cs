// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// UI.Avalonia/ViewModels/MathPlaneSectionViewModel.cs
//
// #1549 (Math Plane S2) — the Control Center "Math Plane" section and the
// on-canvas object editor. The scene lives in FractalParameters.MathPlaneScene
// (text); every edit parses it, applies a MathPlaneSceneEdit, writes the
// serialised scene back and triggers a render — so an edit is an ordinary param
// change that the region snapshot, --batch and the Command builder follow.
//
// On the render surface (IPointerEditHandler, wired by the shell): left-drag a
// handle (point, vector tip / tail, segment end, polyline vertex) to move it,
// Shift snaps to the minor grid; double-click empty space adds a point;
// Delete removes the selected object. Presses away from any handle still pan.
//
// Owned by ShellViewModel (not the Control Center VM) so canvas editing works
// before the Control Center has been opened.

using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Numerics;
using System.Linq;
using System.Reactive;
using FracturingFog.Input;
using FracturingFog.Models;
using FracturingFog.ViewState;
using ReactiveUI;

namespace FracturingFog.UI.Avalonia.ViewModels;

public sealed partial class MathPlaneSectionViewModel : ViewModelBase, IPointerEditHandler
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly FractalViewState _view;
    private readonly Action _trigger;
    private string? _lastText;
    private MathPlaneScene _scene = new();
    private bool _sceneValid = true;
    private MathPlaneHandle? _drag;
    private bool _loadingSelection;

    /// <param name="view">The live view state (scene text, centre, zoom, type).</param>
    /// <param name="trigger">Re-render after an edit.</param>
    /// <param name="switchToMathPlane">Switch the render to Math Plane (the shell's type setter).</param>
    public MathPlaneSectionViewModel(FractalViewState view, Action trigger, Action switchToMathPlane)
    {
        _view = view ?? throw new ArgumentNullException(nameof(view));
        _trigger = trigger ?? throw new ArgumentNullException(nameof(trigger));
        SwitchToMathPlaneCommand = ReactiveCommand.Create(() => { switchToMathPlane(); Refresh(); });
        AddPointCommand = ReactiveCommand.Create(() => AddAtCentre(MathPlaneObjectKind.Point));
        AddVectorCommand = ReactiveCommand.Create(() => AddAtCentre(MathPlaneObjectKind.Vector));
        AddSegmentCommand = ReactiveCommand.Create(() => AddAtCentre(MathPlaneObjectKind.Segment));
        AddPolylineCommand = ReactiveCommand.Create(() => AddAtCentre(MathPlaneObjectKind.Polyline));
        AddFunctionCommand = ReactiveCommand.Create(() => AddCurve(MathPlaneCurveKind.Explicit));
        AddParametricCommand = ReactiveCommand.Create(() => AddCurve(MathPlaneCurveKind.Parametric));
        AddPolarCommand = ReactiveCommand.Create(() => AddCurve(MathPlaneCurveKind.Polar));
        AddImplicitCommand = ReactiveCommand.Create(() => AddCurve(MathPlaneCurveKind.Implicit));
        AddRegionCommand = ReactiveCommand.Create(AddRegion);
        DeleteCommand = ReactiveCommand.Create(DeleteSelected);
        MoveUpCommand = ReactiveCommand.Create(() => MoveSelected(-1));
        MoveDownCommand = ReactiveCommand.Create(() => MoveSelected(+1));
        InitMap();
        InitComplex();
        InitPresets();
        Refresh();
    }

    public ReactiveCommand<Unit, Unit> SwitchToMathPlaneCommand { get; }
    public ReactiveCommand<Unit, Unit> AddPointCommand { get; }
    public ReactiveCommand<Unit, Unit> AddVectorCommand { get; }
    public ReactiveCommand<Unit, Unit> AddSegmentCommand { get; }
    public ReactiveCommand<Unit, Unit> AddPolylineCommand { get; }
    public ReactiveCommand<Unit, Unit> AddFunctionCommand { get; }
    public ReactiveCommand<Unit, Unit> AddParametricCommand { get; }
    public ReactiveCommand<Unit, Unit> AddPolarCommand { get; }
    public ReactiveCommand<Unit, Unit> AddImplicitCommand { get; }
    public ReactiveCommand<Unit, Unit> AddRegionCommand { get; }
    public ReactiveCommand<Unit, Unit> DeleteCommand { get; }
    public ReactiveCommand<Unit, Unit> MoveUpCommand { get; }
    public ReactiveCommand<Unit, Unit> MoveDownCommand { get; }

    /// <summary>True while the render is the Math Plane (the editor is live).</summary>
    public bool IsActive => _view.FractalType == FractalType.MathPlane;
    public bool IsInactive => !IsActive;

    /// <summary>Canvas editing on/off (off: every press pans as usual).</summary>
    private bool _canvasEditing = true;
    public bool CanvasEditing { get => _canvasEditing; set => this.RaiseAndSetIfChanged(ref _canvasEditing, value); }

    /// <summary>The current scene (last successful parse).</summary>
    public MathPlaneScene Scene => _scene;

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

    // ── Selected object's properties ──

    private string _selName = "", _selColor = "", _selCoords = "", _selKind = "";
    private bool _selVisible, _selShowLabel;
    private string? _editError;

    public string SelectedKind { get => _selKind; private set => this.RaiseAndSetIfChanged(ref _selKind, value); }

    public string SelectedName
    {
        get => _selName;
        set
        {
            this.RaiseAndSetIfChanged(ref _selName, value ?? "");
            if (_loadingSelection || !HasSelection) return;
            if (!MathPlaneSceneEdit.IsFreeName(_scene, _selName, _selectedIndex))
            { EditError = $"'{_selName}' is not a free name (letters, digits, '_', optional trailing ')."; return; }
            ApplySelected(o => o with { Name = _selName });
        }
    }

    public string SelectedColor
    {
        get => _selColor;
        set
        {
            this.RaiseAndSetIfChanged(ref _selColor, value ?? "");
            if (_loadingSelection || !HasSelection) return;
            string t = _selColor.Trim();
            if (t.StartsWith('#')) t = t[1..];
            if (t.Length != 6 || !uint.TryParse(t, NumberStyles.HexNumber, Inv, out uint rgb))
            { EditError = $"'{_selColor}' is not a #RRGGBB colour."; return; }
            ApplySelected(o => o with { Color = 0xFF000000 | rgb });
        }
    }

    /// <summary>The selected object's numbers as "x y ..." (a vector: "dx dy [x0 y0]").</summary>
    public string SelectedCoords
    {
        get => _selCoords;
        set
        {
            this.RaiseAndSetIfChanged(ref _selCoords, value ?? "");
            if (_loadingSelection || !HasSelection) return;
            var o = _scene.Objects[_selectedIndex];
            var parts = _selCoords.Split(new[] { ' ', ',', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            var nums = new double[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                if (!double.TryParse(parts[i], NumberStyles.Float, Inv, out nums[i]) || !double.IsFinite(nums[i]))
                { EditError = $"'{parts[i]}' is not a number."; return; }
            bool ok = o.Kind switch
            {
                MathPlaneObjectKind.Point => nums.Length == 2,
                MathPlaneObjectKind.Vector => nums.Length is 2 or 4,
                MathPlaneObjectKind.Segment => nums.Length == 4,
                MathPlaneObjectKind.Curve => o.CurveKind switch
                {
                    MathPlaneCurveKind.Parametric => nums.Length == 2 && nums[1] > nums[0],
                    MathPlaneCurveKind.Implicit => nums.Length == 0,
                    _ => nums.Length == 0 || (nums.Length == 2 && nums[1] > nums[0]),
                },
                MathPlaneObjectKind.Region => nums.Length == 0,
                _ => nums.Length >= 4 && nums.Length % 2 == 0,
            };
            if (!ok) { EditError = o.Kind is MathPlaneObjectKind.Curve or MathPlaneObjectKind.Region ? DomainHint(o) : CoordsHint(o.Kind); return; }
            ApplySelected(x => x with
            {
                Coords = nums,
                HasFrom = x.Kind == MathPlaneObjectKind.Vector && nums.Length == 4,
            });
        }
    }

    // #1550 — a curve's expressions.
    private string _selExpr = "", _selExpr2 = "";
    private bool _selIsCurve, _selIsParametric;
    public bool SelectedIsCurve { get => _selIsCurve; private set => this.RaiseAndSetIfChanged(ref _selIsCurve, value); }
    public bool SelectedIsParametric { get => _selIsParametric; private set => this.RaiseAndSetIfChanged(ref _selIsParametric, value); }
    /// <summary>"Numbers" means coordinates, except for a curve: its domain.</summary>
    public string SelectedCoordsLabel => SelectedIsCurve ? "Domain" : "Numbers";

    /// <summary>The curve's expression (y = f(x); x(t) for parametric; r(theta) for polar).</summary>
    public string SelectedExpression
    {
        get => _selExpr;
        set { this.RaiseAndSetIfChanged(ref _selExpr, value ?? ""); if (!_loadingSelection) ApplyExpressions(); }
    }

    /// <summary>A parametric curve's y(t).</summary>
    public string SelectedExpression2
    {
        get => _selExpr2;
        set { this.RaiseAndSetIfChanged(ref _selExpr2, value ?? ""); if (!_loadingSelection) ApplyExpressions(); }
    }

    private void ApplyExpressions()
    {
        if (!HasSelection || !SelectedIsCurve) return;
        var o = _scene.Objects[_selectedIndex];
        var sources = o.CurveKind == MathPlaneCurveKind.Parametric
            ? new[] { _selExpr.Trim(), _selExpr2.Trim() } : new[] { _selExpr.Trim() };
        var candidate = o with { Sources = sources };
        // Validate through the scene parser, so the editor and the stored form agree.
        var test = MathPlaneSceneEdit.Replace(_scene, _selectedIndex, candidate);
        if (!MathPlaneScene.TryParse(test.Serialize(), out _, out var err))
        { EditError = err!.Message; return; }
        ApplySelected(_ => candidate);
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
        get => _selShowLabel;
        set
        {
            this.RaiseAndSetIfChanged(ref _selShowLabel, value);
            if (!_loadingSelection && HasSelection) ApplySelected(o => o with { ShowLabel = value });
        }
    }

    /// <summary>Why the last property edit was refused (yellow in the view), or null.</summary>
    public string? EditError { get => _editError; private set => this.RaiseAndSetIfChanged(ref _editError, value); }

    // ── Plane settings ──

    public bool ShowAxes
    {
        get => _scene.ShowAxes;
        set { if (value != _scene.ShowAxes) Commit(_scene with { ShowAxes = value }); }
    }
    public bool ShowGrid
    {
        get => _scene.ShowGrid;
        set { if (value != _scene.ShowGrid) Commit(_scene with { ShowGrid = value }); }
    }
    public bool ShowTicks
    {
        get => _scene.ShowTicks;
        set { if (value != _scene.ShowTicks) Commit(_scene with { ShowTicks = value }); }
    }
    public bool ShowDet
    {
        get => _scene.ShowDet;
        set { if (value != _scene.ShowDet) Commit(_scene with { ShowDet = value }); }
    }
    public bool ShowEigen
    {
        get => _scene.ShowEigen;
        set { if (value != _scene.ShowEigen) Commit(_scene with { ShowEigen = value }); }
    }
    public bool ShowLengths
    {
        get => _scene.ShowLengths;
        set { if (value != _scene.ShowLengths) Commit(_scene with { ShowLengths = value }); }
    }

    // ── Scene text (the raw form; also how a hand edit gets in) ──

    private string _sceneText = "";
    private string? _sceneError;
    public string SceneText
    {
        get => _sceneText;
        set
        {
            this.RaiseAndSetIfChanged(ref _sceneText, value ?? "");
            _view.FractalParameters.MathPlaneScene = _sceneText;
            _trigger();
            Refresh();
        }
    }
    public string? SceneError { get => _sceneError; private set => this.RaiseAndSetIfChanged(ref _sceneError, value); }

    // ── Read-outs ──

    private string _pointerReadout = "", _hoverReadout = "";
    public string PointerReadout { get => _pointerReadout; private set => this.RaiseAndSetIfChanged(ref _pointerReadout, value); }
    /// <summary>The object under the pointer: its name and, for a vector, |v| and angle.</summary>
    public string HoverReadout { get => _hoverReadout; private set => this.RaiseAndSetIfChanged(ref _hoverReadout, value); }

    // ── Sync with the view state ──

    /// <summary>Re-read the scene when the param text changed elsewhere (region
    /// recall, the params window, a type switch). Cheap when nothing changed.</summary>
    public void Refresh()
    {
        this.RaisePropertyChanged(nameof(IsActive));
        this.RaisePropertyChanged(nameof(IsInactive));
        RaiseMapProperties();   // #1552 — region recall / batch-style param edits
        string text = _view.FractalParameters.MathPlaneScene ?? "";
        if (text == _lastText) return;
        _lastText = text;
        if (_sceneText != text) { _sceneText = text; this.RaisePropertyChanged(nameof(SceneText)); }
        _sceneValid = MathPlaneScene.TryParse(text, out var scene, out var err);
        SceneError = _sceneValid ? null : err!.ToString();
        if (_sceneValid) LoadScene(scene, keepSelectionByName: true);
    }

    private void LoadScene(MathPlaneScene scene, bool keepSelectionByName)
    {
        string? selName = HasSelection && _selectedIndex < _scene.Objects.Count ? _scene.Objects[_selectedIndex].Name : null;
        _scene = scene;
        Rows.Clear();
        foreach (var o in scene.Objects) Rows.Add(Describe(o));
        int idx = keepSelectionByName && selName != null
            ? scene.Objects.ToList().FindIndex(o => o.Name == selName) : -1;
        _selectedIndex = -2;   // force the setter to reload
        SelectedIndex = idx;
        this.RaisePropertyChanged(nameof(ShowAxes));
        this.RaisePropertyChanged(nameof(ShowGrid));
        this.RaisePropertyChanged(nameof(ShowTicks));
        this.RaisePropertyChanged(nameof(ShowDet));
        this.RaisePropertyChanged(nameof(ShowEigen));
        this.RaisePropertyChanged(nameof(ShowLengths));
        this.RaisePropertyChanged(nameof(Scene));
        RaiseComplexProperties();
        RaiseBridgeProperties();
    }

    private void Commit(MathPlaneScene scene, int? select = null)
    {
        EditError = null;
        string text = scene.Serialize();
        _view.FractalParameters.MathPlaneScene = text;
        _lastText = text;
        _sceneText = text;
        this.RaisePropertyChanged(nameof(SceneText));
        SceneError = null;
        _sceneValid = true;
        string? keep = select is int s ? scene.Objects[s].Name : null;
        LoadScene(scene, keepSelectionByName: keep == null);
        if (select is int si) SelectedIndex = si;
        _trigger();
    }

    /// <summary>A light commit during a drag: param + one row, no list rebuild.</summary>
    private void CommitDrag(MathPlaneScene scene, int index)
    {
        string text = scene.Serialize();
        _view.FractalParameters.MathPlaneScene = text;
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
        Commit(MathPlaneSceneEdit.Replace(_scene, i, change(_scene.Objects[i])), i);
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
                SelectedKind = ""; SelectedName = ""; SelectedColor = ""; SelectedCoords = "";
                SelectedVisible = false; SelectedShowLabel = false;
                SelectedIsCurve = false; SelectedIsParametric = false;
                SelectedExpression = ""; SelectedExpression2 = "";
                return;
            }
            var o = _scene.Objects[_selectedIndex];
            SelectedKind = o.Kind.ToString();
            SelectedName = o.Name;
            SelectedColor = "#" + (o.Color & 0xFFFFFF).ToString("X6", Inv);
            SelectedCoords = string.Join(" ", o.Coords.Select(Num));
            SelectedVisible = o.Visible;
            SelectedShowLabel = o.ShowLabel;
            // "Curve" here means "edited by its expression": curves and regions.
            SelectedIsCurve = o.Kind is MathPlaneObjectKind.Curve or MathPlaneObjectKind.Region;
            SelectedIsParametric = SelectedIsCurve && o.CurveKind == MathPlaneCurveKind.Parametric;
            this.RaisePropertyChanged(nameof(SelectedCoordsLabel));
            SelectedExpression = o.Expressions.Count > 0 ? o.Expressions[0] : "";
            SelectedExpression2 = o.Expressions.Count > 1 ? o.Expressions[1] : "";
            if (o.Kind == MathPlaneObjectKind.Curve) SelectedKind = o.CurveKind + " curve";
        }
        finally { _loadingSelection = false; }
    }

    // ── List commands ──

    private void AddAtCentre(MathPlaneObjectKind kind)
    {
        if (!_sceneValid) return;
        double x = _view.CenterX, y = -_view.CenterY;
        if (kind == MathPlaneObjectKind.Point && x == 0 && y == 0) { x = 1; y = 1; }
        var (scene, idx) = MathPlaneSceneEdit.Add(_scene, kind, x, y, NiceSize());
        Commit(scene, idx);
    }

    private void AddRegion()
    {
        if (!_sceneValid) return;
        var (scene, idx) = MathPlaneSceneEdit.AddRegion(_scene);
        Commit(scene, idx);
    }

    private void AddCurve(MathPlaneCurveKind kind)
    {
        if (!_sceneValid) return;
        var (scene, idx) = MathPlaneSceneEdit.AddCurve(_scene, kind);
        Commit(scene, idx);
    }

    private void DeleteSelected()
    {
        if (!_sceneValid || !HasSelection) return;
        int i = _selectedIndex;
        _selectedIndex = -1;
        Commit(MathPlaneSceneEdit.Remove(_scene, i));
        SelectedIndex = Math.Min(i, _scene.Objects.Count - 1);
    }

    private void MoveSelected(int delta)
    {
        if (!_sceneValid || !HasSelection) return;
        int to = Math.Clamp(_selectedIndex + delta, 0, _scene.Objects.Count - 1);
        Commit(MathPlaneSceneEdit.Reorder(_scene, _selectedIndex, delta), to);
    }

    /// <summary>A new object's size: about an eighth of the view width, on the grid.</summary>
    private double NiceSize()
    {
        double width = 4.0 / Math.Max(1e-300, _view.Zoom);
        double step = MathPlaneSceneEdit.MinorStep(width);
        return Math.Max(step, MathPlaneSceneEdit.Snap(width / 8, step));
    }

    // ── IPointerEditHandler (the render surface) ──

    private bool Editing => IsActive && CanvasEditing && _sceneValid;

    private (double X, double Y) World(PointerInput e)
        => MathPlaneSceneEdit.ClientToWorld(e.X + 0.5, e.Y + 0.5, e.ClientWidth, e.ClientHeight,
                                            _view.CenterX, _view.CenterY, _view.Zoom);

    private double PixelPitch(PointerInput e) => MathPlaneSceneEdit.Pitch(e.ClientWidth, _view.Zoom);

    /// <summary>#1552 — the map the view draws with (identity when off).</summary>
    private Mat2 CurrentMap => MathPlaneMap.Current(_view.FractalParameters);

    /// <summary>#1552 — the scene coordinates under the pointer: the preimage of the
    /// world point under the map, snapped (Shift) in scene coordinates. Null when the
    /// map is singular (the plane is squashed; there is no unique preimage).
    /// #1553: under a complex map the preimage is found by Newton's method from
    /// <paramref name="guess"/> (the dragged handle's place), so a drag follows one
    /// branch; null when it does not converge.</summary>
    private (double X, double Y)? SceneAt(PointerInput e, (double X, double Y)? guess = null)
    {
        var (wx, wy) = World(e);
        var map = CurrentMap;
        if (CurrentComplex is { } cmap)
        {
            var g = guess ?? (wx, wy);
            if (cmap.Preimage(new Complex(wx, wy), new Complex(g.X, g.Y)) is not { } z) return null;
            (wx, wy) = (z.Real, z.Imaginary);
        }
        else if (!map.IsIdentity)
        {
            if (map.Inverse() is not { } inv) return null;
            (wx, wy) = inv.Apply(wx, wy);
        }
        if ((e.Modifiers & InputModifiers.Shift) != 0)
        {
            double step = MathPlaneSceneEdit.MinorStep(e.ClientWidth * PixelPitch(e));
            wx = MathPlaneSceneEdit.Snap(wx, step);
            wy = MathPlaneSceneEdit.Snap(wy, step);
        }
        return (wx, wy);
    }

    private MathPlaneHandle? HitAt(PointerInput e)
    {
        var (wx, wy) = World(e);
        double tol = MathPlaneSceneEdit.HitRadiusPixels * PixelPitch(e);
        if (CurrentComplex is { } cmap) return MathPlaneSceneEdit.HitTest(_scene, wx, wy, tol, cmap.Apply);
        var map = CurrentMap;
        return MathPlaneSceneEdit.HitTest(_scene, wx, wy, tol, map.IsIdentity ? null : map);
    }

    public bool OnPointerDown(PointerInput e)
    {
        if (!IsActive) return false;
        Refresh();
        if (!Editing) return false;
        // #1554 — c (drawn with the orbit) is on top of the scene's objects.
        if (HitsC(e)) { _dragC = true; return true; }
        if (HitAt(e) is not { } h) return false;
        _drag = h;
        SelectedIndex = h.ObjectIndex;
        return true;
    }

    public void OnPointerMove(PointerInput e)
    {
        if (_dragC)
        {
            if (Editing) DragCTo(e);
            return;
        }
        if (_drag is not { } h || !Editing || h.ObjectIndex >= _scene.Objects.Count) return;
        var from = MathPlaneSceneEdit.HandlePosition(_scene.Objects[h.ObjectIndex], h.Point);
        if (SceneAt(e, from) is not { } at) return;
        CommitDrag(MathPlaneSceneEdit.MoveHandle(_scene, h, at.X, at.Y), h.ObjectIndex);
        UpdateReadouts(e);
    }

    public void OnPointerUp(PointerInput e) { _drag = null; _dragC = false; }

    public bool OnDoubleClick(PointerInput e)
    {
        if (!IsActive) return false;
        Refresh();
        if (!Editing) return false;
        if (HitAt(e) is { } hit)
        {
            SelectedIndex = hit.ObjectIndex;
            return true;
        }
        if (SceneAt(e) is not { } at) return true;   // singular map: nothing to add, but no recentre
        var (scene, idx) = MathPlaneSceneEdit.Add(_scene, MathPlaneObjectKind.Point, at.X, at.Y);
        Commit(scene, idx);
        return true;
    }

    public void OnHover(PointerInput e)
    {
        if (!IsActive) return;
        UpdateReadouts(e);
    }

    public bool OnKey(KeyInput e)
    {
        if (!Editing || e.Key != InputKey.Delete || !HasSelection) return false;
        DeleteSelected();
        return true;
    }

    private void UpdateReadouts(PointerInput e)
    {
        var (wx, wy) = World(e);
        PointerReadout = $"x = {Num(wx)}, y = {Num(wy)}";
        // #1553 — f'(z) at the pointer, read as a point z of the plane (the phase
        // portrait is coloured the same way: by f at each z).
        if (CurrentComplex is { } cmap) PointerReadout += "    " + DerivativeReadout(cmap, new Complex(wx, wy));
        var hit = _sceneValid ? HitAt(e) : null;
        HoverReadout = hit is { } h ? Readout(_scene.Objects[h.ObjectIndex]) : "";
    }

    // ── Formatting ──

    /// <summary>The read-out for an object: a vector's |v| and angle, a point's
    /// position, a segment's length.</summary>
    public static string Readout(MathPlaneObject o)
    {
        switch (o.Kind)
        {
            case MathPlaneObjectKind.Vector:
            {
                double dx = o.Coords[0], dy = o.Coords[1];
                double len = Math.Sqrt(dx * dx + dy * dy);
                double deg = Math.Atan2(dy, dx) * 180.0 / Math.PI;
                return $"{o.Name}: |{o.Name}| = {Num(len)}, angle {Num(deg)}°";
            }
            case MathPlaneObjectKind.Segment:
            {
                double dx = o.Coords[2] - o.Coords[0], dy = o.Coords[3] - o.Coords[1];
                return $"{o.Name}: length {Num(Math.Sqrt(dx * dx + dy * dy))}";
            }
            case MathPlaneObjectKind.Point:
                return $"{o.Name} = ({Num(o.Coords[0])}, {Num(o.Coords[1])})";
            case MathPlaneObjectKind.Curve:
            case MathPlaneObjectKind.Region:
                return $"{o.Name}: {CurveFormula(o)}";
            default:
                return $"{o.Name}: {o.PointCount} points";
        }
    }

    public static string Describe(MathPlaneObject o)
    {
        string body = o.Kind switch
        {
            MathPlaneObjectKind.Point => $"point {o.Name} ({Num(o.Coords[0])}, {Num(o.Coords[1])})",
            MathPlaneObjectKind.Vector => o.HasFrom
                ? $"vector {o.Name} ({Num(o.Coords[0])}, {Num(o.Coords[1])}) from ({Num(o.Coords[2])}, {Num(o.Coords[3])})"
                : $"vector {o.Name} ({Num(o.Coords[0])}, {Num(o.Coords[1])})",
            MathPlaneObjectKind.Segment => $"segment {o.Name} ({Num(o.Coords[0])}, {Num(o.Coords[1])}) to ({Num(o.Coords[2])}, {Num(o.Coords[3])})",
            MathPlaneObjectKind.Curve => $"curve {o.Name}: {CurveFormula(o)}",
            MathPlaneObjectKind.Region => $"region {o.Name}: {CurveFormula(o)}",
            _ => $"polyline {o.Name} ({o.PointCount} points)",
        };
        return o.Visible ? body : body + "  [hidden]";
    }

    /// <summary>#1550 — a curve as a formula: y = sin(x), (x, y) = (cos(t), sin(t)), r = 1 + cos(theta).</summary>
    public static string CurveFormula(MathPlaneObject o)
    {
        var e = o.Expressions;
        if (o.Kind == MathPlaneObjectKind.Region) return e.Count > 0 ? e[0] : "";
        string f = o.CurveKind switch
        {
            MathPlaneCurveKind.Implicit => $"{(e.Count > 0 ? e[0] : "")} = 0",
            MathPlaneCurveKind.Explicit => $"y = {(e.Count > 0 ? e[0] : "")}",
            MathPlaneCurveKind.Parametric => $"(x, y) = ({(e.Count > 0 ? e[0] : "")}, {(e.Count > 1 ? e[1] : "")})",
            _ => $"r = {(e.Count > 0 ? e[0] : "")}",
        };
        return o.Domain is { } d ? $"{f}, {Num(d.Min)}..{Num(d.Max)}" : f;
    }

    private static string DomainHint(MathPlaneObject o) =>
        o.Kind == MathPlaneObjectKind.Region || o.CurveKind == MathPlaneCurveKind.Implicit
            ? "An implicit curve or a region has no domain: leave it empty."
            : o.CurveKind == MathPlaneCurveKind.Parametric
                ? "A parametric curve's domain is tmin tmax (max above min)."
                : "The domain is empty (whole view / 0..2pi) or min max (max above min).";

    private static string CoordsHint(MathPlaneObjectKind k) => k switch
    {
        MathPlaneObjectKind.Point => "A point takes x y.",
        MathPlaneObjectKind.Vector => "A vector takes dx dy, or dx dy x0 y0 (drawn from x0, y0).",
        MathPlaneObjectKind.Segment => "A segment takes x1 y1 x2 y2.",
        _ => "A polyline takes two or more x y points.",
    };

    /// <summary>Short display number: up to 6 significant figures, no exponent for
    /// everyday values.</summary>
    public static string Num(double v)
    {
        if (v == 0) return "0";
        double a = Math.Abs(v);
        if (a < 1e-4 || a >= 1e9) return v.ToString("G6", Inv);
        double r = Math.Round(v, Math.Clamp(5 - (int)Math.Floor(Math.Log10(Math.Abs(v))), 0, 15));
        return r.ToString("0.######", Inv);
    }
}
