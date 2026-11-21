// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1993 — the Math Plane 2D trajectory statement: text round trip and errors, the drawn
// path is the fractal's own trace (same gate and same final state as the render path,
// for many random launches), the invariants hold along the recorded path (Henon-Heiles
// energy, the Jacobi constant, the magnetised hole's Hamiltonian), the recorder stays
// bounded, and the plane draws it.

using System;
using System.Linq;
using System.Threading;
using FracturingFog;
using FracturingFog.Help;
using FracturingFog.Models;
using FracturingFog.Scattering;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1993MathPlaneTrajectoryTests
{
    private static MathPlaneObject One(string text)
    {
        Assert.True(MathPlaneScene.TryParse(text, out var scene, out var err), err?.ToString());
        return Assert.Single(scene.Objects);
    }

    /// <summary>The object as the scene parameter carries it: serialised and parsed back.</summary>
    private static MathPlaneObject ThroughText(MathPlaneObject o)
    {
        string text = new MathPlaneScene { Objects = new[] { o } }.Serialize();
        var back = One(text);
        Assert.Equal(o, back);
        return back;
    }

    [Theory]
    [InlineData("trajectory p henonheiles 0.2 0.1")]
    [InlineData("trajectory p henonheiles 0.2 0.1 radial E=0.3 tmax=50 tol=1e-10 #56B4E9 nolabel")]
    [InlineData("trajectory p henonheiles 0 0.1 section E=0.2")]
    [InlineData("trajectory p henonheiles 0.1 0 -37.5 e=0.2")]
    [InlineData("trajectory q threebody 0.9 0.2 retrograde mu=0.3 C=3.1 crash=0.02 escape=20 tmax=80")]
    [InlineData("trajectory r magbh -3 1 45 b=0.5 E=1.2 l=1 branch=-1 escape=50 tmax=500 hidden")]
    [InlineData("trajectory r magbh 4 1.6 kick b=0.5")]
    public void Statement_RoundTripsExactly(string text)
    {
        Assert.True(MathPlaneScene.TryParse(text, out var a, out var e1), e1?.ToString());
        string s = a.Serialize();
        Assert.True(MathPlaneScene.TryParse(s, out var b, out var e2), e2?.ToString());
        Assert.Equal(a, b);
        Assert.Equal(s, b.Serialize());
        Assert.Equal(MathPlaneObjectKind.Trajectory, b.Objects[0].Kind);
    }

    [Theory]
    [InlineData("trajectory p", "needs a family")]
    [InlineData("trajectory p kepler 0 0", "unknown trajectory family")]
    [InlineData("trajectory p henonheiles 0.2", "launch point")]
    [InlineData("trajectory p henonheiles 0.2 0.1 prograde", "unknown henonheiles launch")]
    [InlineData("trajectory p threebody 0.2 0.1 mu=0.5 mu=0.4", "given twice")]
    [InlineData("trajectory p threebody 0.2 0.1 E=1", "unknown threebody key")]
    [InlineData("trajectory p magbh 3 0 branch=2", "'branch' is 1")]
    [InlineData("trajectory p magbh 3 0 tmax=0", "must be above 0")]
    [InlineData("trajectory p magbh 3 0 b=x", "needs a number")]
    [InlineData("trajectory 1p henonheiles 0 0", "needs a name")]
    public void BadStatements_SayWhy(string text, string fragment)
    {
        Assert.False(MathPlaneScene.TryParse(text, out _, out var err));
        Assert.Contains(fragment, err!.Message);
    }

    // ── The path is the fractal's trace ──

    [Fact]
    public void HenonHeiles_PathEndsAtTheFractalsGate()
    {
        var rng = new Random(1993);
        int exits = 0, checkedLaunches = 0;
        for (int k = 0; k < 150; k++)
        {
            var p = new FractalParameters
            {
                HenonHeilesEnergy = 0.17 + 0.2 * rng.NextDouble(),
                HenonHeilesView = k % 3 == 0 ? HenonHeilesView.Section : HenonHeilesView.Plane,
                HenonHeilesLaunch = (HenonHeilesLaunch)rng.Next(3),
                HenonHeilesLaunchAngle = 360 * rng.NextDouble(),
                HenonHeilesMaxTime = 150,
            };
            double a = -0.9 + 1.8 * rng.NextDouble(), b = -0.8 + 1.6 * rng.NextDouble();
            var o = ThroughText(MathPlaneTrajectory.FromFamily(FractalType.HenonHeilesBasins, p, a, b)!);

            Span<double> s = stackalloc double[4];
            var render = HenonHeilesScene.Build(p).Trace(a, b, s);
            var path = ScatterPathTracer.Trace(o);
            Assert.Equal(render.Valid, path.Launched);
            if (!render.Valid) continue;
            checkedLaunches++;
            Assert.Equal(render.Exit, path.Gate);
            if (render.Exit >= 0) exits++;
            // Same final state, bit for bit: the recorded path is the render's trace.
            var st = path.States!;
            Assert.Equal(s[0], st[st.Count - 1, 0]);
            Assert.Equal(s[1], st[st.Count - 1, 1]);
            Assert.Equal(render.Time, path.Time);
        }
        Assert.True(checkedLaunches > 60, $"only {checkedLaunches} launches");
        Assert.True(exits > 20, $"only {exits} exits");
    }

    [Fact]
    public void ThreeBody_PathEndsAtTheFractalsGate()
    {
        var rng = new Random(43);
        var gates = new int[4];
        for (int k = 0; k < 120; k++)
        {
            var p = new FractalParameters
            {
                ThreeBodyMassRatio = 0.05 + 0.45 * rng.NextDouble(),
                ThreeBodyJacobi = 2.6 + 0.8 * rng.NextDouble(),
                ThreeBodyLaunch = (ThreeBodyLaunch)rng.Next(4),
                ThreeBodyLaunchAngle = 360 * rng.NextDouble(),
                ThreeBodyMaxTime = 40,
            };
            double x = -1.5 + 3 * rng.NextDouble(), y = -1.5 + 3 * rng.NextDouble();
            var o = ThroughText(MathPlaneTrajectory.FromFamily(FractalType.ThreeBodyBasins, p, x, y)!);
            Span<double> s = stackalloc double[4];
            var render = ThreeBodyScene.Build(p).Trace(x, y, s);
            var path = ScatterPathTracer.Trace(o);
            Assert.Equal(render.Valid, path.Launched);
            if (!render.Valid) continue;
            Assert.Equal(render.Gate, path.Gate);
            gates[render.Gate + 1]++;
            var st = path.States!;
            if (st.Count > 1)
            {
                Assert.Equal(s[0], st[st.Count - 1, 0]);
                Assert.Equal(s[1], st[st.Count - 1, 1]);
            }
        }
        Assert.True(gates.Count(g => g > 0) >= 3, string.Join(",", gates));
    }

    [Fact]
    public void ThreeBody_LagrangeViewHasNoTrajectory()
        => Assert.Null(MathPlaneTrajectory.FromFamily(FractalType.ThreeBodyBasins,
               new FractalParameters { ThreeBodyView = ThreeBodyView.LagrangeBasins }, 0.1, 0.1));

    [Fact]
    public void MagnetisedHole_PathEndsAtTheFractalsGate()
    {
        var rng = new Random(1944);
        var gates = new int[4];
        for (int k = 0; k < 80; k++)
        {
            bool kick = k % 4 == 0;
            var p = new FractalParameters
            {
                MagBhView = kick ? MagnetisedBhView.Kick : MagnetisedBhView.Meridional,
                MagBhField = 0.1 + 0.6 * rng.NextDouble(),
                MagBhBranch = rng.Next(2) == 0 ? MagnetisedBhBranch.Plus : MagnetisedBhBranch.Minus,
                MagBhEnergy = 1.0 + 0.6 * rng.NextDouble(),
                MagBhAngularMomentum = -2 + 4 * rng.NextDouble(),
                MagBhLaunchAngle = 360 * rng.NextDouble(),
                MagBhEscapeHeight = 30,
                MagBhMaxTime = 300,
            };
            double a = kick ? 1.5 + 6 * rng.NextDouble() : -6 + 12 * rng.NextDouble();
            double c = kick ? 0.8 + 1.0 * rng.NextDouble() : -5 + 10 * rng.NextDouble();
            var o = ThroughText(MathPlaneTrajectory.FromFamily(FractalType.MagnetisedBlackHole, p, a, c)!);
            Span<double> s = stackalloc double[4];
            var render = MagnetisedBhScene.Build(p).Trace(a, c, s);
            var path = ScatterPathTracer.Trace(o);
            Assert.Equal(render.Valid, path.Launched);
            if (!render.Valid) continue;
            Assert.Equal(render.Gate, path.Gate);
            gates[render.Gate + 1]++;
            var st = path.States!;
            Assert.Equal(s[0], st[st.Count - 1, 0]);
            Assert.Equal(s[1], st[st.Count - 1, 1]);
            // Drawn in the meridional plane: the last vertex is (R, z) of the final state.
            var (R, z) = path.Points[^1];
            Assert.Equal(s[0] * Math.Cos(s[1]), z, 12);
            Assert.Equal(s[0] * Math.Sin(s[1]), Math.Abs(R), 12);
            if (!kick) Assert.True(R * a >= 0, "the path stays on the launch side of the axis");
        }
        Assert.True(gates.Count(g => g > 0) >= 3, string.Join(",", gates));
    }

    // ── Invariants along the recorded path ──

    [Fact]
    public void HenonHeiles_EnergyHoldsAlongThePath()
    {
        var path = ScatterPathTracer.Trace(One("trajectory p henonheiles 0 0 60 E=0.18 tmax=200 tol=1e-10"));
        Assert.Equal(-1, path.Gate);   // bound: below the escape energy... at 0.18 still inside here
        var st = path.States!;
        Assert.True(st.Count > 1000);
        double worst = 0;
        Span<double> s = stackalloc double[4];
        for (int i = 0; i < st.Count; i++)
        {
            for (int k = 0; k < 4; k++) s[k] = st[i, k];
            worst = Math.Max(worst, Math.Abs(HenonHeilesSystem.Energy(s) - 0.18));
        }
        Assert.True(worst < 1e-7, $"energy drift {worst}");
    }

    [Fact]
    public void ThreeBody_JacobiConstantHoldsAlongThePath()
    {
        var path = ScatterPathTracer.Trace(One("trajectory p threebody 0.9 0.2 retrograde mu=0.5 C=3 tol=1e-11"));
        var st = path.States!;
        Assert.True(st.Count > 1000);
        double worst = 0;
        Span<double> s = stackalloc double[4];
        for (int i = 0; i < st.Count; i++)
        {
            for (int k = 0; k < 4; k++) s[k] = st[i, k];
            worst = Math.Max(worst, Math.Abs(ThreeBodySystem.Jacobi(0.5, s) - 3.0));
        }
        Assert.True(worst < 1e-7, $"Jacobi drift {worst}");
    }

    [Fact]
    public void MagnetisedHole_HamiltonianHoldsAlongThePath()
    {
        var path = ScatterPathTracer.Trace(One("trajectory p magbh 3 1 135 b=0.3 E=1.5 l=1 tol=1e-11"));
        var st = path.States!;
        Assert.True(st.Count > 100);
        double worst = 0;
        Span<double> s = stackalloc double[4];
        for (int i = 0; i < st.Count; i++)
        {
            for (int k = 0; k < 4; k++) s[k] = st[i, k];
            worst = Math.Max(worst, Math.Abs(MagnetisedBhSystem.Hamiltonian(1.5, 1, 0.3, s) + 0.5));
        }
        Assert.True(worst < 1e-7, $"H drift {worst}");
    }

    [Fact]
    public void Path_StartsAtTheLaunchPoint()
    {
        var hh = ScatterPathTracer.Trace(One("trajectory p henonheiles 0.2 0.1"));
        Assert.Equal((0.2, 0.1), hh.Points[0]);
        // Section launch (y, py) = (0.1, 0.05) starts on the line x = 0 at height y.
        var sec = ScatterPathTracer.Trace(One("trajectory p henonheiles 0.1 0.05 section E=0.2"));
        Assert.Equal((0.0, 0.1), sec.Points[0]);
        var mb = ScatterPathTracer.Trace(One("trajectory p magbh -3 1 135 b=0.3 E=1.5 l=1"));
        Assert.Equal(-3, mb.Points[0].X, 12);
        Assert.Equal(1, mb.Points[0].Y, 12);
    }

    [Fact]
    public void ForbiddenLaunch_DrawsNoPathAndSaysWhy()
    {
        var p = ScatterPathTracer.Trace(One("trajectory p threebody 0 0.6 mu=0.5 C=3"));
        Assert.False(p.Launched);
        Assert.Empty(p.Points);
        Assert.Contains("no launch", p.Fate);
    }

    // ── Recorder ──

    [Fact]
    public void Recorder_ThinsButKeepsTheEnds()
    {
        var rec = new ScatterPathRecorder(2, maxStates: 100);
        Span<double> y = stackalloc double[2];
        for (int i = 0; i <= 10007; i++) { y[0] = i; y[1] = -i; rec.Record(y); }
        rec.Finish();
        Assert.True(rec.Count <= 101, $"{rec.Count}");
        Assert.True(rec.Count >= 50);
        Assert.Equal(0, rec[0, 0]);
        Assert.Equal(10007, rec[rec.Count - 1, 0]);
        for (int i = 1; i < rec.Count; i++) Assert.True(rec[i, 0] > rec[i - 1, 0]);
    }

    [Fact]
    public void LongPath_IsBounded()
    {
        var p = ScatterPathTracer.Trace(One("trajectory p henonheiles 0 0 60 E=0.18 tmax=20000"));
        Assert.InRange(p.Points.Count, 1000, ScatterPathRecorder.DefaultMaxStates + 1);
        Assert.True(p.Time >= 20000);
    }

    // ── Drawing, editing, scripts ──

    [Fact]
    public void Plane_DrawsThePathAndCachesTheTrace()
    {
        const string scene = "axes off; grid off; ticks off; trajectory p henonheiles 0.2 0.1 E=0.25 #E69F00 nolabel";
        var calc = new MathPlaneCalculator(240, 240)
        {
            CenterX = 0, CenterY = -0.6, Zoom = 0.7,
            FractalParameters = new FractalParameters { MathPlaneScene = scene },
        };
        calc.Calculate(CancellationToken.None);
        Assert.Null(calc.LastSceneError);
        int lit = calc.ColorBuffer.Count(c => c != MathPlaneCalculator.BackgroundColor);
        Assert.True(lit > 500, $"{lit} pixels drawn");
        var o = One(scene);
        var first = calc.PathOf(o);
        calc.CenterX = 0.3;   // a pan redraws from the cache
        calc.Calculate(CancellationToken.None);
        Assert.Same(first, calc.PathOf(o));
    }

    [Fact]
    public void Editing_DragsOnlyPositionLaunches()
    {
        Assert.Equal(1, MathPlaneSceneEdit.HandleCount(One("trajectory p henonheiles 0.2 0.1")));
        Assert.Equal(0, MathPlaneSceneEdit.HandleCount(One("trajectory p henonheiles 0.2 0.1 section")));
        Assert.Equal(0, MathPlaneSceneEdit.HandleCount(One("trajectory p magbh 3 1.5 kick")));
        Assert.True(MathPlaneScene.TryParse("trajectory p henonheiles 0.2 0.1", out var sc, out _));
        var moved = MathPlaneSceneEdit.MoveHandle(sc, new MathPlaneHandle(0, 0), 0.3, -0.1);
        Assert.Equal(new[] { 0.3, -0.1 }, moved.Objects[0].Coords);
    }

    [Fact]
    public void Script_DrawsAndMovesTrajectories()
    {
        Assert.True(MathPlaneScene.TryParse("trajectory p henonheiles 0.2 0.1; step 2 draw p; step 1 move p 0.1 0", out _, out var e), e?.ToString());
        Assert.False(MathPlaneScene.TryParse("trajectory p henonheiles 0.2 0.1 section; step 1 move p 0.1 0", out _, out var e2));
        Assert.Contains("launch position", e2!.Message);
    }

    [Fact]
    public void Demos_IncludeThePaths()
    {
        var paths = MathPlanePresets.All.Where(d => d.Scene.Contains("trajectory ")).ToList();
        Assert.True(paths.Count >= 4);
        foreach (var d in paths)
        {
            Assert.True(MathPlaneScene.TryParse(d.Scene, out var sc, out var e), d.Name + ": " + e);
            foreach (var o in sc.Objects.Where(o => o.Kind == MathPlaneObjectKind.Trajectory))
                Assert.True(ScatterPathTracer.Trace(o).Launched, d.Name + " " + o.Name);
        }
    }

    [Fact]
    public void Help_DescribesTheStatement()
    {
        string help = HelpTextBundle.MathPlaneText;
        foreach (var w in new[] { "trajectory", "henonheiles", "threebody", "magbh", "section", "kick" })
            Assert.Contains(w, help);
    }
}
