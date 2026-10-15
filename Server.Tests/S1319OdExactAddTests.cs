// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1319 — OD + OD was the "sloppy" pairwise add. Under cancellation (z² + c
// near a minibrot nucleus) it lost mass, and the deep reference orbit drifted
// far past the pixel size (2.4e-33 against 1.4e-68 pixels at zoom 1e66), so
// renders past ~1e60 landed in the wrong place. OD + OD is now an exact
// expansion sum. These tests measure against exact rational arithmetic
// (BigInteger fixed point), not against another OD routine:
//   • a single add, including total and near-total cancellation and operands
//     whose limbs don't line up, is the exact sum to OD precision;
//   • a 3000-step reference orbit at a real deep view (the user's 2.1e65
//     region) stays within 1e-90 of the exact orbit — far below its pixels;
//   • a real period-1808 minibrot framed at zoom 5.5e68 renders as a copy of
//     the main set: its in-set mask matches M mapped through the atom-size map
//     c = nucleus + size·c′ (before the fix: no in-set pixel at all).

using System;
using System.Numerics;
using FracturingFog.Abstractions.Explore;
using FracturingFog.FFMath;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1319OdExactAddTests
{
    // Fixed point with 2^-Frac resolution: every OD limb down to ~1e-300
    // magnitude is represented exactly enough for these checks.
    private const int Frac = 1300;

    private static BigInteger Fix(double d)
    {
        if (d == 0) return BigInteger.Zero;
        long bits = BitConverter.DoubleToInt64Bits(d);
        int exp = (int)((bits >> 52) & 0x7FF);
        long man = bits & 0xFFFFFFFFFFFFFL;
        if (exp == 0) exp = 1; else man |= 1L << 52;
        int sh = exp - 1075 + Frac;
        var v = sh >= 0 ? new BigInteger(man) << sh : new BigInteger(man) >> -sh;
        return d < 0 ? -v : v;
    }

    private static BigInteger Fix(OD a)
        => Fix(a.X0) + Fix(a.X1) + Fix(a.X2) + Fix(a.X3) + Fix(a.X4) + Fix(a.X5) + Fix(a.X6) + Fix(a.X7);

    /// <summary>|v| · 2^-Frac as a double (0 for zero).</summary>
    private static double Mag(BigInteger v)
        => v.IsZero ? 0.0 : Math.Exp(BigInteger.Log(BigInteger.Abs(v)) - Frac * Math.Log(2));

    private static OD RandomOd(Random r, double scale)
    {
        OD a = new(scale * (r.NextDouble() * 2 - 1));
        for (int k = 1; k < 8; k++) a = a + scale * (r.NextDouble() - 0.5) * Math.Pow(2, -53 * k);
        return a;
    }

    [Fact]
    public void Add_Is_The_Exact_Sum_To_Od_Precision_Even_Under_Cancellation()
    {
        var r = new Random(1319);
        for (int i = 0; i < 2000; i++)
        {
            OD a = RandomOd(r, Math.Pow(2, r.Next(-8, 8)));
            OD b = (i % 4) switch
            {
                0 => RandomOd(r, Math.Pow(2, r.Next(-8, 8))),          // unrelated magnitudes
                1 => -a + RandomOd(r, Math.Pow(2, -53 * r.Next(1, 7))), // near-total cancellation
                2 => -a,                                                 // exact cancellation
                _ => new OD(-a.X0) + RandomOd(r, Math.Pow(2, -60)),      // misaligned limbs
            };
            BigInteger exact = Fix(a) + Fix(b);
            BigInteger got = Fix(a + b);
            double err = Mag(got - exact);
            double bound = Math.Max(Mag(exact), 1e-300) * 1e-118;
            Assert.True(err <= bound, $"case {i % 4}: |error| {err:0.0e0} > {bound:0.0e0} (|sum| {Mag(exact):0.0e0})");
            // The result is a valid OD: limbs strictly shrink in magnitude.
            var o = a + b;
            double[] l = { o.X0, o.X1, o.X2, o.X3, o.X4, o.X5, o.X6, o.X7 };
            for (int k = 1; k < 8; k++)
                if (l[k] != 0) Assert.True(Math.Abs(l[k]) < Math.Abs(l[k - 1]), $"limb {k} not below limb {k - 1}");
        }
    }

    [Fact]
    public void Deep_Reference_Orbit_Tracks_The_Exact_Orbit()
    {
        // The user's "Mandelbrot - Minibrot Bug Region" centre (zoom 2.13e65).
        OD cx = new OD(-1.9918151296901943) + -7.82198368112659e-17 + -5.623719612491063e-33 + 2.192582559874045e-49;
        OD cy = new OD(-5.524041575397243e-06) + -2.956716420283969e-22 + -1.5330231405490887e-38 + 8.864291559092897e-55;
        BigInteger bcx = Fix(cx), bcy = Fix(cy), bx = 0, by = 0;
        OD x = OD.Zero, y = OD.Zero;
        double worst = 0;
        int n;
        for (n = 1; n <= 3000; n++)
        {
            // Same operations as MandelbrotCalculator.ComputeReferenceOrbitOD.
            OD ny = (x * y) * 2.0 + cy;
            x = x.Square() - y.Square() + cx;
            y = ny;
            var tbx = ((bx * bx - by * by) >> Frac) + bcx;
            by = ((2 * bx * by) >> Frac) + bcy;
            bx = tbx;
            worst = Math.Max(worst, Math.Max(Mag(Fix(x) - bx), Mag(Fix(y) - by)));
            if (x.X0 * x.X0 + y.X0 * y.X0 > 4) break;
        }
        Assert.True(n > 2000, $"orbit escaped at {n}: not a deep test");
        // Pixels at this zoom are ~1e-67; the sloppy add drifted ~1e-33 here.
        Assert.True(worst < 1e-90, $"reference orbit drifted {worst:0.0e0} from the exact orbit");
    }

    [Fact]
    public void Deep_Minibrot_Renders_As_A_Copy_Of_The_Main_Set()
    {
        // Found by NucleusFinder.FindMinibrot near the built-in "Will this work"
        // region (radius 1e-42): period 1808, |size| 1.83e-69.
        var nucleus = new FracturingFog.FFMath.DeepComplex(
            new OD(-1.9918151296901943, -7.82198368112659E-17, -5.6237669196087764E-33, 2.6876672348091084E-49,
                   8.344815422720153E-68, 5.62021278812849E-84, 3.758605147229201E-100, 3.74170214755604E-117),
            new OD(-5.524041575397243E-06, -2.9567164202839686E-22, -2.2585741366702907E-38, 6.439806134954378E-55,
                   1.8890258519870084E-71, 1.8268317099722925E-87, -9.199248922388955E-104, 5.069913980636591E-120));
        var size = new Complex(1.2484338945256195E-69, 1.3350422957373403E-69);
        var found = new NucleusResult(NucleusStatus.Found, nucleus, 1808, size, 0);
        var (fc, zoom) = NucleusFinder.Frame(found);
        Assert.True(zoom > 1e68, "precondition: an OD-tier view past the old ~1e60 floor");

        const int W = 96, H = 64;
        int it = NucleusFinder.SuggestedIterations(1808);
        var calc = new MandelbrotCalculator(W, H)
        {
            Quality = QualityPreset.Extreme,
            CenterX = fc.Re.X0, CenterXLo = fc.Re.X1, CenterX2 = fc.Re.X2, CenterX3 = fc.Re.X3,
            CenterX4 = fc.Re.X4, CenterX5 = fc.Re.X5, CenterX6 = fc.Re.X6, CenterX7 = fc.Re.X7,
            CenterY = fc.Im.X0, CenterYLo = fc.Im.X1, CenterY2 = fc.Im.X2, CenterY3 = fc.Im.X3,
            CenterY4 = fc.Im.X4, CenterY5 = fc.Im.X5, CenterY6 = fc.Im.X6, CenterY7 = fc.Im.X7,
            Zoom = zoom, MaxIterations = it, ColorMap = new HsvPalette(),
        };
        calc.Calculate(System.Threading.CancellationToken.None);

        double scale = (3.5 / Math.Max(W, H)) / zoom;
        int mismatch = 0, inMini = 0, inRef = 0;
        for (int py = 0; py < H; py++)
            for (int px = 0; px < W; px++)
            {
                bool a = calc.IterationBuffer[py * W + px] >= it;
                var w = new Complex((px - W * 0.5) * scale, (py - H * 0.5) * scale);
                bool b = InMainSet(w / size + NucleusFinder.HomeCenterX);
                if (a) inMini++;
                if (b) inRef++;
                if (a != b) mismatch++;
            }
        Assert.True(inRef > W * H / 10, "precondition: the framed main set fills part of the view");
        Assert.True(inMini > inRef / 2, $"the minibrot is missing: {inMini} in-set pixels vs {inRef} expected");
        Assert.True(mismatch < W * H * 0.03, $"{mismatch} of {W * H} pixels disagree with the main set");
    }

    private static bool InMainSet(Complex c)
    {
        double x = 0, y = 0;
        for (int i = 0; i < 2000; i++)
        {
            double x2 = x * x, y2 = y * y;
            if (x2 + y2 > 4) return false;
            y = 2 * x * y + c.Imaginary;
            x = x2 - y2 + c.Real;
        }
        return true;
    }
}
