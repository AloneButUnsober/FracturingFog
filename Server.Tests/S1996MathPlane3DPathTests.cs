// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1996 — Math Plane 3D paths from the black hole shadow, spinning binary and Mixmaster families.
// Independent checks:
//   • the recorded path ends in the same fate as the fractal's pixel, for pixels of each family's
//     own render (the send-pixel route: MathPlaneTrajectory.PlaneAt + MathPlane3DScene.SendPath);
//   • invariants along the recorded states: H = 0 on black hole rays, constant spin lengths for the
//     binary, the Hamiltonian constraint for Mixmaster (projected back every segment);
//   • the Misner-type coordinates agree with Cornish & Levin's definitions;
//   • statements round-trip, bad ones say why, bad keys are a render-time scene error;
//   • the send helper adds to a scene of the same family and starts afresh otherwise, and names the
//     keys a statement cannot carry; the 3D plane draws a path and labels its fate; help names it.

using System;
using System.Collections.Generic;
using System.Linq;
using FracturingFog;
using FracturingFog.Help;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Scattering;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1996MathPlane3DPathTests
{
    private static MathPlaneObject One(string text)
    {
        Assert.True(MathPlane3DScene.TryParse(text, out var scene, out var err), err?.ToString());
        return Assert.Single(scene.Objects);
    }

    [Theory]
    [InlineData("geodesic g 3.1 -0.4")]
    [InlineData("geodesic g 3.1 -0.4 BlackHoleSeparation=1.5 BlackHoleMass2=0.5 #56B4E9 nolabel")]
    [InlineData("orbit3 b spinbinary 0.7 2.1 SpinBinChi1=0.9 hidden")]
    [InlineData("orbit3 m mixmaster 0.2 -0.3 MixUExit=12")]
    public void Statements_RoundTrip(string text)
    {
        Assert.True(MathPlane3DScene.TryParse(text, out var a, out var e1), e1?.ToString());
        string s = a.Serialize();
        Assert.True(MathPlane3DScene.TryParse(s, out var b, out var e2), e2?.ToString());
        Assert.Equal(a, b);
        Assert.Equal(s, b.Serialize());
        Assert.Equal(MathPlaneObjectKind.Trajectory, b.Objects[0].Kind);
    }

    [Theory]
    [InlineData("geodesic g 1", "launch point")]
    [InlineData("orbit3 b kepler 1 2", "spinbinary or mixmaster")]
    [InlineData("orbit3 b spinbinary 1 2 SpinBinChi1=0.9 SpinBinChi1=0.8", "given twice")]
    [InlineData("geodesic g 1 2 Cam3DDistance=5", "view setting")]
    public void Bad_Statements_Say_Why(string text, string fragment)
    {
        Assert.False(MathPlane3DScene.TryParse(text, out _, out var err));
        Assert.Contains(fragment, err!.Message);
    }

    [Fact]
    public void A_Bad_Key_Is_A_Render_Time_Scene_Error()
    {
        Assert.Null(ScatterPath3DTracer.Trace(One("geodesic g 1 2 BlackHoleNoSuchKey=3"), out var err));
        Assert.Contains("g:", err);
    }

    // ── Fates match the fractal's pixels (through the send route) ──

    private static void AssertPathsMatch(FractalType type, IFractalCalculator calc, Func<sbyte[]> gates, FractalParameters p,
                                         int w, int h, int minChecked)
    {
        calc.Calculate(TestContext.Current.CancellationToken);
        var g = gates();
        var keys = RegionFractalParams.Snapshot(type, p)?.ToKeyValues() ?? Array.Empty<KeyValuePair<string, string>>();
        int checkedPx = 0;
        var rng = new Random((int)type);
        for (int k = 0; k < 24; k++)
        {
            int px = rng.Next(w), py = rng.Next(h);
            var (a, b) = MathPlaneTrajectory.PlaneAt(px + 0.5, py + 0.5, w, h, calc.CenterX, calc.CenterY, calc.Zoom);
            var sent = MathPlane3DScene.SendPath(type, keys, a, b, null)!.Value;
            Assert.Empty(sent.Dropped);
            Assert.True(MathPlane3DScene.TryParse(sent.Scene, out var scene, out var err), err?.ToString());
            var path = ScatterPath3DTracer.Trace(scene.Objects.Last(), out var terr)!;
            Assert.Null(terr);
            sbyte want = g[py * w + px];
            if (!path.Launched) { Assert.True(want < -1, $"pixel ({px},{py}) gate {want} but no launch"); continue; }
            Assert.Equal(want, (sbyte)path.Gate);
            checkedPx++;
        }
        Assert.True(checkedPx >= minChecked, $"{type}: only {checkedPx} launches checked");
    }

    [Fact]
    public void BlackHole_Geodesics_End_In_The_Pixels_Fate()
    {
        var p = new FractalParameters();
        var c = new BlackHoleShadowCalculator(48, 30) { ColorMap = new HsvPalette(), FractalParameters = p, Zoom = BlackHoleShadowCalculator.DefaultZoom };
        AssertPathsMatch(FractalType.BlackHoleShadow, c, () => c.FateBuffer, p, 48, 30, 20);
    }

    [Fact]
    public void SpinningBinary_Orbits_End_In_The_Pixels_Fate()
    {
        var p = new FractalParameters { SpinBinMaxOrbits = 40 };
        var c = new SpinningBinaryCalculator(12, 8) { ColorMap = new HsvPalette(), FractalParameters = p };
        var d = MiniMapDefaults.For(FractalType.SpinningBinaryBasins);
        c.CenterX = d.CenterX; c.CenterY = d.CenterY; c.Zoom = d.Zoom;
        AssertPathsMatch(FractalType.SpinningBinaryBasins, c, () => c.GateBuffer, p, 12, 8, 15);
    }

    [Fact]
    public void Mixmaster_Paths_End_In_The_Pixels_Fate()
    {
        var p = new FractalParameters();
        var c = new MixmasterCalculator(16, 10) { ColorMap = new HsvPalette(), FractalParameters = p };
        var d = MiniMapDefaults.For(FractalType.MixmasterBasins);
        c.CenterX = d.CenterX; c.CenterY = d.CenterY; c.Zoom = d.Zoom;
        AssertPathsMatch(FractalType.MixmasterBasins, c, () => c.GateBuffer, p, 16, 10, 10);
    }

    // ── Invariants along the recorded states ──

    [Fact]
    public void BlackHole_Rays_Stay_Null_Along_The_Path()
    {
        var path = ScatterPath3DTracer.Trace(One("geodesic g 3.4 0.3"), out _)!;
        var sys = BlackHoleShadowCalculator.Scene.Build(path.Parameters).NewSystem();
        var st = path.States!;
        Assert.True(st.Count > 20);
        Span<double> s = stackalloc double[6];
        for (int i = 0; i < st.Count; i++)
        {
            for (int k = 0; k < 6; k++) s[k] = st[i, k];
            double u = sys.U(s[0], s[1], s[2]);
            Assert.True(Math.Abs(sys.Hamiltonian(s)) / (u * u) < 1e-5, $"state {i}: H = {sys.Hamiltonian(s)}");
        }
        // Holes drawn as wire spheres after the path.
        Assert.True(path.Pieces.Count >= 1 + 2 * 3);
    }

    [Fact]
    public void SpinningBinary_Spin_Lengths_Stay_Constant()
    {
        var path = ScatterPath3DTracer.Trace(One("orbit3 b spinbinary 0.9 2.2 SpinBinMaxOrbits=30"), out _)!;
        var st = path.States!;
        Assert.True(st.Count > 100);
        double L(int i, int o) => Math.Sqrt(st[i, o] * st[i, o] + st[i, o + 1] * st[i, o + 1] + st[i, o + 2] * st[i, o + 2]);
        double s1 = L(0, 6), s2 = L(0, 9);
        Assert.True(s1 > 0 && s2 > 0);
        for (int i = 0; i < st.Count; i++)
        {
            Assert.Equal(1.0, L(i, 6) / s1, 7);
            Assert.Equal(1.0, L(i, 9) / s2, 7);
        }
        // Drawn: the relative orbit, starting at the launch separation on +x.
        Assert.Equal(path.Parameters.SpinBinSeparation, path.Points[0].X, 9);
    }

    [Fact]
    public void Mixmaster_Path_Keeps_The_Constraint_And_Uses_Cornish_Levin_Coordinates()
    {
        // The first allowed launch on a grid over the default view.
        var d = MiniMapDefaults.For(FractalType.MixmasterBasins);
        ScatterPath3D? path = null;
        for (int j = 0; j < 10 && path is not { Launched: true }; j++)
            for (int i = 0; i < 16 && path is not { Launched: true }; i++)
            {
                var (a, b) = MathPlaneTrajectory.PlaneAt(i + 0.5, j + 0.5, 16, 10, d.CenterX, d.CenterY, d.Zoom);
                path = ScatterPath3DTracer.Trace(One($"orbit3 m mixmaster {a.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} {b.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}"), out _);
            }
        Assert.True(path!.Launched);
        var st = path.States!;
        Assert.True(st.Count > 50);
        Span<double> s = stackalloc double[6];
        double worst = 0;
        for (int i = 0; i < st.Count; i++)
        {
            for (int k = 0; k < 6; k++) s[k] = st[i, k];
            double kin = s[3] * s[3] + s[4] * s[4] + s[5] * s[5];
            worst = Math.Max(worst, Math.Abs(MixmasterSystem.Hamiltonian(s)) / Math.Max(1e-300, kin));
        }
        Assert.True(worst < 1e-5, $"constraint drift {worst}");
        // β+ = ⅓ ln(bc/a²), β− = (1/√3) ln(b/c), Ω = ⅓ ln(abc).
        var (bp, bm, om) = ScatterPath3DTracer.Misner(Math.Log(2), Math.Log(3), Math.Log(5));
        Assert.Equal(Math.Log(3.0 * 5 / 4) / 3, bp, 12);
        Assert.Equal(Math.Log(3.0 / 5) / Math.Sqrt(3), bm, 12);
        Assert.Equal(Math.Log(30.0) / 3, om, 12);
    }

    // ── Send helper, drawing, help ──

    [Fact]
    public void SendPath_Adds_To_A_Scene_Of_The_Same_Family_And_Names_What_It_Drops()
    {
        var keys = new List<KeyValuePair<string, string>>
        {
            new("BlackHoleSeparation", "1.5"), new("BlackHoleCustom", "0,0,1,1;0,0,-1,1"), new("Cam3DDistance", "9"),
        };
        var first = MathPlane3DScene.SendPath(FractalType.BlackHoleShadow, keys, 3, 0.5, MathPlane3DScene.DefaultText)!.Value;
        Assert.Equal(new[] { "BlackHoleCustom" }, first.Dropped);
        Assert.True(MathPlane3DScene.TryParse(first.Scene, out var s1, out _));
        Assert.Single(s1.Objects);   // the basis vectors were replaced
        var second = MathPlane3DScene.SendPath(FractalType.BlackHoleShadow, keys, 3.2, 0.5, first.Scene)!.Value;
        Assert.True(MathPlane3DScene.TryParse(second.Scene, out var s2, out _));
        Assert.Equal(2, s2.Objects.Count);
        Assert.NotEqual(s2.Objects[0].Name, s2.Objects[1].Name);
        var other = MathPlane3DScene.SendPath(FractalType.MixmasterBasins, Array.Empty<KeyValuePair<string, string>>(), 0.1, 0.1, second.Scene)!.Value;
        Assert.True(MathPlane3DScene.TryParse(other.Scene, out var s3, out _));
        Assert.Single(s3.Objects);
        Assert.Null(MathPlane3DScene.SendPath(FractalType.Mandelbrot, keys, 0, 0, null));
    }

    [Fact]
    public void The_3D_Plane_Draws_A_Path()
    {
        var p = new FractalParameters { MathPlane3DScene = "axes off; grid off; geodesic g 3.4 0.3 #E69F00; orbit3 m mixmaster 0.2 -0.3 #56B4E9" };
        var calc = new MathPlane3DCalculator(160, 120) { FractalParameters = p };
        calc.Calculate(TestContext.Current.CancellationToken);
        Assert.Null(calc.LastSceneError);
        Assert.Equal(2, calc.Paths3D.Count);
        Assert.True(calc.ColorBuffer.Distinct().Count() > 3);
    }

    [Fact]
    public void Help_Describes_The_Statements()
    {
        string help = HelpTextBundle.MathPlane3DText;
        foreach (var w in new[] { "geodesic", "orbit3", "spinbinary", "mixmaster", "press J" })
            Assert.Contains(w, help);
    }
}
