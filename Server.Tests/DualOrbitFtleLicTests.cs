// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Linq;
using System.Numerics;
using FracturingFog;
using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

// #1128 (epic #1114 S14) — FTLE / Jacobian anisotropy and the LIC flow texture.
// Oracles: central finite differences of (z_N, c_N); singular values by power
// iteration on JᴴJ; the interior limit ln σ₂/N → ln|μ|/p (attracting cycle of
// multiplier μ, closed form for p = 1, 2); the Chebyshev map s = −2 (Lyapunov
// exponent ln 2, also an overflow test); LIC of a constant field = a 1D box
// blur of the noise; far-field gradient of the escape time is radial.
public sealed class DualOrbitFtleLicTests
{
    private sealed class RampMap : IColorMap
    {
        public ColorPaletteType Type => ColorPaletteType.GradientLinear;
        public int MaxIterations { get; set; }
        public uint InSetColor => 0xFF102030u;
        public int Map(float smooth, float distance, int iterations)
            => unchecked((int)(0xFF000000u | ((uint)(smooth * 997f) & 0x00FFFFFFu)));
    }

    private static (Complex z, Complex c) Iter(Complex s, Complex c0, int n)
    {
        Complex z = 0, c = c0;
        for (int k = 0; k < n; k++) { z = z * z + s; c = c * c + s; }
        return (z, c);
    }

    private static (Complex a, Complex b, Complex d) Entries(DualOrbitEscapeCalculator.PairJacobian j)
    {
        return (new Complex(j.Ar, j.Ai) * Math.Exp(j.LogA), new Complex(j.Br, j.Bi) * Math.Exp(j.LogB),
                new Complex(j.Dr, j.Di) * Math.Exp(j.LogD));
    }

    [Theory]
    [InlineData(-0.12, 0.74, 0.3, 0.1, 9)]
    [InlineData(0.28, 0.01, -0.2, 0.3, 7)]
    [InlineData(-0.75, 0.08, 0.05, -0.4, 10)]
    public void Jacobian_MatchesFiniteDifferences(double sx, double sy, double cx, double cy, int n)
    {
        var j = DualOrbitEscapeCalculator.RunJacobian(cx, cy, sx, sy, n, 1e6);
        Assert.Equal(n, j.N);
        var (a, b, d) = Entries(j);
        Complex s = new(sx, sy), c0 = new(cx, cy);
        const double h = 1e-6;
        var (zp, cp) = Iter(s + h, c0, n); var (zm, cm) = Iter(s - h, c0, n);
        var (_, cdp) = Iter(s, c0 + h, n); var (_, cdm) = Iter(s, c0 - h, n);
        Complex aFd = (zp - zm) / (2 * h), bFd = (cp - cm) / (2 * h), dFd = (cdp - cdm) / (2 * h);
        Assert.True((a - aFd).Magnitude <= 1e-5 * (1 + aFd.Magnitude), $"a {a} vs {aFd}");
        Assert.True((b - bFd).Magnitude <= 1e-5 * (1 + bFd.Magnitude), $"b {b} vs {bFd}");
        Assert.True((d - dFd).Magnitude <= 1e-5 * (1 + dFd.Magnitude), $"d {d} vs {dFd}");
        // Holomorphic: the derivative is direction-free (imaginary step agrees).
        var (zi, _) = Iter(s + new Complex(0, h), c0, n); var (zj, _) = Iter(s - new Complex(0, h), c0, n);
        Assert.True((a - (zi - zj) / new Complex(0, 2 * h)).Magnitude <= 1e-5 * (1 + aFd.Magnitude));
    }

    [Fact]
    public void SingularValues_MatchPowerIteration()
    {
        var j = DualOrbitEscapeCalculator.RunJacobian(0.3, 0.1, -0.12, 0.74, 9, 1e6);
        var (a, b, d) = Entries(j);
        // M = JᴴJ (Hermitian 2×2); power iteration for the top eigenvalue.
        Complex m00 = Complex.Conjugate(a) * a + Complex.Conjugate(b) * b, m01 = Complex.Conjugate(b) * d,
                m10 = Complex.Conjugate(d) * b, m11 = Complex.Conjugate(d) * d;
        Complex v0 = 1, v1 = 0.3; double lam = 0;
        for (int k = 0; k < 500; k++)
        {
            Complex w0 = m00 * v0 + m01 * v1, w1 = m10 * v0 + m11 * v1;
            lam = Math.Sqrt(w0.Magnitude * w0.Magnitude + w1.Magnitude * w1.Magnitude);
            v0 = w0 / lam; v1 = w1 / lam;
        }
        double s1 = Math.Sqrt(lam), s2 = (a * d).Magnitude / s1;
        var (l1, l2) = j.LnSingularValues();
        Assert.Equal(Math.Log(s1), l1, 6);
        Assert.Equal(Math.Log(s2), l2, 6);
    }

    // Attracting cycle of the c-orbit, multiplier μ, period p: a, b converge, d ~ μ^(N/p).
    [Theory]
    [InlineData(-0.2, 0.1, 0.3, 0.0, 1)]   // fixed point: μ = 2α, α = (1 − √(1 − 4s))/2
    [InlineData(-1.1, 0.0, 0.2, 0.1, 2)]   // 2-cycle of z² + s: μ = 4(s + 1)
    public void InteriorLimit_LnSigma2OverN_IsLnMultiplierOverPeriod(double sx, double sy, double cx, double cy, int p)
    {
        Complex s = new(sx, sy);
        Complex mu = p == 1 ? 2 * (1 - Complex.Sqrt(1 - 4 * s)) / 2 : 4 * (s + 1);
        const int n = 600;
        var j = DualOrbitEscapeCalculator.RunJacobian(cx, cy, sx, sy, n, 1e6);
        Assert.Equal(n, j.N);
        var (l1, l2) = j.LnSingularValues();
        Assert.Equal(Math.Log(mu.Magnitude) / p, l2 / n, 2);
        Assert.True(Math.Abs(l1 / n) < 0.02, $"FTLE {l1 / n} should → 0 in the interior");
    }

    // s = −2 (a Misiurewicz point): the z-orbit 0 → −2 → 2 lands on the
    // repelling fixed point β = 2, multiplier 4, so ∂z/∂s ~ 4^N → ln σ₁/N = ln 4;
    // the c-orbit is a typical Chebyshev orbit on [−2, 2] (Lyapunov exponent
    // ln 2) → ln σ₂/N = ln|a| + ln|d| − ln σ₁ → ln 2. 4000 steps also exercise
    // the log-scale (4^4000 would overflow a double).
    [Fact]
    public void Misiurewicz_FtleIsLn4_AndSigma2IsTheChebyshevLn2_WithoutOverflow()
    {
        var j = DualOrbitEscapeCalculator.RunJacobian(0.3, 0.0, -2.0, 0.0, 4000, 1e6);
        Assert.Equal(4000, j.N);
        var (l1, l2) = j.LnSingularValues();
        Assert.False(double.IsInfinity(l1) || double.IsNaN(l1) || double.IsInfinity(l2) || double.IsNaN(l2));
        Assert.Equal(Math.Log(4), l1 / j.N, 2);
        Assert.Equal(Math.Log(2), l2 / j.N, 2);
    }

    private static DualOrbitEscapeCalculator Calc(FractalParameters p, double cx = -0.6, double zoom = 1.0, int w = 64, int h = 48)
    {
        var c = new DualOrbitEscapeCalculator(w, h)
        {
            CenterX = cx, CenterY = 0.05, Zoom = zoom, MaxIterations = 300,
            FractalParameters = p, ColorMap = new RampMap(),
        };
        c.Calculate();
        return c;
    }

    [Theory]
    [InlineData(DualOrbitField.Ftle)]
    [InlineData(DualOrbitField.JacobianAnisotropy)]
    public void Fields_AreLiveEverywhere_AndQuaternionIsInterior(DualOrbitField f)
    {
        var c = Calc(new FractalParameters { DualOrbitField = f });
        Assert.All(c.ColorScalarBuffer, v => Assert.True(v > 0 && v <= 300));
        Assert.True(c.ColorScalarBuffer.Distinct().Count() > 50);
        var q = Calc(new FractalParameters { DualOrbitField = f, DualOrbitMap = DualOrbitMap.Quaternion });
        Assert.All(q.ColorBuffer, px => Assert.Equal(0xFF102030u, px));
    }

    // LIC of a constant field is a 1D box blur of the noise along it.
    [Theory]
    [InlineData(0.0, 1, 0)]
    [InlineData(Math.PI, 1, 0)]          // orientation, not direction
    [InlineData(Math.PI / 2, 0, 1)]
    public void Lic_OfAConstantField_IsABoxBlur(double angle, int dx, int dy)
    {
        const int w = 40, h = 30, half = 5;
        var theta = Enumerable.Repeat((float)angle, w * h).ToArray();
        var noise = Enumerable.Range(0, w * h).Select(DualOrbitEscapeCalculator.LicNoise).ToArray();
        var lic = DualOrbitEscapeCalculator.Lic(theta, noise, w, h, half);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double sum = 0; int cnt = 0;
                for (int k = -half; k <= half; k++)
                {
                    int xx = x + k * dx, yy = y + k * dy;
                    if (xx < 0 || yy < 0 || xx >= w || yy >= h) continue;
                    sum += noise[yy * w + xx]; cnt++;
                }
                Assert.Equal(sum / cnt, lic[y * w + x], 1e-6);
            }
    }

    [Fact]
    public void Lic_StopsAtPixelsWithNoOrientation()
    {
        const int w = 20, h = 1;
        var theta = Enumerable.Repeat(0f, w).ToArray();
        theta[10] = float.NaN;
        var noise = Enumerable.Range(0, w).Select(DualOrbitEscapeCalculator.LicNoise).ToArray();
        var lic = DualOrbitEscapeCalculator.Lic(theta, noise, w, h, 8);
        Assert.True(float.IsNaN(lic[10]));
        Assert.Equal(noise.Take(10).Average(), lic[5], 1e-6);   // pixels 0..9: the border on one side, NaN at 10 on the other
    }

    // Far outside M the escape time's level sets are near-circles → its
    // gradient (FieldGradient orientation) is radial.
    [Fact]
    public void FieldGradient_IsRadial_FarFromTheSet()
    {
        var p = new FractalParameters { DualOrbitField = DualOrbitField.EscapeTimeZ, DualOrbitLicSource = DualOrbitLicSource.FieldGradient };
        const int w = 96, h = 96;
        var c = new DualOrbitEscapeCalculator(w, h) { CenterX = 0, CenterY = 0, Zoom = 0.15, MaxIterations = 300, FractalParameters = p, ColorMap = new RampMap() };
        c.Calculate();
        double pitch = 4.0 / w / 0.15; int n = 0;
        for (int y = 1; y < h - 1; y++)
            for (int x = 1; x < w - 1; x++)
            {
                double sx = (x - w / 2.0) * pitch, sy = (y - h / 2.0) * pitch;
                if (sx * sx + sy * sy < 9) continue;
                float t = c.LicOrientation[y * w + x];
                if (float.IsNaN(t)) continue;
                double diff = Math.Abs(Math.IEEERemainder(t - Math.Atan2(sy, sx), Math.PI));
                Assert.True(diff < 0.2, $"({sx:F2},{sy:F2}) orientation off by {diff:F3}");
                n++;
            }
        Assert.True(n > 500, $"only {n} far pixels checked");
    }

    [Fact]
    public void Lic_LengthStrengthAndGradientSources_RecolourFromTheCache()
    {
        var p = new FractalParameters { DualOrbitField = DualOrbitField.GreenRatio };
        var c = Calc(p);
        var plain = (uint[])c.ColorBuffer.Clone();
        p.DualOrbitLicSource = DualOrbitLicSource.FieldContour;
        c.Calculate();
        Assert.True(c.LastCalculateReusedOrbits);
        Assert.NotEqual(plain, c.ColorBuffer);
        p.DualOrbitLicLength = 20; p.DualOrbitLicStrength = 0.5;
        c.Calculate();
        Assert.True(c.LastCalculateReusedOrbits);
        Assert.Equal(Calc(p.Clone()).ColorBuffer, c.ColorBuffer);
        p.DualOrbitLicStrength = 0;
        c.Calculate();
        Assert.Equal(plain, c.ColorBuffer);   // strength 0 = no texture
    }

    [Fact]
    public void SeparationSource_FollowsArgCnMinusZn_AndReiterates()
    {
        // s = 0.4 + 0.05i escapes (inside M_c the orbits share a cycle and D_N → 0: no direction).
        var p = new FractalParameters { DualOrbitField = DualOrbitField.GreenRatio, DualOrbitCSeedX = 0.3, DualOrbitCSeedY = 0.2 };
        var c = Calc(p, cx: 0.4, w: 2, h: 2, zoom: 1e9);
        p.DualOrbitLicSource = DualOrbitLicSource.SeparationDirection;
        c.Calculate();
        Assert.False(c.LastCalculateReusedOrbits);
        var j = DualOrbitEscapeCalculator.RunJacobian(0.3, 0.2, 0.4, 0.05, 300, p.DualOrbitBailout);
        float t = c.LicOrientation[3];
        Assert.Equal(Math.Atan2(j.SepY, j.SepX), t, 3);
    }

    [Fact]
    public void LiveView_RoundTripsThroughTheCommandBuilder()
    {
        var p = new FractalParameters
        {
            DualOrbitField = DualOrbitField.Ftle, DualOrbitFtleSpan = 2.5, DualOrbitAnisotropyScale = 7,
            DualOrbitLicSource = DualOrbitLicSource.FieldContour, DualOrbitLicLength = 18, DualOrbitLicStrength = 0.6,
        };
        var report = BatchCommandBuilder.BuildWithReport(new BatchCommandSnapshot
        {
            Fractal = FractalType.JulibrotPair, Parameters = p,
            FamilyParams = RegionFractalParams.Snapshot(FractalType.JulibrotPair, p)!.ToKeyValues(),
        });
        Assert.True(BatchOptions.TryParse(report.Args.ToArray(), 0, out var o, out var err), err);
        var fresh = new FractalParameters();
        RegionFractalParams.FromKeyValues(o.Params, FractalType.JulibrotPair, out _)!.ApplyTo(fresh);
        Assert.Equal(DualOrbitField.Ftle, fresh.DualOrbitField);
        Assert.Equal(2.5, fresh.DualOrbitFtleSpan);
        Assert.Equal(7, fresh.DualOrbitAnisotropyScale);
        Assert.Equal(DualOrbitLicSource.FieldContour, fresh.DualOrbitLicSource);
        Assert.Equal(18, fresh.DualOrbitLicLength);
        Assert.Equal(0.6, fresh.DualOrbitLicStrength);
    }
}
