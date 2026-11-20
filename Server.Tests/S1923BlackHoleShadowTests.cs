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

// #1923 (epic #1922) — black hole shadows S1: the shared scattering integrator and the
// Majumdar-Papapetrou null-geodesic trace. Every check is an invariant that does not come
// from the code under test:
//   • the integrator reproduces the closed-form harmonic oscillator;
//   • H = 0 (null) and |p| = U² hold along traced rays, from the stored final state;
//   • one hole is extremal Reissner-Nordström: shadow edge at impact parameter L/E = 4m
//     (Yumoto, Nitta, Chiba & Sugiyama 2012 §II.A), i.e. plane offset 4m / U(D)² at distance D;
//   • an equal-mass binary seen side-on is mirror symmetric with the hole labels swapped;
//   • a ray traced forward then back with reversed momentum returns to its start;
//   • a hole with a distant companion has the shifted, elliptic main shadow of Yumoto et al.
//     Eq. 40 (first order in κ/ℓ), and fits it better than the zeroth-order circle Eq. 34;
//   • live and poster renders agree; parameters survive the region snapshot and --param.
public class S1923BlackHoleShadowTests
{
    // ── Shared integrator ─────────────────────────────────────────────────────

    /// <summary>y'' = -y as a 2D first-order system that stops once λ ≥ T.</summary>
    private struct Oscillator : IScatteringSystem
    {
        public double T, Elapsed;
        public readonly int Dimension => 2;
        public readonly void Derivative(ReadOnlySpan<double> y, Span<double> d) { d[0] = y[1]; d[1] = -y[0]; }
        public readonly ScatterFate Classify(ReadOnlySpan<double> y, out int body)
        { body = -1; return Elapsed >= T ? ScatterFate.Escaped : ScatterFate.Running; }
        public readonly double MaxStep(ReadOnlySpan<double> y) => Math.Max(1e-9, Math.Min(0.5, T - Elapsed));
        public void Observe(ReadOnlySpan<double> y, double h) => Elapsed += h;
    }

    [Fact]
    public void Integrator_Matches_The_Closed_Form_Oscillator()
    {
        var sys = new Oscillator { T = 10.0 };
        Span<double> y = stackalloc double[] { 1.0, 0.0 };
        var o = ScatteringIntegrator.Trace(ref sys, y, new ScatterSettings { RelTol = 1e-11, AbsTol = 1e-13, MaxSteps = 100000, InitialStep = 0.01 });
        Assert.Equal(ScatterFate.Escaped, o.Fate);
        Assert.Equal(10.0, o.Lambda, 9);
        Assert.Equal(Math.Cos(10.0), y[0], 8);
        Assert.Equal(-Math.Sin(10.0), y[1], 8);
    }

    [Fact]
    public void Integrator_Reports_Undecided_At_The_Step_Cap()
    {
        var sys = new Oscillator { T = 1e9 };
        Span<double> y = stackalloc double[] { 1.0, 0.0 };
        var o = ScatteringIntegrator.Trace(ref sys, y, new ScatterSettings { RelTol = 1e-9, AbsTol = 1e-12, MaxSteps = 50, InitialStep = 0.1 });
        Assert.Equal(ScatterFate.Undecided, o.Fate);
        Assert.Equal(50, o.Steps);
        Assert.Equal(-1, o.Body);
    }

    // ── Majumdar-Papapetrou physics ──────────────────────────────────────────

    private static BlackHoleShadowCalculator.Scene SceneFor(Action<FractalParameters>? set = null)
    {
        var p = new FractalParameters();
        set?.Invoke(p);
        return BlackHoleShadowCalculator.Scene.Build(p);
    }

    [Fact]
    public void Default_Binary_Sits_On_The_Z_Axis_About_Its_Centre_Of_Mass()
    {
        var holes = BlackHoleLayouts.Build(new FractalParameters { BlackHoleMass1 = 1, BlackHoleMass2 = 3, BlackHoleSeparation = 2 });
        Assert.Equal(2, holes.Length);
        // z± = ±a M∓ / (M₊ + M₋) (Shipley & Dolan §II): hole 1 (m 1) at +1.5, hole 2 (m 3) at -0.5.
        Assert.Equal(1.5, holes[0].Z, 12);
        Assert.Equal(-0.5, holes[1].Z, 12);
        Assert.Equal(0.0, holes[0].M * holes[0].Z + holes[1].M * holes[1].Z, 12);
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(5.0, 1.0)]
    [InlineData(-2.5, 3.0)]
    [InlineData(8.0, -6.0)]
    [InlineData(0.3, 0.2)]
    public void Traced_Rays_Stay_Null(double u, double v)
    {
        var scene = SceneFor();
        Span<double> s = stackalloc double[6];
        var (o, sys) = scene.Trace(u, v, s);
        Assert.NotEqual(ScatterFate.Undecided, o.Fate);
        // Independent check from the final state: |p| / U² = 1 (H = 0 with p_t = -1).
        double uu = sys.U(s[0], s[1], s[2]);
        double pm = Math.Sqrt(s[3] * s[3] + s[4] * s[4] + s[5] * s[5]);
        Assert.InRange(pm / (uu * uu), 1 - 1e-6, 1 + 1e-6);
    }

    [Fact]
    public void Single_Hole_Shadow_Edge_Is_At_Impact_Parameter_4m()
    {
        const double D = 4000.0;
        var scene = SceneFor(p => { p.BlackHoleLayout = BlackHoleLayout.Single; p.BlackHoleMass1 = 1.0; p.BlackHoleCameraDistance = D; });
        bool Captured(double b) => scene.Trace(b, 0.0).Outcome.Fate == ScatterFate.Captured;

        // L / E = b U(D)² is conserved (spherical symmetry), and the extremal RN shadow is
        // L / E = 4m, so the plane edge sits at 4 / U(D)² (U(D) = 1 + 1/D at the launch plane).
        double expected = 4.0 / Math.Pow(1.0 + 1.0 / D, 2);
        Assert.True(Captured(3.9));
        Assert.False(Captured(4.1));
        double lo = 3.9, hi = 4.1;
        for (int i = 0; i < 30; i++) { double mid = 0.5 * (lo + hi); if (Captured(mid)) lo = mid; else hi = mid; }
        Assert.InRange(0.5 * (lo + hi), expected - 2e-4, expected + 2e-4);

        // Spherical symmetry: the edge is the same in every image-plane direction.
        Assert.True(scene.Trace(3.95 * Math.Cos(1.1), 3.95 * Math.Sin(1.1)).Outcome.Fate == ScatterFate.Captured);
        Assert.True(scene.Trace(4.05 * Math.Cos(2.3), 4.05 * Math.Sin(2.3)).Outcome.Fate == ScatterFate.Escaped);
    }

    [Fact]
    public void Equal_Mass_Binary_Is_Mirror_Symmetric_With_Labels_Swapped()
    {
        var scene = SceneFor();   // default: unit masses, a = 2, side-on
        const int N = 40;
        var fate = new int[N, N];
        double Grid(int i) => -10.0 + 20.0 * (i + 0.5) / N;
        int Gate(double u, double v)
        {
            var o = scene.Trace(u, v).Outcome;
            return o.Fate == ScatterFate.Captured ? o.Body : o.Fate == ScatterFate.Escaped ? 2 : -1;
        }
        for (int j = 0; j < N; j++)
            for (int i = 0; i < N; i++)
                fate[i, j] = Gate(Grid(i), Grid(j));
        static int Swap(int g) => g == 0 ? 1 : g == 1 ? 0 : g;
        bool Boundary(int i, int j)
        {
            int g = fate[i, j];
            return (i > 0 && fate[i - 1, j] != g) || (i < N - 1 && fate[i + 1, j] != g)
                || (j > 0 && fate[i, j - 1] != g) || (j < N - 1 && fate[i, j + 1] != g);
        }
        int captured0 = 0, captured1 = 0, mismatches = 0, interiorMismatches = 0;
        for (int j = 0; j < N; j++)
            for (int i = 0; i < N; i++)
            {
                int g = fate[i, j];
                if (g == 0) captured0++;
                if (g == 1) captured1++;
                // u -> -u swaps the holes (hole 0 is on +z = +u); v -> -v keeps them.
                bool bad = fate[N - 1 - i, j] != Swap(g) || fate[i, N - 1 - j] != g;
                if (!bad) continue;
                mismatches++;
                if (!Boundary(i, j)) interiorMismatches++;
            }
        Assert.True(captured0 > N * N / 20 && captured1 > N * N / 20, $"captured {captured0} / {captured1}");
        Assert.InRange(Math.Abs(captured0 - captured1), 0, Math.Max(2, captured0 / 50));
        // A grid symmetric about the origin samples mirror points exactly, so only pixels
        // within an integration error of a fate boundary may disagree.
        Assert.True(mismatches <= N * N / 100, $"mismatches {mismatches}");
        Assert.Equal(0, interiorMismatches);
    }

    /// <summary>Stops a ray after a fixed parameter length (for the reversibility check).</summary>
    private struct Leg : IScatteringSystem
    {
        public MpNullSystem Inner;
        public double Length, Elapsed;
        public readonly int Dimension => 6;
        public readonly void Derivative(ReadOnlySpan<double> y, Span<double> d) => Inner.Derivative(y, d);
        public readonly ScatterFate Classify(ReadOnlySpan<double> y, out int body)
        { body = -1; return Elapsed >= Length ? ScatterFate.Escaped : ScatterFate.Running; }
        public readonly double MaxStep(ReadOnlySpan<double> y) => Math.Max(1e-9, Math.Min(Inner.MaxStep(y), Length - Elapsed));
        public void Observe(ReadOnlySpan<double> y, double h) => Elapsed += h;
    }

    [Fact]
    public void A_Ray_Traced_Forward_Then_Back_Returns_To_Its_Start()
    {
        var scene = SceneFor();
        var settings = new ScatterSettings { RelTol = 1e-12, AbsTol = 1e-14, MaxSteps = 200000, InitialStep = 0.5 };
        Span<double> s = stackalloc double[6];
        var sys = scene.NewSystem();
        scene.Camera.Launch(sys, 6.0, 1.5, s);   // passes close by both holes, then escapes
        Span<double> start = stackalloc double[6];
        s.CopyTo(start);

        var fwd = new Leg { Inner = sys, Length = 80.0 };
        ScatteringIntegrator.Trace(ref fwd, s, settings);
        for (int i = 3; i < 6; i++) s[i] = -s[i];
        var back = new Leg { Inner = sys, Length = fwd.Elapsed };
        ScatteringIntegrator.Trace(ref back, s, settings);

        for (int i = 0; i < 3; i++) Assert.Equal(start[i], s[i], 6);
        for (int i = 3; i < 6; i++) Assert.Equal(-start[i], s[i], 6);
    }

    [Fact]
    public void Main_Shadow_With_A_Distant_Companion_Fits_The_First_Order_Ellipse()
    {
        // Yumoto, Nitta, Chiba & Sugiyama 2012 §II.D: hole m = 1 at the origin, companion
        // κ = 1 at z = ℓ. In celestial coordinates (β along the axis, towards the companion)
        // the main shadow is, to first order in δ = κ/ℓ,
        //   α² + (β + 2κ)² + 2δ(β + 2κ)(2β + 3κ + 2) = 16(1 + 2δ)          (Eq. 40)
        // against the zeroth-order circle α² + (β + 2κ)² = 16 (Eq. 34). Here u is β, v is α.
        const double ell = 20.0, kappa = 1.0, D = 5000.0;
        var scene = SceneFor(p =>
        {
            p.BlackHoleLayout = BlackHoleLayout.Custom;
            p.BlackHoleCustom = $"0,0,0,1;0,0,{ell},{kappa}";
            p.BlackHoleCameraDistance = D;
            p.BlackHoleTolerance = 1e-10;
        });
        // Plane offset → celestial coordinate: L/E-type scaling by U(D)² (as in the single-hole test).
        double scale = Math.Pow(scene.NewSystem().U(D, 0, 0), 2);
        bool Main(double u, double v) { var o = scene.Trace(u / scale, v / scale).Outcome; return o.Fate == ScatterFate.Captured && o.Body == 0; }

        // March outward from the shadow centre to the first non-main pixel, then bisect:
        // this finds the main edge, not an eyebrow further out.
        double Edge(double u0, double v0, double du, double dv)
        {
            double t = 0.0, step = 0.02;
            while (Main(u0 + (t + step) * du, v0 + (t + step) * dv)) t += step;
            double lo = t, hi = t + step;
            for (int i = 0; i < 20; i++) { double m = 0.5 * (lo + hi); if (Main(u0 + m * du, v0 + m * dv)) lo = m; else hi = m; }
            return 0.5 * (lo + hi);
        }
        double c = -2.0 * kappa;
        Assert.True(Main(c, 0));
        double uPlus = c + Edge(c, 0, 1, 0), uMinus = c - Edge(c, 0, -1, 0), vHalf = Edge(c, 0, 0, 1);

        // First-order predictions (Eq. 40 on α = 0, and its α extent at β = -2κ).
        double delta = kappa / ell;
        double A = 1 + 4 * delta, B = 2 * delta * (2 - kappa), C = -16 * (1 + 2 * delta);
        double sq = Math.Sqrt(B * B - 4 * A * C);
        double sPlus = (-B + sq) / (2 * A), sMinus = (-B - sq) / (2 * A);
        double predPlus = sPlus - 2 * kappa, predMinus = sMinus - 2 * kappa, predV = Math.Sqrt(16 * (1 + 2 * delta));

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"edges u+ {uPlus:F4} (Eq.40 {predPlus:F4}), u- {uMinus:F4} ({predMinus:F4}), v {vHalf:F4} ({predV:F4})");
        Assert.InRange(uPlus, predPlus - 0.08, predPlus + 0.08);
        Assert.InRange(uMinus, predMinus - 0.08, predMinus + 0.08);
        Assert.InRange(vHalf, predV - 0.08, predV + 0.08);
        // ...and closer to first order than to the zeroth-order circle (edges 2, -6, half-width 4).
        double e1 = Math.Abs(uPlus - predPlus) + Math.Abs(uMinus - predMinus) + Math.Abs(vHalf - predV);
        double e0 = Math.Abs(uPlus - 2.0) + Math.Abs(uMinus + 6.0) + Math.Abs(vHalf - 4.0);
        Assert.True(e1 < e0, $"first-order error {e1:F4} vs zeroth {e0:F4} (edges {uPlus:F4}, {uMinus:F4}, {vHalf:F4})");
    }

    // ── Calculator, poster, round-trips ──────────────────────────────────────

    private const int W = 48, H = 36;

    private static BlackHoleShadowCalculator Render(FractalParameters p)
    {
        var c = new BlackHoleShadowCalculator(W, H) { FractalParameters = p, ColorMap = new HsvPalette() };
        c.Calculate(TestContext.Current.CancellationToken);
        return c;
    }

    [Fact]
    public void Live_And_Poster_Render_The_Same_Image()
    {
        var p = new FractalParameters();
        var a = Render(p);
        var b = Render(p);
        Assert.Equal(a.ColorBuffer, b.ColorBuffer);
        Assert.Contains(a.FateBuffer, f => f == 0);
        Assert.Contains(a.FateBuffer, f => f == 1);
        Assert.Contains(a.FateBuffer, f => f == 2);

        var req = new PosterRequest
        {
            FractalType = FractalType.BlackHoleShadow, Width = W, Height = H,
            CenterX = 0, CenterY = 0, Zoom = BlackHoleShadowCalculator.DefaultZoom, MaxIterations = a.MaxIterations,
            Quality = QualityPreset.Standard, FractalParameters = p, ColorMap = new HsvPalette(),
        };
        var pixels = PosterRenderer.RenderToPixels(req, TestContext.Current.CancellationToken, out _, out _);
        Assert.Equal(a.ColorBuffer, pixels);
    }

    [Fact]
    public void Parameters_Round_Trip_The_Region_Snapshot_And_The_Command_Line()
    {
        var p = new FractalParameters
        {
            BlackHoleLayout = BlackHoleLayout.Custom, BlackHoleCustom = "0,0,1.25,1;0.5,0,-1,0.75",
            BlackHoleMass1 = 1.5, BlackHoleMass2 = 0.5, BlackHoleSeparation = 3.25,
            BlackHoleCameraDistance = 75, BlackHoleInclination = 60, BlackHoleAzimuth = 15,
            BlackHoleCaptureFraction = 0.03, BlackHoleTolerance = 1e-8, BlackHoleMaxSteps = 12345,
        };
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
            Assert.Equal(p.BlackHoleLayout, q.BlackHoleLayout);
            Assert.Equal(p.BlackHoleCustom, q.BlackHoleCustom);
            Assert.Equal(p.BlackHoleMass1, q.BlackHoleMass1);
            Assert.Equal(p.BlackHoleMass2, q.BlackHoleMass2);
            Assert.Equal(p.BlackHoleSeparation, q.BlackHoleSeparation);
            Assert.Equal(p.BlackHoleCameraDistance, q.BlackHoleCameraDistance);
            Assert.Equal(p.BlackHoleInclination, q.BlackHoleInclination);
            Assert.Equal(p.BlackHoleAzimuth, q.BlackHoleAzimuth);
            Assert.Equal(p.BlackHoleCaptureFraction, q.BlackHoleCaptureFraction);
            Assert.Equal(p.BlackHoleTolerance, q.BlackHoleTolerance);
            Assert.Equal(p.BlackHoleMaxSteps, q.BlackHoleMaxSteps);
        }
        Assert.Equal(Render(p).ColorBuffer, Render(viaCli).ColorBuffer);
        Assert.DoesNotContain(report.Gaps, g => g.Contains("BlackHole", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Custom_Layout_Parses_Up_To_Four_Valid_Holes()
    {
        var h = BlackHoleLayouts.ParseCustom("1,2,3,0.5; bad; 0,0,0,-1; 0,0,1,1;0,0,2,1;0,0,3,1;0,0,4,1");
        Assert.Equal(4, h.Length);
        Assert.Equal(new MpHole(1, 2, 3, 0.5), h[0]);
        Assert.Empty(BlackHoleLayouts.ParseCustom(""));
        // Custom with nothing valid falls back to the binary.
        Assert.Equal(2, BlackHoleLayouts.Build(new FractalParameters { BlackHoleLayout = BlackHoleLayout.Custom }).Length);
    }

    [Fact]
    public void Params_Panel_Shows_The_Controls_Each_Layout_Uses()
    {
        var vm = new FractalParamsViewModel(FractalType.BlackHoleShadow, new FractalParameters());
        Assert.True(vm.IsBlackHoleShadow && vm.BhUsesMass1 && vm.BhUsesMass2 && vm.BhUsesSeparation && !vm.BhIsCustom);
        vm.BlackHoleLayout = BlackHoleLayout.Single;
        Assert.True(vm.BhUsesMass1 && !vm.BhUsesMass2 && !vm.BhUsesSeparation);
        vm.BlackHoleLayout = BlackHoleLayout.Custom;
        Assert.True(vm.BhIsCustom && !vm.BhUsesMass1);
        vm.BlackHoleMaxSteps = 1;
        Assert.Equal(100, vm.BlackHoleMaxSteps);
    }
}
