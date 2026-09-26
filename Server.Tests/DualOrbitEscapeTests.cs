// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using FracturingFog;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

// Dual-orbit escape-geometry field — S1 (#864, epic #850). See
// Docs/Technical/Theoretical-Fractal-RnD.md §3.6.
public sealed class DualOrbitEscapeTests
{
    private static DualOrbitEscapeCalculator Make(DualOrbitField f, double cx, double cy, bool cEqS)
    {
        var p = new FractalParameters
        {
            DualOrbitField = f, DualOrbitCSeedX = cx, DualOrbitCSeedY = cy, DualOrbitCEqualsS = cEqS,
        };
        return new DualOrbitEscapeCalculator(180, 180) { CenterX = -0.5, CenterY = 0, Zoom = 1.0, FractalParameters = p };
    }

    private static double MeanAbs(float[] b)
    {
        double s = 0; foreach (var v in b) s += Math.Abs(v);
        return s / b.Length;
    }

    // The load-bearing degeneracy (§3.6): with c = s the c-orbit is the z-orbit
    // shifted one step, so E_c = E_z and the separation field is identically 0.
    [Fact]
    public void CEqualsS_CollapsesSeparationToZero()
    {
        var c = Make(DualOrbitField.EscapeSeparation, 0.5, 0.0, cEqS: true);
        c.Calculate();
        Assert.True(MeanAbs(c.SmoothBuffer) < 1e-3, $"c=s should give D≡0 but mean={MeanAbs(c.SmoothBuffer)}");
    }

    // Decoupled c gives a non-trivial escape-separation field.
    [Fact]
    public void DecoupledC_ProducesNonTrivialField()
    {
        var c = Make(DualOrbitField.EscapeSeparation, 0.5, 0.0, cEqS: false);
        c.Calculate();
        Assert.True(MeanAbs(c.SmoothBuffer) > 1.0, $"decoupled field mean={MeanAbs(c.SmoothBuffer)}");
    }

    [Fact]
    public void Fields_DifferFromEachOther()
    {
        var sep = Make(DualOrbitField.EscapeSeparation, 0.5, 0.0, false);
        var mid = Make(DualOrbitField.MidpointResidual, 0.5, 0.0, false);
        var ang = Make(DualOrbitField.DualOrbitAngle, 0.5, 0.0, false);
        var dn = Make(DualOrbitField.DeltaN, 0.5, 0.0, false);
        sep.Calculate(); mid.Calculate(); ang.Calculate(); dn.Calculate();
        Assert.NotEqual(sep.ColorBuffer, mid.ColorBuffer);
        Assert.NotEqual(sep.ColorBuffer, ang.ColorBuffer);
        Assert.NotEqual(mid.ColorBuffer, dn.ColorBuffer);
    }

    [Fact]
    public void RespondsToCSeed()
    {
        var a = Make(DualOrbitField.EscapeSeparation, 0.5, 0.0, false);
        var b = Make(DualOrbitField.EscapeSeparation, -0.3, 0.4, false);
        a.Calculate(); b.Calculate();
        Assert.NotEqual(a.ColorBuffer, b.ColorBuffer);
    }

    [Fact]
    public void IsDeterministic()
    {
        var a = Make(DualOrbitField.DualOrbitAngle, 0.5, 0.0, false);
        var b = Make(DualOrbitField.DualOrbitAngle, 0.5, 0.0, false);
        a.Calculate(); b.Calculate();
        Assert.Equal(a.ColorBuffer, b.ColorBuffer);
    }

    [Fact]
    public void RespondsToPan()
    {
        var a = Make(DualOrbitField.EscapeSeparation, 0.5, 0.0, false);
        var b = Make(DualOrbitField.EscapeSeparation, 0.5, 0.0, false);
        b.CenterX = 0.3;
        a.Calculate(); b.Calculate();
        Assert.NotEqual(a.ColorBuffer, b.ColorBuffer);
    }

    [Fact]
    public void SmoothBuffer_DrivesReliefHeight()
        => Assert.IsAssignableFrom<Interefaces.IHeightFieldSource>(
               new DualOrbitEscapeCalculator(8, 8));

    // ── Quaternion map variant (S3, #866) ────────────────────────────────────

    private static DualOrbitEscapeCalculator MakeQuat(DualOrbitField f, double cx, double cy, double cz, double sz, bool cEqS)
    {
        var p = new FractalParameters
        {
            DualOrbitMap = DualOrbitMap.Quaternion, DualOrbitField = f,
            DualOrbitCSeedX = cx, DualOrbitCSeedY = cy, DualOrbitCSeedZ = cz, DualOrbitSZ = sz,
            DualOrbitCEqualsS = cEqS,
        };
        return new DualOrbitEscapeCalculator(180, 180) { CenterX = -0.5, CenterY = 0, Zoom = 1.0, FractalParameters = p };
    }

    [Fact]
    public void Quaternion_DecoupledC_ProducesNonTrivialField()
    {
        var c = MakeQuat(DualOrbitField.EscapeSeparation, 0.5, 0.0, 0.3, 0.0, false);
        c.Calculate();
        Assert.True(MeanAbs(c.SmoothBuffer) > 1.0, $"quat field mean={MeanAbs(c.SmoothBuffer)}");
    }

    [Fact]
    public void Quaternion_DiffersFromComplex()
    {
        var q = MakeQuat(DualOrbitField.EscapeSeparation, 0.5, 0.0, 0.3, 0.0, false);
        var z = Make(DualOrbitField.EscapeSeparation, 0.5, 0.0, false);
        q.Calculate(); z.Calculate();
        Assert.NotEqual(q.ColorBuffer, z.ColorBuffer);
    }

    [Fact]
    public void Quaternion_RespondsToCSeedZ_AndSZ()
    {
        var a = MakeQuat(DualOrbitField.EscapeSeparation, 0.5, 0.0, 0.3, 0.0, false);
        var b = MakeQuat(DualOrbitField.EscapeSeparation, 0.5, 0.0, 0.7, 0.0, false);
        var d = MakeQuat(DualOrbitField.EscapeSeparation, 0.5, 0.0, 0.3, 0.4, false);
        a.Calculate(); b.Calculate(); d.Calculate();
        Assert.NotEqual(a.ColorBuffer, b.ColorBuffer);   // c-seed Z drives it
        Assert.NotEqual(a.ColorBuffer, d.ColorBuffer);   // s_z dial drives it
    }

    // The shift degeneracy holds in the quaternion map too: c = s ⇒ c-orbit is the
    // z-orbit advanced one step ⇒ D ≡ 0. Decoupling (c-seed off ŝ) is required.
    [Fact]
    public void Quaternion_CEqualsS_CollapsesSeparationToZero()
    {
        var c = MakeQuat(DualOrbitField.EscapeSeparation, 0.5, 0.0, 0.3, 0.0, cEqS: true);
        c.Calculate();
        Assert.True(MeanAbs(c.SmoothBuffer) < 1e-3, $"quat c=s mean={MeanAbs(c.SmoothBuffer)}");
    }

    [Fact]
    public void Quaternion_ClonesAndPersists()
    {
        var p = new FractalParameters
        {
            DualOrbitMap = DualOrbitMap.Quaternion, DualOrbitCSeedZ = 0.7, DualOrbitSZ = -0.2,
        };
        var cl = p.Clone();
        Assert.Equal(DualOrbitMap.Quaternion, cl.DualOrbitMap);
        Assert.Equal(0.7, cl.DualOrbitCSeedZ);
        Assert.Equal(-0.2, cl.DualOrbitSZ);

        var snap = RegionFractalParams.Snapshot(FractalType.DualOrbitEscape, p);
        var restored = new FractalParameters();
        snap!.ApplyTo(restored);
        Assert.Equal(DualOrbitMap.Quaternion, restored.DualOrbitMap);
        Assert.Equal(0.7, restored.DualOrbitCSeedZ);
        Assert.Equal(-0.2, restored.DualOrbitSZ);
    }

    // ── Intrinsic fields (S7, #970) ──────────────────────────────────────────
    // Invariants checked against independent ground truth, not self-consistency:
    // bailout-independence, the analytic Green's-function ratio, and known
    // Mandelbrot external angles.

    private static DualOrbitEscapeCalculator MakeR(DualOrbitField f, double bailout)
    {
        var p = new FractalParameters
        {
            DualOrbitField = f, DualOrbitCSeedX = 0.3, DualOrbitCSeedY = 0.2, DualOrbitBailout = bailout,
        };
        return new DualOrbitEscapeCalculator(160, 160) { CenterX = -0.5, CenterY = 0, Zoom = 1.0, MaxIterations = 400, FractalParameters = p };
    }

    // Pixels where both runs produced a value (both orbits escaped in both).
    private static (int n, double maxDiff, int within) Compare(float[] a, float[] b, double tol, bool circular, double period)
    {
        int n = 0, within = 0; double maxDiff = 0;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] == 0f || b[i] == 0f) continue;
            double d = Math.Abs(a[i] - b[i]);
            if (circular) d = Math.Min(d, period - d);
            n++; maxDiff = Math.Max(maxDiff, d);
            if (d <= tol) within++;
        }
        return (n, maxDiff, within);
    }

    [Fact]
    public void GreenRatio_IsBailoutIndependent()
    {
        var a = MakeR(DualOrbitField.GreenRatio, 128); var b = MakeR(DualOrbitField.GreenRatio, 4096);
        a.Calculate(); b.Calculate();
        var (n, maxDiff, _) = Compare(a.SmoothBuffer, b.SmoothBuffer, 0, false, 0);
        Assert.True(n > 5000, $"only {n} comparable pixels");
        // 400 iters · 0.5/8 per octave = 25 units/octave; 0.05 units ≈ 0.002 octaves.
        Assert.True(maxDiff < 0.05, $"GreenRatio moved {maxDiff} with the bailout");
    }

    [Fact]
    public void EscapeLocationFields_DependOnBailout()
    {
        // The finding behind #970: separation is an artifact of R.
        var a = MakeR(DualOrbitField.EscapeSeparation, 128); var b = MakeR(DualOrbitField.EscapeSeparation, 4096);
        a.Calculate(); b.Calculate();
        var (n, maxDiff, _) = Compare(a.SmoothBuffer, b.SmoothBuffer, 0, false, 0);
        Assert.True(n > 5000 && maxDiff > 10.0, $"separation should change with R (n={n}, maxDiff={maxDiff})");
    }

    [Fact]
    public void ExternalAngleDelta_IsBailoutIndependent()
    {
        var a = MakeR(DualOrbitField.ExternalAngleDelta, 128); var b = MakeR(DualOrbitField.ExternalAngleDelta, 4096);
        a.Calculate(); b.Calculate();
        // Period = maxIter (a full turn); 1e-3 turn tolerance.
        var (n, _, within) = Compare(a.SmoothBuffer, b.SmoothBuffer, 0.4, true, 400);
        Assert.True(n > 5000, $"only {n} comparable pixels");
        Assert.True(within >= 0.97 * n, $"{within}/{n} pixels agree across bailouts");
    }

    [Fact]
    public void GreenRatio_MatchesAnalyticGreenFunction()
    {
        // One pixel pinned at s (tiny pixel pitch). Independent G via a huge-radius
        // iteration: G(u) = lim log|u_n| / 2^n.
        static double G(double ux, double uy, double sx, double sy)
        {
            for (int n = 0; n < 2000; n++)
            {
                double r = Math.Sqrt(ux * ux + uy * uy);
                if (r > 1e30) return Math.Log(r) / Math.Pow(2, n);   // 1e30: next square stays finite
                double nx = ux * ux - uy * uy + sx; uy = 2 * ux * uy + sy; ux = nx;
            }
            return 0;
        }
        const double span = 8.0; const int maxIter = 400;
        foreach (var (sx, sy, cx, cy) in new[] { (0.45, 0.45, 0.5, 0.3), (-0.8, 0.3, 0.1, -0.4), (0.3, -0.6, -0.2, 0.6) })
        {
            var p = new FractalParameters { DualOrbitField = DualOrbitField.GreenRatio, DualOrbitCSeedX = cx, DualOrbitCSeedY = cy };
            var calc = new DualOrbitEscapeCalculator(1, 1) { CenterX = sx, CenterY = sy, Zoom = 1e9, MaxIterations = maxIter, FractalParameters = p };
            calc.Calculate();
            double octaves = (calc.SmoothBuffer[0] / maxIter - 0.5) * 2.0 * span;
            double expected = Math.Log2(G(cx, cy, sx, sy) / G(0, 0, sx, sy));
            Assert.True(Math.Abs(octaves - expected) < 1e-3, $"s=({sx},{sy}) c=({cx},{cy}): field {octaves} vs analytic {expected} (Gc={G(cx, cy, sx, sy)}, Gz={G(0, 0, sx, sy)})");
        }
    }

    [Theory]
    [InlineData(1.0, 0.0, 0.0)]     // real axis right of 1/4: external ray 0
    [InlineData(2.5, 0.0, 0.0)]
    [InlineData(-3.0, 0.0, 0.5)]    // real axis left of −2: external ray 1/2
    [InlineData(-2.2, 0.0, 0.5)]
    public void ExternalAngle_OfSeedZero_IsMandelbrotParameterAngle(double sx, double sy, double expected)
    {
        double t = DualOrbitEscapeCalculator.ExternalAngleTurns(0, 0, sx, sy, 400);
        double d = Math.Abs(t - expected); d = Math.Min(d, 1 - d);
        Assert.True(d < 1e-6, $"s={sx}: angle {t}, expected {expected}");
    }

    [Theory]
    [InlineData(0.3, 0.7)]
    [InlineData(-0.9, 0.35)]
    [InlineData(0.1, 1.1)]
    public void ExternalAngle_ConjugateParameter_IsNegatedAngle(double sx, double sy)
    {
        // f_{s̄}(ū) = conj f_s(u) ⇒ θ(s̄) = −θ(s) mod 1.
        double a = DualOrbitEscapeCalculator.ExternalAngleTurns(0, 0, sx, sy, 400);
        double b = DualOrbitEscapeCalculator.ExternalAngleTurns(0, 0, sx, -sy, 400);
        double d = Math.Abs((a + b) % 1.0); d = Math.Min(d, 1 - d);
        Assert.True(d < 1e-9, $"θ(s)={a}, θ(s̄)={b}");
    }

    [Fact]
    public void EscapeSeparation_AtDefaultBailout_MatchesHandIteration()
    {
        // Guards the bailout refactor: the legacy field is still |E_c − E_z| / 2R at R=128.
        static (double x, double y) Escape(double ux, double uy, double sx, double sy)
        {
            while (ux * ux + uy * uy <= 128.0 * 128.0) { double nx = ux * ux - uy * uy + sx; uy = 2 * ux * uy + sy; ux = nx; }
            return (ux, uy);
        }
        const double sx = 0.45, sy = 0.45; const int maxIter = 256;
        var p = new FractalParameters { DualOrbitField = DualOrbitField.EscapeSeparation, DualOrbitCSeedX = 0.5, DualOrbitCSeedY = 0.3 };
        var calc = new DualOrbitEscapeCalculator(1, 1) { CenterX = sx, CenterY = sy, Zoom = 1e12, MaxIterations = maxIter, FractalParameters = p };
        calc.Calculate();
        var ez = Escape(0, 0, sx, sy); var ec = Escape(0.5, 0.3, sx, sy);
        double d = Math.Sqrt((ec.x - ez.x) * (ec.x - ez.x) + (ec.y - ez.y) * (ec.y - ez.y));
        double expected = Math.Min(d / 256.0, 1.0) * maxIter;
        Assert.True(Math.Abs(calc.SmoothBuffer[0] - expected) < 1e-3 * Math.Max(1, expected),
            $"field {calc.SmoothBuffer[0]} vs hand {expected}");
    }

    [Fact]
    public void IntrinsicParams_CloneAndPersist()
    {
        var p = new FractalParameters
        {
            DualOrbitField = DualOrbitField.ExternalAngleDelta, DualOrbitBailout = 1000, DualOrbitRatioSpan = 3.5,
        };
        var cl = p.Clone();
        Assert.Equal(1000, cl.DualOrbitBailout);
        Assert.Equal(3.5, cl.DualOrbitRatioSpan);

        var restored = new FractalParameters();
        RegionFractalParams.Snapshot(FractalType.DualOrbitEscape, p)!.ApplyTo(restored);
        Assert.Equal(DualOrbitField.ExternalAngleDelta, restored.DualOrbitField);
        Assert.Equal(1000, restored.DualOrbitBailout);
        Assert.Equal(3.5, restored.DualOrbitRatioSpan);
    }

    // ── Registration ─────────────────────────────────────────────────────────

    [Fact]
    public void MotionClass_IsZoomable2D()
        => Assert.Equal(FractalMotionClass.Zoomable2D,
                        FractalMotionCapabilities.MotionClass(FractalType.DualOrbitEscape));

    [Fact]
    public void Capabilities_SuppliesHistogram()
        => Assert.Equal(FractalCapabilities.SuppliesHistogram,
                        FractalCapabilityMap.For(FractalType.DualOrbitEscape));

    [Fact]
    public void HasDisplayName()
        => Assert.Equal("Dual-Orbit Escape", Fractals.FractalNameByNameType[FractalType.DualOrbitEscape]);

    [Fact]
    public void ClonesAndPersists()
    {
        var p = new FractalParameters
        {
            DualOrbitField = DualOrbitField.DualOrbitAngle,
            DualOrbitCSeedX = -0.3, DualOrbitCSeedY = 0.4, DualOrbitCEqualsS = true,
        };
        var cl = p.Clone();
        Assert.Equal(DualOrbitField.DualOrbitAngle, cl.DualOrbitField);
        Assert.Equal(-0.3, cl.DualOrbitCSeedX);
        Assert.True(cl.DualOrbitCEqualsS);

        var pp = new FractalParameters { DualOrbitField = DualOrbitField.DeltaN, DualOrbitCSeedX = 0.9 };
        var snap = RegionFractalParams.Snapshot(FractalType.DualOrbitEscape, pp);
        var restored = new FractalParameters();
        snap!.ApplyTo(restored);
        Assert.Equal(DualOrbitField.DeltaN, restored.DualOrbitField);
        Assert.Equal(0.9, restored.DualOrbitCSeedX);
    }
}
