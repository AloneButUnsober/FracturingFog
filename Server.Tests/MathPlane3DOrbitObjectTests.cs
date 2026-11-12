// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Server.Tests/MathPlane3DOrbitObjectTests.cs
//
// #1732 (Math Plane 3D fractal bridge B2) — the orbit of a point under a 3D fractal's own step.
// The orbit is checked against the calculators' own code, not against itself:
//   - Mandelbulb, Mandelbox, quaternion Julia / Mandelbrot: the closest the orbit passes to the
//     origin equals the calculator's orbit-trap estimator (the same loop) bit for bit;
//   - KIFS (every fold kind): the DE read off the orbit's last point equals KifsCalculator's DE.
// Then the scene text, drawing (clipped to the display ball), errors, batch and the Control Center.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using FracturingFog;
using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.Export;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using FracturingFog.UI.Avalonia.ViewModels;
using FracturingFog.ViewState;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class MathPlane3DOrbitObjectTests
{
    private const int W = 320, H = 200;

    private static MathPlaneObject Object(string stmt)
    {
        Assert.True(MathPlane3DScene.TryParse("v1; " + stmt, out var s, out var err), err?.ToString());
        return s.Objects.Single();
    }

    private static IEnumerable<(double X, double Y, double Z)> RandomPoints(int seed, int count, double r)
    {
        var rnd = new Random(seed);
        for (int k = 0; k < count; k++) yield return (rnd.NextDouble() * 2 * r - r, rnd.NextDouble() * 2 * r - r, rnd.NextDouble() * 2 * r - r);
    }

    private static double Len4((double X, double Y, double Z, double W) q) => Math.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W);

    // ── Parity with the calculators ──

    [Theory]
    [InlineData(8.0, 8)]
    [InlineData(3.0, 12)]
    public void Mandelbulb_MatchesTheCalculatorsOrbitTrap(double power, int iter)
    {
        var p = new FractalParameters { BulbPower = power, BulbIterations = iter };
        var trap = (IOrbitTrapEstimator)RaymarchMeshSampler.For(FractalType.Mandelbulb, p)!;
        foreach (var v in RandomPoints(1732, 300, 1.4))
        {
            var o = MathPlane3DOrbit.Run(FractalType.Mandelbulb, p, v);
            // The trap measures z0 .. the escaping point, or z0 .. z(iter-1) when none escapes.
            int n = o.Escaped ? o.Points.Count : Math.Min(o.Points.Count, iter);
            double minR = o.Points.Take(n).Min(q => Math.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z));
            Assert.Equal(trap.OrbitTrap(v.X, v.Y, v.Z), Math.Clamp(minR * 0.5, 0.0, 1.0));
            // Escaped: the last point is past r = 2. Not escaped: every point the loop tested is inside
            // (the final point after the last iteration is not tested, as in the calculator).
            if (o.Escaped) Assert.True(Math.Sqrt(Sq(o.Points[^1])) > 2.0);
            else Assert.All(o.Points.Take(o.Points.Count - 1), q => Assert.True(Math.Sqrt(Sq(q)) <= 2.0));
        }
    }

    [Theory]
    [InlineData(FractalType.Mandelbox)]
    [InlineData(FractalType.QuaternionJulia)]
    [InlineData(FractalType.QuaternionMandelbrot)]
    public void PostStepFamilies_MatchTheCalculatorsOrbitTrap(FractalType type)
    {
        var p = new FractalParameters();
        var trap = (IOrbitTrapEstimator)RaymarchMeshSampler.For(type, p)!;
        double bail = MathPlane3DOrbit.EscapeRadius(type, p);
        double range = RaymarchMeshSampler.SuggestedRange(type, p);
        int escaped = 0;
        foreach (var v in RandomPoints(1733, 300, range))
        {
            var o = MathPlane3DOrbit.Run(type, p, v);
            // These traps measure each point after a step, up to the escaping one.
            double minR = o.Points.Skip(1).Min(Len4);
            Assert.Equal(trap.OrbitTrap(v.X, v.Y, v.Z), Math.Clamp(minR / bail, 0.0, 1.0));
            if (o.Escaped) { escaped++; Assert.True(Len4(o.Points[^1]) > bail); }
        }
        Assert.InRange(escaped, 1, 299);   // the sample has points on both sides
    }

    [Theory]
    [InlineData(KifsFoldKind.Menger, 1.0)]
    [InlineData(KifsFoldKind.Menger, -0.6)]   // a negative z offset makes the corner mirror fire
    [InlineData(KifsFoldKind.Sierpinski, 1.0)]
    [InlineData(KifsFoldKind.Octahedron, 1.0)]
    [InlineData(KifsFoldKind.Dodecahedron, 1.0)]
    [InlineData(KifsFoldKind.MandelboxRot, 1.0)]
    public void Kifs_TheDeReadOffTheOrbit_EqualsTheCalculatorsDe(KifsFoldKind fold, double offsetZ)
    {
        var p = new FractalParameters { KifsFold = fold, KifsOffsetZ = offsetZ };
        var de = RaymarchMeshSampler.For(FractalType.Kifs, p)!;
        double scale = KifsCalculator.ResolveScale(fold, p.KifsScale);
        int iter = Math.Max(2, p.KifsIterations);
        foreach (var v in RandomPoints(1734, 200, 2.0))
        {
            var o = MathPlane3DOrbit.Run(FractalType.Kifs, p, v);
            var z = o.Points[^1];
            double r = Math.Sqrt(z.X * z.X + z.Y * z.Y + z.Z * z.Z);
            double mine = fold switch
            {
                KifsFoldKind.Menger => (r - 2.0) * Math.Pow(scale, -iter),
                KifsFoldKind.Dodecahedron => r * Math.Pow(scale, -(o.Iterations + 1)),
                KifsFoldKind.MandelboxRot => r / Math.Max(Math.Abs(o.Dr), 1e-10),
                _ => r * Math.Pow(scale, -iter),
            };
            Assert.Equal(de.Evaluate(v.X, v.Y, v.Z), mine);
        }
    }

    private static double Sq((double X, double Y, double Z, double W) q) => q.X * q.X + q.Y * q.Y + q.Z * q.Z;

    // ── Axes, settings, drawing ──

    [Fact]
    public void TheAxes_AreTheFractalObjectsRotationInverted()
    {
        foreach (var v in RandomPoints(1735, 50, 3))
        {
            var back = MathPlane3DOrbit.ToObjectAxes(MathPlane3DFractalMesher.ToSceneAxes(v));
            Assert.Equal(v, back);
        }
        // The orbit starts where the scene says (Mandelbulb: z0 = the point), in scene axes.
        var path = MathPlane3DOrbit.Build(Object("orbit o Mandelbulb 0.6 0.4 0.5"), out var err)!;
        Assert.Null(err);
        Assert.Equal((0.6, 0.4, 0.5), path.Points[0]);
        // Quaternion Mandelbrot: q0 = 0, q1 = c = the point.
        var qm = MathPlane3DOrbit.Build(Object("orbit q QuaternionMandelbrot 0.2 -0.3 0.1"), out _)!;
        Assert.Equal((0.0, 0.0, 0.0), qm.Points[0]);
        var q1 = qm.Points[1];
        Assert.Equal(0.2, q1.X, 12); Assert.Equal(-0.3, q1.Y, 12); Assert.Equal(0.1, q1.Z, 12);
    }

    [Fact]
    public void SettingsAndSteps_ChangeTheOrbit_AndBadKeysAreErrors()
    {
        var a = MathPlane3DOrbit.Build(Object("orbit o Mandelbulb 0.3 0.2 0.1"), out _)!;
        var b = MathPlane3DOrbit.Build(Object("orbit o Mandelbulb 0.3 0.2 0.1 BulbPower=3"), out _)!;
        Assert.NotEqual(a.Points[2], b.Points[2]);
        var capped = MathPlane3DOrbit.Build(Object("orbit o Mandelbox 0.1 0.2 0.3 steps 3"), out _)!;
        Assert.True(capped.Points.Count <= 4);
        var full = MathPlane3DOrbit.Build(Object("orbit o Mandelbox 0.1 0.2 0.3 MandelboxIterations=30"), out _)!;
        Assert.True(full.Points.Count > 4);

        var bad = Object("orbit o Mandelbulb 0 0 0 BulbPowr=3");
        Assert.Contains("orbit o: Unknown --param key 'BulbPowr'", MathPlane3DOrbit.Validate(bad));
        var calc = new MathPlane3DCalculator(W, H) { Zoom = 1, FractalParameters = new FractalParameters { MathPlane3DScene = "v1; orbit o Mandelbulb 0 0 0 BulbPowr=3" } };
        calc.Calculate(CancellationToken.None);
        Assert.StartsWith("orbit o: Unknown --param key", calc.LastSceneError!.ToString());
    }

    [Fact]
    public void TheDrawnOrbit_StaysInTheDisplayBall_AndAnEscapeIsMarked()
    {
        // A Mandelbulb point that escapes: its last step can reach |z| ~ 2^8, but nothing drawn is past r = 2.
        var path = MathPlane3DOrbit.Build(Object("orbit o Mandelbulb 0.6 0.4 0.5"), out _)!;
        Assert.True(path.Escaped);
        Assert.Equal(2.0, path.DisplayRadius);
        Assert.True(path.Points.Max(q => Math.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z)) > 2.0);
        Assert.All(path.Pieces.SelectMany(x => x), q => Assert.True(Math.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z) <= 2.0 + 1e-9));
        // The last drawn point is where the orbit crosses the ball.
        var e = path.Pieces[^1][^1];
        Assert.Equal(2.0, Math.Sqrt(e.X * e.X + e.Y * e.Y + e.Z * e.Z), 9);

        // The origin stays (z = 0 + 0 = 0) and never escapes.
        var still = MathPlane3DOrbit.Build(Object("orbit o Mandelbulb 0 0 0"), out _)!;
        Assert.False(still.Escaped);
    }

    [Fact]
    public void Clip_KeepsTheChordOfASegmentThatPassesThroughTheBall()
    {
        var pieces = MathPlane3DOrbit.Clip(new[] { (-3.0, 0.0, 0.0), (3.0, 0.0, 0.0), (3.0, 3.0, 0.0), (0.5, 0.0, 0.0) }, 1.0);
        Assert.Equal(2, pieces.Count);
        Assert.Equal(new[] { (-1.0, 0.0, 0.0), (1.0, 0.0, 0.0) }, pieces[0]);
        Assert.Equal((0.5, 0.0, 0.0), pieces[1][^1]);
        Assert.Equal(1.0, Math.Sqrt(pieces[1][0].Item1 * pieces[1][0].Item1 + pieces[1][0].Item2 * pieces[1][0].Item2), 9);
    }

    [Fact]
    public void AnOrbit_Renders_AndFollowsTheMap()
    {
        var p = new FractalParameters
        {
            MathPlane3DScene = "v1; axes off; grid off; orbit o Mandelbox 0.4 0.3 0.2 #E69F00",
            MathPlane3DCameraDistance = 6, MathPlane3DCameraTheta = 0.7, MathPlane3DCameraPhi = 1.1,
        };
        var calc = new MathPlane3DCalculator(W, H) { Zoom = 1, FractalParameters = p };
        calc.Calculate(CancellationToken.None);
        Assert.Null(calc.LastSceneError);
        var plain = (uint[])calc.ColorBuffer.Clone();
        Assert.Contains(plain, c => c != MathPlane3DCalculator.BackgroundColor);
        p.MathPlane3DMapOn = true; p.MathPlane3DM11 = 0.5; p.MathPlane3DM12 = 0; p.MathPlane3DT = 1;
        calc.Calculate(CancellationToken.None);
        Assert.NotEqual(plain, calc.ColorBuffer);
    }

    // ── Scene text ──

    [Fact]
    public void TheStatement_ParsesAndRoundTrips()
    {
        const string text = "v1; orbit Box mandelbox 0.25 -0.5 1 steps 20 MandelboxScale=-1.5 #56B4E980 nolabel";
        Assert.True(MathPlane3DScene.TryParse(text, out var s, out var err), err?.ToString());
        var o = s.Objects.Single();
        Assert.True(MathPlane3DScene.IsOrbit(o));
        Assert.Equal(new[] { 0.25, -0.5, 1.0 }, o.Coords);
        Assert.Equal(20, o.Mesh);
        Assert.Equal(new[] { "Mandelbox", "MandelboxScale=-1.5" }, o.Expressions);
        string round = s.Serialize();
        Assert.Equal("v1; orbit Box Mandelbox 0.25 -0.5 1 steps 20 MandelboxScale=-1.5 #56B4E980 nolabel", round);
        Assert.True(MathPlane3DScene.TryParse(round, out var s2, out _));
        Assert.Equal(s, s2);
    }

    [Theory]
    [InlineData("orbit o", "needs a type")]
    [InlineData("orbit o Kleinian 0 0 0", "needs a type")]
    [InlineData("orbit o Mandelbulb 0 0", "start point x y z")]
    [InlineData("orbit o Mandelbulb 0 0 0 steps 0", "'steps' takes")]
    [InlineData("orbit o Mandelbulb 0 0 0 steps 1001", "'steps' takes")]
    [InlineData("orbit o Mandelbulb 0 0 0 Cam3DPhi=1", "view setting")]
    [InlineData("orbit o Mandelbulb 0 0 0 mesh 10", "unknown option 'mesh'")]
    public void BadStatements_SayWhy(string stmt, string fragment)
    {
        Assert.False(MathPlane3DScene.TryParse("v1; " + stmt, out _, out var err));
        Assert.Contains(fragment, err!.Message);
    }

    [Fact]
    public void TheCommandBuilder_RoundTripsAnOrbitScene()
    {
        var p = new FractalParameters
        {
            MathPlane3DScene = "v1; orbit o Kifs 0.3 0.2 0.1 steps 6 KifsFold=1; fractal F Kifs KifsFold=1 mesh 16 #9A9A9A60",
            MathPlane3DCameraDistance = 7, MathPlane3DCameraTheta = 0.7, MathPlane3DCameraPhi = 1.1,
        };
        var snap = RegionFractalParams.Snapshot(FractalType.MathPlane3D, p)!;
        var report = BatchCommandBuilder.BuildWithReport(new BatchCommandSnapshot
        {
            Fractal = FractalType.MathPlane3D, CenterX = 0, CenterY = 0, Zoom = 1, Parameters = p, FamilyParams = snap.ToKeyValues(),
        });
        Assert.Empty(report.Gaps);
        Assert.True(BatchOptions.TryParse(report.Args.ToArray(), 0, out var o, out var perr), perr);
        var fresh = new FractalParameters();
        RegionFractalParams.FromKeyValues(o.Params, FractalType.MathPlane3D, out var ferr)!.ApplyTo(fresh);
        Assert.Null(ferr);
        MathPlane3DCalculator R(FractalParameters q) { var c = new MathPlane3DCalculator(W, H) { Zoom = 1, FractalParameters = q }; c.Calculate(CancellationToken.None); return c; }
        var a = R(p); var b = R(fresh);
        Assert.Null(a.LastSceneError);
        Assert.Equal(a.ColorBuffer, b.ColorBuffer);
    }

    // ── Control Center ──

    [Fact]
    public void PlusOrbit_AddsAMandelbulbOrbit_WhoseStartIsADraggableHandle()
    {
        var view = new FractalViewState { FractalType = FractalType.MathPlane3D, Zoom = 1 };
        view.FractalParameters.MathPlane3DScene = "v1";
        var vm = new MathPlane3DSectionViewModel(view, () => { }, () => { });
        vm.AddOrbitCommand.Execute().Subscribe();
        Assert.True(vm.SelectedIsOrbit);
        Assert.True(vm.SelectedIsFractal);
        Assert.True(vm.HasCoords);
        Assert.False(vm.HasExpression1);
        Assert.Equal(MathPlane3DScene.OrbitTypes.Select(t => t.ToString()), vm.FractalTypeChoices);
        Assert.Equal("Mandelbulb", vm.SelectedFractalType);
        Assert.Contains("orbit o1 Mandelbulb 0.6 0.4 0.5", view.FractalParameters.MathPlane3DScene);

        vm.SelectedSteps = 5;
        vm.SelectedFractalType = "QuaternionJulia";
        vm.SelectedCoords = "0.1 0.2 0.3";
        Assert.Null(vm.EditError);
        Assert.Contains("orbit o1 QuaternionJulia 0.1 0.2 0.3 steps 5", view.FractalParameters.MathPlane3DScene);
        string before = view.FractalParameters.MathPlane3DScene;
        vm.SelectedCoords = "1 2";
        Assert.NotNull(vm.EditError);
        Assert.Equal(before, view.FractalParameters.MathPlane3DScene);

        // The start point is a handle (as a point's is) and dragging writes it.
        Assert.True(MathPlane3DScene.TryParse(before, out var s, out _));
        Assert.Equal(1, MathPlane3DSceneEdit.HandleCount(s.Objects[0]));
        var moved = MathPlane3DSceneEdit.MoveHandle(s, new MathPlane3DHandle(0, 0), 0.4, 0.5, 0.6);
        Assert.Equal(new[] { 0.4, 0.5, 0.6 }, moved.Objects[0].Coords);
        Assert.Contains("orbit o1: QuaternionJulia from (0.1, 0.2, 0.3)", vm.Rows.Single());

        // Switching to a fractal surface shows the mesh types again.
        vm.AddFractalCommand.Execute().Subscribe();
        Assert.False(vm.SelectedIsOrbit);
        Assert.Equal(MathPlane3DScene.FractalTypes.Select(t => t.ToString()), vm.FractalTypeChoices);
    }
}
