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

// #1943 — restricted three-body basins. Independent checks:
//   • the Jacobi constant C = 2Ω − v² is conserved along traced paths, and no state enters the
//     forbidden region 2Ω < C (measured from the integrator's states, through a wrapper system,
//     with Ω written out again in the test);
//   • the equilibria: L1-L3 match a separate Newton solve of ∂Ω/∂x = 0 on the axis written in the
//     test, L4 / L5 are one unit from both primaries, and ∇Ω vanishes at all five; for two equal
//     masses the Jacobi values are C(L1) = 4 and C(L4) = 2.75 exactly;
//   • the Newton map converges to the equilibrium it starts next to, and for equal masses its
//     basin map has both mirror symmetries with the matching label swaps;
//   • the Copenhagen fate map is point-symmetric under the half turn with the crash labels swapped
//     (and the check discriminates);
//   • between C(L2) and C(L1) nothing near the primaries escapes and nothing far out crashes;
//   • presets render the same live, from the command line and as a poster; animated frames equal
//     the stills; masked AA frames are exact; help names everything.
public sealed class S1943ThreeBodyTests
{
    private static readonly string[] Presets =
    {
        "Three-Body - Copenhagen Fates", "Three-Body - Copenhagen Trapped Zone", "Three-Body - Retrograde Launch",
        "Three-Body - Unequal Masses", "Three-Body - Lagrange Basins", "Three-Body - Lagrange Basins, Unequal",
    };
    private static readonly string[] Animations = { "Three-body: falling Jacobi", "Three-body: mass ratio sweep" };

    private static double Omega(double mu, double x, double y)
        => 0.5 * (x * x + y * y) + (1 - mu) / Math.Sqrt((x + mu) * (x + mu) + y * y) + mu / Math.Sqrt((x - 1 + mu) * (x - 1 + mu) + y * y);

    private static ThreeBodyCalculator Render(FractalParameters p, int w, int h, double zoom = ThreeBodyCalculator.DefaultZoom,
                                              double cx = 0, double cy = 0, ThreeBodyCalculator? reuse = null)
    {
        var c = reuse ?? new ThreeBodyCalculator(w, h) { ColorMap = new HsvPalette() };
        c.FractalParameters = p;
        c.CenterX = cx; c.CenterY = cy; c.Zoom = zoom;
        c.Calculate(TestContext.Current.CancellationToken);
        return c;
    }

    private struct JacobiWatch : IScatteringSystem
    {
        public ThreeBodySystem Inner;
        public double C0, MaxError, MinKinetic;
        public readonly int Dimension => 4;
        public readonly void Derivative(ReadOnlySpan<double> s, Span<double> ds) => Inner.Derivative(s, ds);
        public ScatterFate Classify(ReadOnlySpan<double> s, out int body) => Inner.Classify(s, out body);
        public readonly double MaxStep(ReadOnlySpan<double> s) => Inner.MaxStep(s);
        public void Observe(ReadOnlySpan<double> s, double h)
        {
            Inner.Observe(s, h);
            double twoOmega = 2.0 * Omega(Inner.Mu, s[0], s[1]);
            double c = twoOmega - (s[2] * s[2] + s[3] * s[3]);
            MaxError = Math.Max(MaxError, Math.Abs(c - C0) / Math.Max(1.0, twoOmega));
            MinKinetic = Math.Min(MinKinetic, twoOmega - C0);
        }
    }

    [Theory]
    [InlineData(0.5, 3.0)]
    [InlineData(0.1, 3.2)]
    public void Jacobi_Constant_Is_Conserved_And_No_Path_Enters_The_Forbidden_Region(double mu, double jacobi)
    {
        var scene = new ThreeBodyScene(mu, ThreeBodyView.Fates, jacobi, ThreeBodyLaunch.Prograde, 0, 0.01, 10, 100, 1e-9, 60);
        var rng = new Random(1943);
        Span<double> s = stackalloc double[4];
        int traced = 0, crashes = 0;
        double worst = 0, minKinetic = double.MaxValue;
        while (traced < 150)
        {
            double x = rng.NextDouble() * 3 - 1.5, y = rng.NextDouble() * 2 - 1;
            if (!scene.LaunchState(x, y, s)) continue;
            Assert.Equal(jacobi, 2 * Omega(mu, x, y) - (s[2] * s[2] + s[3] * s[3]), 9);
            var sys = new JacobiWatch
            {
                Inner = new ThreeBodySystem { Mu = mu, CrashRadius = 0.01, EscapeRadius = 10, TimeCap = 100, Gate = -1 },
                C0 = jacobi, MinKinetic = double.MaxValue,
            };
            var settings = scene.Settings;
            ScatteringIntegrator.Trace(ref sys, s, in settings);
            if (sys.Inner.Gate is 0 or 1) crashes++;
            worst = Math.Max(worst, sys.MaxError);
            minKinetic = Math.Min(minKinetic, sys.MinKinetic);
            traced++;
        }
        Assert.True(worst < 1e-5, $"relative Jacobi drift {worst:E2}");
        Assert.True(minKinetic > -1e-5, $"entered the forbidden region: 2 Omega - C = {minKinetic:E2}");
        Assert.True(crashes > 0, "no crash in the sample: the near-primary paths were not exercised");
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(0.1)]
    [InlineData(0.01)]
    public void The_Five_Equilibria_Match_Independent_Solves(double mu)
    {
        var l = ThreeBodySystem.LagrangePoints(mu);
        // Collinear: Newton on dOmega/dx along the axis, from starts in each interval (written here).
        double Fx(double x) => x - (1 - mu) * (x + mu) / Math.Pow(Math.Abs(x + mu), 3) - mu * (x - 1 + mu) / Math.Pow(Math.Abs(x - 1 + mu), 3);
        double Solve(double x)
        {
            for (int i = 0; i < 200; i++)
            {
                double d = (Fx(x + 1e-7) - Fx(x - 1e-7)) / 2e-7;
                double nx = x - Fx(x) / d;
                if (Math.Abs(nx - x) < 1e-15) return nx;
                x = nx;
            }
            return x;
        }
        Assert.Equal(Solve(0.5 - mu), l[0].X, 8);
        Assert.Equal(Solve(1 - mu + 0.3), l[1].X, 8);
        Assert.Equal(Solve(-mu - 0.9), l[2].X, 8);
        Assert.True(l[0].X > -mu && l[0].X < 1 - mu);
        Assert.True(l[1].X > 1 - mu);
        Assert.True(l[2].X < -mu);
        for (int k = 3; k < 5; k++)
        {
            var (x, y) = l[k];
            Assert.Equal(1.0, Math.Sqrt((x + mu) * (x + mu) + y * y), 12);
            Assert.Equal(1.0, Math.Sqrt((x - 1 + mu) * (x - 1 + mu) + y * y), 12);
        }
        foreach (var (x, y) in l)
        {
            var (gx, gy) = ThreeBodySystem.Gradient(mu, x, y);
            Assert.True(Math.Abs(gx) < 1e-8 && Math.Abs(gy) < 1e-8, $"grad Omega = ({gx}, {gy}) at ({x}, {y})");
        }
        if (mu == 0.5)
        {
            Assert.Equal(4.0, 2 * Omega(mu, l[0].X, 0), 9);
            Assert.Equal(2.75, 2 * Omega(mu, l[3].X, l[3].Y), 12);
            double cL2 = 2 * Omega(mu, l[1].X, 0);
            Assert.InRange(cL2, 3.45, 3.46);   // the help text quotes about 3.457
        }
    }

    [Fact]
    public void Newton_Converges_To_The_Equilibrium_It_Starts_Beside()
    {
        foreach (double mu in new[] { 0.5, 0.1 })
        {
            var scene = new ThreeBodyScene(mu, ThreeBodyView.LagrangeBasins, 3, ThreeBodyLaunch.Prograde, 0, 0.01, 10, 100, 1e-9, 60);
            for (int k = 0; k < 5; k++)
            {
                var (x, y) = scene.LagrangePoints[k];
                Assert.Equal(k, scene.Newton(x + 1e-3, y - 2e-3).Gate);
            }
        }
    }

    [Fact]
    public void Equal_Mass_Lagrange_Basins_Have_Both_Mirror_Symmetries()
    {
        const int W = 96, H = 64;
        var c = Render(new FractalParameters { ThreeBodyView = ThreeBodyView.LagrangeBasins }, W, H);
        int[] mirrorX = { 0, 2, 1, 3, 4 };   // x -> -x swaps L2 and L3
        int[] mirrorY = { 0, 1, 2, 4, 3 };   // y -> -y swaps L4 and L5
        int n = 0, okX = 0, okY = 0, sameY = 0;
        var seen = new HashSet<int>();
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int g = c.GateBuffer[y * W + x];
                if (g < 0) continue;
                seen.Add(g);
                n++;
                int gx = c.GateBuffer[y * W + (W - 1 - x)], gy = c.GateBuffer[(H - 1 - y) * W + x];
                if (gx == mirrorX[g]) okX++;
                if (gy == mirrorY[g]) okY++;
                if (gy == g) sameY++;
            }
        Assert.Equal(5, seen.Count);
        Assert.True(okX >= n * 0.97, $"x mirror {okX}/{n}");
        Assert.True(okY >= n * 0.97, $"y mirror {okY}/{n}");
        Assert.True(sameY <= n * 0.8, $"y check does not discriminate ({sameY}/{n})");
    }

    [Fact]
    public void Copenhagen_Fates_Are_Point_Symmetric_With_The_Primaries_Swapped()
    {
        const int W = 72, H = 48;
        var c = Render(new FractalParameters(), W, H);
        int[] swap = { 1, 0, 2 };
        int n = 0, ok = 0, unswapped = 0, crashes = 0;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int g = c.GateBuffer[y * W + x];
                if (g < 0) continue;
                int m = c.GateBuffer[(H - 1 - y) * W + (W - 1 - x)];
                n++;
                if (m == swap[g]) ok++;
                if (g < 2) { crashes++; if (m == g) unswapped++; }
            }
        Assert.True(crashes > n / 10, $"only {crashes} crash pixels");
        Assert.True(ok >= n * 0.9, $"half-turn symmetry {ok}/{n}");
        Assert.True(unswapped <= crashes * 0.5, $"check does not discriminate ({unswapped}/{crashes} crash labels unswapped)");
    }

    [Fact]
    public void Between_C_L2_And_C_L1_Inner_Launches_Never_Escape_And_Outer_Ones_Never_Crash()
    {
        const int W = 72, H = 48;
        var p = new FractalParameters { ThreeBodyJacobi = 3.6 };
        var c = Render(p, W, H, zoom: 0.6);   // wide enough for launches past r = 2
        int inner = 0, outer = 0;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                var (px, py) = ThreeBodyCalculator.PlaneAt(x + 0.5, y + 0.5, W, H, c.CenterX, c.CenterY, c.Zoom);
                int g = c.GateBuffer[y * W + x];
                if (g == ThreeBodyCalculator.ForbiddenGate) continue;
                if (Math.Abs(px) < 1.0 && Math.Abs(py) < 0.3) { inner++; Assert.NotEqual(ThreeBodySystem.EscapeGate, g); }
                if (px * px + py * py > 4.0) { outer++; Assert.True(g == ThreeBodySystem.EscapeGate || g == -1, $"outer launch ({px:F2}, {py:F2}) crashed"); }
            }
        Assert.True(inner > 50 && outer > 50, $"inner {inner}, outer {outer}");
    }

    private static (FractalRegion Region, FractalParameters P) Preset(string name)
    {
        var region = FractalRegionLibrary.Instance.BuiltIns.Single(r => r.Name == name);
        Assert.Equal(FractalType.ThreeBodyBasins, region.FractalType);
        var p = new FractalParameters();
        region.Params!.ApplyTo(p);
        return (region, p);
    }

    private static FractalParameters ViaCommandLine(FractalParameters p)
    {
        var snap = RegionFractalParams.Snapshot(FractalType.ThreeBodyBasins, p)!;
        var report = BatchCommandBuilder.BuildWithReport(new BatchCommandSnapshot
        {
            Fractal = FractalType.ThreeBodyBasins, Parameters = p, FamilyParams = snap.ToKeyValues(),
        });
        Assert.DoesNotContain(report.Gaps, g => g.Contains("ThreeBody", StringComparison.OrdinalIgnoreCase));
        Assert.True(BatchOptions.TryParse(report.Args.ToArray(), 0, out var o, out var err), err);
        var q = new FractalParameters();
        RegionFractalParams.FromKeyValues(o.Params, FractalType.ThreeBodyBasins, out var perr)!.ApplyTo(q);
        Assert.Null(perr);
        return q;
    }

    [Fact]
    public void Every_Setting_Survives_The_Region_Snapshot_And_The_Command_Line()
    {
        var p = new FractalParameters
        {
            ThreeBodyView = ThreeBodyView.LagrangeBasins, ThreeBodyMassRatio = 0.23, ThreeBodyJacobi = 3.17,
            ThreeBodyLaunch = ThreeBodyLaunch.FixedAngle, ThreeBodyLaunchAngle = 41.5, ThreeBodyCrashRadius = 0.004,
            ThreeBodyEscapeRadius = 17, ThreeBodyMaxTime = 333, ThreeBodyTolerance = 3e-10, ThreeBodyNewtonIterations = 77,
            AdaptiveAa = true,
        };
        var q = ViaCommandLine(p);
        foreach (var prop in typeof(FractalParameters).GetProperties().Where(x => x.Name.StartsWith("ThreeBody", StringComparison.Ordinal)))
            Assert.Equal(prop.GetValue(p), prop.GetValue(q));
        Assert.True(q.AdaptiveAa);
        Assert.True(AdaptiveAaCapabilities.Supports(FractalType.ThreeBodyBasins));
    }

    [Fact]
    public void Every_Preset_Renders_The_Same_Live_From_The_Command_Line_And_As_A_Poster()
    {
        foreach (var name in Presets)
        {
            var (region, p) = Preset(name);
            var live = Render(p, 40, 24, region.Zoom, region.CenterX, region.CenterY);
            var cli = Render(ViaCommandLine(p), 40, 24, region.Zoom, region.CenterX, region.CenterY);
            Assert.True(live.ColorBuffer.SequenceEqual(cli.ColorBuffer), $"{name}: --param render differs from live");
            var req = new PosterRequest
            {
                FractalType = FractalType.ThreeBodyBasins, Width = 40, Height = 24,
                CenterX = region.CenterX, CenterY = region.CenterY, Zoom = region.Zoom, MaxIterations = live.MaxIterations,
                Quality = QualityPreset.Standard, FractalParameters = p, ColorMap = new HsvPalette(),
            };
            var poster = PosterRenderer.RenderToPixels(req, TestContext.Current.CancellationToken, out _, out _);
            Assert.True(live.ColorBuffer.SequenceEqual(poster), $"{name}: poster differs from live");
        }
    }

    [Theory]
    [InlineData("Three-Body - Copenhagen Fates", "ThreeBodyJacobi")]
    [InlineData("Three-Body - Lagrange Basins", "ThreeBodyMassRatio")]
    public void Animated_Frames_Equal_The_Stills_At_Each_Frames_Value(string presetName, string param)
    {
        var lib = AnimationLibrary.Instance;
        lib.Load();
        var (region, p0) = Preset(presetName);
        var anim = lib.GetByName(region.AnimationName!)!;
        Assert.Contains(FractalType.ThreeBodyBasins, anim.TargetFractalTypes);
        var animatable = FractalAnimatableParamsMap.For(FractalType.ThreeBodyBasins).Select(d => d.ParamName).ToHashSet();
        Assert.All(anim.Tracks, t => Assert.Contains(t.ParamName, animatable));
        var prop = typeof(FractalParameters).GetProperty(param)!;
        const int fps = 2;
        var video = p0.Clone();
        ThreeBodyCalculator? calc = null;
        var seen = new List<double>();
        foreach (int frame in new[] { 0, 9, 17, 26 })
        {
            anim.ApplyAt(video, frame / (double)fps);
            double value = (double)prop.GetValue(video)!;
            seen.Add(value);
            calc = Render(video, 40, 24, region.Zoom, region.CenterX, region.CenterY, calc);
            var frameColours = (uint[])calc.ColorBuffer.Clone();
            var still = p0.Clone();
            prop.SetValue(still, value);
            var stillCalc = Render(ViaCommandLine(still), 40, 24, region.Zoom, region.CenterX, region.CenterY);
            Assert.True(frameColours.SequenceEqual(stillCalc.ColorBuffer), $"{param} = {value}: frame {frame} differs from the still");
        }
        Assert.True(seen.Distinct().Count() >= 3, $"{param} barely moved: {string.Join(", ", seen)}");
    }

    [Theory]
    [InlineData(ThreeBodyView.Fates)]
    [InlineData(ThreeBodyView.LagrangeBasins)]
    public void A_Masked_Frame_Computes_Only_Marked_Pixels_And_Matches_The_Full_Frame_There(ThreeBodyView view)
    {
        const int W = 48, H = 32;
        var p = new FractalParameters { ThreeBodyView = view };
        var full = Render(p, W, H);
        var masked = Render(p, W, H);
        Array.Fill(masked.ColorBuffer, 0x12345678u);
        var mask = new byte[W * H];
        var rng = new Random(1943);
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
        string help = HelpTextBundle.MathThreeBodyText.Replace("\r\n", "\n");
        Assert.All(Presets, n => Assert.Contains(n, help));
        Assert.All(Animations, a => Assert.Contains(a, help));
        foreach (var source in new[] { "Nagler", "066218", "026227", "Zotos", "361, 181" })
            Assert.Contains(source, help);
    }
}
