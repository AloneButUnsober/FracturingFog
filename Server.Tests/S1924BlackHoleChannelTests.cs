// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Linq;
using System.Text.Json;
using FracturingFog;
using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.Imaging;
using FracturingFog.Models;
using FracturingFog.Scattering;
using FracturingFog.UI.Avalonia.ViewModels;
using Xunit;

namespace FracturingFog.Server.Tests;

// #1924 (epic #1922) — black hole shadows S2: per-ray channels. The physics checks compare
// the traced channels with an independent quadrature of the extreme Reissner-Nordström orbit
// equation (Yumoto, Nitta, Chiba & Sugiyama 2012, Eqs. 3-4, areal radius R = r + M):
//     (du/dφ)² = 1/b² − u²(1 − M u)²,   u = 1/R,   b = L/E
// whose periastron root is u₂ = (1 − √(1 − 4M/b)) / (2M), and whose total sweep from
// infinity to infinity is 2φ∞ = 2∫₀^{u₂} du / √(1/b² − u²(1 − M u)²).
//   • winding: swept angle = 2φ∞ less the two finite-distance end angles (≈ 2b/D);
//   • deflection: final vs launch direction = 2φ∞ − π, and for a binary seen from far away
//     the monopole value of the total mass (→ 4M/b);
//   • closest approach: isotropic periastron r_p = 1/u₂ − M, and never more than the
//     minimum over the trajectory's own stored samples (independent pass).
public class S1924BlackHoleChannelTests
{
    // ── Independent extremal-RN quadrature ───────────────────────────────────

    private static double U2(double b, double m) => (1.0 - Math.Sqrt(1.0 - 4.0 * m / b)) / (2.0 * m);

    /// <summary>2φ∞ for impact parameter b (b &gt; 4m): substitute u = u₂(1 − t²), which removes the
    /// inverse-square-root endpoint, then composite Simpson on t ∈ [0, 1].</summary>
    private static double TotalSweep(double b, double m)
    {
        double u2 = U2(b, m), ib2 = 1.0 / (b * b);
        double F(double t)
        {
            if (t <= 0.0)
            {
                // Limit t → 0: g(u) ≈ g'(u₂)(u − u₂) = −g'(u₂) u₂ t², with
                // g'(u) = −2u(1 − Mu)(1 − 2Mu), so the integrand → 2 √(u₂ / −g'(u₂)).
                double gp = -2.0 * u2 * (1 - m * u2) * (1 - 2 * m * u2);
                return 2.0 * Math.Sqrt(u2 / -gp);
            }
            double u = u2 * (1.0 - t * t);
            double g = ib2 - u * u * (1 - m * u) * (1 - m * u);
            return 2.0 * u2 * t / Math.Sqrt(Math.Max(g, 1e-300));
        }
        const int N = 200000;
        double h = 1.0 / N, sum = F(0) + F(1);
        for (int i = 1; i < N; i++) sum += (i % 2 == 1 ? 4 : 2) * F(i * h);
        return 2.0 * sum * h / 3.0;
    }

    [Fact]
    public void Quadrature_Reproduces_The_Weak_Field_Limit()
    {
        // Self-check of the reference: far out the deflection 2φ∞ − π tends to 4M/b.
        double b = 2000.0, th = TotalSweep(b, 1.0) - Math.PI;
        Assert.InRange(th * b / 4.0, 0.995, 1.005);
    }

    private static BlackHoleShadowCalculator.Scene Single(double d, double tol = 1e-11) =>
        BlackHoleShadowCalculator.Scene.Build(new FractalParameters
        {
            BlackHoleLayout = BlackHoleLayout.Single, BlackHoleMass1 = 1.0,
            BlackHoleCameraDistance = d, BlackHoleTolerance = tol, BlackHoleMaxSteps = 200000,
        });

    [Theory]
    [InlineData(4.3)]
    [InlineData(5.0)]
    [InlineData(8.0)]
    [InlineData(20.0)]
    public void Winding_Is_The_Analytic_Sweep_Of_A_Single_Hole(double bTrue)
    {
        const double D = 1e5;
        var scene = Single(D);
        double uD = scene.NewSystem().U(D, 0, 0);
        double bPlane = bTrue / (uD * uD);   // L/E = b_plane · U(D)² (conserved)
        Span<double> s = stackalloc double[6];
        var (o, sys) = scene.Trace(bPlane, 0.0, s);
        Assert.Equal(ScatterFate.Escaped, o.Fate);

        double r = Math.Sqrt(s[0] * s[0] + s[1] * s[1] + s[2] * s[2]);
        double expected = TotalSweep(bTrue, 1.0) - Math.Atan(bPlane / D) - Math.Asin(bTrue / r);
        double wound = sys.Wind[0];
        Assert.InRange(wound, expected - 5e-4, expected + 5e-4);
        Assert.Equal(wound / (2 * Math.PI), scene.ChannelValue(BlackHoleChannel.Winding, o, sys, s), 12);
    }

    [Theory]
    [InlineData(4.3)]
    [InlineData(6.0)]
    [InlineData(12.0)]
    public void Deflection_Is_The_Analytic_Bend_Of_A_Single_Hole(double bTrue)
    {
        const double D = 1e5;
        var scene = Single(D);
        double uD = scene.NewSystem().U(D, 0, 0);
        Span<double> s = stackalloc double[6];
        var (o, _) = scene.Trace(bTrue / (uD * uD), 0.0, s);
        Assert.Equal(ScatterFate.Escaped, o.Fate);
        double theta = TotalSweep(bTrue, 1.0) - Math.PI;
        // Deflection is reported as an angle in [0, π]; past π (a full loop) it folds back.
        double folded = Math.Abs(Math.IEEERemainder(theta, 2 * Math.PI));
        Assert.InRange(scene.Deflection(s), folded - 2e-5, folded + 2e-5);
    }

    [Fact]
    public void Far_Field_Binary_Deflects_Like_Its_Total_Mass()
    {
        const double D = 1e5;
        var scene = BlackHoleShadowCalculator.Scene.Build(new FractalParameters
        {
            BlackHoleMass1 = 1.0, BlackHoleMass2 = 1.0, BlackHoleSeparation = 2.0,
            BlackHoleCameraDistance = D, BlackHoleTolerance = 1e-11,
        });
        double uD = scene.NewSystem().U(D, 0, 0);
        foreach (double b in new[] { 100.0, 400.0 })
        {
            Span<double> s = stackalloc double[6];
            var (o, _) = scene.Trace(0.0, b / (uD * uD), s);   // v: across the binary axis
            Assert.Equal(ScatterFate.Escaped, o.Fate);
            double monopole = TotalSweep(b, 2.0) - Math.PI;
            Assert.InRange(scene.Deflection(s) / monopole, 0.995, 1.005);
            // → 4M/b to leading order; the next order is a few % at M/b = 0.02, so check far out.
            if (b >= 400.0) Assert.InRange(scene.Deflection(s) * b / (4.0 * 2.0), 0.98, 1.03);
        }
    }

    /// <summary>Records every accepted state's distance to hole 0 (the independent pass).</summary>
    private struct Recorder : IScatteringSystem
    {
        public MpNullSystem Inner;
        public double MinSample;
        public readonly int Dimension => 6;
        public readonly void Derivative(ReadOnlySpan<double> y, Span<double> d) => Inner.Derivative(y, d);
        public readonly ScatterFate Classify(ReadOnlySpan<double> y, out int body) => Inner.Classify(y, out body);
        public readonly double MaxStep(ReadOnlySpan<double> y) => Math.Min(Inner.MaxStep(y), 0.002 * Math.Sqrt(y[0] * y[0] + y[1] * y[1] + y[2] * y[2]));
        public void Observe(ReadOnlySpan<double> y, double h)
        {
            Inner.Observe(y, h);
            double d = Math.Sqrt(y[0] * y[0] + y[1] * y[1] + y[2] * y[2]);
            if (d < MinSample) MinSample = d;
        }
    }

    [Theory]
    [InlineData(4.5)]
    [InlineData(7.0)]
    [InlineData(15.0)]
    public void Closest_Approach_Is_The_Analytic_Periastron(double bTrue)
    {
        const double D = 2000.0;
        var scene = Single(D);
        double uD = scene.NewSystem().U(D, 0, 0);
        double bPlane = bTrue / (uD * uD);
        var (o, sys) = scene.Trace(bPlane, 0.0);
        Assert.Equal(ScatterFate.Escaped, o.Fate);
        double rp = 1.0 / U2(bTrue, 1.0) - 1.0;   // isotropic r = R − M
        Assert.InRange(sys.Min[0], rp * (1 - 1e-4), rp * (1 + 1e-4));

        // Independent pass: densely sampled trajectory; the refined minimum is never above the
        // smallest stored sample and agrees with it to the sampling resolution.
        var rec = new Recorder { Inner = scene.NewSystem(), MinSample = double.PositiveInfinity };
        Span<double> s = stackalloc double[6];
        scene.Camera.Launch(rec.Inner, bPlane, 0.0, s);
        rec.Inner.Begin(s);
        ScatteringIntegrator.Trace(ref rec, s, scene.Settings with { MaxSteps = 2_000_000 });
        Assert.True(sys.Min[0] <= rec.MinSample + 1e-9, $"{sys.Min[0]} vs sample {rec.MinSample}");
        Assert.InRange(rec.MinSample - sys.Min[0], 0.0, 1e-4 * rp);
    }

    [Fact]
    public void A_Captured_Ray_Winds_Round_Its_Own_Hole_And_Has_No_Deflection()
    {
        var scene = Single(2000.0);
        Span<double> s = stackalloc double[6];
        var (o, sys) = scene.Trace(1.0, 0.0, s);
        Assert.Equal(ScatterFate.Captured, o.Fate);
        Assert.Equal(0, scene.ReferenceHole(o, sys));
        Assert.True(double.IsNaN(scene.ChannelValue(BlackHoleChannel.Deflection, o, sys, s)));
        Assert.True(sys.Min[0] <= scene.CaptureFraction * 1.0 + 1e-12);
    }

    // ── Calculator, poster, round-trips ──────────────────────────────────────

    private const int W = 40, H = 30;

    private static BlackHoleShadowCalculator Render(FractalParameters p)
    {
        var c = new BlackHoleShadowCalculator(W, H) { FractalParameters = p, ColorMap = new HsvPalette() };
        c.Calculate(TestContext.Current.CancellationToken);
        return c;
    }

    [Theory]
    [InlineData(BlackHoleChannel.Winding, false)]
    [InlineData(BlackHoleChannel.Delay, false)]
    [InlineData(BlackHoleChannel.Steps, true)]
    [InlineData(BlackHoleChannel.ClosestApproach, false)]
    [InlineData(BlackHoleChannel.Deflection, true)]
    public void Every_Channel_Renders_A_Varied_Deterministic_Image(BlackHoleChannel ch, bool shade)
    {
        var p = new FractalParameters { BlackHoleChannel = ch, BlackHoleShadeFates = shade, BlackHoleChannelScale = 1.5 };
        var a = Render(p);
        int ds = a.SmoothBuffer.Distinct().Count(), dc = a.ColorBuffer.Distinct().Count();
        Assert.True(ds > 10 && dc > 4, $"distinct smooth {ds}, colours {dc}");
        Assert.Equal(a.ColorBuffer, Render(p).ColorBuffer);

        var req = new PosterRequest
        {
            FractalType = FractalType.BlackHoleShadow, Width = W, Height = H,
            CenterX = 0, CenterY = 0, Zoom = BlackHoleShadowCalculator.DefaultZoom, MaxIterations = a.MaxIterations,
            Quality = QualityPreset.Standard, FractalParameters = p, ColorMap = new HsvPalette(),
        };
        Assert.Equal(a.ColorBuffer, PosterRenderer.RenderToPixels(req, TestContext.Current.CancellationToken, out _, out _));
    }

    [Fact]
    public void Shaded_Fates_Keep_The_Fate_Hue()
    {
        var fate = Render(new FractalParameters());
        var shaded = Render(new FractalParameters { BlackHoleChannel = BlackHoleChannel.Winding, BlackHoleShadeFates = true });
        Assert.Equal(fate.FateBuffer, shaded.FateBuffer);
        // Same fate → same hue: the brightest RGB channel of a shaded shadow pixel is the
        // brightest channel of that hole's unshaded (Fate-mode) colour.
        static int Dominant(uint c)
        {
            int r = (int)((c >> 16) & 0xFF), g = (int)((c >> 8) & 0xFF), b = (int)(c & 0xFF);
            return r >= g && r >= b ? 0 : g >= b ? 1 : 2;
        }
        int checkedPixels = 0;
        for (int i = 0; i < fate.FateBuffer.Length; i++)
        {
            if (fate.FateBuffer[i] is not (0 or 1)) continue;
            checkedPixels++;
            Assert.True(Dominant(shaded.ColorBuffer[i]) == Dominant(fate.ColorBuffer[i]),
                $"pixel {i} (hole {fate.FateBuffer[i]}): {shaded.ColorBuffer[i]:X8} vs fate {fate.ColorBuffer[i]:X8}");
        }
        Assert.True(checkedPixels > 50);
    }

    [Fact]
    public void Fate_Channel_Is_The_S1_Image()
    {
        var p = new FractalParameters();
        var c = Render(p);
        var scene = BlackHoleShadowCalculator.Scene.Build(p);
        // Regression (#1924): S1 passed the palette value as Map's iteration cap, so escape-time
        // palettes drew every escaped ray in the interior colour. Escaped rays now vary.
        int escapedColours = Enumerable.Range(0, W * H).Where(i => c.FateBuffer[i] == 2).Select(i => c.ColorBuffer[i]).Distinct().Count();
        Assert.True(escapedColours > 4, $"escaped colours {escapedColours}");
        // The SmoothBuffer of the default channel is S1's: log(1 + delay) for escaped rays, 0 otherwise.
        for (int y = 0; y < H; y += 7)
            for (int x = 0; x < W; x += 5)
            {
                var (u, v) = BlackHoleShadowCalculator.PlaneAt(x + 0.5, y + 0.5, W, H, 0, 0, BlackHoleShadowCalculator.DefaultZoom);
                var (o, sys) = scene.Trace(u, v);
                float want = o.Fate == ScatterFate.Escaped ? (float)Math.Log(1.0 + Math.Max(0.0, sys.Delay)) : 0f;
                Assert.Equal(want, c.SmoothBuffer[y * W + x]);
            }
    }

    [Fact]
    public void Channel_Settings_Round_Trip_The_Region_Snapshot_And_The_Command_Line()
    {
        var p = new FractalParameters { BlackHoleChannel = BlackHoleChannel.ClosestApproach, BlackHoleShadeFates = true, BlackHoleChannelScale = 2.75 };
        var snap = RegionFractalParams.Snapshot(FractalType.BlackHoleShadow, p)!;
        var viaJson = new FractalParameters();
        JsonSerializer.Deserialize<RegionFractalParams>(JsonSerializer.Serialize(snap))!.ApplyTo(viaJson);
        var report = BatchCommandBuilder.BuildWithReport(new BatchCommandSnapshot
        {
            Fractal = FractalType.BlackHoleShadow, Parameters = p, FamilyParams = snap.ToKeyValues(),
        });
        Assert.True(BatchOptions.TryParse(report.Args.ToArray(), 0, out var o, out var err), err);
        var viaCli = new FractalParameters();
        RegionFractalParams.FromKeyValues(o.Params, FractalType.BlackHoleShadow, out var perr)!.ApplyTo(viaCli);
        Assert.Null(perr);
        foreach (var q in new[] { viaJson, viaCli, p.Clone() })
        {
            Assert.Equal(p.BlackHoleChannel, q.BlackHoleChannel);
            Assert.Equal(p.BlackHoleShadeFates, q.BlackHoleShadeFates);
            Assert.Equal(p.BlackHoleChannelScale, q.BlackHoleChannelScale);
        }
        Assert.Equal(Render(p).ColorBuffer, Render(viaCli).ColorBuffer);
        Assert.DoesNotContain(report.Gaps, g => g.Contains("BlackHole", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Params_Panel_Shows_Channel_Controls_Only_For_Channels()
    {
        var vm = new FractalParamsViewModel(FractalType.BlackHoleShadow, new FractalParameters());
        Assert.False(vm.BhUsesChannel);
        vm.BlackHoleChannel = BlackHoleChannel.Winding;
        Assert.True(vm.BhUsesChannel);
        vm.BlackHoleChannelScale = 0;
        Assert.Equal(0.01, vm.BlackHoleChannelScale);
    }
}
