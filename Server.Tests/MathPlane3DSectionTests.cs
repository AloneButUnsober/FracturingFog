// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1716 (Math Plane 3D T4, #1556) — the Control Center section and its canvas editor,
// driven through the real FractalInputController (behind the shell's handler chain)
// the way the render surface drives it. Drag results are checked against a pinhole
// camera written out here (not the editor's own maths): a dragged handle keeps its
// depth along the view and lands under the pointer; an orbit changes θ and φ only;
// the section follows a region recall.

using System;
using System.Linq;
using FracturingFog;
using FracturingFog.Input;
using FracturingFog.Models;
using FracturingFog.UI.Avalonia.Services;
using FracturingFog.UI.Avalonia.ViewModels;
using FracturingFog.ViewState;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class MathPlane3DSectionTests
{
    private const int W = 800, H = 600;

    private static MathPlane3DScene Parse(string s)
    {
        Assert.True(MathPlane3DScene.TryParse(s, out var scene, out var e), e?.ToString());
        return scene;
    }

    // An independent pinhole camera: z up, eye on the sphere round the target, 45° vertical FOV.
    private sealed record Pin((double X, double Y, double Z) Eye, (double X, double Y, double Z) F,
                              (double X, double Y, double Z) R, (double X, double Y, double Z) U, double Focal)
    {
        public static Pin Of(FractalParameters p, double zoom)
        {
            double d = p.MathPlane3DCameraDistance / zoom, th = p.MathPlane3DCameraTheta, ph = p.MathPlane3DCameraPhi;
            var t = (p.MathPlane3DTargetX, p.MathPlane3DTargetY, p.MathPlane3DTargetZ);
            var eye = (t.Item1 + d * Math.Sin(ph) * Math.Cos(th), t.Item2 + d * Math.Sin(ph) * Math.Sin(th), t.Item3 + d * Math.Cos(ph));
            var f = Unit((t.Item1 - eye.Item1, t.Item2 - eye.Item2, t.Item3 - eye.Item3));
            var r = Unit((f.Y * 1 - f.Z * 0, f.Z * 0 - f.X * 1, 0.0));   // f × (0, 0, 1)
            var u = (r.Y * f.Z - r.Z * f.Y, r.Z * f.X - r.X * f.Z, r.X * f.Y - r.Y * f.X);
            return new Pin(eye, f, r, u, H / 2.0 / Math.Tan(Math.PI / 8));
        }
        static (double X, double Y, double Z) Unit((double X, double Y, double Z) v)
        {
            double n = Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
            return (v.X / n, v.Y / n, v.Z / n);
        }
        static double Dot((double X, double Y, double Z) a, (double X, double Y, double Z) b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        public double Depth((double X, double Y, double Z) p) => Dot((p.X - Eye.X, p.Y - Eye.Y, p.Z - Eye.Z), F);
        public (double X, double Y) Pixel((double X, double Y, double Z) p)
        {
            var d = (p.X - Eye.X, p.Y - Eye.Y, p.Z - Eye.Z);
            double z = Dot(d, F);
            return (W / 2.0 + Focal * Dot(d, R) / z, H / 2.0 - Focal * Dot(d, U) / z);
        }
    }

    private sealed class Rig
    {
        public readonly FractalViewState View = new() { FractalType = FractalType.MathPlane3D, Zoom = 1 };
        public readonly FractalInputController Input;
        public readonly MathPlane3DSectionViewModel Vm;
        public readonly MathPlaneSectionViewModel Vm2D;
        public int Renders;

        public Rig(string scene)
        {
            View.FractalParameters.MathPlane3DScene = scene;
            View.FractalParameters.MathPlane3DCameraDistance = 8;
            View.FractalParameters.MathPlane3DCameraTheta = 0.7;
            View.FractalParameters.MathPlane3DCameraPhi = 1.0;
            Input = new FractalInputController(View);
            Vm = new MathPlane3DSectionViewModel(View, () => Renders++, () => View.FractalType = FractalType.MathPlane3D);
            Vm2D = new MathPlaneSectionViewModel(View, () => Renders++, () => View.FractalType = FractalType.MathPlane);
            Input.EditHandler = new PointerEditHandlerChain(Vm2D, Vm);   // as the shell wires it
        }

        public FractalParameters P => View.FractalParameters;
        public Pin Pin => Pin.Of(P, View.Zoom);

        public PointerInput At((double X, double Y) px, PointerButton b = PointerButton.Left, InputModifiers m = InputModifiers.None)
            => new((int)Math.Floor(px.X), (int)Math.Floor(px.Y), W, H, b, m);

        public PointerInput AtWorld((double X, double Y, double Z) w, PointerButton b = PointerButton.Left)
            => At(Pin.Pixel(w), b);

        public MathPlane3DScene Scene => Parse(P.MathPlane3DScene);
    }

    // ── Objects ──

    [Fact]
    public void TheAddButtons_AddEveryKind_WithFreshNames_AndAParsingScene()
    {
        var r = new Rig("v1");
        r.Vm.AddPointCommand.Execute().Subscribe();
        r.Vm.AddPointCommand.Execute().Subscribe();
        r.Vm.AddVectorCommand.Execute().Subscribe();
        r.Vm.AddSegmentCommand.Execute().Subscribe();
        r.Vm.AddPolylineCommand.Execute().Subscribe();
        r.Vm.AddCurveCommand.Execute().Subscribe();
        r.Vm.AddSurfaceCommand.Execute().Subscribe();
        var s = r.Scene;
        Assert.Equal(new[] { MathPlaneObjectKind.Point, MathPlaneObjectKind.Point, MathPlaneObjectKind.Vector, MathPlaneObjectKind.Segment,
                             MathPlaneObjectKind.Polyline, MathPlaneObjectKind.Curve, MathPlaneObjectKind.Surface }, s.Objects.Select(o => o.Kind));
        Assert.Equal(s.Objects.Count, s.Objects.Select(o => o.Name).Distinct().Count());
        Assert.Equal(7, r.Vm.Rows.Count);
        Assert.Equal(6, r.Vm.SelectedIndex);   // the new object is selected
        Assert.True(r.Renders >= 7);
    }

    [Fact]
    public void EditingTheSelectedObject_WritesTheScene_AndBadEditsAreReportedNotWritten()
    {
        var r = new Rig("v1; point A 1 2 3; surface S1 explicit \"x*y\"");
        r.Vm.SelectedIndex = 0;
        Assert.Equal("1 2 3", r.Vm.SelectedCoords);
        r.Vm.SelectedName = "Q";
        r.Vm.SelectedColor = "#56B4E980";
        r.Vm.SelectedCoords = "4 5 6";
        var a = r.Scene.Objects[0];
        Assert.Equal(("Q", 0x8056B4E9u), (a.Name, a.Color));
        Assert.Equal(new[] { 4.0, 5.0, 6.0 }, a.Coords);

        string before = r.P.MathPlane3DScene;
        r.Vm.SelectedCoords = "1 2";   // a point needs three numbers
        Assert.NotNull(r.Vm.EditError);
        Assert.Equal(before, r.P.MathPlane3DScene);
        r.Vm.SelectedName = "S1";      // taken
        Assert.NotNull(r.Vm.EditError);

        r.Vm.SelectedIndex = 1;
        Assert.True(r.Vm.SelectedIsSurface);
        Assert.Equal(MathPlane3DScene.DefaultMesh, r.Vm.SelectedMesh);
        r.Vm.SelectedExpression1 = "x^2 - y^2";
        r.Vm.SelectedMesh = 24;
        r.Vm.SelectedCoords = "-1 1 -2 2";
        var s = r.Scene.Objects[1];
        Assert.Equal("x^2 - y^2", s.Expressions[0]);
        Assert.Equal(24, s.Mesh);
        Assert.Equal((-1.0, 1.0, -2.0, 2.0), MathPlane3DScene.SurfaceDomain(s));
        before = r.P.MathPlane3DScene;
        r.Vm.SelectedExpression1 = "x + z";   // an explicit surface reads only x, y
        Assert.Contains("reads only x, y", r.Vm.EditError);
        Assert.Equal(before, r.P.MathPlane3DScene);
    }

    [Fact]
    public void Delete_Up_Down_AndTheDisplayToggles_EditTheScene()
    {
        var r = new Rig("v1; point A 0 0 1; point B 0 1 0; point C 1 0 0");
        r.Vm.SelectedIndex = 2;
        r.Vm.MoveUpCommand.Execute().Subscribe();
        Assert.Equal(new[] { "A", "C", "B" }, r.Scene.Objects.Select(o => o.Name));
        r.Vm.DeleteCommand.Execute().Subscribe();
        Assert.Equal(new[] { "A", "B" }, r.Scene.Objects.Select(o => o.Name));
        r.Vm.GridXZ = true;
        r.Vm.GridXY = false;
        r.Vm.ShowDet = true;
        r.Vm.FillXY = true;
        r.Vm.FlatShading = true;
        var s = r.Scene;
        Assert.Equal(MathPlane3DGrid.XZ, s.Grid);
        Assert.Equal(MathPlane3DGrid.XY, s.Fill);
        Assert.True(s.ShowDet && s.FlatShading);
    }

    [Fact]
    public void TheMapControls_WriteTheParams()
    {
        var r = new Rig("v1");
        var rotZ = MathPlane3DSceneEdit.Presets.Single(p => p.Name == "Rotate z");
        r.Vm.ApplyPresetCommand.Execute(rotZ).Subscribe();
        Assert.True(r.P.MathPlane3DMapOn);
        Assert.Equal(rotZ.M, MathPlane3DMap.Matrix(r.P));
        r.Vm.T = 0.25;
        r.Vm.Interpolation = MathPlaneInterpolation.Polar;
        r.Vm.M33 = 2;
        Assert.Equal((0.25, MathPlaneInterpolation.Polar, 2.0), (r.P.MathPlane3DT, r.P.MathPlane3DInterpolation, r.P.MathPlane3DM33));
        Assert.Contains("det", r.Vm.MapReadout);
        r.Vm.T = 7;   // clamped
        Assert.Equal(1.0, r.P.MathPlane3DT);
        // Every preset's matrix is what its name says: check two by their columns.
        var proj = MathPlane3DSceneEdit.Presets.Single(p => p.Name == "Project xy").M;
        Assert.Equal(0.0, proj.Det);
        Assert.Equal((0.0, 1.0, 0.0), rotZ.M.Column(0));   // i -> j
    }

    // ── The canvas: dragging ──

    [Fact]
    public void DraggingAPoint_KeepsItsDepthAlongTheView_AndPutsItUnderThePointer()
    {
        var r = new Rig("v1; point A 1 0.5 0.8");
        var a0 = (1.0, 0.5, 0.8);
        double depth0 = r.Pin.Depth(a0);
        var p0 = r.Pin.Pixel(a0);
        r.Input.OnPointerDown(r.At(p0));
        var target = (p0.X + 63, p0.Y - 41);
        r.Input.OnPointerMove(r.At(target));
        r.Input.OnPointerUp(r.At(target));
        var a = MathPlane3DScene.Point(r.Scene.Objects[0], 0);
        Assert.Equal(depth0, r.Pin.Depth(a), 9);
        var px = r.Pin.Pixel(a);
        Assert.Equal(Math.Floor(target.Item1) + 0.5, px.X, 6);
        Assert.Equal(Math.Floor(target.Item2) + 0.5, px.Y, 6);
        // The camera did not move.
        Assert.Equal((8.0, 0.7, 1.0), (r.P.MathPlane3DCameraDistance, r.P.MathPlane3DCameraTheta, r.P.MathPlane3DCameraPhi));
    }

    [Fact]
    public void UnderAMap_TheDrawnPointFollowsThePointer_AndTheSceneHoldsItsPreimage()
    {
        var r = new Rig("v1; point A 1 0 0");
        r.P.MathPlane3DMapOn = true;
        (r.P.MathPlane3DM11, r.P.MathPlane3DM12, r.P.MathPlane3DM13) = (1, 0.5, 0);
        (r.P.MathPlane3DM21, r.P.MathPlane3DM22, r.P.MathPlane3DM23) = (0, 1.5, 0.2);
        (r.P.MathPlane3DM31, r.P.MathPlane3DM32, r.P.MathPlane3DM33) = (0.3, 0, 1);
        var m = MathPlane3DMap.Current(r.P);
        var drawn0 = m.Apply(1, 0, 0);
        double depth0 = r.Pin.Depth(drawn0);
        var p0 = r.Pin.Pixel(drawn0);
        r.Input.OnPointerDown(r.At(p0));
        var target = (p0.X - 50, p0.Y + 30);
        r.Input.OnPointerMove(r.At(target));
        var drawn = m.Apply(MathPlane3DScene.Point(r.Scene.Objects[0], 0));
        Assert.Equal(depth0, r.Pin.Depth(drawn), 9);
        Assert.Equal(Math.Floor(target.Item1) + 0.5, r.Pin.Pixel(drawn).X, 6);
        Assert.Equal(Math.Floor(target.Item2) + 0.5, r.Pin.Pixel(drawn).Y, 6);
    }

    [Fact]
    public void DraggingAVectorTip_ChangesItsComponents_AndTheTailStays()
    {
        var r = new Rig("v1; vector v 1 1 0 from 0.5 0 0");
        var tip0 = (1.5, 1.0, 0.0);
        var p0 = r.Pin.Pixel(tip0);
        double depth0 = r.Pin.Depth(tip0);
        r.Input.OnPointerDown(r.At(p0));
        var target = (p0.X + 40, p0.Y + 10);
        r.Input.OnPointerMove(r.At(target));
        var v = r.Scene.Objects[0];
        Assert.Equal((0.5, 0.0, 0.0), MathPlane3DScene.Tail(v));
        var tip = MathPlane3DScene.Tip(v);
        Assert.Equal(depth0, r.Pin.Depth(tip), 9);
        Assert.Equal(Math.Floor(target.Item1) + 0.5, r.Pin.Pixel(tip).X, 6);

        // Dragging the tail moves the tail and keeps the tip.
        var t0 = r.Pin.Pixel((0.5, 0.0, 0.0));
        r.Input.OnPointerUp(r.At(target));
        r.Input.OnPointerDown(r.At(t0));
        r.Input.OnPointerMove(r.At((t0.X - 30, t0.Y + 20)));
        var v2 = r.Scene.Objects[0];
        var tipAfter = MathPlane3DScene.Tip(v2);
        Assert.Equal(tip.X, tipAfter.X, 12); Assert.Equal(tip.Y, tipAfter.Y, 12); Assert.Equal(tip.Z, tipAfter.Z, 12);
        Assert.NotEqual((0.5, 0.0, 0.0), MathPlane3DScene.Tail(v2));
    }

    [Fact]
    public void Shift_SnapsToTheGrid_AndAlt_KeepsOneAxis()
    {
        var r = new Rig("v1; point A 1.03 0.51 0.77");
        var a0 = (1.03, 0.51, 0.77);
        var p0 = r.Pin.Pixel(a0);
        r.Input.OnPointerDown(r.At(p0));
        r.Input.OnPointerMove(r.At((p0.X + 37, p0.Y + 23), m: InputModifiers.Shift));
        var a = MathPlane3DScene.Point(r.Scene.Objects[0], 0);
        var cam = MathPlane3DCamera.For(r.P, 1, W, H);
        double step = MathPlane3DSceneEdit.SnapStep(cam, r.Pin.Depth(a0));
        foreach (double c in new[] { a.X, a.Y, a.Z }) Assert.Equal(0.0, Math.IEEERemainder(c, step), 9);
        r.Input.OnPointerUp(r.At(p0));

        var r2 = new Rig("v1; point A 1 0.5 0.8");
        var q0 = r2.Pin.Pixel((1, 0.5, 0.8));
        r2.Input.OnPointerDown(r2.At(q0));
        r2.Input.OnPointerMove(r2.At((q0.X + 80, q0.Y + 5), m: InputModifiers.Alt));
        var b = MathPlane3DScene.Point(r2.Scene.Objects[0], 0);
        int changed = (b.X != 1 ? 1 : 0) + (b.Y != 0.5 ? 1 : 0) + (b.Z != 0.8 ? 1 : 0);
        Assert.Equal(1, changed);
    }

    [Fact]
    public void Alt_KeepsTheFirstAxisForTheWholeDrag()
    {
        // The axis is chosen once; a later swing that would favour another axis must not
        // hop the point onto that axis's line (that read as "moves freely").
        var start = (X: 1.0, Y: 0.5, Z: 0.8);
        var r = new Rig("v1; point A 1 0.5 0.8");
        var q0 = r.Pin.Pixel(start);
        r.Input.OnPointerDown(r.At(q0));
        r.Input.OnPointerMove(r.At((q0.X + 2, q0.Y), m: InputModifiers.Alt));     // inside the dead zone
        Assert.Equal(start, MathPlane3DScene.Point(r.Scene.Objects[0], 0));

        r.Input.OnPointerMove(r.At((q0.X + 80, q0.Y + 5), m: InputModifiers.Alt));
        var b = MathPlane3DScene.Point(r.Scene.Objects[0], 0);
        bool[] first = { b.X != start.X, b.Y != start.Y, b.Z != start.Z };
        Assert.Equal(1, first.Count(c => c));

        // Precondition: a free drag to the second pixel changes another coordinate most.
        var far = (q0.X + 80, q0.Y + 160);
        var free = MathPlane3DSceneEdit.DragTo(MathPlane3DCamera.For(r.P, 1, W, H), Mat3.Identity, start, far.Item1 + 0.5, far.Item2 + 0.5)!.Value;
        Assert.NotEqual(Array.IndexOf(first, true), MathPlane3DSceneEdit.DominantAxis(start, free));

        r.Input.OnPointerMove(r.At(far, m: InputModifiers.Alt));
        var c2 = MathPlane3DScene.Point(r.Scene.Objects[0], 0);
        bool[] later = { c2.X != start.X, c2.Y != start.Y, c2.Z != start.Z };
        Assert.Equal(first, later);
        Assert.Contains("only]", r.Vm.PointerReadout);

        // Releasing Alt mid-drag frees the point.
        r.Input.OnPointerMove(r.At(far));
        var d = MathPlane3DScene.Point(r.Scene.Objects[0], 0);
        Assert.True((d.X != start.X ? 1 : 0) + (d.Y != start.Y ? 1 : 0) + (d.Z != start.Z ? 1 : 0) >= 2);
    }

    // ── The canvas: orbit ──

    [Fact]
    public void ALeftDragOnEmptySpace_ChangesThetaAndPhiOnly()
    {
        var r = new Rig("v1; point A 1 1 1");
        string scene = r.P.MathPlane3DScene;
        (r.P.MathPlane3DTargetX, r.P.MathPlane3DTargetY, r.P.MathPlane3DTargetZ) = (0.5, -0.25, 0.1);
        var view0 = (r.View.CenterX, r.View.CenterY, r.View.Zoom);
        r.Input.OnPointerDown(r.At((20, 30)));
        r.Input.OnPointerMove(r.At((20 + 200, 30 + 150)));
        r.Input.OnPointerUp(r.At((220, 180)));
        Assert.Equal(0.7 + 200.0 / W * Math.PI, r.P.MathPlane3DCameraTheta, 12);
        Assert.Equal(1.0 - 150.0 / H * Math.PI, r.P.MathPlane3DCameraPhi, 12);
        Assert.Equal(8.0, r.P.MathPlane3DCameraDistance);
        Assert.Equal((0.5, -0.25, 0.1), (r.P.MathPlane3DTargetX, r.P.MathPlane3DTargetY, r.P.MathPlane3DTargetZ));
        Assert.Equal(scene, r.P.MathPlane3DScene);
        Assert.Equal(view0, (r.View.CenterX, r.View.CenterY, r.View.Zoom));   // no pan, no zoom
        // Phi stays inside (0, π).
        r.Input.OnPointerDown(r.At((20, 30)));
        r.Input.OnPointerMove(r.At((20, 30 - 5000)));
        Assert.InRange(r.P.MathPlane3DCameraPhi, 0.0, Math.PI);
    }

    [Fact]
    public void WithCanvasEditingOff_ADragOnAHandleOrbits()
    {
        var r = new Rig("v1; point A 1 0.5 0.8");
        r.Vm.CanvasEditing = false;
        var p0 = r.Pin.Pixel((1, 0.5, 0.8));
        r.Input.OnPointerDown(r.At(p0));
        r.Input.OnPointerMove(r.At((p0.X + 50, p0.Y)));
        Assert.Equal((1.0, 0.5, 0.8), MathPlane3DScene.Point(r.Scene.Objects[0], 0));
        Assert.NotEqual(0.7, r.P.MathPlane3DCameraTheta);
    }

    // ── Sync and wiring ──

    [Fact]
    public void TheSection_FollowsARegionRecall()
    {
        var r = new Rig("v1; point A 1 1 1");
        var other = new FractalParameters
        {
            MathPlane3DScene = "v1; det on; vector u 1 2 3; curve c \"cos(t)\" \"sin(t)\" \"t\" 0 6",
            MathPlane3DMapOn = true, MathPlane3DM11 = 2, MathPlane3DT = 0.5,
            MathPlane3DCameraDistance = 12, MathPlane3DCameraTheta = 0.2, MathPlane3DCameraPhi = 0.8,
        };
        RegionFractalParams.Snapshot(FractalType.MathPlane3D, other)!.ApplyTo(r.View.FractalParameters);
        r.Vm.Refresh();
        Assert.Equal(2, r.Vm.Rows.Count);
        Assert.StartsWith("vector u", r.Vm.Rows[0]);
        Assert.True(r.Vm.ShowDet);
        Assert.True(r.Vm.MapEnabled);
        Assert.Equal((2.0, 0.5), (r.Vm.M11, r.Vm.T));
        Assert.Contains("distance 12", r.Vm.CameraReadout);
        Assert.Equal(r.P.MathPlane3DScene, r.Vm.SceneText);
    }

    [Fact]
    public void ABadSceneText_IsAYellowError_AndTheListKeepsTheLastGoodScene()
    {
        var r = new Rig("v1; point A 1 1 1");
        r.Vm.SceneText = "v1; point A 1 1";
        Assert.Contains("needs 3 numbers", r.Vm.SceneError);
        Assert.Single(r.Vm.Rows);
        r.Vm.SceneText = "v1; point A 1 1 1; point B 2 2 2";
        Assert.Null(r.Vm.SceneError);
        Assert.Equal(2, r.Vm.Rows.Count);
    }

    [Fact]
    public void ResetView_RestoresTheDefaultOrbit()
    {
        var r = new Rig("v1");
        r.View.Zoom = 3;
        (r.P.MathPlane3DTargetX, r.P.MathPlane3DCameraTheta) = (4, 2);
        r.Vm.ResetViewCommand.Execute().Subscribe();
        var d = new FractalParameters();
        Assert.Equal((d.MathPlane3DCameraDistance, d.MathPlane3DCameraTheta, d.MathPlane3DCameraPhi, 0.0, 1.0),
                     (r.P.MathPlane3DCameraDistance, r.P.MathPlane3DCameraTheta, r.P.MathPlane3DCameraPhi, r.P.MathPlane3DTargetX, r.View.Zoom));
    }

    [Fact]
    public void TheTwoEditors_ShareTheInputSlot_EachOnlyInItsOwnView()
    {
        // In 3D the 2D editor claims nothing; in 2D the 3D editor claims nothing and the drag pans.
        var r = new Rig("v1; point A 1 1 1");
        r.View.FractalType = FractalType.MathPlane;
        r.View.FractalParameters.MathPlaneScene = "v1";
        r.Vm.Refresh(); r.Vm2D.Refresh();
        double theta = r.P.MathPlane3DCameraTheta;
        r.Input.OnPointerDown(new PointerInput(100, 100, W, H, PointerButton.Left, InputModifiers.None));
        r.Input.OnPointerMove(new PointerInput(160, 100, W, H, PointerButton.Left, InputModifiers.None));
        r.Input.OnPointerUp(new PointerInput(160, 100, W, H, PointerButton.Left, InputModifiers.None));
        Assert.Equal(theta, r.P.MathPlane3DCameraTheta);
        Assert.NotEqual(0.0, r.View.CenterX);   // the 2D drag panned
    }

    [Fact]
    public void TheSection_HasItsOwnNavEntryAndDetachedWindowRole()
    {
        var role = ControlCenterDetachService.RoleFor(ControlCenterSection.MathPlane3D);
        Assert.Equal(WindowRole.DetachedMathPlane3DPanel, role);
        Assert.Equal(ControlCenterSection.MathPlane3D, ControlCenterDetachService.SectionFor(role));
        // Every section has its own role.
        var roles = Enum.GetValues<ControlCenterSection>().Select(ControlCenterDetachService.RoleFor).ToList();
        Assert.Equal(roles.Count, roles.Distinct().Count());
    }
}
