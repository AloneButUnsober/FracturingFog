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

// #1946 — Mixmaster (Bianchi IX), Cornish & Levin 1997. Independent checks:
//   • the constraint sign: along the papers' τ equations (no FF stabilisation), H with −¼P is conserved
//     and H with +¼P is not (the two papers print opposite signs);
//   • the launches satisfy the constraint (Kasner: kinetic term zero and P(Ω0) = 0, both re-derived here);
//   • arc-length time changes only the clock: for quick-exiting universes the outcome traced in τ (plain
//     DP5 with the papers' equations, no projection) equals FF's;
//   • the equations are symmetric under relabelling the axes: permuting a, b, c permutes the derivative
//     exactly and the outcome label;
//   • BKL: successive Kasner epochs in the full dynamics follow the u-map u → u − 1 / 1/(u − 1) (PRD eq. 10);
//   • the u-map's topological entropy is ln 2 (PRD eq. 32): period-k points counted by forward checks;
//   • FF's constraint stabilisation keeps |H| / Σ(ln x)′² tiny to the exit;
//   • parity: snapshot / CLI / poster / animation / masked AA; help names everything.
public sealed class S1946MixmasterTests
{
    private static readonly string[] Presets =
    {
        "Mixmaster - Kasner Basins", "Mixmaster - Kasner Close-Up", "Mixmaster - Late Exit", "Mixmaster - Anisotropy Plane",
    };
    private static readonly string[] Animations = { "Mixmaster: exit threshold", "Mixmaster: volume sweep" };

    // Plain τ system: the papers' equations, no reparametrisation, no projection.
    private struct TauSystem : IScatteringSystem
    {
        public MixmasterSystem Exit;
        public double MaxH0, MaxH1, K0;
        public readonly int Dimension => 6;
        public readonly void Derivative(ReadOnlySpan<double> y, Span<double> dy) => MixmasterSystem.DerivativeTau(y, dy);
        public ScatterFate Classify(ReadOnlySpan<double> y, out int body) => Exit.Classify(y, out body);
        public readonly double MaxStep(ReadOnlySpan<double> y) => 0.02;
        public void Observe(ReadOnlySpan<double> y, double h)
        {
            Exit.Observe(y, h);
            MaxH0 = Math.Max(MaxH0, Math.Abs(MixmasterSystem.Hamiltonian(y, -1.0)));
            MaxH1 = Math.Max(MaxH1, Math.Abs(MixmasterSystem.Hamiltonian(y, +1.0) - K0));
        }
    }

    [Fact]
    public void Only_The_Minus_Sign_Constraint_Is_Conserved_By_The_Equations()
    {
        var scene = new MixmasterScene(MixmasterView.KasnerUV, 8, 1.0 / 3, -1, 10, 10, 1e30, 1e-9);
        var s = new double[6];
        Assert.True(scene.LaunchState(1.4, 0.5, s));
        var sys = new TauSystem { Exit = new MixmasterSystem { UExit = 8, TimeCap = 60, Gate = -1 }, K0 = MixmasterSystem.Hamiltonian(s, +1.0) };
        var settings = new ScatterSettings { RelTol = 1e-11, AbsTol = 1e-300, MaxSteps = 1_000_000, InitialStep = 1e-3 };
        ScatteringIntegrator.Trace(ref sys, s, in settings);
        Assert.True(sys.MaxH0 < 1e-9, $"minus-sign H drift {sys.MaxH0:E2}");
        Assert.True(sys.MaxH1 > 1e-3, $"plus-sign H barely moved ({sys.MaxH1:E2}): no bounce exercised");
    }

    [Fact]
    public void Kasner_Launch_Satisfies_The_Constraint()
    {
        var scene = new MixmasterScene(MixmasterView.KasnerUV, 8, 1.0 / 3, -1, 10, 10, 1e30, 1e-9);
        var s = new double[6];
        foreach (var (u, v) in new[] { (1.0, 0.5), (1.3, 0.2), (1.9, 0.9), (3.7, 2.0) })
        {
            Assert.True(scene.LaunchState(u, v, s));
            // Kasner: Σp = Σp² = 1 → Σ_{i<j} p_i p_j = 0.
            double d = 1 + u + u * u, pa = (1 + u) / d, pb = -u / d, pc = (u + u * u) / d;
            Assert.Equal(1.0, pa + pb + pc, 12);
            Assert.Equal(1.0, pa * pa + pb * pb + pc * pc, 12);
            double a = Math.Exp(s[0]), b = Math.Exp(s[1]), c = Math.Exp(s[2]);
            double pot = Math.Pow(a, 4) + Math.Pow(b, 4) + Math.Pow(c, 4) - 2 * (a * a * b * b + b * b * c * c + c * c * a * a);
            Assert.True(Math.Abs(pot) < 1e-12, $"P(Omega0) = {pot:E2}");
            Assert.True(Math.Abs(MixmasterSystem.Hamiltonian(s)) < 1e-12);
        }
        var an = new MixmasterScene(MixmasterView.Anisotropy, 8, 1.0 / 3, -1, 10, 10, 1e30, 1e-9);
        Assert.True(an.LaunchState(-2.3, 0.4, s));
        Assert.True(Math.Abs(MixmasterSystem.Hamiltonian(s)) < 1e-9);
        // Omega = ln(abc)/3 is the set volume, and (β+, β−) round-trip through PRD eqs. 70-72.
        Assert.Equal(-1.0, (s[0] + s[1] + s[2]) / 3, 12);
        Assert.Equal(-2.3, (s[1] + s[2] - 2 * s[0]) / 3, 12);
        Assert.Equal(0.4, (s[1] - s[2]) / Math.Sqrt(3), 12);
    }

    [Fact]
    public void Arc_Length_Time_Gives_The_Same_Outcome_As_The_Papers_Time()
    {
        var scene = new MixmasterScene(MixmasterView.KasnerUV, 8, 1.0 / 3, -1, 10, 10, 1e30, 1e-10);
        var rng = new Random(1946);
        int compared = 0;
        var s = new double[6];
        var t = new double[6];
        for (int i = 0; i < 400 && compared < 30; i++)
        {
            double u = 1 + rng.NextDouble(), v = 0.2 + 0.6 * rng.NextDouble();
            var o = scene.Trace(u, v, s);
            if (o.Gate < 0 || o.Steps > 400) continue;   // quick exits only (few bounces, τ stays moderate)
            scene.LaunchState(u, v, t);
            var sys = new TauSystem { Exit = new MixmasterSystem { UExit = 8, TimeCap = 1e6, Gate = -1 } };
            var settings = new ScatterSettings { RelTol = 1e-11, AbsTol = 1e-300, MaxSteps = 2_000_000, InitialStep = 1e-3 };
            var ot = ScatteringIntegrator.Trace(ref sys, t, in settings);
            if (ot.Fate != ScatterFate.Escaped) continue;
            Assert.Equal(o.Gate, sys.Exit.Gate);
            compared++;
        }
        Assert.True(compared >= 10, $"only {compared} quick exits compared");
    }

    [Fact]
    public void Relabelling_The_Axes_Permutes_The_Equations_And_The_Outcome()
    {
        var rng = new Random(7);
        Span<double> y = stackalloc double[6], p = stackalloc double[6], dy = stackalloc double[6], dp = stackalloc double[6];
        for (int i = 0; i < 20; i++)
        {
            for (int k = 0; k < 6; k++) y[k] = rng.NextDouble() * 2 - 1;
            // (a, b, c) -> (b, c, a)
            p[0] = y[1]; p[1] = y[2]; p[2] = y[0]; p[3] = y[4]; p[4] = y[5]; p[5] = y[3];
            MixmasterSystem.DerivativeTau(y, dy);
            MixmasterSystem.DerivativeTau(p, dp);
            Assert.Equal(dy[4], dp[3], 14); Assert.Equal(dy[5], dp[4], 14); Assert.Equal(dy[3], dp[5], 14);
        }
        var scene = new MixmasterScene(MixmasterView.KasnerUV, 8, 1.0 / 3, -1, 10, 10, 1e30, 1e-9);
        var s = new double[6];
        int checkedN = 0;
        for (int i = 0; i < 40; i++)
        {
            double u = 1 + rng.NextDouble(), v = 0.2 + 0.6 * rng.NextDouble();
            scene.LaunchState(u, v, s);
            var sys = scene.System();
            var settings = scene.Settings;
            var o = ScatteringIntegrator.Trace(ref sys, s.ToArray().AsSpan(), in settings);
            if (o.Fate != ScatterFate.Escaped || o.Steps > 300) continue;
            var q = new double[] { s[1], s[2], s[0], s[4], s[5], s[3] };
            var sq = scene.System();
            var oq = ScatteringIntegrator.Trace(ref sq, q, in settings);
            Assert.Equal(ScatterFate.Escaped, oq.Fate);
            Assert.Equal(sys.Gate, (sq.Gate + 1) % 3);   // relabelled axis k is original axis (k + 1) mod 3
            checkedN++;
        }
        Assert.True(checkedN >= 5, $"only {checkedN} checked");
    }

    private struct EpochWatch : IScatteringSystem
    {
        public MixmasterSystem Inner;
        public List<double> Epochs;
        public bool InEpoch;
        public readonly int Dimension => 6;
        public readonly void Derivative(ReadOnlySpan<double> y, Span<double> dy) => Inner.Derivative(y, dy);
        public ScatterFate Classify(ReadOnlySpan<double> y, out int body) => Inner.Classify(y, out body);
        public readonly double MaxStep(ReadOnlySpan<double> y) => Inner.MaxStep(y);
        public void Observe(ReadOnlySpan<double> y, double h)
        {
            Inner.Observe(y, h);
            bool k = MixmasterSystem.InKasnerEpoch(y, out double u, out _);
            if (k && !InEpoch) Epochs.Add(u);
            InEpoch = k;
        }
    }

    [Fact]
    public void Successive_Kasner_Epochs_Follow_The_U_Map()
    {
        var scene = new MixmasterScene(MixmasterView.KasnerUV, 1000, 1.0 / 3, -1, 10, 10, 1e30, 1e-10);
        var errors = new List<double>();
        var rng = new Random(11);
        for (int i = 0; i < 10; i++)
        {
            var s = new double[6];
            scene.LaunchState(1 + rng.NextDouble(), 0.2 + 0.6 * rng.NextDouble(), s);
            var w = new EpochWatch { Inner = scene.System() with { UExit = 30 }, Epochs = new List<double>() };
            var settings = scene.Settings with { MaxSteps = 100_000 };
            ScatteringIntegrator.Trace(ref w, s, in settings);
            for (int k = 2; k + 1 < w.Epochs.Count && k < 8; k++)   // skip the launch epoch
            {
                double u = w.Epochs[k], next = w.Epochs[k + 1];
                if (!(u > 1.05) || !double.IsFinite(next)) continue;
                errors.Add(Math.Abs(next - MixmasterSystem.UMap(u)) / MixmasterSystem.UMap(u));
            }
        }
        errors.Sort();
        Assert.True(errors.Count >= 20, $"only {errors.Count} transitions");
        double median = errors[errors.Count / 2];
        Assert.True(median < 0.05, $"median u-map error {median:F3}");
    }

    [Fact]
    public void U_Map_Topological_Entropy_Is_Ln_2()
    {
        // Period-k points: one per binary word (O = u - 1, B = 1/(u - 1)) with at least one B, built from
        // the inverse branches and kept only if the forward map really returns to it after k steps.
        double PeriodicPoints(int k)
        {
            var found = new SortedSet<double>();
            for (int w = 1; w < (1 << k); w++)
            {
                double u = 1.5;
                // Forward order is bit k-1 first, so u = F_b0(... F_b(k-1)(u)): apply the inverses
                // from bit 0 up.
                for (int it = 0; it < 200; it++)
                    for (int j = 0; j < k; j++)
                        u = ((w >> j) & 1) == 1 ? 1 + 1 / u : u + 1;   // inverse of B, of O
                double f = u;
                bool ok = true;
                for (int j = 0; j < k; j++)
                {
                    bool bounce = f < 2;
                    if (bounce != (((w >> (k - 1 - j)) & 1) == 1)) { ok = false; break; }
                    f = MixmasterSystem.UMap(f);
                }
                if (ok && Math.Abs(f - u) < 1e-9 * Math.Max(1, u)) found.Add(Math.Round(u, 9));
            }
            return found.Count;
        }
        int k1 = 10, k2 = 14;
        double h = (Math.Log(PeriodicPoints(k2)) - Math.Log(PeriodicPoints(k1))) / (k2 - k1);
        Assert.Equal(Math.Log(2), h, 2);
    }

    [Fact]
    public void Stabilised_Trace_Keeps_The_Constraint()
    {
        var scene = new MixmasterScene(MixmasterView.KasnerUV, 8, 1.0 / 3, -1, 10, 10, 1e30, 1e-9);
        var rng = new Random(3);
        var s = new double[6];
        int exited = 0;
        for (int i = 0; i < 60; i++)
        {
            var o = scene.Trace(1 + rng.NextDouble(), 0.2 + 0.6 * rng.NextDouble(), s);
            if (o.Gate >= 0) exited++;
            double k2 = s[3] * s[3] + s[4] * s[4] + s[5] * s[5];
            Assert.True(Math.Abs(MixmasterSystem.Hamiltonian(s)) < 1e-7 * k2, $"H/K = {MixmasterSystem.Hamiltonian(s) / k2:E2}");
        }
        Assert.True(exited >= 57, $"only {exited}/60 exited");
    }

    private static MixmasterCalculator Render(FractalParameters p, int w, int h, double cx, double cy, double zoom, MixmasterCalculator? reuse = null)
    {
        var c = reuse ?? new MixmasterCalculator(w, h) { ColorMap = new HsvPalette() };
        c.FractalParameters = p;
        c.CenterX = cx; c.CenterY = cy; c.Zoom = zoom;
        c.Calculate(TestContext.Current.CancellationToken);
        return c;
    }

    private static (FractalRegion Region, FractalParameters P) Preset(string name)
    {
        var region = FractalRegionLibrary.Instance.BuiltIns.Single(r => r.Name == name);
        Assert.Equal(FractalType.MixmasterBasins, region.FractalType);
        var p = new FractalParameters();
        region.Params!.ApplyTo(p);
        return (region, p);
    }

    private static FractalParameters ViaCommandLine(FractalParameters p)
    {
        var snap = RegionFractalParams.Snapshot(FractalType.MixmasterBasins, p)!;
        var report = BatchCommandBuilder.BuildWithReport(new BatchCommandSnapshot
        {
            Fractal = FractalType.MixmasterBasins, Parameters = p, FamilyParams = snap.ToKeyValues(),
        });
        Assert.DoesNotContain(report.Gaps, g => g.Contains("Mix", StringComparison.OrdinalIgnoreCase));
        Assert.True(BatchOptions.TryParse(report.Args.ToArray(), 0, out var o, out var err), err);
        var q = new FractalParameters();
        RegionFractalParams.FromKeyValues(o.Params, FractalType.MixmasterBasins, out var perr)!.ApplyTo(q);
        Assert.Null(perr);
        return q;
    }

    [Fact]
    public void Every_Setting_Survives_The_Region_Snapshot_And_The_Command_Line()
    {
        var p = new FractalParameters
        {
            MixView = MixmasterView.Anisotropy, MixUExit = 11.5, MixOmegaDot = 0.4, MixOmega = -1.7, MixAlphaDot = 7.5,
            MixBetaDot = 12.25, MixMaxTime = 3e20, MixTolerance = 3e-10, AdaptiveAa = true,
        };
        var q = ViaCommandLine(p);
        foreach (var prop in typeof(FractalParameters).GetProperties().Where(x => x.Name.StartsWith("Mix", StringComparison.Ordinal)))
            Assert.Equal(prop.GetValue(p), prop.GetValue(q));
        Assert.True(q.AdaptiveAa);
        Assert.True(AdaptiveAaCapabilities.Supports(FractalType.MixmasterBasins));
    }

    [Fact]
    public void Every_Preset_Renders_The_Same_Live_From_The_Command_Line_And_As_A_Poster()
    {
        foreach (var name in Presets)
        {
            var (region, p) = Preset(name);
            var live = Render(p, 24, 14, region.CenterX, region.CenterY, region.Zoom);
            var cli = Render(ViaCommandLine(p), 24, 14, region.CenterX, region.CenterY, region.Zoom);
            Assert.True(live.ColorBuffer.SequenceEqual(cli.ColorBuffer), $"{name}: --param render differs from live");
            var req = new PosterRequest
            {
                FractalType = FractalType.MixmasterBasins, Width = 24, Height = 14,
                CenterX = region.CenterX, CenterY = region.CenterY, Zoom = region.Zoom, MaxIterations = live.MaxIterations,
                Quality = QualityPreset.Standard, FractalParameters = p, ColorMap = new HsvPalette(),
            };
            var poster = PosterRenderer.RenderToPixels(req, TestContext.Current.CancellationToken, out _, out _);
            Assert.True(live.ColorBuffer.SequenceEqual(poster), $"{name}: poster differs from live");
            Assert.True(live.GateBuffer.Count(g => g >= 0).CompareTo(live.GateBuffer.Length * 9 / 10) >= 0, $"{name}: many undecided pixels");
        }
    }

    [Theory]
    [InlineData("Mixmaster - Kasner Basins", "MixUExit")]
    [InlineData("Mixmaster - Anisotropy Plane", "MixOmega")]
    public void Animated_Frames_Equal_The_Stills_At_Each_Frames_Value(string presetName, string param)
    {
        var lib = AnimationLibrary.Instance;
        lib.Load();
        var (region, p0) = Preset(presetName);
        var anim = lib.GetByName(region.AnimationName!)!;
        Assert.Contains(FractalType.MixmasterBasins, anim.TargetFractalTypes);
        var animatable = FractalAnimatableParamsMap.For(FractalType.MixmasterBasins).Select(d => d.ParamName).ToHashSet();
        Assert.All(anim.Tracks, t => Assert.Contains(t.ParamName, animatable));
        var prop = typeof(FractalParameters).GetProperty(param)!;
        const int fps = 2;
        var video = p0.Clone();
        MixmasterCalculator? calc = null;
        var seen = new List<double>();
        foreach (int frame in new[] { 0, 9, 17, 26 })
        {
            anim.ApplyAt(video, frame / (double)fps);
            double value = (double)prop.GetValue(video)!;
            seen.Add(value);
            calc = Render(video, 16, 10, region.CenterX, region.CenterY, region.Zoom, calc);
            var frameColours = (uint[])calc.ColorBuffer.Clone();
            var still = p0.Clone();
            prop.SetValue(still, value);
            var stillCalc = Render(ViaCommandLine(still), 16, 10, region.CenterX, region.CenterY, region.Zoom);
            Assert.True(frameColours.SequenceEqual(stillCalc.ColorBuffer), $"{param} = {value}: frame {frame} differs from the still");
        }
        Assert.True(seen.Distinct().Count() >= 3, $"{param} barely moved: {string.Join(", ", seen)}");
    }

    [Fact]
    public void A_Masked_Frame_Computes_Only_Marked_Pixels_And_Matches_The_Full_Frame_There()
    {
        const int W = 24, H = 14;
        var p = new FractalParameters();
        double cx = MixmasterCalculator.DefaultCenterX, cy = MixmasterCalculator.DefaultCenterY, z = MixmasterCalculator.DefaultZoom;
        var full = Render(p, W, H, cx, cy, z);
        var masked = Render(p, W, H, cx, cy, z);
        Array.Fill(masked.ColorBuffer, 0x12345678u);
        var mask = new byte[W * H];
        var rng = new Random(1946);
        for (int i = 0; i < mask.Length; i++) mask[i] = (byte)(rng.Next(3) == 0 ? 1 : 0);
        masked.PixelMask = mask;
        Assert.True(masked.MaskExact);
        masked.Calculate(TestContext.Current.CancellationToken);
        masked.PixelMask = null;
        for (int i = 0; i < mask.Length; i++)
            Assert.Equal(mask[i] != 0 ? full.ColorBuffer[i] : 0x12345678u, masked.ColorBuffer[i]);
    }

    [Fact]
    public void Help_Names_Presets_Animations_And_Sources()
    {
        string help = HelpTextBundle.MathMixmasterText.Replace("\r\n", "\n");
        Assert.All(Presets, n => Assert.Contains(n, help));
        Assert.All(Animations, a => Assert.Contains(a, help));
        foreach (var source in new[] { "Cornish & Levin", "Physical Review Letters 78", "7489", "Motter", "Letelier", "Rugh" })
            Assert.Contains(source, help);
    }
}
