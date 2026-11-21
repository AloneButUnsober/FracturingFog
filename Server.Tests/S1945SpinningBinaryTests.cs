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

// #1945 — spinning compact binaries (Kidder 1995 equations, as used by Levin 2000). The equations
// are long, so the tests check each transcribed piece against Kidder's own conserved quantities:
//   • N + SS (with only the S × S precession) is a Hamiltonian system with energy E_N + E_SS
//     (Eqs. 9a, 9e): conserved to integrator precision, and dropping E_SS shows the term is live;
//   • 1PN, 1PN + 2PN, spin-orbit and the full conservative system each conserve their Kidder energy
//     (Eqs. 9b-9d, 8) and angular momentum (Eqs. 10-11d) only to the next order, so the check is a
//     ratio (drift with the piece's term / drift without) that is small and shrinks with r; a wrong
//     coefficient would leave it O(1). Spin lengths stay constant;
//   • no spins, no radiation reaction: the orbit stays in its plane and the outcome map is one
//     colour (non-spinning 2PN two-body motion is integrable);
//   • Newtonian dynamics with Kidder's radiation reaction merges a circular binary in the time of Peters (1964)
//     time 5 (r0⁴ − r⁴) / (256 μ m²) to 1 % (checks Eq. 4f);
//   • parity: snapshot / CLI / poster / animation / masked AA; help names everything.
public sealed class S1945SpinningBinaryTests
{
    private static readonly string[] Presets =
    {
        "Spinning Binary - Levin Conservative", "Spinning Binary - Conservative Close-Up",
        "Spinning Binary - Radiation Reaction On", "Spinning Binary - Merger Side (Dissipative)",
        "Spinning Binary - No Spins",
    };
    private static readonly string[] Animations = { "Spinning binary: spin-up", "Spinning binary: speed sweep" };

    private const double M1 = 0.75, M2 = 0.25;   // m2/m1 = 1/3, m = 1

    // Trace one binary with the given terms, returning the worst relative drift of each energy measure.
    private static double[] Drift(PnTerms terms, double r0, double v0, double theta1, double theta2, double orbits,
                                  params PnTerms[] energyTerms)
    {
        var s = new double[SpinningBinarySystem.StateSize];
        s[0] = r0; s[4] = v0;
        double a1 = 0.6 * M1 * M1, a2 = 0.6 * M2 * M2;
        s[6] = a1 * Math.Sin(theta1); s[8] = a1 * Math.Cos(theta1);
        s[9] = a2 * Math.Sin(theta2) * Math.Cos(1.0); s[10] = a2 * Math.Sin(theta2) * Math.Sin(1.0); s[11] = a2 * Math.Cos(theta2);
        var e0 = energyTerms.Select(t => SpinningBinarySystem.Energy(M1, M2, s, t)).ToArray();
        var worst = new double[energyTerms.Length];
        var sys = new Watch
        {
            Inner = new SpinningBinarySystem { M1 = M1, M2 = M2, Terms = terms, MergeRadius = 1.5, SeparateRadius = 1e4, MaxOrbitAngle = 2 * Math.PI * orbits, End = -1 },
            EnergyTerms = energyTerms, E0 = e0, Worst = worst,
        };
        var settings = new ScatterSettings { RelTol = 1e-12, AbsTol = 1e-15, MaxSteps = 10_000_000, InitialStep = 0.01 };
        ScatteringIntegrator.Trace(ref sys, s, in settings);
        Assert.Equal(2, sys.Inner.End);   // completed the orbits (no merger / separation in these checks)
        return worst;
    }

    private struct Watch : IScatteringSystem
    {
        public SpinningBinarySystem Inner;
        public PnTerms[] EnergyTerms;
        public double[] E0, Worst;
        public readonly int Dimension => SpinningBinarySystem.StateSize;
        public readonly void Derivative(ReadOnlySpan<double> y, Span<double> dy) => Inner.Derivative(y, dy);
        public ScatterFate Classify(ReadOnlySpan<double> y, out int body) => Inner.Classify(y, out body);
        public readonly double MaxStep(ReadOnlySpan<double> y) => Inner.MaxStep(y);
        public readonly void Observe(ReadOnlySpan<double> y, double h)
        {
            for (int i = 0; i < EnergyTerms.Length; i++)
                Worst[i] = Math.Max(Worst[i], Math.Abs(SpinningBinarySystem.Energy(M1, M2, y, EnergyTerms[i]) - E0[i]) / Math.Abs(E0[i]));
        }
    }

    [Fact]
    public void Spin_Spin_Terms_Conserve_Their_Energy_Exactly()
    {
        var d = Drift(PnTerms.SpinSpin, 6, 0.38, 0.9, 2.2, 5, PnTerms.SpinSpin, PnTerms.None);
        Assert.True(d[0] < 1e-8, $"E_N + E_SS drift {d[0]:E2}");
        Assert.True(d[1] > 100 * d[0], $"dropping E_SS barely matters ({d[1]:E2} vs {d[0]:E2}): SS term not live");
    }

    // Each truncated system conserves its own Kidder energy only to the next order, so the check is
    // relative and by scaling: the drift with the piece's energy term, over the drift without it, is
    // small and shrinks as the orbit widens (a wrong coefficient leaves it at O(1)).
    private static double Ratio(PnTerms dynamics, PnTerms with, PnTerms without, double r0)
    {
        var d = Drift(dynamics, r0, 0.9 * Math.Sqrt(1 / r0), 0.7, 2.0, 3, with, without);
        return d[0] / d[1];
    }

    [Theory]
    [InlineData(PnTerms.PN1, PnTerms.PN1, PnTerms.None)]
    [InlineData(PnTerms.PN1 | PnTerms.PN2, PnTerms.PN1 | PnTerms.PN2, PnTerms.PN1)]
    [InlineData(PnTerms.SpinOrbit, PnTerms.SpinOrbit, PnTerms.None)]
    [InlineData(PnTerms.Conservative, PnTerms.Conservative, PnTerms.None)]
    public void Each_Piece_Conserves_Kidder_Energy_To_Its_Order(PnTerms dynamics, PnTerms with, PnTerms without)
    {
        double q40 = Ratio(dynamics, with, without, 40), q80 = Ratio(dynamics, with, without, 80);
        Assert.True(q40 < 0.2, $"{dynamics}: energy ratio {q40:F3} at r = 40");
        Assert.True(q80 < 0.8 * q40, $"{dynamics}: ratio does not shrink with r ({q40:F3} -> {q80:F3})");
    }

    [Fact]
    public void Total_Angular_Momentum_And_Spin_Lengths_Are_Conserved()
    {
        foreach (var (dyn, name) in new[] { (PnTerms.PN1 | PnTerms.PN2, "PN"), (PnTerms.SpinOrbit, "SO"), (PnTerms.Conservative, "all") })
        {
            double q40 = JRatio(dyn, 40), q80 = JRatio(dyn, 80);
            Assert.True(q40 < 0.3, $"{name}: J ratio {q40:F3} at r = 40");
            Assert.True(q80 < 0.8 * q40, $"{name}: J ratio does not shrink with r ({q40:F3} -> {q80:F3})");
        }
        // Spin-spin coupling only trades angular momentum between L_N and the spins.
        JRatio(PnTerms.SpinSpin, 10, out double ssDrift);
        Assert.True(ssDrift < 1e-9, $"spin-spin J drift {ssDrift:E2}");

        var scene = new SpinningBinaryScene(1.0 / 3, 0.6, 0.6, 0, 60, 15, 0.26, false, SpinningBinaryOutcomeMode.Fates, 2, 1000, 5, 1e-12);
        Span<double> s = stackalloc double[SpinningBinarySystem.StateSize];
        scene.LaunchState(1.1, 2.4, s);
        double s1 = Math.Sqrt(s[6] * s[6] + s[7] * s[7] + s[8] * s[8]), s2 = Math.Sqrt(s[9] * s[9] + s[10] * s[10] + s[11] * s[11]);
        Assert.Equal(2, scene.Trace(1.1, 2.4, s).Gate);
        Assert.Equal(s1, Math.Sqrt(s[6] * s[6] + s[7] * s[7] + s[8] * s[8]), 9);
        Assert.Equal(s2, Math.Sqrt(s[9] * s[9] + s[10] * s[10] + s[11] * s[11]), 9);
    }

    // Worst |J - J0| with Kidder's J (Eqs. 10-11d, terms matching the dynamics) over the same for L_N + S.
    private static double JRatio(PnTerms dynamics, double r0) => JRatio(dynamics, r0, out _);

    private static double JRatio(PnTerms dynamics, double r0, out double absolute)
    {
        var s = new double[SpinningBinarySystem.StateSize];
        s[0] = r0; s[4] = 0.9 * Math.Sqrt(1 / r0);
        double a1 = 0.6 * M1 * M1, a2 = 0.6 * M2 * M2;
        s[6] = a1 * Math.Sin(0.7); s[8] = a1 * Math.Cos(0.7);
        s[9] = a2 * Math.Sin(2.0) * Math.Cos(1.0); s[10] = a2 * Math.Sin(2.0) * Math.Sin(1.0); s[11] = a2 * Math.Cos(2.0);
        var w = new JWatch
        {
            Inner = new SpinningBinarySystem { M1 = M1, M2 = M2, Terms = dynamics, MergeRadius = 1, SeparateRadius = 1e5, MaxOrbitAngle = 2 * Math.PI * 3, End = -1 },
            JTerms = dynamics, J0 = SpinningBinarySystem.TotalAngularMomentum(M1, M2, s, dynamics),
            N0 = SpinningBinarySystem.TotalAngularMomentum(M1, M2, s, PnTerms.None), Worst = new double[2],
        };
        var settings = new ScatterSettings { RelTol = 1e-12, AbsTol = 1e-15, MaxSteps = 10_000_000, InitialStep = 0.01 };
        ScatteringIntegrator.Trace(ref w, s, in settings);
        absolute = w.Worst[0];
        return w.Worst[0] / Math.Max(1e-300, w.Worst[1]);
    }

    private struct JWatch : IScatteringSystem
    {
        public SpinningBinarySystem Inner;
        public PnTerms JTerms;
        public (double X, double Y, double Z) J0, N0;
        public double[] Worst;
        public readonly int Dimension => SpinningBinarySystem.StateSize;
        public readonly void Derivative(ReadOnlySpan<double> y, Span<double> dy) => Inner.Derivative(y, dy);
        public ScatterFate Classify(ReadOnlySpan<double> y, out int body) => Inner.Classify(y, out body);
        public readonly double MaxStep(ReadOnlySpan<double> y) => Inner.MaxStep(y);
        public readonly void Observe(ReadOnlySpan<double> y, double h)
        {
            var j = SpinningBinarySystem.TotalAngularMomentum(M1, M2, y, JTerms);
            var n = SpinningBinarySystem.TotalAngularMomentum(M1, M2, y, PnTerms.None);
            Worst[0] = Math.Max(Worst[0], Dist(j, J0));
            Worst[1] = Math.Max(Worst[1], Dist(n, N0));
        }
        private static double Dist((double X, double Y, double Z) a, (double X, double Y, double Z) b)
            => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));
    }

    [Fact]
    public void Without_Spins_Or_Radiation_The_Orbit_Is_Planar_And_The_Map_Is_One_Colour()
    {
        var p = new FractalParameters { SpinBinChi1 = 0, SpinBinChi2 = 0, SpinBinMaxOrbits = 10 };
        var c = Render(p, 16, 10);
        Assert.Single(c.GateBuffer.Distinct());
        var scene = SpinningBinaryScene.Build(p);
        Span<double> s = stackalloc double[SpinningBinarySystem.StateSize];
        scene.Trace(0.5, 1.5, s);
        Assert.Equal(0.0, s[2]);
        Assert.Equal(0.0, s[5]);
    }

    [Theory]
    [InlineData(20.0)]
    [InlineData(40.0)]
    public void Radiation_Reaction_Merges_A_Newtonian_Circular_Binary_In_The_Peters_Time(double r0)
    {
        // Peters (1964): circular coalescence time T = a^4 / (4 beta), beta = (64/5) m1 m2 (m1 + m2),
        // i.e. 5 a^4 / (256 mu m^2) with G = c = 1, for Newtonian orbits decaying under the quadrupole
        // loss. Newtonian dynamics plus Kidder's a_RR (Eq. 4f) only, so leading order against leading
        // order: within 1 %.
        var s = new double[SpinningBinarySystem.StateSize];
        s[0] = r0; s[4] = Math.Sqrt(1 / r0);
        var sys = new SpinningBinarySystem { M1 = 0.5, M2 = 0.5, Terms = PnTerms.RadiationReaction, MergeRadius = 1.0, SeparateRadius = 1e5, MaxOrbitAngle = 1e12, End = -1 };
        var settings = new ScatterSettings { RelTol = 1e-10, AbsTol = 1e-13, MaxSteps = 50_000_000, InitialStep = 0.01 };
        var o = ScatteringIntegrator.Trace(ref sys, s, in settings);
        Assert.Equal(0, sys.End);   // merged
        double peters = 5 * (Math.Pow(r0, 4) - 1.0) / (256 * 0.25);   // from r0 down to the merge radius 1
        Assert.InRange(o.Lambda / peters, 0.99, 1.01);
    }

    [Fact]
    public void With_Radiation_Reaction_On_A_Close_Binary_Always_Merges()
    {
        var p = new FractalParameters { SpinBinRadiationReaction = true };
        var c = Render(p, 12, 8);
        Assert.All(c.GateBuffer, g => Assert.Equal(0, g));
    }

    private static SpinningBinaryCalculator Render(FractalParameters p, int w, int h, double cx = 0, double cy = 0,
                                                   double zoom = SpinningBinaryCalculator.DefaultZoom, SpinningBinaryCalculator? reuse = null)
    {
        var c = reuse ?? new SpinningBinaryCalculator(w, h) { ColorMap = new HsvPalette() };
        c.FractalParameters = p;
        c.CenterX = cx; c.CenterY = cy; c.Zoom = zoom;
        c.Calculate(TestContext.Current.CancellationToken);
        return c;
    }

    private static (FractalRegion Region, FractalParameters P) Preset(string name)
    {
        var region = FractalRegionLibrary.Instance.BuiltIns.Single(r => r.Name == name);
        Assert.Equal(FractalType.SpinningBinaryBasins, region.FractalType);
        var p = new FractalParameters();
        region.Params!.ApplyTo(p);
        return (region, p);
    }

    private static FractalParameters ViaCommandLine(FractalParameters p)
    {
        var snap = RegionFractalParams.Snapshot(FractalType.SpinningBinaryBasins, p)!;
        var report = BatchCommandBuilder.BuildWithReport(new BatchCommandSnapshot
        {
            Fractal = FractalType.SpinningBinaryBasins, Parameters = p, FamilyParams = snap.ToKeyValues(),
        });
        Assert.DoesNotContain(report.Gaps, g => g.Contains("SpinBin", StringComparison.OrdinalIgnoreCase));
        Assert.True(BatchOptions.TryParse(report.Args.ToArray(), 0, out var o, out var err), err);
        var q = new FractalParameters();
        RegionFractalParams.FromKeyValues(o.Params, FractalType.SpinningBinaryBasins, out var perr)!.ApplyTo(q);
        Assert.Null(perr);
        return q;
    }

    [Fact]
    public void Every_Setting_Survives_The_Region_Snapshot_And_The_Command_Line()
    {
        var p = new FractalParameters
        {
            SpinBinMassRatio = 0.42, SpinBinChi1 = 0.71, SpinBinChi2 = 0.33, SpinBinPhi1 = 12.5, SpinBinPhi2 = -40,
            SpinBinSeparation = 9.5, SpinBinVelocity = 0.31, SpinBinRadiationReaction = true,
            SpinBinOutcome = SpinningBinaryOutcomeMode.MergerSide, SpinBinMergeRadius = 2.5, SpinBinSeparateRadius = 500,
            SpinBinMaxOrbits = 33, SpinBinTolerance = 3e-10, AdaptiveAa = true,
        };
        var q = ViaCommandLine(p);
        foreach (var prop in typeof(FractalParameters).GetProperties().Where(x => x.Name.StartsWith("SpinBin", StringComparison.Ordinal)))
            Assert.Equal(prop.GetValue(p), prop.GetValue(q));
        Assert.True(q.AdaptiveAa);
        Assert.True(AdaptiveAaCapabilities.Supports(FractalType.SpinningBinaryBasins));
    }

    [Fact]
    public void Every_Preset_Renders_The_Same_Live_From_The_Command_Line_And_As_A_Poster()
    {
        foreach (var name in Presets)
        {
            var (region, p) = Preset(name);
            p.SpinBinMaxOrbits = Math.Min(p.SpinBinMaxOrbits, 5);   // keep the test quick; same settings on every path
            var live = Render(p, 12, 8, region.CenterX, region.CenterY, region.Zoom);
            var cli = Render(ViaCommandLine(p), 12, 8, region.CenterX, region.CenterY, region.Zoom);
            Assert.True(live.ColorBuffer.SequenceEqual(cli.ColorBuffer), $"{name}: --param render differs from live");
            var req = new PosterRequest
            {
                FractalType = FractalType.SpinningBinaryBasins, Width = 12, Height = 8,
                CenterX = region.CenterX, CenterY = region.CenterY, Zoom = region.Zoom, MaxIterations = live.MaxIterations,
                Quality = QualityPreset.Standard, FractalParameters = p, ColorMap = new HsvPalette(),
            };
            var poster = PosterRenderer.RenderToPixels(req, TestContext.Current.CancellationToken, out _, out _);
            Assert.True(live.ColorBuffer.SequenceEqual(poster), $"{name}: poster differs from live");
        }
    }

    [Theory]
    [InlineData("Spinning Binary - Levin Conservative", "SpinBinChi1")]
    [InlineData("Spinning Binary - Conservative Close-Up", "SpinBinVelocity")]
    public void Animated_Frames_Equal_The_Stills_At_Each_Frames_Value(string presetName, string param)
    {
        var lib = AnimationLibrary.Instance;
        lib.Load();
        var (region, p0) = Preset(presetName);
        p0.SpinBinMaxOrbits = 5;
        var anim = lib.GetByName(region.AnimationName!)!;
        Assert.Contains(FractalType.SpinningBinaryBasins, anim.TargetFractalTypes);
        var animatable = FractalAnimatableParamsMap.For(FractalType.SpinningBinaryBasins).Select(d => d.ParamName).ToHashSet();
        Assert.All(anim.Tracks, t => Assert.Contains(t.ParamName, animatable));
        var prop = typeof(FractalParameters).GetProperty(param)!;
        const int fps = 2;
        var video = p0.Clone();
        SpinningBinaryCalculator? calc = null;
        var seen = new List<double>();
        foreach (int frame in new[] { 0, 9, 17, 26 })
        {
            anim.ApplyAt(video, frame / (double)fps);
            double value = (double)prop.GetValue(video)!;
            seen.Add(value);
            calc = Render(video, 12, 8, region.CenterX, region.CenterY, region.Zoom, calc);
            var frameColours = (uint[])calc.ColorBuffer.Clone();
            var still = p0.Clone();
            prop.SetValue(still, value);
            var stillCalc = Render(ViaCommandLine(still), 12, 8, region.CenterX, region.CenterY, region.Zoom);
            Assert.True(frameColours.SequenceEqual(stillCalc.ColorBuffer), $"{param} = {value}: frame {frame} differs from the still");
        }
        Assert.True(seen.Distinct().Count() >= 3, $"{param} barely moved: {string.Join(", ", seen)}");
    }

    [Fact]
    public void A_Masked_Frame_Computes_Only_Marked_Pixels_And_Matches_The_Full_Frame_There()
    {
        const int W = 16, H = 10;
        var p = new FractalParameters { SpinBinMaxOrbits = 5 };
        var full = Render(p, W, H);
        var masked = Render(p, W, H);
        Array.Fill(masked.ColorBuffer, 0x12345678u);
        var mask = new byte[W * H];
        var rng = new Random(1945);
        for (int i = 0; i < mask.Length; i++) mask[i] = (byte)(rng.Next(3) == 0 ? 1 : 0);
        masked.PixelMask = mask;
        Assert.True(masked.MaskExact);
        masked.Calculate(TestContext.Current.CancellationToken);
        masked.PixelMask = null;
        for (int i = 0; i < mask.Length; i++)
            Assert.Equal(mask[i] != 0 ? full.ColorBuffer[i] : 0x12345678u, masked.ColorBuffer[i]);
    }

    [Fact]
    public void Help_Names_Presets_Animations_Sources_And_The_Dispute()
    {
        string help = HelpTextBundle.MathSpinningBinaryText.Replace("\r\n", "\n");
        Assert.All(Presets, n => Assert.Contains(n, help));
        Assert.All(Animations, a => Assert.Contains(a, help));
        foreach (var source in new[] { "Levin", "Kidder", "Hughes", "Cornish", "Schnittman", "Hartl", "Peters", "fractal-looking at finite\nresolution" })
            Assert.Contains(source, help);
    }
}
