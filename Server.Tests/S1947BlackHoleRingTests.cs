// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Collections.Generic;
using System.Linq;
using FracturingFog;
using FracturingFog.Abstractions.Animation;
using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.Help;
using FracturingFog.Imaging;
using FracturingFog.Models;
using FracturingFog.Rendering;
using FracturingFog.Scattering;
using Xunit;

namespace FracturingFog.Server.Tests;

// #1947 — black hole + Bach-Weyl ring (Semerák & Suková 2010). Independent checks:
//   • elliptic integrals: the AGM K, E against direct quadrature of their defining integrals (DLMF 19.2.8);
//   • ν gradients against finite differences of ν;
//   • λ: the paper's closed form for Schwarzschild (4) equals the line integral of λ's gradient (2)
//     computed in the test; the superposed λ the scene uses equals the full line integral;
//   • along orbits: the normalisation (−1 = …) holds and the carried λ equals a fresh line integral at the
//     end point (path independence);
//   • ring mass 0 is Schwarzschild, which is integrable: at every equatorial crossing of one orbit the
//     Schwarzschild total angular momentum Q = r²[(E² − (u^r)²)/(1 − 2M/r) − 1] is the same; with a ring it
//     is not (discrimination);
//   • equatorial reflection: launching downward gives the same crossings;
//   • parity: snapshot / CLI / poster / animation; help names everything.
public sealed class S1947BlackHoleRingTests
{
    private static readonly string[] Presets =
    {
        "Black Hole + Ring - Poincare Section", "Black Hole + Ring - Without the Ring", "Black Hole + Ring - Heavy Ring",
        "Black Hole + Ring - Chaotic Sea", "Black Hole + Ring - Section Density", "Black Hole + Ring - Fates",
    };
    private static readonly string[] Animations = { "Black hole + ring: ring mass", "Black hole + ring: energy" };

    private static double Quad(double a, double b, Func<double, double> f, int n = 200000)
    {
        double h = (b - a) / n, sum = 0;
        for (int i = 0; i < n; i++) sum += f(a + (i + 0.5) * h);   // midpoint rule
        return sum * h;
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.3)]
    [InlineData(0.75)]
    [InlineData(0.99)]
    public void Agm_Elliptic_Integrals_Match_Their_Definitions(double m)
    {
        var (k, e) = EllipticAgm.KE(m);
        double kq = Quad(0, Math.PI / 2, t => 1 / Math.Sqrt(1 - m * Math.Sin(t) * Math.Sin(t)));
        double eq = Quad(0, Math.PI / 2, t => Math.Sqrt(1 - m * Math.Sin(t) * Math.Sin(t)));
        Assert.Equal(kq, k, 8);
        Assert.Equal(eq, e, 8);
        if (m > 0)
        {
            double hh = 1e-6, kp = EllipticAgm.KE(m + hh).K, km = EllipticAgm.KE(m - hh).K;
            Assert.Equal((kp - km) / (2 * hh), EllipticAgm.DKdm(m, k, e), 5);
        }
    }

    [Fact]
    public void Potential_Gradients_Match_Finite_Differences()
    {
        var f = new WeylBhRing(0.5, 20);
        foreach (var (rho, z) in new[] { (3.0, 0.7), (10.0, -2.0), (18.0, 1.5), (25.0, 0.3), (0.8, 2.5) })
        {
            f.Nu(rho, z, out _, out double nr, out double nz);
            double h = 1e-6;
            f.Nu(rho + h, z, out double a, out _, out _); f.Nu(rho - h, z, out double b, out _, out _);
            f.Nu(rho, z + h, out double c, out _, out _); f.Nu(rho, z - h, out double d, out _, out _);
            Assert.Equal((a - b) / (2 * h), nr, 6);
            Assert.Equal((c - d) / (2 * h), nz, 6);
        }
    }

    // λ by integrating its gradient (2) from the axis, written in the test: z = Z leg then ρ = const leg.
    private static double LineIntegral(Func<double, double, (double Nr, double Nz)> grad, double rho, double z)
    {
        double zTop = 80;
        double h1 = Quad(0, rho, x => { var (a, c) = grad(x, zTop); return x * (a * a - c * c); }, 20000);
        double h2 = Quad(zTop, z, y => { var (a, c) = grad(rho, y); return 2 * rho * a * c; }, 20000);
        return h1 + h2;
    }

    [Fact]
    public void Lambda_Matches_The_Line_Integral_Of_Its_Gradient()
    {
        // Schwarzschild alone: the paper's closed form (4) against the integral of (2).
        var bare = new WeylBhRing(0, 20);
        (double, double) SchwGrad(double x, double y) { bare.Nu(x, y, out _, out double a, out double c); return (a, c); }
        foreach (var (rho, z) in new[] { (5.0, 0.0), (12.0, 3.0), (30.0, -4.0), (17.0, 0.5) })
            Assert.Equal(LineIntegral(SchwGrad, rho, z), WeylBhRing.LambdaSchwarzschild(rho, z), 6);
        // With the ring: the scene's λ against a plain test-side integral of the full gradient, along
        // a different path height (path independence too). Away from the ring only.
        var full = new WeylBhRing(0.5, 20);
        (double, double) FullGrad(double x, double y) { full.Nu(x, y, out _, out double a, out double c); return (a, c); }
        foreach (var (rho, z) in new[] { (5.0, 0.0), (12.0, 3.0), (30.0, -4.0), (14.0, 0.0), (26.0, 0.0) })
            Assert.Equal(LineIntegral(FullGrad, rho, z), full.Lambda(rho, z), 6);
    }

    [Fact]
    public void Orbits_Keep_The_Normalisation_And_The_Carried_Lambda_Is_Path_Independent()
    {
        var scene = new BhRingScene(0.5, 20, 0.977, 3.75, 60, 5000, 3000, 8, 1e-11);
        var s = new double[5];
        int n = 0;
        foreach (double r in new[] { 8.0, 12.0, 16.0, 24.0 })
        {
            if (!scene.LaunchState(r, 0, s)) continue;
            var sys = scene.System(3000);
            var settings = scene.Settings;
            ScatteringIntegrator.Trace(ref sys, s, in settings);
            Assert.True(Math.Abs(BhRingSystem.NormalisationError(scene.Field, scene.E, scene.L, s)) < 1e-8, $"r0 = {r}");
            if (sys.Gate == -1)
            {
                Assert.Equal(scene.Field.LambdaLineIntegral(s[0], s[1]), s[4], 6);
                n++;
            }
        }
        Assert.True(n >= 2, $"only {n} bound orbits checked");
    }

    private static List<(double R, double Ur)> Crossings(BhRingScene scene, double r0, double time, bool down = false)
    {
        var s = new double[5];
        Assert.True(scene.LaunchState(r0, 0, s));
        if (down) s[3] = -s[3];
        var pts = new List<(double R, double Ur)>();
        var sys = scene.System(time, pts);
        var settings = scene.Settings;
        ScatteringIntegrator.Trace(ref sys, s, in settings);
        return pts;
    }

    private static double Q(double r, double ur, double e) => r * r * ((e * e - ur * ur) / (1 - 2 / r) - 1);

    [Fact]
    public void Without_The_Ring_Every_Orbit_Is_Integrable_And_With_It_Not()
    {
        var bare = new BhRingScene(0, 20, 0.977, 3.75, 60, 5000, 20000, 8, 1e-11);
        var withRing = new BhRingScene(1.0, 20, 0.977, 3.75, 60, 5000, 20000, 8, 1e-11);
        foreach (double r0 in new[] { 10.0, 14.0 })
        {
            var pts = Crossings(bare, r0, 20000);
            Assert.True(pts.Count > 20, $"{pts.Count} crossings");
            var qs = pts.Select(p => Q(p.R, p.Ur, 0.977)).ToList();
            double spread = (qs.Max() - qs.Min()) / qs.Average();
            Assert.True(spread < 1e-4, $"bare r0 = {r0}: Q spread {spread:E2}");
        }
        // The heavy ring breaks the Schwarzschild invariant somewhere in the chaotic sea.
        double worst = 0;
        foreach (double r0 in new[] { 8.0, 10.0, 12.0, 14.0 })
        {
            if (!withRing.LaunchState(r0, 0, new double[5])) continue;
            var qs = Crossings(withRing, r0, 20000).Select(p => Q(p.R, p.Ur, 0.977)).ToList();
            if (qs.Count > 5) worst = Math.Max(worst, (qs.Max() - qs.Min()) / Math.Abs(qs.Average()));
        }
        Assert.True(worst > 1e-2, $"ring barely changes the invariant ({worst:E2})");
    }

    [Fact]
    public void Launching_Down_Gives_The_Same_Section_As_Launching_Up()
    {
        var scene = new BhRingScene(0.5, 20, 0.977, 3.75, 60, 5000, 5000, 8, 1e-11);
        var up = Crossings(scene, 12, 3000);
        var down = Crossings(scene, 12, 3000, down: true);
        int n = Math.Min(up.Count, down.Count);
        Assert.True(n > 5);
        for (int i = 0; i < Math.Min(n, 10); i++)
        {
            Assert.Equal(up[i].R, down[i].R, 6);
            Assert.Equal(up[i].Ur, down[i].Ur, 6);
        }
    }

    private static BlackHoleRingCalculator Render(FractalParameters p, int w, int h, double cx, double zoom, BlackHoleRingCalculator? reuse = null)
    {
        var c = reuse ?? new BlackHoleRingCalculator(w, h) { ColorMap = new HsvPalette() };
        c.FractalParameters = p;
        c.CenterX = cx; c.CenterY = 0; c.Zoom = zoom;
        c.Calculate(TestContext.Current.CancellationToken);
        return c;
    }

    private static (FractalRegion Region, FractalParameters P) Preset(string name)
    {
        var region = FractalRegionLibrary.Instance.BuiltIns.Single(r => r.Name == name);
        Assert.Equal(FractalType.BlackHoleRingOrbits, region.FractalType);
        var p = new FractalParameters();
        region.Params!.ApplyTo(p);
        return (region, p);
    }

    private static FractalParameters ViaCommandLine(FractalParameters p)
    {
        var snap = RegionFractalParams.Snapshot(FractalType.BlackHoleRingOrbits, p)!;
        var report = BatchCommandBuilder.BuildWithReport(new BatchCommandSnapshot
        {
            Fractal = FractalType.BlackHoleRingOrbits, Parameters = p, FamilyParams = snap.ToKeyValues(),
        });
        Assert.DoesNotContain(report.Gaps, g => g.Contains("BhRing", StringComparison.OrdinalIgnoreCase));
        Assert.True(BatchOptions.TryParse(report.Args.ToArray(), 0, out var o, out var err), err);
        var q = new FractalParameters();
        RegionFractalParams.FromKeyValues(o.Params, FractalType.BlackHoleRingOrbits, out var perr)!.ApplyTo(q);
        Assert.Null(perr);
        return q;
    }

    [Theory]
    [InlineData(BlackHoleRingView.Section)]
    [InlineData(BlackHoleRingView.Fates)]
    public void The_Hole_Colour_Marks_Exactly_The_Radii_Inside_The_Capture_Radius(BlackHoleRingView view)
    {
        // Also the generic all-types frame (centre -0.5, zoom 1): every r there is inside the hole or not a radius.
        var p = new FractalParameters { BhRingView = view, BhRingOrbits = 4, BhRingOrbitTime = 500, BhRingFateTime = 50 };
        var c = Render(p, 40, 30, -0.5, 1.0);
        int hole = 0;
        for (int y = 0; y < 30; y++)
            for (int x = 0; x < 40; x++)
            {
                var (r, _) = BlackHoleRingCalculator.PlaneAt(x + 0.5, y + 0.5, 40, 30, -0.5, 0, 1.0, p.BhRingVelocityScale);
                bool inside = r > 0 && r < BhRingSystem.CaptureR;
                Assert.Equal(inside, c.ColorBuffer[y * 40 + x] == BlackHoleRingCalculator.HoleColor);
                if (inside) hole++;
            }
        Assert.True(hole > 0 && hole < 40 * 30);
    }

    [Fact]
    public void Every_Setting_Survives_The_Region_Snapshot_And_The_Command_Line()
    {
        var p = new FractalParameters
        {
            BhRingView = BlackHoleRingView.Fates, BhRingMass = 0.73, BhRingRadius = 17.5, BhRingEnergy = 0.962,
            BhRingAngularMomentum = 3.9, BhRingVelocityScale = 45, BhRingOrbits = 33, BhRingOrbitTime = 123456,
            BhRingFateTime = 2222, BhRingColour = BlackHoleRingColour.Density, BhRingTolerance = 3e-11,
        };
        var q = ViaCommandLine(p);
        foreach (var prop in typeof(FractalParameters).GetProperties().Where(x => x.Name.StartsWith("BhRing", StringComparison.Ordinal)))
            Assert.Equal(prop.GetValue(p), prop.GetValue(q));
    }

    [Fact]
    public void Every_Preset_Renders_The_Same_Live_From_The_Command_Line_And_As_A_Poster()
    {
        foreach (var name in Presets)
        {
            var (region, p) = Preset(name);
            p.BhRingOrbitTime = Math.Min(p.BhRingOrbitTime, 5000); p.BhRingFateTime = Math.Min(p.BhRingFateTime, 200); p.BhRingOrbits = Math.Min(p.BhRingOrbits, 8);
            var live = Render(p, 32, 18, region.CenterX, region.Zoom);
            var cli = Render(ViaCommandLine(p), 32, 18, region.CenterX, region.Zoom);
            Assert.True(live.ColorBuffer.SequenceEqual(cli.ColorBuffer), $"{name}: --param render differs from live");
            var req = new PosterRequest
            {
                FractalType = FractalType.BlackHoleRingOrbits, Width = 32, Height = 18,
                CenterX = region.CenterX, CenterY = 0, Zoom = region.Zoom, MaxIterations = live.MaxIterations,
                Quality = QualityPreset.Standard, FractalParameters = p, ColorMap = new HsvPalette(),
            };
            var poster = PosterRenderer.RenderToPixels(req, TestContext.Current.CancellationToken, out _, out _);
            Assert.True(live.ColorBuffer.SequenceEqual(poster), $"{name}: poster differs from live");
        }
    }

    [Theory]
    [InlineData("Black Hole + Ring - Poincare Section", "BhRingMass")]
    [InlineData("Black Hole + Ring - Chaotic Sea", "BhRingEnergy")]
    public void Animated_Frames_Equal_The_Stills_At_Each_Frames_Value(string presetName, string param)
    {
        var lib = AnimationLibrary.Instance;
        lib.Load();
        var (region, p0) = Preset(presetName);
        p0.BhRingOrbitTime = 3000; p0.BhRingOrbits = 6;
        var anim = lib.GetByName(region.AnimationName!)!;
        Assert.Contains(FractalType.BlackHoleRingOrbits, anim.TargetFractalTypes);
        var animatable = FractalAnimatableParamsMap.For(FractalType.BlackHoleRingOrbits).Select(d => d.ParamName).ToHashSet();
        Assert.All(anim.Tracks, t => Assert.Contains(t.ParamName, animatable));
        var prop = typeof(FractalParameters).GetProperty(param)!;
        const int fps = 2;
        var video = p0.Clone();
        BlackHoleRingCalculator? calc = null;
        var seen = new List<double>();
        foreach (int frame in new[] { 0, 9, 17, 26 })
        {
            anim.ApplyAt(video, frame / (double)fps);
            double value = (double)prop.GetValue(video)!;
            seen.Add(value);
            calc = Render(video, 24, 14, region.CenterX, region.Zoom, calc);
            var frameColours = (uint[])calc.ColorBuffer.Clone();
            var still = p0.Clone();
            prop.SetValue(still, value);
            var stillCalc = Render(ViaCommandLine(still), 24, 14, region.CenterX, region.Zoom);
            Assert.True(frameColours.SequenceEqual(stillCalc.ColorBuffer), $"{param} = {value}: frame {frame} differs from the still");
        }
        Assert.True(seen.Distinct().Count() >= 3, $"{param} barely moved: {string.Join(", ", seen)}");
    }

    [Fact]
    public void Help_Names_Presets_Animations_And_Sources()
    {
        string help = HelpTextBundle.MathBlackHoleRingText.Replace("\r\n", "\n");
        Assert.All(Presets, n => Assert.Contains(n, help));
        Assert.All(Animations, a => Assert.Contains(a, help));
        foreach (var source in new[] { "Semerak & Sukova", "404, 545", "DLMF" })
            Assert.Contains(source, help);
    }
}
