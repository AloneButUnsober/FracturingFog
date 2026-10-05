// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FracturingFog;
using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.Imaging;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

// #1124 (epic #1114 S10) — Dual Buddhabrot. Oracles are independent of the new
// code: the classic BuddhabrotCalculator (Z channel), the c = 0 degeneracy
// (c-orbit ≡ z-orbit), M_c ⊂ M, mirror asymmetry statistics, a fresh render
// (recolour guard), and the CLI / region round trip.
public sealed class DualBuddhabrotTests
{
    private sealed class RampMap : IColorMap
    {
        public ColorPaletteType Type => ColorPaletteType.GradientLinear;
        public int MaxIterations { get; set; }
        public uint InSetColor => 0xFF000000u;
        public int Map(float smooth, float distance, int iterations)
            => unchecked((int)(0xFF000000u | ((uint)(smooth * 997f) & 0x00FFFFFFu)));
    }

    private static FractalParameters Params(double cx = 0.5, double cy = 0.0, int minIter = 12,
        BuddhaQualityMode q = BuddhaQualityMode.Standard, bool mh = false, int samples = 40_000)
        => new()
        {
            BuddhaSamples = samples, BuddhaIterHigh = 400, BuddhaQualityMode = q, BuddhaMetropolis = mh,
            BuddhaZoomCompensation = false, BuddhaSeed = 777,
            DualBuddhaCSeedX = cx, DualBuddhaCSeedY = cy, DualBuddhaMinIter = minIter,
        };

    private static T Render<T>(T calc, FractalParameters p, int size = 96) where T : BuddhaFamilyCalculator
    {
        calc.CenterX = -0.5; calc.CenterY = 0; calc.Zoom = 1; calc.MaxIterations = 400;
        calc.FractalParameters = p; calc.ColorMap = new RampMap();
        calc.Calculate();
        return calc;
    }

    private static DualBuddhabrotCalculator Dual(FractalParameters p, int size = 96)
        => Render(new DualBuddhabrotCalculator(size, size), p, size);

    // With uniform sampling and no escape cut the Z channel IS the classic
    // Buddhabrot (sum of its three iteration bands), hit for hit — in both
    // qualities (the c splats draw from a separate RNG stream).
    [Theory]
    [InlineData(BuddhaQualityMode.Standard)]
    [InlineData(BuddhaQualityMode.HighDefinition)]
    public void ZChannel_EqualsTheClassicBuddhabrot(BuddhaQualityMode q)
    {
        var p = Params(minIter: 0, q: q);
        var classic = Render(new BuddhabrotCalculator(96, 96), p.Clone());
        var dual = Dual(p);
        var total = classic.HitsR.Zip(classic.HitsG, (a, b) => a + b).Zip(classic.HitsB, (a, b) => a + b).ToArray();
        Assert.True(total.Sum(v => (long)v) > 1000);
        Assert.Equal(total, dual.HitsR);
    }

    // c = 0: the c-orbit is the z-orbit, so nothing escapes with z bounded (CB
    // empty) and the both-escaped channel reproduces Z exactly.
    [Fact]
    public void ZeroSeed_IsDegenerate_CBEmpty_CEEqualsZ()
    {
        var dual = Dual(Params(cx: 0, cy: 0));
        Assert.All(dual.HitsG, h => Assert.Equal(0u, h));
        Assert.Equal(dual.HitsR, dual.HitsB);
    }

    // M_c ⊂ M: "z escaped while c stays bounded" is (to the iteration cap)
    // impossible; the new CB channel is populated.
    [Theory]
    [InlineData(0.5, 0.0)]
    [InlineData(-0.3, 0.6)]
    public void McInsideM_AndCBChannelIsPopulated(double cx, double cy)
    {
        var dual = Dual(Params(cx, cy, samples: 60_000));
        Assert.True(dual.OutcomeCounts[3] < 60_000 * 1e-3, $"z escaped / c bounded: {dual.OutcomeCounts[3]}");
        Assert.True(dual.OutcomeCounts[1] > 100, $"CB deposits: {dual.OutcomeCounts[1]}");
        Assert.True(dual.HitsG.Sum(v => (long)v) > 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SameParams_RenderIdentically(bool mh)
    {
        var a = Dual(Params(q: BuddhaQualityMode.HighDefinition, mh: mh));
        var b = Dual(Params(q: BuddhaQualityMode.HighDefinition, mh: mh));
        Assert.Equal(a.HitsR, b.HitsR); Assert.Equal(a.HitsG, b.HitsG); Assert.Equal(a.HitsB, b.HitsB);
        Assert.Equal(a.ColorBuffer, b.ColorBuffer);
    }

    // HD mirror: an off-axis c-seed must NOT be mirrored — conj(s) carries the
    // orbit of conj(c). The Z channel (mirrored) is up/down symmetric to noise;
    // the CB channel of c = −0.3+0.6i is strongly asymmetric. Mirroring it
    // anyway would force it symmetric.
    [Fact]
    public void HdMirror_IsOffForAnOffAxisSeed()
    {
        var dual = Dual(Params(-0.3, 0.6, q: BuddhaQualityMode.HighDefinition, samples: 150_000), size: 128);
        double zAsym = Asymmetry(dual.HitsR, 128), cbAsym = Asymmetry(dual.HitsG, 128);
        Assert.True(zAsym < 0.25, $"Z asymmetry {zAsym}");
        Assert.True(cbAsym > 2 * zAsym && cbAsym > 0.3, $"CB asymmetry {cbAsym} vs Z {zAsym}");

        // A real seed keeps the mirror (and the symmetry).
        var real = Dual(Params(0.5, 0.0, q: BuddhaQualityMode.HighDefinition, samples: 150_000), size: 128);
        Assert.True(Asymmetry(real.HitsG, 128) < 0.3, $"real-seed CB asymmetry {Asymmetry(real.HitsG, 128)}");

        static double Asymmetry(uint[] h, int n)
        {
            double diff = 0, sum = 0;
            for (int y = 0; y < n / 2; y++)
                for (int x = 0; x < n; x++)
                {
                    double a = h[y * n + x], b = h[(n - 1 - y) * n + x];
                    diff += Math.Abs(a - b); sum += a + b;
                }
            return sum > 0 ? diff / sum : 0;
        }
    }

    // Colour-only parameters re-composite the cached hits; everything that
    // feeds the sampler re-samples. Oracle: a fresh calculator.
    private static readonly HashSet<string> ColourOnly = new()
    {
        nameof(FractalParameters.DualBuddhaComposite),
        nameof(FractalParameters.DualBuddhaColorZ), nameof(FractalParameters.DualBuddhaColorCB), nameof(FractalParameters.DualBuddhaColorCE),
        nameof(FractalParameters.DualBuddhaGainZ), nameof(FractalParameters.DualBuddhaGainCB), nameof(FractalParameters.DualBuddhaGainCE),
        nameof(FractalParameters.BuddhaColorMode),   // classic band composite — unused by the dual composite
    };

    public static IEnumerable<object[]> SamplerAndColourParams()
        => typeof(FractalParameters).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(pi => (pi.Name.StartsWith("DualBuddha", StringComparison.Ordinal) || pi.Name.StartsWith("Buddha", StringComparison.Ordinal))
                      && pi.CanWrite)
            .Select(pi => new object[] { pi.Name });

    [Theory]
    [MemberData(nameof(SamplerAndColourParams))]
    public void EveryParameter_IsClassified_AndReuseMatchesAFreshRender(string name)
    {
        var p = Params(samples: 8_000);
        var calc = Dual(p, 48);
        var pi = typeof(FractalParameters).GetProperty(name)!;
        pi.SetValue(p, Mutate(pi.GetValue(p)));
        calc.Calculate();
        if (ColourOnly.Contains(name)) Assert.True(calc.LastCalculateReusedSamples, $"{name} is colour-only but re-sampled");
        else Assert.False(calc.LastCalculateReusedSamples, $"{name} did not invalidate the samples — add it to the sample key, or to ColourOnly");
        Assert.Equal(Dual(p.Clone(), 48).ColorBuffer, calc.ColorBuffer);

        static object? Mutate(object? v) => v switch
        {
            double d => d + 0.137,
            int i => i + 1_000,
            uint u => u ^ 0x00A5A5A5u,
            bool b => !b,
            Enum e => Enum.GetValues(e.GetType()).GetValue(
                (Array.IndexOf(Enum.GetValues(e.GetType()), e) + 1) % Enum.GetValues(e.GetType()).Length),
            _ => v,
        };
    }

    [Fact]
    public void ClassicBuddhabrot_NeverReusesSamples()
    {
        var c = Render(new BuddhabrotCalculator(32, 32), Params(samples: 2_000));
        c.Calculate();
        Assert.False(c.LastCalculateReusedSamples);
    }

    [Fact]
    public void ProgressiveCallback_ForcesARealRun()
    {
        var p = Params(samples: 4_000); p.BuddhaProgressive = true;
        var calc = Dual(p, 32);
        int calls = 0;
        calc.OnBatchComposited = (_, _) => calls++;
        calc.Calculate();
        Assert.False(calc.LastCalculateReusedSamples);
        Assert.True(calls > 0);
    }

    // Channels composite: a single visible channel is that channel's colour
    // scaled by its own log-normalised density.
    [Fact]
    public void ChannelsComposite_SingleChannel_IsItsTintedLogDensity()
    {
        var p = Params(); p.DualBuddhaGainZ = 0; p.DualBuddhaGainCE = 0; p.DualBuddhaColorCB = 0xFFFFFFFFu;
        var calc = Dual(p);
        uint max = calc.HitsG.Max();
        for (int i = 0; i < calc.HitsG.Length; i += 7)
        {
            int v = (int)(Math.Log(calc.HitsG[i] + 1.0) / Math.Log(max + 1.0) * 255 + 0.5);
            Assert.Equal(0xFF000000u | ((uint)v << 16) | ((uint)v << 8) | (uint)v, calc.ColorBuffer[i]);
        }
    }

    [Fact]
    public void ThemeComposite_EmptyPixels_AreTheInSetColour()
    {
        var p = Params(); p.DualBuddhaComposite = DualBuddhaComposite.Theme;
        var calc = Dual(p);
        for (int i = 0; i < calc.ColorBuffer.Length; i++)
            if (calc.HitsR[i] + calc.HitsG[i] + calc.HitsB[i] == 0) Assert.Equal(0xFF000000u, calc.ColorBuffer[i]);
    }

    // Batch parity: the live view's dual + sampler settings reach the CLI
    // command and back.
    [Fact]
    public void LiveView_RoundTripsThroughTheCommandBuilder()
    {
        var p = new FractalParameters
        {
            DualBuddhaCSeedX = -0.3, DualBuddhaCSeedY = 0.6, DualBuddhaMinIter = 20,
            DualBuddhaComposite = DualBuddhaComposite.Theme, DualBuddhaGainCB = 1.7, DualBuddhaColorCE = 0xFF336699u,
            BuddhaSamples = 2_000_000, BuddhaQualityMode = BuddhaQualityMode.HighDefinition, BuddhaSeed = 4,
        };
        var report = BatchCommandBuilder.BuildWithReport(new BatchCommandSnapshot
        {
            Fractal = FractalType.DualBuddhabrot, Parameters = p,
            FamilyParams = RegionFractalParams.Snapshot(FractalType.DualBuddhabrot, p)!.ToKeyValues(),
        });
        Assert.Contains("--param DualBuddhaCSeedY=0.6", report.Command);
        Assert.True(BatchOptions.TryParse(report.Args.ToArray(), 0, out var o, out var err), err);
        var fresh = new FractalParameters();
        RegionFractalParams.FromKeyValues(o.Params, FractalType.DualBuddhabrot, out _)!.ApplyTo(fresh);
        Assert.Equal(-0.3, fresh.DualBuddhaCSeedX);
        Assert.Equal(0.6, fresh.DualBuddhaCSeedY);
        Assert.Equal(20, fresh.DualBuddhaMinIter);
        Assert.Equal(DualBuddhaComposite.Theme, fresh.DualBuddhaComposite);
        Assert.Equal(1.7, fresh.DualBuddhaGainCB);
        Assert.Equal(0xFF336699u, fresh.DualBuddhaColorCE);
        Assert.Equal(2_000_000, fresh.BuddhaSamples);
        Assert.Equal(BuddhaQualityMode.HighDefinition, fresh.BuddhaQualityMode);
        Assert.Equal(4, fresh.BuddhaSeed);
    }

    [Fact]
    public void Defaults_AreOmittedFromTheRegionSnapshot()
    {
        var snap = RegionFractalParams.Snapshot(FractalType.DualBuddhabrot, new FractalParameters());
        Assert.True(snap == null || snap.ToKeyValues().Count == 0);
    }

    [Fact]
    public void Registered_AsANonSpatialBuddhaFamilyType_WithAnOfflineCalculator()
    {
        Assert.Equal(FractalMotionClass.NonSpatial, FractalMotionCapabilities.MotionClass(FractalType.DualBuddhabrot));
        var c = PosterRenderer.BuildCaptureCalculator(new PosterRequest
        {
            FractalType = FractalType.DualBuddhabrot, Width = 16, Height = 16, Zoom = 1, MaxIterations = 64,
            Quality = QualityPreset.Standard, ColorMap = new HsvPalette(), FractalParameters = new FractalParameters(),
        });
        Assert.IsType<DualBuddhabrotCalculator>(c);
    }
}
