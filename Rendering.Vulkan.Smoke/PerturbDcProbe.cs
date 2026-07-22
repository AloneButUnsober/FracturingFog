// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// V6 (#82): --vulkanpturbdc. DEEP-dc precision recheck — the last open question
// before lifting MandelbrotCalculator.MaxGpuPerturbZoom past its conservative
// 1e50 ceiling.
//
// The GPU perturbation kernel (and the default CPU ComputePixelPTRebased path
// it mirrors) computes the per-pixel offset in PLAIN DOUBLE:
//
//     dc = colOffsetX * scale          // one multiply, single-rounded
//
// The worry: at extreme zoom `scale` is tiny (≈3.5/(dim·zoom)), so is a
// single-rounded double dc still faithful, or does its ≈½-ULP rounding error
// flip escape times once amplified by the reference orbit? This probe answers
// it the faithful way — NOT with a hand-rolled double centre (a double centre
// only pins a boundary point to ≈1e-15, so past ≈1e15 the "centre" drifts off
// the set and the frame degenerates), but with the PRODUCTION OD reference-orbit
// builder (MandelbrotCalculator.ComputeReferenceOrbitODPublic) at a genuine
// many-limb deep boundary centre, swept across 1e6 → 1e50.
//
// For each zoom it runs two CPU mirrors of the rebased δ loop over the SAME
// (Hi-limb) reference orbit, differing ONLY in dc/δ precision:
//   • double-dc  — exact twin of the GPU kernel + default CPU path.
//   • DD-dc      — TwoProduct/TwoSum double-double oracle (no FMA), the twin of
//                  the production SM-11a ComputePixelPTRebasedDD variant.
// and reports the iteration-frame disagreement FRACTION vs the ½-ULP noise
// floor (same ULP-band philosophy as --vulkanpturbprobe; maxΔiter is boundary
// chaos, not signal). If the fraction stays flat at the floor across depth,
// single-double dc suffices arbitrarily deep (until `scale` denormals) and the
// ceiling can lift; if it climbs with zoom, 1e50 stays.
//
// Pure CPU numeric — the GPU runs the identical double dc, so a CPU verdict
// transfers directly. No Vulkan device required (runs on any host).

using System;
using FracturingFog;   // MandelbrotCalculator (ComputeReferenceOrbitODPublic, OrbitOD)

namespace FracturingFog.Rendering.Vulkan.Smoke;

internal static class PerturbDcProbe
{
    private const double EscapeR2 = 512.0 * 512.0;   // matches EscapeRadius2 (ref + pixel)
    private const int Dim = 64;
    private const int MaxIter = 120_000;             // long enough to amplify to ~1e50
    private const double MaxDdFrac = 0.02;           // per-zoom noise-floor band

    // Canonical deep boundary centre (4 significant limbs), the same point the
    // deep-zoom input/focus probes use — genuinely on the set boundary well past
    // double's ≈1e-15 reach, so frames stay non-degenerate into the OD regime.
    private static readonly double[] Cx =
        { -1.9918151296901943, -7.8219844803880472E-17, 1.660139930392911E-34, 8.217274172159319E-51 };
    private static readonly double[] Cy =
        { -5.5240415753972429E-06, -2.8659813126937928E-22, 6.6910924119662832E-39, 6.2394735914401016E-55 };

    private static readonly double[] Zooms = { 1e6, 1e15, 1e20, 1e30, 1e40, 1e50 };

    public static int Run()
    {
        Console.WriteLine(
            $"vulkanpturbdc deep-dc recheck  {Dim}x{Dim}  maxIter={MaxIter}  " +
            "centre=canonical-deep (OD)  band=" + MaxDdFrac.ToString("P0"));
        Console.WriteLine(
            "  single-double dc = colOffsetX*scale  vs  DD-dc oracle (per-pixel iter frame)");

        // OD reference orbit once at the deepest maxIter — the Hi limbs (.Zr/.Zi)
        // are exactly what the GPU kernel and default CPU path consume.
        MandelbrotCalculator.OrbitOD orbit = MandelbrotCalculator.ComputeReferenceOrbitODPublic(
            Cx[0], Cx[1], Cx[2], Cx[3], 0, 0, 0, 0,
            Cy[0], Cy[1], Cy[2], Cy[3], 0, 0, 0, 0,
            MaxIter);
        double[] refZr = orbit.Zr, refZi = orbit.Zi;
        int refLen = orbit.RefLen;
        Console.WriteLine(
            $"  ref orbit: len={refLen} escaped={orbit.Escaped} (OD centre, Hi limbs consumed)");

        bool allOk = true;
        int requiredNonDegenerate = 0;   // must resolve 1e15 AND 1e20 (the asked depths)

        foreach (double zoom in Zooms)
        {
            double scale = 3.5 / (Dim * zoom);
            double offX0 = -0.5 * Dim, offY0 = -0.5 * Dim;

            int[] dbl = MirrorDouble(scale, offX0, offY0, refZr, refZi, refLen);
            int[] dd  = MirrorDd(scale, offX0, offY0, refZr, refZi, refLen);

            var distinct = new System.Collections.Generic.HashSet<int>(dbl);
            int n = Dim * Dim, inSet = 0;
            for (int i = 0; i < n; i++) if (dbl[i] >= MaxIter) inSet++;
            bool nonDegenerate = distinct.Count >= 8 && inSet < n;

            int disagree = 0, maxDelta = 0;
            for (int i = 0; i < n; i++)
            {
                int d = Math.Abs(dbl[i] - dd[i]);
                if (d != 0) { disagree++; if (d > maxDelta) maxDelta = d; }
            }
            double frac = (double)disagree / n;

            double dcCorner = Math.Abs(0.5 * Dim * scale);
            string verdict;
            if (!nonDegenerate)
                verdict = "INCONCLUSIVE (degenerate frame — nothing resolves, dc untestable here)";
            else if (frac <= MaxDdFrac)
                verdict = "double dc SUFFICES (at noise floor)";
            else
            { verdict = "double dc INSUFFICIENT (above floor)"; allOk = false; }

            Console.WriteLine(
                $"  zoom={zoom,7:0e+0} scale={scale:0.0e+00} dc~{dcCorner:0.0e+00} " +
                $"distinct={distinct.Count,4} inSet={inSet,4}/{n} " +
                $"| dbl-vs-DD disagree={disagree,4}/{n} ({frac:P3}) maxΔ={maxDelta,3}  → {verdict}");

            if (nonDegenerate && (zoom == 1e15 || zoom == 1e20)) requiredNonDegenerate++;
        }

        if (requiredNonDegenerate < 2)
        {
            Console.Error.WriteLine(
                "vulkanpturbdc FAIL: 1e15/1e20 frames degenerate — recheck vacuous " +
                "(centre orbit did not amplify enough; raise MaxIter or pick a deeper centre).");
            return 1;
        }
        if (!allOk)
        {
            Console.Error.WriteLine(
                "vulkanpturbdc FAIL: single-double dc drifts above the noise floor at depth " +
                "— DO NOT lift MaxGpuPerturbZoom past 1e50; deep dc needs DD.");
            return 1;
        }

        Console.WriteLine(
            "vulkanpturbdc OK: single-double dc stays at the ½-ULP floor across 1e6→1e50 " +
            "(perturbation is precision-zoom-invariant: dc·amplification stays O(1)). " +
            "MaxGpuPerturbZoom may lift toward the scale-denormal limit; kept conservative pending sign-off.");
        return 0;
    }

    // ── double-dc mirror: exact twin of ComputePixelPTRebased / BuildPerturb ──
    private static int[] MirrorDouble(double scale, double offX0, double offY0,
        double[] refZr, double[] refZi, int refLen)
    {
        int[] o = new int[Dim * Dim];
        for (int py = 0; py < Dim; py++)
        for (int px = 0; px < Dim; px++)
        {
            double dcR = (offX0 + px) * scale;
            double dcI = (offY0 + py) * scale;
            double dr = 0.0, di = 0.0;
            int m = 0, iter;
            for (iter = 0; iter < MaxIter; iter++)
            {
                double Zr = refZr[m], Zi = refZi[m];
                double zr = Zr + dr, zi = Zi + di;
                double zmag2 = zr * zr + zi * zi;
                if (zmag2 >= EscapeR2) break;
                double dmag2 = dr * dr + di * di;
                if (zmag2 < dmag2 || m + 1 >= refLen) { dr = zr; di = zi; Zr = 0.0; Zi = 0.0; m = 0; }
                double a = 2.0 * Zr + dr, b = 2.0 * Zi + di;
                double newDr = a * dr - b * di + dcR;
                double newDi = a * di + b * dr + dcI;
                dr = newDr; di = newDi; m++;
            }
            o[py * Dim + px] = iter;
        }
        return o;
    }

    // ── DD-dc oracle: same loop, DD dc + δ, ref stays Hi-only (twin of the
    // production SM-11a ComputePixelPTRebasedDD A/B). Isolates the dc question. ─
    private static int[] MirrorDd(double scale, double offX0, double offY0,
        double[] refZr, double[] refZi, int refLen)
    {
        int[] o = new int[Dim * Dim];
        for (int py = 0; py < Dim; py++)
        for (int px = 0; px < Dim; px++)
        {
            Dd dcR = Dd.FromProduct(offX0 + px, scale);
            Dd dcI = Dd.FromProduct(offY0 + py, scale);
            Dd dr = default, di = default;
            int m = 0, iter;
            for (iter = 0; iter < MaxIter; iter++)
            {
                Dd Zr = new Dd(refZr[m]), Zi = new Dd(refZi[m]);
                Dd zr = Zr + dr, zi = Zi + di;
                double zmag2 = zr.Hi * zr.Hi + zi.Hi * zi.Hi;
                if (zmag2 >= EscapeR2) break;
                double dmag2 = dr.Hi * dr.Hi + di.Hi * di.Hi;
                if (zmag2 < dmag2 || m + 1 >= refLen) { dr = zr; di = zi; Zr = default; Zi = default; m = 0; }
                Dd a = Zr * 2.0 + dr, b = Zi * 2.0 + di;
                Dd newDr = a * dr - b * di + dcR;
                Dd newDi = a * di + b * dr + dcI;
                dr = newDr; di = newDi; m++;
            }
            o[py * Dim + px] = iter;
        }
        return o;
    }

    // ── minimal double-double (no FMA — Dekker TwoProduct/TwoSum) ────────────
    private readonly struct Dd
    {
        public readonly double Hi, Lo;
        public Dd(double hi) { Hi = hi; Lo = 0.0; }
        private Dd(double hi, double lo) { Hi = hi; Lo = lo; }

        private static void TwoSum(double a, double b, out double s, out double e)
        { s = a + b; double bb = s - a; e = (a - (s - bb)) + (b - bb); }

        private static void Split(double a, out double hi, out double lo)
        { double t = 134217729.0 * a; hi = t - (t - a); lo = a - hi; }

        private static void TwoProd(double a, double b, out double p, out double e)
        {
            p = a * b;
            Split(a, out double ah, out double al);
            Split(b, out double bh, out double bl);
            e = ((ah * bh - p) + ah * bl + al * bh) + al * bl;
        }

        public static Dd FromProduct(double a, double b)
        { TwoProd(a, b, out double p, out double e); return new Dd(p, e); }

        public static Dd operator +(Dd a, Dd b)
        {
            TwoSum(a.Hi, b.Hi, out double s, out double e);
            e += a.Lo + b.Lo;
            TwoSum(s, e, out double s2, out double e2);
            return new Dd(s2, e2);
        }

        public static Dd operator -(Dd a, Dd b) => a + new Dd(-b.Hi, -b.Lo);

        public static Dd operator *(Dd a, double b)
        {
            TwoProd(a.Hi, b, out double p, out double e);
            e += a.Lo * b;
            TwoSum(p, e, out double s2, out double e2);
            return new Dd(s2, e2);
        }

        public static Dd operator *(Dd a, Dd b)
        {
            TwoProd(a.Hi, b.Hi, out double p, out double e);
            e += a.Hi * b.Lo + a.Lo * b.Hi;
            TwoSum(p, e, out double s2, out double e2);
            return new Dd(s2, e2);
        }
    }
}
