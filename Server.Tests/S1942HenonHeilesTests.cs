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

// #1942 — Hénon-Heiles exit basins. Independent checks, none trusting the integrator's own
// error estimate or the calculator's labels:
//   • energy H = (px² + py²)/2 + V is conserved along traced trajectories (measured from the
//     states the integrator hands out, through a wrapper system);
//   • below the escape energy 1/6 nothing escapes (the zero-velocity curve closes inside r = 1);
//   • a launch straight up the y axis stays on it (x = px = 0 is invariant) and leaves by the
//     top channel; its 120° rotations leave by the other two;
//   • the potential's threefold symmetry: rotating every launch of a rendered frame by +120°
//     permutes the exits k -> k + 1 (mismatches only at a small share of boundary pixels),
//     and the check discriminates (without the permutation most pixels mismatch);
//   • Wada: in small windows around bisected boundary points all three exits appear;
//   • presets render the same live, from the command line and as a poster; animated frames
//     equal the stills; masked AA frames are exact; help names everything.
public sealed class S1942HenonHeilesTests
{
    private const int W = 96, H = 64;
    private static readonly string[] Presets =
    {
        "Henon-Heiles - Exit Basins", "Henon-Heiles - Poincare Section", "Henon-Heiles - Near Escape Energy",
        "Henon-Heiles - High Energy", "Henon-Heiles - Escape Time", "Henon-Heiles - Parallel Launch",
    };
    private static readonly string[] Animations = { "Henon-Heiles: rising energy", "Henon-Heiles: turning launch" };

    private static HenonHeilesCalculator Render(FractalParameters p, double cx = 0.0, double cy = HenonHeilesCalculator.DefaultCenterY,
                                                double zoom = HenonHeilesCalculator.DefaultZoom, int w = W, int h = H,
                                                HenonHeilesCalculator? reuse = null)
    {
        var c = reuse ?? new HenonHeilesCalculator(w, h) { ColorMap = new HsvPalette() };
        c.FractalParameters = p;
        c.CenterX = cx; c.CenterY = cy; c.Zoom = zoom;
        c.Calculate(TestContext.Current.CancellationToken);
        return c;
    }

    // Records the largest energy error over every accepted state.
    private struct EnergyWatch : IScatteringSystem
    {
        public HenonHeilesSystem Inner;
        public double E0, MaxError;
        public readonly int Dimension => 4;
        public readonly void Derivative(ReadOnlySpan<double> y, Span<double> dy) => Inner.Derivative(y, dy);
        public readonly ScatterFate Classify(ReadOnlySpan<double> y, out int body) => Inner.Classify(y, out body);
        public readonly double MaxStep(ReadOnlySpan<double> y) => Inner.MaxStep(y);
        public void Observe(ReadOnlySpan<double> y, double h)
        {
            Inner.Observe(y, h);
            double e = 0.5 * (y[2] * y[2] + y[3] * y[3]) + 0.5 * (y[0] * y[0] + y[1] * y[1]) + y[0] * y[0] * y[1] - y[1] * y[1] * y[1] / 3.0;
            MaxError = Math.Max(MaxError, Math.Abs(e - E0));
        }
    }

    [Fact]
    public void Energy_Is_Conserved_Along_Traced_Trajectories()
    {
        var scene = new HenonHeilesScene(0.25, HenonHeilesView.Plane, HenonHeilesLaunch.Tangential, 0, 200, 1e-8);
        var rng = new Random(1942);
        Span<double> s = stackalloc double[4];
        int traced = 0;
        double worst = 0;
        while (traced < 200)
        {
            double a = rng.NextDouble() * 2.0 - 1.0, b = rng.NextDouble() * 2.0 - 1.0;
            if (!scene.LaunchState(a, b, s)) continue;
            double e0 = 0.5 * (s[2] * s[2] + s[3] * s[3]) + 0.5 * (a * a + b * b) + a * a * b - b * b * b / 3.0;
            Assert.Equal(0.25, e0, 12);   // the launch has the requested energy
            var sys = new EnergyWatch { Inner = HenonHeilesSystem.Begin(s, scene.TimeCap), E0 = e0 };
            var settings = scene.Settings;
            ScatteringIntegrator.Trace(ref sys, s, in settings);
            worst = Math.Max(worst, sys.MaxError);
            traced++;
        }
        Assert.True(worst < 1e-5, $"energy drift {worst:E2}");
    }

    [Fact]
    public void Below_The_Escape_Energy_Nothing_Escapes()
    {
        var p = new FractalParameters { HenonHeilesEnergy = 0.15, HenonHeilesMaxTime = 60 };
        // Inside the well (r < 1, the saddles' circle) the zero-velocity curve is closed below 1/6,
        // so no launch there can leave. Launches already outside the well, down a channel past a
        // saddle, still roll away: those are not in the well at all.
        var c = Render(p, w: 48, h: 32);
        int inside = 0;
        for (int y = 0; y < 32; y++)
            for (int x = 0; x < 48; x++)
            {
                var (a, b) = HenonHeilesCalculator.PlaneAt(x + 0.5, y + 0.5, 48, 32, c.CenterX, c.CenterY, c.Zoom);
                sbyte e = c.ExitBuffer[y * 48 + x];
                if (a * a + b * b >= 1.0 || e == HenonHeilesCalculator.ForbiddenGate) continue;
                inside++;
                Assert.True(e < 0, $"launch ({a:F3}, {b:F3}) left by exit {e} below 1/6");
            }
        Assert.True(inside > 100, $"only {inside} allowed launches in the well");

        // Above it, the same frame does escape through all three channels.
        p.HenonHeilesEnergy = 0.25;
        var open = Render(p, w: 48, h: 32);
        for (int k = 0; k < 3; k++) Assert.Contains((sbyte)k, open.ExitBuffer);
    }

    [Fact]
    public void A_Launch_Up_The_Axis_Leaves_By_The_Top_Channel_And_Its_Rotations_By_The_Others()
    {
        var scene = new HenonHeilesScene(0.3, HenonHeilesView.Plane, HenonHeilesLaunch.Radial, 0, 200, 1e-9);
        Span<double> s = stackalloc double[4];
        var up = scene.Trace(0.0, 0.5, s);
        Assert.Equal(0, up.Exit);
        Assert.Equal(0.0, s[0], 9);   // stayed on the axis (x = px = 0 is invariant)
        for (int k = 1; k < 3; k++)
        {
            double ang = Math.PI / 2 + k * 2 * Math.PI / 3;
            Assert.Equal(k, scene.Trace(0.5 * Math.Cos(ang), 0.5 * Math.Sin(ang), s).Exit);
        }
    }

    [Theory]
    [InlineData(HenonHeilesLaunch.Tangential)]
    [InlineData(HenonHeilesLaunch.Radial)]
    public void Rotating_Launches_By_120_Degrees_Permutes_The_Exits(HenonHeilesLaunch launch)
    {
        var p = new FractalParameters { HenonHeilesLaunch = launch };
        var c = Render(p);
        var scene = HenonHeilesScene.Build(p);
        Span<double> s = stackalloc double[4];
        double cos = Math.Cos(2 * Math.PI / 3), sin = Math.Sin(2 * Math.PI / 3);
        int decided = 0, permuted = 0, same = 0;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int e = c.ExitBuffer[y * W + x];
                if (e < 0) continue;
                var (a, b) = HenonHeilesCalculator.PlaneAt(x + 0.5, y + 0.5, W, H, c.CenterX, c.CenterY, c.Zoom);
                var o = scene.Trace(cos * a - sin * b, sin * a + cos * b, s);
                Assert.True(o.Valid);   // V is rotation invariant, so the rotated launch is allowed
                decided++;
                if (o.Exit == (e + 1) % 3) permuted++;
                if (o.Exit == e) same++;
            }
        Assert.True(decided > W * H / 4, $"{decided} decided pixels");
        Assert.True(permuted >= decided * 0.95, $"{launch}: {permuted}/{decided} permuted");
        Assert.True(same <= decided * 0.6, $"{launch}: check does not discriminate ({same}/{decided} unpermuted)");
    }

    [Fact]
    public void Small_Windows_Round_Boundary_Points_Hold_All_Three_Exits()
    {
        // The section of Aguirre, Vallejo & Sanjuán: (y, py) on x = 0 at E = 0.25.
        var scene = new HenonHeilesScene(0.25, HenonHeilesView.Section, HenonHeilesLaunch.Tangential, 0, 400, 1e-10);
        var buf = new double[4];
        int Exit(double a, double b) => scene.Trace(a, b, buf).Exit;

        int checkedPoints = 0;
        foreach (double py in new[] { -0.2, 0.0, 0.15 })
        {
            // Walk along y until the exit changes, then bisect to a boundary point.
            double a0 = -0.3, step = 0.01;
            int e0 = Exit(a0, py);
            double a1 = a0;
            int e1 = e0;
            while (e1 == e0 && a1 < 0.8) { a1 += step; e1 = Exit(a1, py); }
            if (e1 == e0 || e0 < 0 || e1 < 0) continue;
            for (int i = 0; i < 40; i++)
            {
                double m = 0.5 * (a0 + a1);
                int em = Exit(m, py);
                if (em == e0) a0 = m; else { a1 = m; e1 = em; }
            }
            double centre = 0.5 * (a0 + a1);
            var seen = new HashSet<int>();
            const double half = 1e-4;
            for (int j = 0; j < 31; j++)
                for (int i = 0; i < 31; i++)
                {
                    int e = Exit(centre + half * (i / 15.0 - 1.0), py + half * (j / 15.0 - 1.0));
                    if (e >= 0) seen.Add(e);
                }
            Assert.True(seen.Count == 3, $"window at ({centre:F8}, {py}) holds exits {string.Join(",", seen)}");
            checkedPoints++;
        }
        Assert.True(checkedPoints >= 2, $"only {checkedPoints} boundary points found");
    }

    [Fact]
    public void Points_Where_The_Potential_Exceeds_The_Energy_Are_Not_Launched()
    {
        // V(0, -1.2) = 0.72 + 0.576 = 1.296 > 0.25.
        var scene = new HenonHeilesScene(0.25, HenonHeilesView.Plane, HenonHeilesLaunch.Tangential, 0, 200, 1e-8);
        Span<double> s = stackalloc double[4];
        Assert.False(scene.Trace(0.0, -1.2, s).Valid);
        // Section: |py| > sqrt(2E) at y = 0 has no real px.
        var sec = new HenonHeilesScene(0.25, HenonHeilesView.Section, HenonHeilesLaunch.Tangential, 0, 200, 1e-8);
        Assert.False(sec.Trace(0.0, 0.72, s).Valid);
        Assert.True(sec.Trace(0.0, 0.70, s).Valid);
    }

    private static (FractalRegion Region, FractalParameters P) Preset(string name)
    {
        var region = FractalRegionLibrary.Instance.BuiltIns.Single(r => r.Name == name);
        Assert.Equal(FractalType.HenonHeilesBasins, region.FractalType);
        var p = new FractalParameters();
        region.Params!.ApplyTo(p);
        return (region, p);
    }

    private static FractalParameters ViaCommandLine(FractalParameters p)
    {
        var snap = RegionFractalParams.Snapshot(FractalType.HenonHeilesBasins, p)!;
        var report = BatchCommandBuilder.BuildWithReport(new BatchCommandSnapshot
        {
            Fractal = FractalType.HenonHeilesBasins, Parameters = p, FamilyParams = snap.ToKeyValues(),
        });
        Assert.DoesNotContain(report.Gaps, g => g.Contains("Henon", StringComparison.OrdinalIgnoreCase));
        Assert.True(BatchOptions.TryParse(report.Args.ToArray(), 0, out var o, out var err), err);
        var q = new FractalParameters();
        RegionFractalParams.FromKeyValues(o.Params, FractalType.HenonHeilesBasins, out var perr)!.ApplyTo(q);
        Assert.Null(perr);
        return q;
    }

    [Fact]
    public void Every_Setting_Survives_The_Region_Snapshot_And_The_Command_Line()
    {
        var p = new FractalParameters
        {
            HenonHeilesEnergy = 0.31, HenonHeilesView = HenonHeilesView.Section, HenonHeilesLaunch = HenonHeilesLaunch.FixedAngle,
            HenonHeilesLaunchAngle = 37.5, HenonHeilesChannel = HenonHeilesChannel.SaddleDistance, HenonHeilesChannelScale = 2.5,
            HenonHeilesMaxTime = 333, HenonHeilesTolerance = 3e-9, AdaptiveAa = true,
        };
        var q = ViaCommandLine(p);
        foreach (var prop in typeof(FractalParameters).GetProperties().Where(x => x.Name.StartsWith("HenonHeiles", StringComparison.Ordinal)))
            Assert.Equal(prop.GetValue(p), prop.GetValue(q));
        Assert.True(q.AdaptiveAa);
        Assert.True(AdaptiveAaCapabilities.Supports(FractalType.HenonHeilesBasins));
    }

    [Fact]
    public void Every_Preset_Renders_The_Same_Live_From_The_Command_Line_And_As_A_Poster()
    {
        foreach (var name in Presets)
        {
            var (region, p) = Preset(name);
            p.HenonHeilesMaxTime = Math.Min(p.HenonHeilesMaxTime, 200);   // keep the near-escape preset quick
            var live = Render(p, region.CenterX, region.CenterY, region.Zoom, 48, 32);
            var cli = Render(ViaCommandLine(p), region.CenterX, region.CenterY, region.Zoom, 48, 32);
            Assert.True(live.ColorBuffer.SequenceEqual(cli.ColorBuffer), $"{name}: --param render differs from live");
            var req = new PosterRequest
            {
                FractalType = FractalType.HenonHeilesBasins, Width = 48, Height = 32,
                CenterX = region.CenterX, CenterY = region.CenterY, Zoom = region.Zoom, MaxIterations = live.MaxIterations,
                Quality = QualityPreset.Standard, FractalParameters = p, ColorMap = new HsvPalette(),
            };
            var poster = PosterRenderer.RenderToPixels(req, TestContext.Current.CancellationToken, out _, out _);
            Assert.True(live.ColorBuffer.SequenceEqual(poster), $"{name}: poster differs from live");
            Assert.True(live.ExitBuffer.Distinct().Count(e => e >= 0) == 3, $"{name}: not all three exits in frame");
        }
    }

    [Theory]
    [InlineData("Henon-Heiles - Exit Basins", "HenonHeilesEnergy")]
    [InlineData("Henon-Heiles - Parallel Launch", "HenonHeilesLaunchAngle")]
    public void Animated_Frames_Equal_The_Stills_At_Each_Frames_Value(string presetName, string param)
    {
        var lib = AnimationLibrary.Instance;
        lib.Load();
        var (region, p0) = Preset(presetName);
        var anim = lib.GetByName(region.AnimationName!)!;
        Assert.Contains(FractalType.HenonHeilesBasins, anim.TargetFractalTypes);
        var animatable = FractalAnimatableParamsMap.For(FractalType.HenonHeilesBasins).Select(d => d.ParamName).ToHashSet();
        Assert.All(anim.Tracks, t => Assert.Contains(t.ParamName, animatable));
        var prop = typeof(FractalParameters).GetProperty(param)!;
        const int fps = 2;
        var video = p0.Clone();
        HenonHeilesCalculator? calc = null;
        var seen = new List<double>();
        foreach (int frame in new[] { 0, 9, 17, 26 })
        {
            anim.ApplyAt(video, frame / (double)fps);
            double value = (double)prop.GetValue(video)!;
            seen.Add(value);
            calc = Render(video, region.CenterX, region.CenterY, region.Zoom, 48, 32, calc);
            var frameColours = (uint[])calc.ColorBuffer.Clone();
            var still = p0.Clone();
            prop.SetValue(still, value);
            var stillCalc = Render(ViaCommandLine(still), region.CenterX, region.CenterY, region.Zoom, 48, 32);
            Assert.True(frameColours.SequenceEqual(stillCalc.ColorBuffer), $"{param} = {value}: frame {frame} differs from the still");
        }
        Assert.True(seen.Distinct().Count() >= 3, $"{param} barely moved: {string.Join(", ", seen)}");
    }

    [Fact]
    public void A_Masked_Frame_Traces_Only_Marked_Pixels_And_Matches_The_Full_Frame_There()
    {
        var p = new FractalParameters();
        var full = Render(p);
        var masked = Render(p);
        Array.Fill(masked.ColorBuffer, 0x12345678u);
        var mask = new byte[W * H];
        var rng = new Random(1942);
        for (int i = 0; i < mask.Length; i++) mask[i] = (byte)(rng.Next(3) == 0 ? 1 : 0);
        masked.PixelMask = mask;
        Assert.True(masked.MaskExact);
        masked.Calculate(TestContext.Current.CancellationToken);
        masked.PixelMask = null;
        for (int i = 0; i < mask.Length; i++)
            Assert.Equal(mask[i] != 0 ? full.ColorBuffer[i] : 0x12345678u, masked.ColorBuffer[i]);
    }

    [Fact]
    public void Help_Names_Every_Preset_Animation_And_Source()
    {
        string help = HelpTextBundle.MathHenonHeilesText;
        Assert.All(Presets, n => Assert.Contains(n, help));
        Assert.All(Animations, a => Assert.Contains(a, help));
        foreach (var source in new[] { "Henon & Heiles 1964", "Aguirre", "Physical\nReview E 64, 066208", "Astronomical Journal 69" })
            Assert.Contains(source, help.Replace("\r\n", "\n"));
    }
}
