// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1870 (epic #1428, C7c) — DeepExternalRay: an angle landed past octuple-double. Targets
// with independent answers:
//   • .(0 1ⁿ⁻¹) lands on the period-n antenna minibrot next to −2 (its nucleus by Newton
//     from −2 in BigFloat, its size by the atom-domain estimate: S1864 / S1866). At n = 125
//     (~1e-149) ExternalRay (OD) fails; the deep trace lands on that nucleus, to 1e-6 of
//     its size. At n = 30, where OD lands, both agree.
//   • Tuning (Douady–Hubbard): replacing each digit of M(4,1)'s angle .001(010) by the
//     antenna's pair (0 → 0 1ⁿ⁻¹, 1 → 1 0ⁿ⁻¹) gives an angle landing on M(4,1)'s copy in
//     the period-n minibrot, at about nucleus + Size·M(4,1). At n = 60 (~1e-70; the OD trace
//     fails there) the landing is checked against that (to 2% of the size), and as a
//     Misiurewicz point in BigFloat (z_{k+p} = z_k, minimal (k, p)).
//   • Angle 0 is refused (the cusp).

using System;
using FracturingFog.Abstractions.Explore;
using FracturingFog.FFMath;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1870DeepExternalRayTests
{
    private const double Log2Of10 = 3.3219280948873622;

    private static (BigFloat C, BigFloat Size) Antenna(int n, int prec)
    {
        var c = BigFloat.FromDouble(-2, prec);
        var one = BigFloat.FromDouble(1, prec);
        for (int step = 0; step < 40; step++)
        {
            var z = BigFloat.Zero(prec); var dz = BigFloat.Zero(prec);
            for (int k = 0; k < n; k++)
            {
                dz = BigFloat.Add(BigFloat.Mul(z, dz, prec).ScaleB(1), one, prec);
                z = BigFloat.Add(BigFloat.Mul(z, z, prec), c, prec);
            }
            c = BigFloat.Sub(c, BigFloat.Div(z, dz, prec), prec);
        }
        var zz = BigFloat.Zero(prec); var l = one; var b = one;
        for (int q = 1; q < n; q++)
        {
            zz = BigFloat.Add(BigFloat.Mul(zz, zz, prec), c, prec);
            l = BigFloat.Mul(zz, l, prec).ScaleB(1);
            b = BigFloat.Add(b, BigFloat.Div(one, l, prec), prec);
        }
        return (c, BigFloat.Div(one, BigFloat.Mul(b, BigFloat.Mul(l, l, prec), prec), prec));
    }

    private static double Log2(BigFloat v) => v.ToFloatExp().Log2Abs();

    private static ExternalAngle AntennaAngle(int n) => ExternalAngle.FromBits("", "0" + new string('1', n - 1));

    [Fact]
    public void A_Periodic_Angle_Lands_On_A_Minibrot_Past_OD()
    {
        const int n = 125;   // ~1e-149
        var angle = AntennaAngle(n);
        Assert.Equal(RayLandingStatus.Failed, ExternalRay.Land(angle).Status);   // past OD

        var landing = DeepExternalRay.Land(angle);
        Assert.Equal(RayLandingStatus.Component, landing.Status);
        Assert.Equal(n, landing.Nucleus.Period);
        int prec = landing.Nucleus.Nucleus.Precision + 64;
        var (c, size) = Antenna(n, prec);
        double err = new CFx(BigFloat.Sub(landing.Nucleus.Nucleus.Re, c, prec).ToFloatExp(), landing.Nucleus.Nucleus.Im.ToFloatExp()).Log2Abs();
        Assert.True(err < Log2(size) - 19.9, $"nucleus off by 2^{err:F1}, size 2^{Log2(size):F1}");
        Assert.True(Math.Abs(landing.Nucleus.SizeLog10 - Log2(size) / Log2Of10) < 1e-6);
    }

    [Fact]
    public void Where_OD_Lands_The_Deep_Trace_Agrees()
    {
        var angle = AntennaAngle(30);
        var od = ExternalRay.Land(angle);
        var deep = DeepExternalRay.Land(angle);
        Assert.Equal(RayLandingStatus.Component, od.Status);
        Assert.Equal(RayLandingStatus.Component, deep.Status);
        Assert.Equal(od.Nucleus.Period, deep.Nucleus.Period);
        int prec = deep.Nucleus.Nucleus.Precision;
        var odn = ExactCentre.FromDeep(od.Nucleus.Nucleus, prec);
        double err = new CFx(BigFloat.Sub(deep.Nucleus.Nucleus.Re, odn.Re, prec).ToFloatExp(), BigFloat.Sub(deep.Nucleus.Nucleus.Im, odn.Im, prec).ToFloatExp()).Log2Abs();
        Assert.True(err < Math.Log2(od.Nucleus.Size.Magnitude) - 19.9, $"deep vs OD 2^{err:F1}");
    }

    [Fact]
    public void A_Tuned_Preperiodic_Angle_Lands_On_The_Copy_Of_Its_Point()
    {
        const int n = 60;   // ~1e-70
        string w0 = "0" + new string('1', n - 1), w1 = "1" + new string('0', n - 1);
        string Tune(string s) { var sb = new System.Text.StringBuilder(); foreach (char ch in s) sb.Append(ch == '0' ? w0 : w1); return sb.ToString(); }
        var angle = ExternalAngle.FromBits(Tune("001"), Tune("010"));   // M(4,1)'s .001(010), tuned

        var landing = DeepExternalRay.Land(angle);
        Assert.Equal(RayLandingStatus.Misiurewicz, landing.Status);
        var m = landing.Misiurewicz;
        Assert.Equal(3 * n + 1, m.Preperiod);
        Assert.Equal(n, m.Period);
        Assert.True(double.IsFinite(landing.ContextRadiusLog10));

        // The copy of M(4,1) in the minibrot: nucleus + Size·M(4,1), to 2% of the size.
        const int prec = 900;
        var (c, size) = Antenna(n, prec);
        var mx = BigFloat.Add(c, BigFloat.Mul(size, BigFloat.FromDouble(-0.10109636384562216, prec), prec), prec);
        var my = BigFloat.Mul(size, BigFloat.FromDouble(0.95628651080914150, prec), prec);
        // The copy may be the conjugate (the angle's mirror lands at conj): compare |Im|.
        double dRe = BigFloat.Sub(m.Point.Re.WithPrecision(prec), mx, prec).ToFloatExp().Log2Abs();
        double dIm = BigFloat.Sub(m.Point.Im.WithPrecision(prec).Abs(), my, prec).ToFloatExp().Log2Abs();
        Assert.True(Math.Max(dRe, dIm) < Log2(size) + Math.Log2(0.02), $"off the copy by 2^{Math.Max(dRe, dIm):F1}, size 2^{Log2(size):F1}");

        // And it is that Misiurewicz point, independently: z_{k+p} = z_k, no smaller k or divisor p.
        int k = m.Preperiod, p = m.Period, len = k + p, pr = m.Point.Precision + 256;
        var zr = new BigFloat[len + 1]; var zi = new BigFloat[len + 1]; var dr = new BigFloat[len + 1]; var di = new BigFloat[len + 1];
        zr[0] = zi[0] = dr[0] = di[0] = BigFloat.Zero(pr);
        var cr = m.Point.Re.WithPrecision(pr); var ci = m.Point.Im.WithPrecision(pr); var one = BigFloat.FromDouble(1, pr);
        for (int j = 0; j < len; j++)
        {
            dr[j + 1] = BigFloat.Add(BigFloat.Sub(BigFloat.Mul(zr[j], dr[j], pr), BigFloat.Mul(zi[j], di[j], pr), pr).ScaleB(1), one, pr);
            di[j + 1] = BigFloat.Add(BigFloat.Mul(zr[j], di[j], pr), BigFloat.Mul(zi[j], dr[j], pr), pr).ScaleB(1);
            zr[j + 1] = BigFloat.Add(BigFloat.Sub(BigFloat.Mul(zr[j], zr[j], pr), BigFloat.Mul(zi[j], zi[j], pr), pr), cr, pr);
            zi[j + 1] = BigFloat.Add(BigFloat.Mul(zr[j], zi[j], pr).ScaleB(1), ci, pr);
        }
        double Step(int a, int b2)
            => new CFx(BigFloat.Sub(zr[a + b2], zr[a], pr).ToFloatExp(), BigFloat.Sub(zi[a + b2], zi[a], pr).ToFloatExp()).Log2Abs()
             - new CFx(BigFloat.Sub(dr[a + b2], dr[a], pr).ToFloatExp(), BigFloat.Sub(di[a + b2], di[a], pr).ToFloatExp()).Log2Abs();
        double floor = Log2(size) - 40;
        Assert.True(Step(k, p) < floor, $"(k, p) step 2^{Step(k, p):F1}");
        Assert.True(Step(k - 1, p) > floor + 10, "preperiod k − 1 also holds");
        foreach (int q in new[] { 1, 2, 3, 4, 5, 6, 10, 12, 15, 20, 30 })
            Assert.True(Step(k, q) > floor + 10, $"period {q} also holds");
    }

    [Fact]
    public void Angle_Zero_Is_Refused()
    {
        var r = DeepExternalRay.Land(ExternalAngle.FromBits("", "0"));
        Assert.Equal(RayLandingStatus.Failed, r.Status);
        Assert.Contains("cusp", r.Detail);
    }
}
