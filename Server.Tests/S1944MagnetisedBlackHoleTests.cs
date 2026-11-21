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

// #1944 — charged particles round a Schwarzschild black hole in Wald's field (units r_g = 2M = 1).
// Independent checks:
//   • the Hamiltonian H = −½ is conserved along traced paths, and the energy relation
//     ℰ² = (dρ/dσ)² + ρ(ρ − 1)(dθ/dσ)² + U holds at every state (written out in the test from
//     Bautista & Vega's eqs. 3.6–3.7, through a wrapper system);
//   • b = 0 is Schwarzschild: the circular-orbit ℓ± equals ρ/√(2ρ − 3), its energy equals
//     (1 − 1/ρ)/√(1 − 3/(2ρ)) (both from the textbook M = 1/2 forms), the smallest ℓ (ISCO) sits at
//     ρ = 3 (= 6M), an unkicked stable circular orbit stays circular, and radial infall is captured;
//   • with the plus branch nothing escapes below ℰ = 1 (far out U ≥ 1);
//   • horizontal Meridional launches give a map symmetric about the equator with the escapes swapped
//     (and the check discriminates);
//   • presets render the same live, from the command line and as a poster; animated frames equal the
//     stills; masked AA frames are exact; help names everything.
public sealed class S1944MagnetisedBlackHoleTests
{
    private static readonly string[] Presets =
    {
        "Magnetised Black Hole - Kick Basins", "Magnetised Black Hole - Kick Close-Up", "Magnetised Black Hole - Minus Branch",
        "Magnetised Black Hole - Strong Field", "Magnetised Black Hole - Meridional Upward Launch",
        "Magnetised Black Hole - Meridional, High Angular Momentum",
    };
    private static readonly string[] Animations = { "Magnetised black hole: field sweep", "Magnetised black hole: turning launch" };

    private static MagnetisedBlackHoleCalculator Render(FractalParameters p, int w, int h, double cx, double cy, double zoom,
                                                        MagnetisedBlackHoleCalculator? reuse = null)
    {
        var c = reuse ?? new MagnetisedBlackHoleCalculator(w, h) { ColorMap = new HsvPalette() };
        c.FractalParameters = p;
        c.CenterX = cx; c.CenterY = cy; c.Zoom = zoom;
        c.Calculate(TestContext.Current.CancellationToken);
        return c;
    }

    private struct Watch : IScatteringSystem
    {
        public MagnetisedBhSystem Inner;
        public double MaxHError, MaxEnergyRelError;
        public readonly int Dimension => 4;
        public readonly void Derivative(ReadOnlySpan<double> y, Span<double> dy) => Inner.Derivative(y, dy);
        public ScatterFate Classify(ReadOnlySpan<double> y, out int body) => Inner.Classify(y, out body);
        public readonly double MaxStep(ReadOnlySpan<double> y) => Inner.MaxStep(y);
        public void Observe(ReadOnlySpan<double> y, double h)
        {
            Inner.Observe(y, h);
            double rho = y[0], th = y[1], f = 1 - 1 / rho;
            double s = Math.Sin(th);
            double e = Inner.Energy, l = Inner.AngularMomentum, b = Inner.Field;
            double H = 0.5 * (f * y[2] * y[2] + y[3] * y[3] / (rho * rho) - e * e / f + Math.Pow(l / (rho * s) - b * rho * s, 2));
            MaxHError = Math.Max(MaxHError, Math.Abs(H + 0.5));
            // Bautista & Vega eq. 3.6 with U from eq. 3.7, from the coordinate velocities.
            double rhoDot = f * y[2], thetaDot = y[3] / (rho * rho);
            double u = f * (1 + Math.Pow(l / (rho * s) - b * rho * s, 2));
            double e2 = rhoDot * rhoDot + rho * (rho - 1) * thetaDot * thetaDot + u;
            MaxEnergyRelError = Math.Max(MaxEnergyRelError, Math.Abs(e2 - e * e) / (e * e));
        }
    }

    [Theory]
    [InlineData(MagnetisedBhView.Kick)]
    [InlineData(MagnetisedBhView.Meridional)]
    public void Hamiltonian_And_Energy_Relation_Hold_Along_Traced_Paths(MagnetisedBhView view)
    {
        var scene = new MagnetisedBhScene(view, 0.3, MagnetisedBhBranch.Plus, 1.5, 1.0, 30, 100, 300, 1e-10);
        var rng = new Random(1944);
        Span<double> s = stackalloc double[4];
        int traced = 0, captured = 0, escaped = 0;
        double worstH = 0, worstE = 0;
        for (int tries = 0; traced < 80 && tries < 20000; tries++)
        {
            double a = view == MagnetisedBhView.Kick ? 1.5 + rng.NextDouble() * 3.5 : rng.NextDouble() * 8 - 4;
            double c = view == MagnetisedBhView.Kick ? rng.NextDouble() * 3 : rng.NextDouble() * 8 - 4;
            if (!scene.LaunchState(a, c, s, out double e, out double l)) continue;
            var sys = new Watch
            {
                Inner = new MagnetisedBhSystem { Energy = e, AngularMomentum = l, Field = 0.3, EscapeHeight = 100, TimeCap = 300, Gate = -1 },
            };
            var settings = scene.Settings;
            ScatteringIntegrator.Trace(ref sys, s, in settings);
            if (sys.Inner.Gate == MagnetisedBhSystem.CapturedGate) captured++;
            if (sys.Inner.Gate is MagnetisedBhSystem.UpstreamGate or MagnetisedBhSystem.DownstreamGate) escaped++;
            worstH = Math.Max(worstH, sys.MaxHError);
            worstE = Math.Max(worstE, sys.MaxEnergyRelError);
            traced++;
        }
        Assert.Equal(80, traced);
        Assert.True(captured > 0 && escaped > 0, $"captured {captured}, escaped {escaped}: sample too narrow");
        Assert.True(worstH < 1e-5, $"H drift {worstH:E2}");
        Assert.True(worstE < 1e-5, $"energy relation drift {worstE:E2}");
    }

    [Fact]
    public void Zero_Field_Circular_Orbits_Are_Schwarzschild_With_The_ISCO_At_Six_M()
    {
        // Textbook Schwarzschild with M = 1/2 (r_g = 1): L² = M r²/(r − 3M), E = (1 − 2M/r)/√(1 − 3M/r).
        foreach (double rho in new[] { 1.6, 2.0, 3.0, 4.5, 10.0 })
        {
            double l = MagnetisedBhSystem.CircularAngularMomentum(0.0, rho, +1);
            Assert.Equal(Math.Sqrt(0.5 * rho * rho / (rho - 1.5)), Math.Abs(l), 9);
            double eCirc = (1 - 1 / rho) / Math.Sqrt(1 - 1.5 / rho);
            Assert.Equal(eCirc * eCirc, MagnetisedBhSystem.EffectivePotential(l, 0.0, rho, Math.PI / 2), 9);
        }
        Assert.True(double.IsNaN(MagnetisedBhSystem.CircularAngularMomentum(0.0, 1.4, +1)));   // inside the photon sphere
        // ISCO: ℓ(ρ) has its minimum at ρ = 3 (6M).
        double best = 0, bestL = double.MaxValue;
        for (double rho = 1.6; rho < 8; rho += 1e-4)
        {
            double l = MagnetisedBhSystem.CircularAngularMomentum(0.0, rho, +1);
            if (l < bestL) { bestL = l; best = rho; }
        }
        Assert.Equal(3.0, best, 3);
    }

    [Fact]
    public void Zero_Field_Stable_Circular_Orbit_Stays_Circular_And_Radial_Infall_Is_Captured()
    {
        Span<double> s = stackalloc double[4];
        double rho0 = 4.0;
        double l = MagnetisedBhSystem.CircularAngularMomentum(0.0, rho0, +1);
        double eCirc = Math.Sqrt(MagnetisedBhSystem.EffectivePotential(l, 0.0, rho0, Math.PI / 2));
        var kick = new MagnetisedBhScene(MagnetisedBhView.Kick, 0.0, MagnetisedBhBranch.Plus, 0, 0, 0, 100, 200, 1e-11);
        Assert.True(kick.LaunchState(rho0, eCirc, s, out _, out _));
        Assert.Equal(0.0, s[3], 6);   // no kick at the orbit's own energy
        var o = kick.Trace(rho0, eCirc, s);
        Assert.Equal(-1, o.Gate);   // still on the orbit at the time limit
        Assert.Equal(rho0, s[0], 5);
        Assert.Equal(Math.PI / 2, s[1], 6);

        // Radial infall (ℓ = 0, launched straight in) is captured.
        var infall = new MagnetisedBhScene(MagnetisedBhView.Meridional, 0.0, MagnetisedBhBranch.Plus, 0.95, 0.0, 180, 100, 200, 1e-10);
        Assert.Equal(MagnetisedBhSystem.CapturedGate, infall.Trace(5.0, 0.5, s).Gate);
    }

    [Fact]
    public void Plus_Branch_Kicks_Below_Energy_One_Never_Escape()
    {
        const int W = 64, H = 40;
        var c = Render(new FractalParameters(), W, H, MagnetisedBlackHoleCalculator.DefaultCenterX, MagnetisedBlackHoleCalculator.DefaultCenterY, MagnetisedBlackHoleCalculator.DefaultZoom);
        int below = 0, above = 0, escapesAbove = 0;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                var (_, e) = MagnetisedBlackHoleCalculator.PlaneAt(x + 0.5, y + 0.5, W, H, c.CenterX, c.CenterY, c.Zoom);
                int g = c.GateBuffer[y * W + x];
                if (g < -1) continue;   // no launch, or inside the hole
                if (e < 1.0) { below++; Assert.True(g == MagnetisedBhSystem.CapturedGate || g == -1, $"escape at energy {e:F3}"); }
                else { above++; if (g is 1 or 2) escapesAbove++; }
            }
        Assert.True(below > 20 && escapesAbove > above / 4, $"below {below}, above {above}, escapes above {escapesAbove}");
        Assert.Contains((sbyte)MagnetisedBhSystem.DownstreamGate, c.GateBuffer);   // all three fates in the frame
        Assert.Contains((sbyte)MagnetisedBhSystem.CapturedGate, c.GateBuffer);
    }

    [Theory]
    [InlineData(MagnetisedBhView.Kick)]
    [InlineData(MagnetisedBhView.Meridional)]
    public void The_Hole_Colour_Marks_Exactly_The_Points_Inside_The_Capture_Radius(MagnetisedBhView view)
    {
        // Also the generic all-types frame (centre -0.5, zoom 1): no Kick launch exists there, but the
        // hole edge at rho0 = 1.01 still shows.
        const int W = 40, H = 30;
        var c = Render(new FractalParameters { MagBhView = view }, W, H, -0.5, 0, 1.0);
        int hole = 0;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                var (a, e) = MagnetisedBlackHoleCalculator.PlaneAt(x + 0.5, y + 0.5, W, H, c.CenterX, c.CenterY, c.Zoom);
                double r = view == MagnetisedBhView.Kick ? a : Math.Sqrt(a * a + e * e);
                bool inside = r < 1.01;
                Assert.Equal(inside, c.GateBuffer[y * W + x] == MagnetisedBlackHoleCalculator.HoleGate);
                Assert.Equal(inside, c.ColorBuffer[y * W + x] == MagnetisedBlackHoleCalculator.HoleColor);
                if (inside) hole++;
            }
        Assert.True(hole > 0 && hole < W * H);
    }

    [Fact]
    public void The_Minus_Branch_Has_Negative_Angular_Momentum()
    {
        for (double rho = 2; rho < 6; rho += 0.5)
            Assert.True(MagnetisedBhSystem.CircularAngularMomentum(0.3, rho, -1) < 0);
    }

    [Fact]
    public void Horizontal_Meridional_Launches_Are_Mirror_Symmetric_About_The_Equator()
    {
        const int W = 64, H = 48;
        var p = new FractalParameters { MagBhView = MagnetisedBhView.Meridional, MagBhEnergy = 2.0, MagBhAngularMomentum = 3.0, MagBhLaunchAngle = 0 };
        var c = Render(p, W, H, 0, 0, 0.3);
        int[] swap = { 0, 2, 1 };
        int n = 0, ok = 0, escapes = 0, unswapped = 0;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int g = c.GateBuffer[y * W + x];
                if (g < 0) continue;
                int m = c.GateBuffer[(H - 1 - y) * W + x];
                n++;
                if (m == swap[g]) ok++;
                if (g > 0) { escapes++; if (m == g) unswapped++; }
            }
        Assert.True(escapes > n / 20, $"only {escapes} escapes of {n}");
        Assert.True(ok >= n * 0.9, $"equatorial mirror {ok}/{n}");
        Assert.True(unswapped <= escapes * 0.5, $"check does not discriminate ({unswapped}/{escapes})");
    }

    private static (FractalRegion Region, FractalParameters P) Preset(string name)
    {
        var region = FractalRegionLibrary.Instance.BuiltIns.Single(r => r.Name == name);
        Assert.Equal(FractalType.MagnetisedBlackHole, region.FractalType);
        var p = new FractalParameters();
        region.Params!.ApplyTo(p);
        return (region, p);
    }

    private static FractalParameters ViaCommandLine(FractalParameters p)
    {
        var snap = RegionFractalParams.Snapshot(FractalType.MagnetisedBlackHole, p)!;
        var report = BatchCommandBuilder.BuildWithReport(new BatchCommandSnapshot
        {
            Fractal = FractalType.MagnetisedBlackHole, Parameters = p, FamilyParams = snap.ToKeyValues(),
        });
        Assert.DoesNotContain(report.Gaps, g => g.Contains("MagBh", StringComparison.OrdinalIgnoreCase));
        Assert.True(BatchOptions.TryParse(report.Args.ToArray(), 0, out var o, out var err), err);
        var q = new FractalParameters();
        RegionFractalParams.FromKeyValues(o.Params, FractalType.MagnetisedBlackHole, out var perr)!.ApplyTo(q);
        Assert.Null(perr);
        return q;
    }

    [Fact]
    public void Every_Setting_Survives_The_Region_Snapshot_And_The_Command_Line()
    {
        var p = new FractalParameters
        {
            MagBhView = MagnetisedBhView.Meridional, MagBhField = 0.43, MagBhBranch = MagnetisedBhBranch.Minus, MagBhEnergy = 1.77,
            MagBhAngularMomentum = -2.25, MagBhLaunchAngle = 33.5, MagBhEscapeHeight = 250, MagBhMaxTime = 777, MagBhTolerance = 3e-10,
            AdaptiveAa = true,
        };
        var q = ViaCommandLine(p);
        foreach (var prop in typeof(FractalParameters).GetProperties().Where(x => x.Name.StartsWith("MagBh", StringComparison.Ordinal)))
            Assert.Equal(prop.GetValue(p), prop.GetValue(q));
        Assert.True(q.AdaptiveAa);
        Assert.True(AdaptiveAaCapabilities.Supports(FractalType.MagnetisedBlackHole));
    }

    [Fact]
    public void Every_Preset_Renders_The_Same_Live_From_The_Command_Line_And_As_A_Poster()
    {
        foreach (var name in Presets)
        {
            var (region, p) = Preset(name);
            p.MagBhMaxTime = 200;   // keep the test quick; same settings on every path
            var live = Render(p, 32, 20, region.CenterX, region.CenterY, region.Zoom);
            var cli = Render(ViaCommandLine(p), 32, 20, region.CenterX, region.CenterY, region.Zoom);
            Assert.True(live.ColorBuffer.SequenceEqual(cli.ColorBuffer), $"{name}: --param render differs from live");
            var req = new PosterRequest
            {
                FractalType = FractalType.MagnetisedBlackHole, Width = 32, Height = 20,
                CenterX = region.CenterX, CenterY = region.CenterY, Zoom = region.Zoom, MaxIterations = live.MaxIterations,
                Quality = QualityPreset.Standard, FractalParameters = p, ColorMap = new HsvPalette(),
            };
            var poster = PosterRenderer.RenderToPixels(req, TestContext.Current.CancellationToken, out _, out _);
            Assert.True(live.ColorBuffer.SequenceEqual(poster), $"{name}: poster differs from live");
        }
    }

    [Theory]
    [InlineData("Magnetised Black Hole - Kick Basins", "MagBhField")]
    [InlineData("Magnetised Black Hole - Meridional Upward Launch", "MagBhLaunchAngle")]
    public void Animated_Frames_Equal_The_Stills_At_Each_Frames_Value(string presetName, string param)
    {
        var lib = AnimationLibrary.Instance;
        lib.Load();
        var (region, p0) = Preset(presetName);
        p0.MagBhMaxTime = 200;
        var anim = lib.GetByName(region.AnimationName!)!;
        Assert.Contains(FractalType.MagnetisedBlackHole, anim.TargetFractalTypes);
        var animatable = FractalAnimatableParamsMap.For(FractalType.MagnetisedBlackHole).Select(d => d.ParamName).ToHashSet();
        Assert.All(anim.Tracks, t => Assert.Contains(t.ParamName, animatable));
        var prop = typeof(FractalParameters).GetProperty(param)!;
        const int fps = 2;
        var video = p0.Clone();
        MagnetisedBlackHoleCalculator? calc = null;
        var seen = new List<double>();
        foreach (int frame in new[] { 0, 9, 17, 26 })
        {
            anim.ApplyAt(video, frame / (double)fps);
            double value = (double)prop.GetValue(video)!;
            seen.Add(value);
            calc = Render(video, 32, 20, region.CenterX, region.CenterY, region.Zoom, calc);
            var frameColours = (uint[])calc.ColorBuffer.Clone();
            var still = p0.Clone();
            prop.SetValue(still, value);
            var stillCalc = Render(ViaCommandLine(still), 32, 20, region.CenterX, region.CenterY, region.Zoom);
            Assert.True(frameColours.SequenceEqual(stillCalc.ColorBuffer), $"{param} = {value}: frame {frame} differs from the still");
        }
        Assert.True(seen.Distinct().Count() >= 3, $"{param} barely moved: {string.Join(", ", seen)}");
    }

    [Fact]
    public void A_Masked_Frame_Computes_Only_Marked_Pixels_And_Matches_The_Full_Frame_There()
    {
        const int W = 40, H = 24;
        var p = new FractalParameters { MagBhMaxTime = 200 };
        double cx = MagnetisedBlackHoleCalculator.DefaultCenterX, cy = MagnetisedBlackHoleCalculator.DefaultCenterY, z = MagnetisedBlackHoleCalculator.DefaultZoom;
        var full = Render(p, W, H, cx, cy, z);
        var masked = Render(p, W, H, cx, cy, z);
        Array.Fill(masked.ColorBuffer, 0x12345678u);
        var mask = new byte[W * H];
        var rng = new Random(1944);
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
        string help = HelpTextBundle.MathMagnetisedBlackHoleText.Replace("\r\n", "\n");
        Assert.All(Presets, n => Assert.Contains(n, help));
        Assert.All(Animations, a => Assert.Contains(a, help));
        foreach (var source in new[] { "Wald", "Bautista & Vega", "155016", "Kopacek", "Stuchlik", "Al Zahrani" })
            Assert.Contains(source, help);
    }
}
