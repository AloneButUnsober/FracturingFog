// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1866 (epic #1428, C7a) — DeepNucleusFinder: period detection and zoom to minibrot past
// octuple-double. The targets are the real-axis antenna minibrots next to c = −2 (S1864):
// their nucleus (Newton from −2 in BigFloat) and size (the atom-domain estimate in
// BigFloat) are computed here independently of the finder. Checked:
//   • In a view around the period-n minibrot (offset from it, radius a few sizes) at
//     ~1e149 / ~1e299 / ~1e498, the ball scan's candidate is ≤ n, and FindMinibrot returns
//     period n with the independent nucleus (to 1e-6 of its size) and size (to 1e-6).
//   • Frame puts the view centre at nucleus − Size/2 and the log zoom at −log10 Size.
//   • On a view OD still handles (n = 60, ~1e-70) it agrees with NucleusFinder.
//   • Refusals: Newton for period 2n from the nucleus reports LowerPeriod (n); a view
//     disk that doesn't hold the nucleus reports LeftView; a disk outside the set escapes.

using System;
using FracturingFog.Abstractions.Explore;
using FracturingFog.FFMath;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1866DeepNucleusFinderTests
{
    private static BigFloat Nucleus(int n, int prec)
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
        return c;
    }

    private static BigFloat Size(BigFloat c, int n, int prec)
    {
        var z = BigFloat.Zero(prec); var l = BigFloat.FromDouble(1, prec); var b = BigFloat.FromDouble(1, prec);
        var one = BigFloat.FromDouble(1, prec);
        for (int q = 1; q < n; q++)
        {
            z = BigFloat.Add(BigFloat.Mul(z, z, prec), c, prec);
            l = BigFloat.Mul(z, l, prec).ScaleB(1);
            b = BigFloat.Add(b, BigFloat.Div(one, l, prec), prec);
        }
        return BigFloat.Div(one, BigFloat.Mul(b, BigFloat.Mul(l, l, prec), prec), prec);
    }

    private static double Log2(BigFloat v) => v.ToFloatExp().Log2Abs();

    private static (BigFloat c, BigFloat size, int prec) Target(int n)
    {
        int prec = BigFloat.PrecisionForZoom(1.25 * n + 20) + 64;
        var c = Nucleus(n, prec);
        return (c, Size(c, n, prec), prec);
    }

    [Theory]
    [InlineData(125)]   // ~1e149
    [InlineData(250)]   // ~1e299
    [InlineData(415)]   // ~1e498, past double
    public void It_Finds_A_Deep_Antenna_Minibrot(int n)
    {
        var (c, size, prec) = Target(n);
        // The view: centred 3 sizes off the nucleus (up and right), radius 20 sizes.
        var offRe = BigFloat.Mul(size, BigFloat.FromDouble(3, prec), prec);
        var offIm = BigFloat.Mul(size, BigFloat.FromDouble(2, prec), prec);
        var view = new ExactCentre(BigFloat.Add(c, offRe, prec), offIm);
        double radiusLog10 = (Log2(size) + Math.Log2(20)) / 3.3219280948873622;

        var d = DeepNucleusFinder.Detect(view, radiusLog10);
        Assert.Equal(PeriodStop.Found, d.Stop);
        Assert.InRange(d.Period, 1, n);

        var found = DeepNucleusFinder.FindMinibrot(view, radiusLog10);
        Assert.True(found.Found, $"{found.Status} p={found.Period}");
        Assert.Equal(n, found.Period);

        // The nucleus, to a millionth of the minibrot's size.
        double errLog2 = new CFx(BigFloat.Sub(found.Nucleus.Re, c, prec).ToFloatExp(), BigFloat.Sub(found.Nucleus.Im, BigFloat.Zero(prec), prec).ToFloatExp()).Log2Abs();
        Assert.True(errLog2 < Log2(size) - 19.9, $"nucleus off by 2^{errLog2:F1}, size 2^{Log2(size):F1}");
        // The size, to 1e-6 relative; real and positive (no rotation).
        var sizeFx = size.ToFloatExp();
        double relLog2 = (found.Size - new CFx(sizeFx, FloatExp.Zero)).Log2Abs() - sizeFx.Log2Abs();
        Assert.True(relLog2 < -19.9, $"size relative error 2^{relLog2:F1}");

        // Framing: centre nucleus − Size/2, zoom 1/|Size|.
        var (centre, zl) = DeepNucleusFinder.Frame(found);
        double fErr = new CFx(BigFloat.Sub(centre.Re, BigFloat.Sub(c, size.ScaleB(-1), prec), prec).ToFloatExp(), centre.Im.ToFloatExp()).Log2Abs();
        Assert.True(fErr < Log2(size) - 19.9, $"frame centre off by 2^{fErr:F1}");
        Assert.Equal(-Log2(size) / 3.3219280948873622, zl, 6);
    }

    [Fact]
    public void It_Agrees_With_The_OD_Finder_Where_OD_Holds()
    {
        const int n = 60;   // ~1e-70
        var (c, size, prec) = Target(n);
        var view = new ExactCentre(BigFloat.Add(c, BigFloat.Mul(size, BigFloat.FromDouble(3, prec), prec), prec), BigFloat.Zero(prec));
        double radiusLog10 = (Log2(size) + Math.Log2(20)) / 3.3219280948873622;

        var deep = DeepNucleusFinder.FindMinibrot(view, radiusLog10);
        var od = NucleusFinder.FindMinibrot(view.ToDeep(), Math.Pow(10, radiusLog10));
        Assert.True(deep.Found && od.Found, $"{deep.Status} / {od.Status}");
        Assert.Equal(od.Period, deep.Period);
        var odNucleus = ExactCentre.FromDeep(od.Nucleus, prec);
        double err = new CFx(BigFloat.Sub(deep.Nucleus.Re, odNucleus.Re, prec).ToFloatExp(), BigFloat.Sub(deep.Nucleus.Im, odNucleus.Im, prec).ToFloatExp()).Log2Abs();
        Assert.True(err < Log2(size) - 19.9, $"deep vs OD nucleus 2^{err:F1}");
        Assert.Equal(od.Size.Magnitude, deep.Size.Magnitude().ToDouble(), od.Size.Magnitude * 1e-9);
    }

    [Fact]
    public void It_Refuses_Lower_Periods_Roots_Outside_The_View_And_Escaping_Disks()
    {
        const int n = 250;
        var (c, size, prec) = Target(n);
        var at = new ExactCentre(c, BigFloat.Zero(prec));
        double sizeLog10 = Log2(size) / 3.3219280948873622;

        // Newton on f^(2n) from the period-n nucleus converges there: a lower period.
        var twice = DeepNucleusFinder.Find(at, 2 * n, sizeLog10 + 1);
        Assert.Equal(NucleusStatus.LowerPeriod, twice.Status);
        Assert.Equal(n, twice.ActualPeriod);

        // The same nucleus, but the view disk is 100 sizes away and 10 sizes wide.
        var away = new ExactCentre(BigFloat.Add(c, BigFloat.Mul(size, BigFloat.FromDouble(100, prec), prec), prec), BigFloat.Zero(prec));
        var left = DeepNucleusFinder.Find(away, n, sizeLog10 + 1);
        Assert.Equal(NucleusStatus.LeftView, left.Status);

        // Converging 3 sizes from the seed with a disk 1 size wide: within Newton's wander
        // limit (8 radii), but the root is outside the view.
        var near = new ExactCentre(BigFloat.Add(c, BigFloat.Mul(size, BigFloat.FromDouble(3, prec), prec), prec), BigFloat.Zero(prec));
        var outsideDisk = DeepNucleusFinder.Find(near, n, sizeLog10);
        Assert.Equal(NucleusStatus.LeftView, outsideDisk.Status);
        Assert.True(DeepNucleusFinder.Find(near, n, sizeLog10 + 1).Found);   // a 10-size disk holds it

        // A disk outside the set (c = −2.1) escapes.
        var outside = new ExactCentre(BigFloat.FromDouble(-2.1, prec), BigFloat.Zero(prec));
        Assert.Equal(PeriodStop.Escaped, DeepNucleusFinder.Detect(outside, -200).Stop);
    }

    // The Misiurewicz point near -0.1011 + 0.9563i (S1860), 340 digits: dense and complex.
    private const string Mx = "-0.1010963638456221610257854457386225654638054428262534838769311776607808407404705842748212198105167790334045319085567411939715461442609118823557039076803016157584181434354544823068106664029419378267411891607819526448995396995334466823393126699009817557150425206538866991861344333961600610562775385144259609560703568221643279956133036926714760";
    private const string My = "0.9562865108091415007710960577299774358098333365105291700343143215005246590657167325269784107873398072043444724926469284366752406567465722656200815719741551313831228054884443810571677870203738395055477279309548311953190385170024877917670113105649462054553485859930615004427397447762045980085973915977603864276670744042253335354323566859404712";

    [Fact]
    public void It_Finds_A_Complex_Minibrot_In_A_View_Past_OD()
    {
        // A 64x64 view at 1e150 on the Misiurewicz point: minibrots pile up next to it.
        Assert.True(ExactCentre.TryParse(Mx, My, out var view));
        double radiusLog10 = DeepNucleusFinder.ViewDiskRadiusLog10(150, 64, 64);
        var found = DeepNucleusFinder.FindMinibrot(view, radiusLog10);
        Assert.True(found.Found, $"{found.Status} p={found.Period}");

        // Independently, in BigFloat at a higher precision: the nucleus is a root of f^p
        // (the Newton step there is under a millionth of the size), it is in the view, its
        // period is minimal (no proper divisor's step is that small), and the size matches
        // the atom-domain estimate computed here in BigFloat.
        int p = found.Period;
        int prec = found.Nucleus.Precision + 128;
        var cx = found.Nucleus.Re.WithPrecision(prec); var cy = found.Nucleus.Im.WithPrecision(prec);
        double sizeLog2 = found.Size.Log2Abs();
        Assert.True(sizeLog2 < radiusLog10 * 3.3219280948873622, "a minibrot smaller than the view");
        BigFloat zx = BigFloat.Zero(prec), zy = BigFloat.Zero(prec), dx = BigFloat.Zero(prec), dy = BigFloat.Zero(prec);
        BigFloat lx = BigFloat.FromDouble(1, prec), ly = BigFloat.Zero(prec), bx = BigFloat.FromDouble(1, prec), by = BigFloat.Zero(prec);
        var one = BigFloat.FromDouble(1, prec);
        for (int k = 1; k <= p; k++)
        {
            // dz' = 2 z dz + 1; z' = z² + c
            var ndx = BigFloat.Add(BigFloat.Sub(BigFloat.Mul(zx, dx, prec), BigFloat.Mul(zy, dy, prec), prec).ScaleB(1), one, prec);
            var ndy = BigFloat.Add(BigFloat.Mul(zx, dy, prec), BigFloat.Mul(zy, dx, prec), prec).ScaleB(1);
            var nzx = BigFloat.Add(BigFloat.Sub(BigFloat.Mul(zx, zx, prec), BigFloat.Mul(zy, zy, prec), prec), cx, prec);
            var nzy = BigFloat.Add(BigFloat.Mul(zx, zy, prec).ScaleB(1), cy, prec);
            (zx, zy, dx, dy) = (nzx, nzy, ndx, ndy);
            double stepLog2 = Mag(zx, zy) - Mag(dx, dy);
            if (k < p && p % k == 0)
                Assert.True(stepLog2 > sizeLog2 - 19.9, $"period {k} divides {p} and is a root too");
            if (k == p)
                Assert.True(stepLog2 < sizeLog2 - 19.9, $"f^p step 2^{stepLog2:F1} vs size 2^{sizeLog2:F1}");
            if (k < p)
            {
                // l = 2 z l; b += 1/l
                var nlx = BigFloat.Sub(BigFloat.Mul(zx, lx, prec), BigFloat.Mul(zy, ly, prec), prec).ScaleB(1);
                var nly = BigFloat.Add(BigFloat.Mul(zx, ly, prec), BigFloat.Mul(zy, lx, prec), prec).ScaleB(1);
                (lx, ly) = (nlx, nly);
                var n2 = BigFloat.Add(BigFloat.Mul(lx, lx, prec), BigFloat.Mul(ly, ly, prec), prec);
                bx = BigFloat.Add(bx, BigFloat.Div(lx, n2, prec), prec);
                by = BigFloat.Sub(by, BigFloat.Div(ly, n2, prec), prec);
            }
        }
        // Size = 1 / (b l²)
        var l2x = BigFloat.Sub(BigFloat.Mul(lx, lx, prec), BigFloat.Mul(ly, ly, prec), prec);
        var l2y = BigFloat.Mul(lx, ly, prec).ScaleB(1);
        var qx = BigFloat.Sub(BigFloat.Mul(bx, l2x, prec), BigFloat.Mul(by, l2y, prec), prec);
        var qy = BigFloat.Add(BigFloat.Mul(bx, l2y, prec), BigFloat.Mul(by, l2x, prec), prec);
        var qn = BigFloat.Add(BigFloat.Mul(qx, qx, prec), BigFloat.Mul(qy, qy, prec), prec);
        var sx = BigFloat.Div(qx, qn, prec); var sy = -BigFloat.Div(qy, qn, prec);
        double rel = (found.Size - new CFx(sx.ToFloatExp(), sy.ToFloatExp())).Log2Abs() - sizeLog2;
        Assert.True(rel < -19.9, $"size relative error 2^{rel:F1}");
        Assert.True(Math.Abs(sy.ToFloatExp().Log2Abs() - sizeLog2) < 60, "a complex (rotated) size");   // not on the real axis

        // In the view.
        double off = new CFx(BigFloat.Sub(cx, view.Re.WithPrecision(prec), prec).ToFloatExp(), BigFloat.Sub(cy, view.Im.WithPrecision(prec), prec).ToFloatExp()).Log2Abs();
        Assert.True(off <= radiusLog10 * 3.3219280948873622, "nucleus outside the view");
    }

    [Fact]
    public void The_Jump_Plan_Carries_The_Exact_Centre_Log_Zoom_And_Unbounded_Tier()
    {
        const int n = 415;   // ~1e498, past double
        var (c, size, prec) = Target(n);
        var view = new ExactCentre(BigFloat.Add(c, BigFloat.Mul(size, BigFloat.FromDouble(3, prec), prec), prec), BigFloat.Zero(prec));
        double radiusLog10 = (Log2(size) + Math.Log2(20)) / 3.3219280948873622;
        var found = DeepNucleusFinder.FindMinibrot(view, radiusLog10);
        Assert.True(found.Found);

        var plan = MinibrotJump.PlanDeep(found, FracturingFog.Models.QualityPreset.Standard, iterLocked: false, lockedIterations: 0);
        var (centre, zl) = DeepNucleusFinder.Frame(found);
        Assert.Same(FracturingFog.Models.QualityPreset.Unbounded, plan.Quality);
        Assert.Equal(centre, plan.CenterExact);
        Assert.Equal(centre.ToDeep(), plan.Center);
        Assert.Equal(zl, plan.ZoomLog10);
        Assert.Equal(double.MaxValue, plan.Zoom);
        // Iterations: the tier's formula at the log zoom, at least period × 100.
        Assert.Equal(Math.Max(FracturingFog.Models.QualityPreset.Unbounded.ComputeIterationsLog10(zl), n * 100), plan.PreferredIterations);

        var locked = MinibrotJump.PlanDeep(found, FracturingFog.Models.QualityPreset.Standard, iterLocked: true, lockedIterations: 1000);
        Assert.Equal(0, locked.PreferredIterations);
        Assert.True(locked.IterationsShort);
    }

    [Fact]
    public void An_OD_Nucleus_Is_Carried_On_Past_OD()
    {
        // The handoff from a shallower view: NucleusFinder's OD nucleus as the seed, Newton
        // in BigFloat to the minibrot's own depth (OD holds this nucleus to ~1e-262, many sizes off).
        const int n = 250;   // ~1e-299
        var (c, size, prec) = Target(n);
        var seed = ExactCentre.FromDeep(new ExactCentre(c, BigFloat.Zero(prec)).ToDeep(), 1100);
        double sizeLog10 = Log2(size) / 3.3219280948873622;
        var found = DeepNucleusFinder.Find(seed, n, -120);   // the disk OD can tell apart
        Assert.True(found.Found, found.Status.ToString());
        double errLog2 = new CFx(BigFloat.Sub(found.Nucleus.Re, c, prec).ToFloatExp(), found.Nucleus.Im.ToFloatExp()).Log2Abs();
        Assert.True(errLog2 < Log2(size) - 19.9, $"nucleus off by 2^{errLog2:F1}, size 2^{Log2(size):F1}");
        Assert.True(found.SizeLog10 < sizeLog10 + 0.01 && found.SizeLog10 > sizeLog10 - 0.01);
    }

    [Fact]
    public void A_Minibrot_Found_At_1e100_And_Framed_Past_It_Is_Carried_On()
    {
        // The user's saved region "Mandelbrot - 1E100 Test" (zoom exactly 1e100, so the OD
        // finder runs): it finds a period-57,563 minibrot whose framing lies past 1e100, so
        // MinibrotJump.Plan refuses it ("beyond the deepest zoom"). Smoke test, 2026-11-17:
        // the hand-off searched a disk of 10 minibrot sizes (size ~1.6e-200) around the OD
        // nucleus, which OD only places to ~1e-125, so Newton left that disk and the jump
        // fell back to the refusal.
        var centre = DeepComplex.FromLimbs(
            0.34370735950670833, 2.282346286510163e-17, 7.932326910602696e-34, 5.440277507410099e-50,
            2.7985445248229647e-66, -1.756305901847006e-82, -1.17803752676779e-99, -1.9164857908757306e-116,
            -0.050349769895588446, -2.7181764697723013e-18, 1.9007460651095413e-34, -1.9862816982871027e-51,
            1.3274836757917591e-67, -7.854059219129196e-84, -2.5601909822564924e-100, 1.5354184230474017e-116);
        double radius = PeriodDetector.ViewDiskRadius(1e100, 1920, 1080);
        var od = NucleusFinder.FindMinibrot(centre, radius);
        Assert.True(od.Found, od.Status.ToString());
        Assert.Null(MinibrotJump.Plan(od, FracturingFog.Models.QualityPreset.Extreme, false, 0));

        var deep = DeepNucleusFinder.CarryOn(od, centre, radius);
        Assert.True(deep.Found, $"{deep.Status} p={deep.Period}");
        Assert.Equal(od.Period, deep.Period);
        var (_, zl) = DeepNucleusFinder.Frame(deep);
        Assert.True(zl > 100, $"framed at 1e{zl:F1}");
        var plan = MinibrotJump.PlanDeep(deep, FracturingFog.Models.QualityPreset.Extreme, false, 0);
        Assert.Same(FracturingFog.Models.QualityPreset.Unbounded, plan.Quality);

        // The carried nucleus is where OD put it, to OD's grain (|c| · 2^-400 or so).
        var odExact = ExactCentre.FromDeep(od.Nucleus, deep.Nucleus.Precision);
        double moved = new CFx(BigFloat.Sub(deep.Nucleus.Re, odExact.Re, deep.Nucleus.Precision).ToFloatExp(),
                               BigFloat.Sub(deep.Nucleus.Im, odExact.Im, deep.Nucleus.Precision).ToFloatExp()).Log2Abs();
        Assert.True(moved < -380, $"moved 2^{moved:F1} from the OD nucleus");
    }

    private static double Mag(BigFloat x, BigFloat y) => new CFx(x.ToFloatExp(), y.ToFloatExp()).Log2Abs();
}
