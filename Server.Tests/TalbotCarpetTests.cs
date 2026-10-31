// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Linq;
using System.Numerics;
using System.Threading;
using FracturingFog;
using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

// #1570 (double-slit epic #1565 S6) — the fractal Talbot carpet. Oracles: the
// exact self-imaging identities at ζ = 1, ½, ¼ (Gauss sums; Berry & Klein
// 1996), the grating itself at ζ = 0, Parseval (energy conserved along z),
// mirror symmetry, and the direct sum against the per-row recurrence.
public sealed class TalbotCarpetTests
{
    private sealed class RampMap : IColorMap
    {
        public ColorPaletteType Type => ColorPaletteType.GradientLinear;
        public int MaxIterations { get; set; }
        public uint InSetColor => 0xFF000000u;
        public int Map(float smooth, float distance, int iterations)
            => unchecked((int)(0xFF000000u | ((uint)(smooth * 997f) & 0x00FFFFFFu)));
    }

    private static FractalParameters P(Action<FractalParameters>? tweak = null)
    {
        var p = new FractalParameters();
        tweak?.Invoke(p);
        return p;
    }

    private static void Close(Complex want, Complex got, double tol)
    {
        Assert.True((want - got).Magnitude <= tol * Math.Max(1.0, want.Magnitude), $"{got} vs {want}");
    }

    public static TheoryData<TalbotProfile> Profiles => new() { TalbotProfile.Ronchi, TalbotProfile.DeltaComb, TalbotProfile.SlitCascade };

    // ζ = 1: every phase e^{−2πi·n²} is 1 — the grating images itself.
    [Theory]
    [MemberData(nameof(Profiles))]
    public void WholeTalbotDistance_ReproducesTheGrating(TalbotProfile profile)
    {
        var c = TalbotCarpetCalculator.Coefficients(P(q => q.TalbotProfile = profile));
        foreach (double xi in new[] { 0.0, 0.13, 0.5, 0.77 })
            Close(TalbotCarpetCalculator.Field(c, xi, 0), TalbotCarpetCalculator.Field(c, xi, 1), 1e-9);
    }

    // ζ = ½: e^{−iπn²} = (−1)ⁿ = e^{iπn} — the image shifted by half a period.
    [Theory]
    [MemberData(nameof(Profiles))]
    public void HalfTalbotDistance_ShiftsByHalfAPeriod(TalbotProfile profile)
    {
        var c = TalbotCarpetCalculator.Coefficients(P(q => q.TalbotProfile = profile));
        foreach (double xi in new[] { 0.0, 0.21, 0.6 })
            Close(TalbotCarpetCalculator.Field(c, xi + 0.5, 0), TalbotCarpetCalculator.Field(c, xi, 0.5), 1e-9);
    }

    // ζ = ¼ (fractional Talbot): e^{−iπn²/2} is 1 for even n and −i for odd n,
    // = (1−i)/2 + (1+i)/2·(−1)ⁿ, so ψ(ξ, ¼) = (1−i)/2·ψ₀(ξ) + (1+i)/2·ψ₀(ξ + ½):
    // two copies of the grating, half a period apart, with phases ∓π/4.
    [Theory]
    [MemberData(nameof(Profiles))]
    public void QuarterTalbotDistance_IsTwoShiftedCopies(TalbotProfile profile)
    {
        var c = TalbotCarpetCalculator.Coefficients(P(q => q.TalbotProfile = profile));
        var a = new Complex(0.5, -0.5); var b = new Complex(0.5, 0.5);
        foreach (double xi in new[] { 0.0, 0.17, 0.42, 0.9 })
        {
            var want = a * TalbotCarpetCalculator.Field(c, xi, 0) + b * TalbotCarpetCalculator.Field(c, xi + 0.5, 0);
            Close(want, TalbotCarpetCalculator.Field(c, xi, 0.25), 1e-9);
        }
    }

    // ζ = 0 is the grating: a Ronchi ruling of duty a is open (|ψ| ≈ 1) inside
    // |ξ| < a/2 and dark outside, away from the Gibbs ringing at the edges.
    [Fact]
    public void ZeroDistance_IsTheRonchiGrating()
    {
        var c = TalbotCarpetCalculator.Coefficients(P(q => { q.TalbotDuty = 0.3; q.TalbotModes = 2000; }));
        foreach (double xi in new[] { 0.0, 0.08, -0.08, 1.0 })
            Assert.Equal(1.0, TalbotCarpetCalculator.Field(c, xi, 0).Magnitude, 0.02);
        foreach (double xi in new[] { 0.3, 0.5, -0.4 })
            Assert.Equal(0.0, TalbotCarpetCalculator.Field(c, xi, 0).Magnitude, 0.02);
    }

    // Parseval: the mean of |ψ|² over a period is Σ|cₙ|² at every ζ — free
    // propagation conserves energy — including irrational distances.
    [Theory]
    [InlineData(0.0)]
    [InlineData(0.3183098861837907)]   // 1/π
    [InlineData(0.6180339887498949)]   // golden ratio − 1
    public void Parseval_EnergyIsConservedAlongTheCarpet(double zeta)
    {
        var c = TalbotCarpetCalculator.Coefficients(P(q => q.TalbotModes = 40));
        double want = c.Sum(v => v.Real * v.Real + v.Imaginary * v.Imaginary);
        const int samples = 1024;   // > 2·(2M+1): the discrete mean is exact
        double mean = 0;
        for (int k = 0; k < samples; k++) { var f = TalbotCarpetCalculator.Field(c, (k + 0.5) / samples, zeta); mean += (f.Real * f.Real + f.Imaginary * f.Imaginary) / samples; }
        Assert.Equal(want, mean, 1e-9);
    }

    // A symmetric grating (cₙ = c₋ₙ) gives a mirror-symmetric carpet.
    [Fact]
    public void SymmetricGrating_GivesAMirrorSymmetricCarpet()
    {
        var c = TalbotCarpetCalculator.Coefficients(P());
        foreach (var (xi, zeta) in new[] { (0.11, 0.37), (0.4, 0.7071), (0.23, 0.05) })
            Assert.Equal(TalbotCarpetCalculator.Field(c, xi, zeta).Magnitude, TalbotCarpetCalculator.Field(c, -xi, zeta).Magnitude, 10);
    }

    // The renderer's per-row recurrence (bₙ once per row, wⁿ by multiplication,
    // angles reduced mod 1) must equal the direct sum, mapped on the documented
    // scale: log(I / Σ|cₙ|²) from −DR to the bound (Σ|cₙ|)².
    [Theory]
    [InlineData(TalbotProfile.Ronchi, TalbotView.Intensity, 1.9, 0.0, 0.5)]
    [InlineData(TalbotProfile.DeltaComb, TalbotView.Intensity, 0.7, 3.25, 41.3)]
    [InlineData(TalbotProfile.SlitCascade, TalbotView.Phase, 1.9, -0.4, 0.5)]
    public void Render_MatchesTheDirectSum(TalbotProfile profile, TalbotView view, double zoom, double cx, double cy)
    {
        const int w = 40, h = 24, it = 400;
        var p = P(q => { q.TalbotProfile = profile; q.TalbotView = view; q.TalbotModes = 50; });
        var calc = new TalbotCarpetCalculator(w, h) { CenterX = cx, CenterY = cy, Zoom = zoom, MaxIterations = it, ColorMap = new RampMap(), FractalParameters = p };
        calc.Calculate(CancellationToken.None);
        var c = TalbotCarpetCalculator.Coefficients(p);
        double mean = c.Sum(v => v.Real * v.Real + v.Imaginary * v.Imaginary), sumAbs = c.Sum(v => v.Magnitude);
        double decades = p.TalbotDynamicRange, top = 2 * Math.Log10(sumAbs) - Math.Log10(mean);
        double pitch = 4.0 / w / zoom;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var f = TalbotCarpetCalculator.Field(c, cx + (x - w * 0.5) * pitch, cy + (y - h * 0.5) * pitch);
                double t = view == TalbotView.Phase
                    ? Math.Max(1e-9, f.Phase / (2 * Math.PI) - Math.Floor(f.Phase / (2 * Math.PI)))
                    : (Math.Log10(f.Real * f.Real + f.Imaginary * f.Imaginary) - Math.Log10(mean) + decades) / (decades + top);
                float want = t > 0 ? (float)(Math.Min(1.0, t) * it) : 0f;
                float got = calc.SmoothBuffer[y * w + x];
                if (view == TalbotView.Phase) { double d = Math.Abs(want - got) / it; Assert.True(Math.Min(d, 1 - d) < 1e-6, $"({x},{y}) {got} vs {want}"); }
                else Assert.Equal(want, got, 0.01f);
            }
    }

    // The SlitCascade profile is one period of the cascade's Line aperture: at
    // ζ = 0 a leaf's centre carries Π √pᵢ along its address; c₀ is the mean
    // transmitted amplitude a·(Σ√pᵢ)^D over the period of 2 (a = leaf half-width).
    [Fact]
    public void CascadeProfile_IsOnePeriodOfTheCascadeAperture()
    {
        var p = P(q => { q.TalbotProfile = TalbotProfile.SlitCascade; q.TalbotModes = 3000; q.SlitCascadeDepth = 3; q.SlitCascadeChildren = 3; q.SlitCascadeRatio = 0.25; q.SlitCascadeWavelength = 1.2; });
        var line = p.Clone(); line.SlitCascadeLayout = SlitCascadeLayout.Line;
        var cas = SlitCascadeCalculator.Build(line);
        var c = TalbotCarpetCalculator.Coefficients(p);
        int m = (c.Length - 1) / 2;
        double a = Math.Pow(0.25, 3);
        Assert.Equal(a * Math.Pow(cas.P.Sum(Math.Sqrt), 3), c[m].Real, 12);
        // Leaf reached by children 0, 2, 1: aperture x = Σ rⁿ·b, ξ = x / period (2).
        double x = 0, s = 1, amp = 1;
        foreach (int i in new[] { 0, 2, 1 }) { x += s * cas.Bx[i]; amp *= Math.Sqrt(cas.P[i]); s *= 0.25; }
        Assert.Equal(amp, TalbotCarpetCalculator.Field(c, x / 2, 0).Magnitude, 0.05 * amp + 1e-3);
    }

    [Fact]
    public void Views_AllRender_AndBadParametersAreClamped()
    {
        foreach (var profile in Enum.GetValues<TalbotProfile>())
            foreach (var view in Enum.GetValues<TalbotView>())
            {
                var calc = new TalbotCarpetCalculator(32, 18) { ColorMap = new RampMap(), FractalParameters = P(q => { q.TalbotProfile = profile; q.TalbotView = view; }) };
                calc.Calculate(CancellationToken.None);
                Assert.Contains(calc.SmoothBuffer, v => v > 0);
                Assert.All(calc.SmoothBuffer, v => Assert.InRange(v, 0f, 400f));
            }
        var bad = new TalbotCarpetCalculator(16, 9) { ColorMap = new RampMap(), FractalParameters = P(q => { q.TalbotModes = -5; q.TalbotDuty = double.NaN; q.TalbotDynamicRange = 0; }) };
        bad.Calculate(CancellationToken.None);
        Assert.All(bad.SmoothBuffer, v => Assert.False(float.IsNaN(v)));
    }

    [Fact]
    public void DutyAndModes_AreAnimatable()
    {
        var names = FracturingFog.Abstractions.Animation.FractalAnimatableParamsMap.For(FractalType.TalbotCarpet).Select(d => d.ParamName).ToArray();
        Assert.Contains(nameof(FractalParameters.TalbotDuty), names);
        Assert.Contains(nameof(FractalParameters.TalbotModes), names);
    }

    [Fact]
    public void LiveView_RoundTripsThroughTheCommandBuilder_WithTheCascadeSettings()
    {
        var p = new FractalParameters
        {
            TalbotProfile = TalbotProfile.SlitCascade, TalbotDuty = 0.41, TalbotModes = 77, TalbotView = TalbotView.Phase, TalbotDynamicRange = 3.5,
            SlitCascadeChildren = 4, SlitCascadeRatio = 0.22, SlitCascadeWavelength = 0.9,
        };
        var report = BatchCommandBuilder.BuildWithReport(new BatchCommandSnapshot
        {
            Fractal = FractalType.TalbotCarpet, Parameters = p,
            FamilyParams = RegionFractalParams.Snapshot(FractalType.TalbotCarpet, p)!.ToKeyValues(),
        });
        Assert.True(BatchOptions.TryParse(report.Args.ToArray(), 0, out var o, out var err), err);
        var fresh = new FractalParameters();
        RegionFractalParams.FromKeyValues(o.Params, FractalType.TalbotCarpet, out _)!.ApplyTo(fresh);
        Assert.Equal(TalbotProfile.SlitCascade, fresh.TalbotProfile);
        Assert.Equal(0.41, fresh.TalbotDuty);
        Assert.Equal(77, fresh.TalbotModes);
        Assert.Equal(TalbotView.Phase, fresh.TalbotView);
        Assert.Equal(3.5, fresh.TalbotDynamicRange);
        Assert.Equal(4, fresh.SlitCascadeChildren);   // the cascade's own settings ride along
        Assert.Equal(0.22, fresh.SlitCascadeRatio);
        Assert.Equal(0.9, fresh.SlitCascadeWavelength);
        // A Ronchi carpet's snapshot leaves the cascade settings out.
        Assert.Null(RegionFractalParams.Snapshot(FractalType.TalbotCarpet, new FractalParameters())!.SlitCascadeChildren);
    }
}
