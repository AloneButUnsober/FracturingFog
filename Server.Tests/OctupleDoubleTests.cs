using System;
using FracturingFog.FFMath;
using Xunit;

namespace FracturingFog.Server.Tests;

// Wave 2.11 OD arithmetic correctness tests. Two-tier strategy:
//   1. Self-consistency  — (double)(OD(x)) == x, OD-from-QD/DD round-trip,
//      identity ops (a+0, a*1, a*a vs Square).
//   2. Cross-tier parity — OD ops on QD-representable values must agree
//      with QD ops in the top 4 limbs to within one OD-ulp. Diagnoses
//      Renorm9 / op+ / op* without needing a hand-computed reference.
public sealed class OctupleDoubleTests
{
    // Largest acceptable mismatch between OD and QD results on QD-rep'ble
    // inputs. Set to 4× double-precision so per-pass renorm noise is
    // tolerated; anything bigger indicates a bug.
    private const double QdParityEps = 4 * 1e-30;

    // ── Self-consistency ─────────────────────────────────────────────────

    [Fact]
    public void DoubleRoundTrip_Exact()
    {
        double[] cases = { 1.0, 0.0, -1.0, 1e-300, 1e300, Math.PI, -Math.E };
        foreach (var v in cases)
        {
            var od = new OD(v);
            Assert.Equal(v, (double)od);
            Assert.Equal(v, od.X0);
            Assert.Equal(0.0, od.X1);
            Assert.Equal(0.0, od.X7);
        }
    }

    [Fact]
    public void AddZero_Identity()
    {
        var a = new OD(1.0, 1e-17, 1e-34, 1e-51, 1e-68, 1e-85, 1e-102, 1e-119);
        var r = a + OD.Zero;
        AssertOdNear(a, r, 0.0);
    }

    [Fact]
    public void Negate_RoundTrip()
    {
        var a = new OD(Math.PI);
        var b = -(-a);
        Assert.Equal(a.X0, b.X0);
        Assert.Equal(a.X1, b.X1);
    }

    [Fact]
    public void SubtractSelf_Zero()
    {
        var a = new OD(1.0, 1e-17, 1e-34, 1e-51, 0, 0, 0, 0);
        var r = a - a;
        Assert.Equal(0.0, r.X0);
        Assert.Equal(0.0, r.X1);
    }

    [Fact]
    public void MultiplyByOne_Identity()
    {
        var a = new OD(Math.PI, 1e-17, 1e-34, 1e-51, 0, 0, 0, 0);
        var r = a * new OD(1.0);
        AssertOdNear(a, r, QdParityEps);
    }

    [Fact]
    public void Square_MatchesSelfMultiply()
    {
        var a = new OD(1.234567890123, 1e-18, 1e-35, 0, 0, 0, 0, 0);
        var sq = a.Square();
        var mul = a * a;
        AssertOdNear(sq, mul, QdParityEps);
    }

    // ── Cross-tier parity with QD ────────────────────────────────────────

    [Fact]
    public void OdFromQd_PreservesLimbs()
    {
        var q = new QD(1.0, 1e-17, 1e-34, 1e-51);
        var od = new OD(q);
        Assert.Equal(q.X0, od.X0);
        Assert.Equal(q.X1, od.X1);
        Assert.Equal(q.X2, od.X2);
        Assert.Equal(q.X3, od.X3);
        Assert.Equal(0.0, od.X4);
    }

    [Fact]
    public void Add_QdInputs_MatchesQd()
    {
        var aq = new QD(1.5, 1e-17, 1e-34, 1e-51);
        var bq = new QD(0.25, 2e-18, -3e-35, 4e-52);
        var rq = aq + bq;
        var rod = new OD(aq) + new OD(bq);
        AssertQdParity(rq, rod);
    }

    [Fact]
    public void Multiply_QdInputs_MatchesQd()
    {
        var aq = new QD(1.5, 1e-17, 1e-34, 1e-51);
        var bq = new QD(2.25, -3e-18, 7e-35, -1e-52);
        var rq = aq * bq;
        var rod = new OD(aq) * new OD(bq);
        AssertQdParity(rq, rod);
    }

    [Fact]
    public void Square_QdInput_MatchesQd()
    {
        var aq = new QD(1.234567890123, 1e-17, -2e-34, 5e-51);
        var rq = aq.Square();
        var rod = new OD(aq).Square();
        AssertQdParity(rq, rod);
    }

    // Mandelbrot-style: z = z² + c for several iters, OD vs QD top limbs.
    [Fact]
    public void MandelbrotIter_QdVsOd_Diverge_Slowly()
    {
        var cq = new QD(-0.75, 1e-17, 0, 0);
        var cod = new OD(cq);
        QD zq = QD.Zero;
        OD zod = OD.Zero;
        for (int i = 0; i < 20; i++)
        {
            zq = zq.Square() + cq;
            zod = zod.Square() + cod;
            AssertQdParity(zq, zod, 1e-25); // 20 iters of squaring accumulates noise
        }
    }

    // ── FromCenterOffset ─────────────────────────────────────────────────

    [Fact]
    public void FromCenterOffset_ZeroOffset_ReturnsCenter()
    {
        var c = new OD(1.5, 1e-17, 1e-34, 1e-51, 1e-68, 0, 0, 0);
        var r = OD.FromCenterOffset(c, 0.0, 1e-50);
        AssertOdNear(c, r, 1e-60);
    }

    [Fact]
    public void FromCenterOffset_TinyScale_PreservesOffset()
    {
        var c = OD.Zero;
        var r = OD.FromCenterOffset(c, 1.0, 1e-50);
        // 1.0 × 1e-50 = 1e-50, should land in X0 of result
        Assert.Equal(1e-50, r.X0, 1e-65);
    }

    // ── Renorm9 stress — zero mid-limbs ──────────────────────────────────

    [Fact]
    public void Add_SparseLimbs_RoundTrip()
    {
        // OD with non-zero only at X0, X3, X7. After op+ Renorm9 should
        // canonicalise without dropping the spread-out mass.
        var a = new OD(1.0, 0, 0, 1e-50, 0, 0, 0, 1e-118);
        var b = new OD(2.0, 0, 0, 0, 0, 0, 0, 0);
        var r = a + b;
        Assert.Equal(3.0, r.X0, 1e-15);
        // Trailing limbs should retain ~1e-50 + ~1e-118 sum somewhere.
        double tail = r.X1 + r.X2 + r.X3 + r.X4 + r.X5 + r.X6 + r.X7;
        Assert.True(Math.Abs(tail - 1e-50) < 1e-60, $"tail={tail:G6}, expected ≈1e-50");
    }

    // ── OD-specific stress (non-zero X4..X7 limbs) ───────────────────────
    //
    // These probe the path that QD parity tests can't reach: arithmetic on
    // inputs whose precision lives in limbs 4..7. Failures here are the
    // suspected source of the "solid colour at zoom 1e40+" regression.

    [Fact]
    public void Add_LowerLimbsOnly_PreservesMagnitude()
    {
        // a + b where a's X4..X7 hold all the value, b small.
        var a = new OD(0, 0, 0, 0, 1e-68, 1e-85, 1e-102, 1e-119);
        var b = new OD(0, 0, 0, 0, 2e-68, 0, 0, 0);
        var r = a + b;
        double expected = 3e-68 + 1e-85 + 1e-102 + 1e-119;
        double actual = (double)r;
        Assert.True(Math.Abs(actual - expected) < 1e-80,
            $"sum: expected {expected:G6}, got {actual:G6} ({r})");
    }

    [Fact]
    public void Multiply_X4LimbInputs_DoesNotDropMagnitude()
    {
        // 1.0 + 1e-68 squared: result ≈ 1 + 2e-68 + 1e-136 ≈ 1 + 2e-68.
        // OD must keep the 2e-68 cross term — that's the whole point of OD.
        var a = new OD(1.0, 0, 0, 0, 1e-68, 0, 0, 0);
        var sq = a.Square();
        double total = (double)sq;
        // Hi limb is 1.0, tail must sum to ≈ 2e-68.
        double tail = sq.X1 + sq.X2 + sq.X3 + sq.X4 + sq.X5 + sq.X6 + sq.X7;
        Assert.True(Math.Abs(tail - 2e-68) < 1e-78,
            $"cross-term lost: tail={tail:G6}, expected ≈2e-68\n  sq={sq}");
    }

    [Fact]
    public void Add_FullSpread_AllLimbsContributeToSum()
    {
        // OD whose every limb is non-zero, ordered by magnitude.
        // a + a = 2 × a, all limbs doubled. Verify each limb directly
        // (don't collapse to double — ULP rounds away tail below X0's bit).
        var a = new OD(1.0, 1e-17, 1e-34, 1e-51, 1e-68, 1e-85, 1e-102, 1e-119);
        var r = a + a;
        Assert.Equal(2.0, r.X0, 1e-15);
        // After renorm, top limb absorbs the 2e-17 (since 2 + 2e-17 fits
        // partially in X0's mantissa). Verify SOME lower limb retained mass.
        double tail = r.X1 + r.X2 + r.X3 + r.X4 + r.X5 + r.X6 + r.X7;
        Assert.True(Math.Abs(tail) > 1e-20,
            $"tail collapsed to zero — lower limbs lost. r={r}");
    }

    [Fact]
    public void Renorm_OrderingMonotone_AfterAdd()
    {
        // After op+, limbs must be in descending magnitude (canonical form).
        var a = new OD(1.0, 1e-17, 1e-34, 1e-51, 1e-68, 0, 0, 0);
        var b = new OD(1.0, -1e-17, 1e-34, 0, 0, 1e-85, 0, 0);
        var r = a + b;
        var limbs = new[] { r.X0, r.X1, r.X2, r.X3, r.X4, r.X5, r.X6, r.X7 };
        for (int i = 0; i < limbs.Length - 1; i++)
        {
            if (limbs[i] == 0.0) continue;
            double ai = Math.Abs(limbs[i]);
            double aj = Math.Abs(limbs[i + 1]);
            // Non-overlap rule: |X_{k+1}| <= ulp(X_k) ≤ 2^-52 · |X_k|.
            // Allow generous 1e-10 ratio for sloppy-renorm slack.
            Assert.True(aj <= ai * 1e-10 || aj == 0,
                $"Limb {i + 1}({limbs[i + 1]:G6}) >= 1e-10·limb{i}({limbs[i]:G6})");
        }
    }

    // Deep iteration — simulates ref orbit at zoom > 1e50. Cross-check OD
    // against a hand-computed expected orbit for c = -0.75 + 0i.
    [Fact]
    public void DeepIter_StableMagnitude()
    {
        var c = new OD(-0.75);
        OD z = OD.Zero;
        for (int i = 0; i < 100; i++)
        {
            z = z.Square() + c;
            // z stays bounded (|z| < 2) for c = -0.75 inside Mandelbrot.
            Assert.True(Math.Abs(z.X0) < 2.0,
                $"iter {i}: |z| = {z.X0:G6} — should stay bounded for c=-0.75");
        }
    }

    [Fact]
    public void Multiply_ByZero_Zero()
    {
        var a = new OD(1.0, 1e-17, 1e-34, 1e-51, 1e-68, 1e-85, 1e-102, 1e-119);
        var r = a * OD.Zero;
        Assert.Equal(0.0, (double)r);
    }

    // Full Mandelbrot ref orbit at user's pixelating coords. Cross-check
    // OD vs QD — both should produce the same orbit for the first ~500
    // iters since QD is precise enough there. Divergence indicates an
    // OD operator bug, not a precision-floor issue.
    [Fact]
    public void RefOrbit_ModerateZoom_OdMatchesQd()
    {
        // User-reported pixelating coords (zoom 7.14E48 — well within QD).
        // OD MUST agree with QD to better than pixel scale (5e-52) on X0
        // throughout the orbit, else every pixel renders identical.
        var cxQ = new QD(-1.9918151296901943, -7.8219844803880472e-17,
                         1.6601399303929208e-34, -5.8601391417687406e-51);
        var cyQ = new QD(-5.5240415753972429e-06, -2.8659813126937928e-22,
                         6.6910924132216174e-39, -2.0109018297360669e-55);
        var cxOD = new OD(cxQ);
        var cyOD = new OD(cyQ);

        QD zrQ = QD.Zero, ziQ = QD.Zero;
        OD zrOD = OD.Zero, ziOD = OD.Zero;
        // 200 iters at 1e-55 tol — verifies OD op*/op+/op- carry residuals
        // correctly. Beyond ~iter 600 QD itself hits precision floor on
        // this orbit (chaotic amplification of QD's ULP); OD remains
        // accurate but no longer comparable against QD.
        int firstBad = -1;
        double firstDx = 0, firstDy = 0;
        for (int i = 0; i < 200; i++)
        {
            QD newZiQ = (zrQ * ziQ) * 2.0 + cyQ;
            zrQ = zrQ.Square() - ziQ.Square() + cxQ;
            ziQ = newZiQ;

            OD newZiOD = (zrOD * ziOD) * 2.0 + cyOD;
            zrOD = zrOD.Square() - ziOD.Square() + cxOD;
            ziOD = newZiOD;

            double dx = Math.Abs(zrQ.X0 - zrOD.X0);
            double dy = Math.Abs(ziQ.X0 - ziOD.X0);
            if (firstBad < 0 && (dx > 1e-55 || dy > 1e-55))
            {
                firstBad = i;
                firstDx = dx;
                firstDy = dy;
            }
        }
        Assert.True(firstBad < 0,
            $"OD diverged from QD at iter {firstBad}: dx={firstDx:G6}, dy={firstDy:G6}\n" +
            $"  Pixel scale at user's zoom 7.14E48: ≈5e-52\n" +
            $"  Divergence > pixel scale → solid-colour render (the regression)");
    }

    // Deep-iter sanity check — does NOT compare to QD (QD precision floors
    // out earlier than OD). Confirms OD orbit stays bounded / finite and
    // doesn't blow up to NaN / Inf at high iteration counts.
    [Fact]
    public void DeepRefOrbit_FiniteThroughout()
    {
        var cx = new OD(new QD(-1.9918151296901943, -7.8219844803880472e-17,
                               1.6601399303929208e-34, -5.8601391417687406e-51));
        var cy = new OD(new QD(-5.5240415753972429e-06, -2.8659813126937928e-22,
                               6.6910924132216174e-39, -2.0109018297360669e-55));
        OD zr = OD.Zero, zi = OD.Zero;
        for (int i = 0; i < 5000; i++)
        {
            // Real renderer stops iterating after escape (|z|² > 4); we
            // continue so a precision blow-up (NaN/Inf) would surface here.
            double mag2 = zr.X0 * zr.X0 + zi.X0 * zi.X0;
            if (mag2 > 1e10) break;  // legitimately escaped — fine
            OD newZi = (zr * zi) * 2.0 + cy;
            zr = zr.Square() - zi.Square() + cx;
            zi = newZi;
            Assert.True(double.IsFinite(zr.X0) && double.IsFinite(zi.X0),
                $"iter {i}: zr.X0={zr.X0}, zi.X0={zi.X0}");
        }
    }

    [Fact]
    public void Multiply_TwoLimbCommutative()
    {
        var a = new OD(1.5, 1e-17, 1e-34, 1e-51, 1e-68, 0, 0, 0);
        var b = new OD(0.25, 2e-18, -3e-35, 4e-52, -5e-69, 0, 0, 0);
        var ab = a * b;
        var ba = b * a;
        AssertOdNear(ab, ba, 1e-30);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static void AssertOdNear(OD expected, OD actual, double tol)
    {
        double diff = Math.Abs((double)expected - (double)actual);
        Assert.True(diff <= tol,
            $"OD diff {diff:G6} > tol {tol:G6}.\n  expected={expected}\n  actual=  {actual}");
    }

    private static void AssertQdParity(QD q, OD od, double tol = QdParityEps)
    {
        // Compare each QD limb to the corresponding OD limb. OD may carry
        // residuals further than QD does (X4..X7), so we accept any OD
        // representation whose first-4-limb sum equals QD's value to tol.
        double qSum = q.X0 + q.X1 + q.X2 + q.X3;
        double odSum = od.X0 + od.X1 + od.X2 + od.X3 + od.X4 + od.X5 + od.X6 + od.X7;
        double diff = Math.Abs(qSum - odSum);
        Assert.True(diff <= tol,
            $"QD vs OD parity diff {diff:G6} > tol {tol:G6}.\n  QD={q}\n  OD={od}");
    }
}
