// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1549 (Math Plane S2) — the object editor: the UI-free edits, and the section
// view model driven through the real FractalInputController the way the render
// surface drives it. Pixel positions are cross-checked against
// MathPlaneCalculator.ToPixel (the renderer's mapping), not the editor's own.

using System;
using System.Linq;
using FracturingFog;
using FracturingFog.Input;
using FracturingFog.Models;
using FracturingFog.UI.Avalonia.ViewModels;
using FracturingFog.ViewState;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class MathPlaneEditTests
{
    private const int W = 800, H = 600;

    private static MathPlaneScene Parse(string s)
    {
        Assert.True(MathPlaneScene.TryParse(s, out var scene, out var e), e?.ToString());
        return scene;
    }

    // ── Mapping ──

    [Theory]
    [InlineData(0.0, 0.0, 0.5)]
    [InlineData(1.25, -3.5, 2.0)]
    [InlineData(-7.0, 4.0, 0.1)]
    public void ClientMapping_MatchesTheRenderer_AndRoundTrips(double cx, double cy, double zoom)
    {
        foreach (var (wx, wy) in new[] { (0.0, 0.0), (1.0, 2.0), (-3.3, 0.7) })
        {
            var (px, py) = MathPlaneSceneEdit.WorldToClient(wx, wy, W, H, cx, cy, zoom);
            var (rx, ry) = MathPlaneCalculator.ToPixel(wx, wy, W, H, cx, cy, zoom);
            Assert.Equal(rx, px, 9);
            Assert.Equal(ry, py, 9);
            var (bx, by) = MathPlaneSceneEdit.ClientToWorld(px, py, W, H, cx, cy, zoom);
            Assert.Equal(wx, bx, 9);
            Assert.Equal(wy, by, 9);
        }
    }

    [Theory]
    [InlineData(8.0, 0.2)]     // major 1 -> minor 0.2
    [InlineData(16.0, 0.5)]    // major 2 -> minor 0.5
    [InlineData(40.0, 1.0)]    // major 5 -> minor 1
    [InlineData(0.08, 0.002)]
    public void MinorStep_MatchesTheDrawnMinorGrid(double width, double minor)
        => Assert.Equal(minor, MathPlaneSceneEdit.MinorStep(width), 12);

    [Fact]
    public void Snap_LandsOnMultiples()
    {
        Assert.Equal(1.4, MathPlaneSceneEdit.Snap(1.43, 0.2), 12);
        Assert.Equal(-0.6, MathPlaneSceneEdit.Snap(-0.55, 0.2), 12);
        Assert.Equal(3.0, MathPlaneSceneEdit.Snap(2.6, 1.0), 12);
    }

    // ── Handles ──

    [Fact]
    public void HitTest_FindsTheNearestVisibleHandle_TopMostWins()
    {
        var s = Parse("point A 1 1; vector v 2 0 from 1 1; point B 1 1 hidden; segment s 0 0 3 3");
        // (1, 1): A and v's tail coincide; v is drawn later, so it wins. Hidden B never.
        Assert.Equal(new MathPlaneHandle(1, 1), MathPlaneSceneEdit.HitTest(s, 1.01, 0.99, 0.1));
        Assert.Equal(new MathPlaneHandle(1, 0), MathPlaneSceneEdit.HitTest(s, 3.0, 1.05, 0.1));   // tip = tail + (2, 0)
        Assert.Equal(new MathPlaneHandle(3, 1), MathPlaneSceneEdit.HitTest(s, 2.98, 3.0, 0.1));
        Assert.Null(MathPlaneSceneEdit.HitTest(s, 5, 5, 0.1));
    }

    [Fact]
    public void MoveTip_ChangesComponents_MoveTail_TranslatesTheVector()
    {
        var s = Parse("vector v 2 1");
        var tip = MathPlaneSceneEdit.MoveHandle(s, new MathPlaneHandle(0, 0), 3, -1);
        Assert.Equal(new double[] { 3, -1 }, tip.Objects[0].Coords);
        Assert.False(tip.Objects[0].HasFrom);

        var tail = MathPlaneSceneEdit.MoveHandle(s, new MathPlaneHandle(0, 1), -1, 2);
        var v = tail.Objects[0];
        Assert.True(v.HasFrom);
        Assert.Equal((-1.0, 2.0), v.Tail);
        Assert.Equal(new double[] { 2, 1 }, v.Coords.Take(2));
        Assert.Equal((1.0, 3.0), MathPlaneSceneEdit.HandlePosition(v, 0));
    }

    [Fact]
    public void MoveVertex_OnlyMovesThatVertex()
    {
        var s = Parse("polyline p 0 0 1 1 2 0");
        var m = MathPlaneSceneEdit.MoveHandle(s, new MathPlaneHandle(0, 1), 5, 6);
        Assert.Equal(new double[] { 0, 0, 5, 6, 2, 0 }, m.Objects[0].Coords);
    }

    // ── List ──

    [Fact]
    public void Add_GivesFreshNames_AndTheSceneStillParses()
    {
        var s = Parse("point A 0 0; vector u 1 0");
        var (s1, i1) = MathPlaneSceneEdit.Add(s, MathPlaneObjectKind.Point, 1, 2);
        var (s2, i2) = MathPlaneSceneEdit.Add(s1, MathPlaneObjectKind.Vector, 0, 0);
        var (s3, _) = MathPlaneSceneEdit.Add(s2, MathPlaneObjectKind.Segment, 1, 1);
        Assert.Equal("B", s1.Objects[i1].Name);
        Assert.Equal("v", s2.Objects[i2].Name);
        Assert.False(s2.Objects[i2].HasFrom);
        Assert.Equal(s3.Objects.Count, s3.Objects.Select(o => o.Name).Distinct().Count());
        Assert.Equal(s3, Parse(s3.Serialize()));
    }

    [Fact]
    public void Remove_AndReorder_KeepTheOtherObjects()
    {
        var s = Parse("point A 0 0; point B 1 1; point C 2 2");
        Assert.Equal(new[] { "A", "C" }, MathPlaneSceneEdit.Remove(s, 1).Objects.Select(o => o.Name));
        Assert.Equal(new[] { "B", "A", "C" }, MathPlaneSceneEdit.Reorder(s, 0, +1).Objects.Select(o => o.Name));
        Assert.Equal(new[] { "C", "A", "B" }, MathPlaneSceneEdit.Reorder(s, 2, -5).Objects.Select(o => o.Name));
        Assert.Same(s, MathPlaneSceneEdit.Reorder(s, 2, +1));
    }

    [Fact]
    public void IsFreeName_RejectsTakenAndInvalid()
    {
        var s = Parse("point A 0 0; point B 1 1");
        Assert.True(MathPlaneSceneEdit.IsFreeName(s, "A", 0));     // its own name
        Assert.False(MathPlaneSceneEdit.IsFreeName(s, "B", 0));
        Assert.False(MathPlaneSceneEdit.IsFreeName(s, "2x", 0));
    }

    // ── The section VM through the real input controller ──

    private sealed class Rig
    {
        public readonly FractalViewState View = new() { FractalType = FractalType.MathPlane, CenterX = 0, CenterY = 0, Zoom = 0.5 };
        public readonly FractalInputController Input;
        public readonly MathPlaneSectionViewModel Vm;
        public int Renders;

        public Rig(string scene)
        {
            View.FractalParameters.MathPlaneScene = scene;
            Input = new FractalInputController(View);
            Vm = new MathPlaneSectionViewModel(View, () => Renders++, () => View.FractalType = FractalType.MathPlane);
            Input.EditHandler = Vm;
        }

        public PointerInput At(double wx, double wy, PointerButton b = PointerButton.Left, InputModifiers m = InputModifiers.None)
        {
            var (px, py) = MathPlaneCalculator.ToPixel(wx, wy, W, H, View.CenterX, View.CenterY, View.Zoom);
            return new PointerInput((int)Math.Floor(px), (int)Math.Floor(py), W, H, b, m);
        }

        public MathPlaneScene Scene => Parse(View.FractalParameters.MathPlaneScene);
    }

    [Fact]
    public void DragPoint_MovesIt_AndDoesNotPan()
    {
        var r = new Rig("point A 1 1");
        r.Input.OnPointerDown(r.At(1, 1));
        r.Input.OnPointerMove(r.At(2, -1));
        r.Input.OnPointerUp(r.At(2, -1));
        Assert.Equal(0.0, r.View.CenterX);
        Assert.Equal(0.0, r.View.CenterY);
        var a = r.Scene.Objects[0];
        // Within one pixel of the target (the pointer is whole pixels).
        double pitch = MathPlaneSceneEdit.Pitch(W, r.View.Zoom);
        Assert.InRange(a.Coords[0] - 2, -pitch, pitch);
        Assert.InRange(a.Coords[1] + 1, -pitch, pitch);
        Assert.Equal(0, r.Vm.SelectedIndex);
        Assert.True(r.Renders > 0);
    }

    [Fact]
    public void ShiftDrag_SnapsToTheMinorGrid()
    {
        var r = new Rig("point A 1 1");
        r.Input.OnPointerDown(r.At(1, 1));
        r.Input.OnPointerMove(r.At(1.53, 0.71, m: InputModifiers.Shift));
        var c = r.Scene.Objects[0].Coords;
        double step = MathPlaneSceneEdit.MinorStep(4.0 / r.View.Zoom);   // 8 wide -> 0.2
        Assert.Equal(0.2, step, 12);
        Assert.Equal(1.6, c[0], 9);
        Assert.Equal(0.8, c[1], 9);
    }

    [Fact]
    public void DragAwayFromObjects_StillPans()
    {
        var r = new Rig("point A 1 1");
        r.Input.OnPointerDown(r.At(-2, -2));
        r.Input.OnPointerMove(new PointerInput(10, 10, W, H, PointerButton.Left, InputModifiers.None));
        r.Input.OnPointerUp(new PointerInput(10, 10, W, H, PointerButton.Left, InputModifiers.None));
        Assert.NotEqual(0.0, r.View.CenterX);
        Assert.Equal(new double[] { 1, 1 }, r.Scene.Objects[0].Coords);
    }

    [Fact]
    public void DoubleClickEmpty_AddsAPoint_ThereAndSelectsIt()
    {
        var r = new Rig("point A 1 1");
        r.Input.OnPointerDoubleClick(r.At(-1.5, 2.5));
        var s = r.Scene;
        Assert.Equal(2, s.Objects.Count);
        var b = s.Objects[1];
        Assert.Equal(MathPlaneObjectKind.Point, b.Kind);
        double pitch = MathPlaneSceneEdit.Pitch(W, r.View.Zoom);
        Assert.InRange(b.Coords[0] + 1.5, -pitch, pitch);
        Assert.InRange(b.Coords[1] - 2.5, -pitch, pitch);
        Assert.Equal(1, r.Vm.SelectedIndex);
        Assert.Equal(0.0, r.View.CenterX);   // no recentre
    }

    [Fact]
    public void DeleteKey_RemovesTheSelection()
    {
        var r = new Rig("point A 1 1; point B 2 2");
        r.Vm.SelectedIndex = 0;
        Assert.True(r.Input.OnKeyDown(new KeyInput(InputKey.Delete, InputModifiers.None, W, H)));
        Assert.Equal(new[] { "B" }, r.Scene.Objects.Select(o => o.Name));
    }

    [Fact]
    public void NotMathPlane_TheEditorStaysOutOfTheWay()
    {
        var r = new Rig("point A 1 1");
        r.View.FractalType = FractalType.Mandelbrot;
        r.Vm.Refresh();
        r.Input.OnPointerDown(r.At(1, 1));
        r.Input.OnPointerMove(new PointerInput(10, 10, W, H, PointerButton.Left, InputModifiers.None));
        Assert.NotEqual(0.0, r.View.CenterX);   // panned
        Assert.Equal(new double[] { 1, 1 }, r.Scene.Objects[0].Coords);
        Assert.False(r.Input.OnKeyDown(new KeyInput(InputKey.Delete, InputModifiers.None, W, H)) && r.Scene.Objects.Count == 0);
    }

    [Fact]
    public void CanvasEditingOff_Pans()
    {
        var r = new Rig("point A 1 1") { };
        r.Vm.CanvasEditing = false;
        r.Input.OnPointerDown(r.At(1, 1));
        r.Input.OnPointerMove(new PointerInput(10, 10, W, H, PointerButton.Left, InputModifiers.None));
        Assert.NotEqual(0.0, r.View.CenterX);
    }

    [Fact]
    public void PropertyEdits_WriteTheScene_BadOnesAreRefused()
    {
        var r = new Rig("point A 1 1; vector v 1 0");
        r.Vm.SelectedIndex = 1;
        r.Vm.SelectedCoords = "2 3 -1 -1";
        r.Vm.SelectedColor = "#0072B2";
        r.Vm.SelectedName = "w";
        var v = r.Scene.Objects[1];
        Assert.Equal("w", v.Name);
        Assert.Equal(0xFF0072B2u, v.Color);
        Assert.True(v.HasFrom);
        Assert.Equal((-1.0, -1.0), v.Tail);

        r.Vm.SelectedName = "A";             // taken
        Assert.NotNull(r.Vm.EditError);
        Assert.Equal("w", r.Scene.Objects[1].Name);
        r.Vm.SelectedCoords = "1 2 3";       // wrong count
        Assert.NotNull(r.Vm.EditError);
        Assert.Equal(new double[] { 2, 3, -1, -1 }, r.Scene.Objects[1].Coords);
    }

    [Fact]
    public void ExternalSceneChange_IsPickedUp_SelectionKeptByName()
    {
        var r = new Rig("point A 1 1; point B 2 2");
        r.Vm.SelectedIndex = 1;
        r.View.FractalParameters.MathPlaneScene = "point Z 0 0; point A 1 1; point B 5 5";   // e.g. region recall
        r.Vm.Refresh();
        Assert.Equal(3, r.Vm.Rows.Count);
        Assert.Equal(2, r.Vm.SelectedIndex);
        Assert.Equal("5 5", r.Vm.SelectedCoords);
    }

    [Fact]
    public void Readout_GivesVectorLengthAndAngle()
    {
        var v = Parse("vector v 3 4").Objects[0];
        Assert.Equal("v: |v| = 5, angle 53.1301°", MathPlaneSectionViewModel.Readout(v));
    }
}
