// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1912 (epic #1253, D7) — the engine for TwoStatePlan (Phoenix, Spider, User Equation prev): one
// BigFloat reference of both states (stored as doubles), a per-pixel δ for each, the plan's glitch and
// pole tests with secondary references and the direct BigFloat fallback, as Render does for one state.
// No BLA and no rebase yet (TwoStatePlan's header says why).
//
// #1957 (after #1921) — past double, as Render: a FloatExp pixel scale puts the offsets in units of
// 2^s0, and while both δs are below 2^FxExitLog2 they are held as d·2^s (one s for both states) and
// stepped by the plan's own DeltaStep on inputs rescaled to ~2^−500 (the scaled-δ prelude: rescaling
// after mathr 2021, bibliography #mathr-deep-zoom-2021). The tangent is held as t·2^k past
// 2^DerivRenormLog2, and the sink gets it times 2^−derivExp2.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FracturingFog.FFMath;

namespace FracturingFog.Perturbation;

public static partial class PerturbationEngine
{
    private sealed class TwoStateReference
    {
        public double[] Zr = Array.Empty<double>(), Zi = Array.Empty<double>(), Wr = Array.Empty<double>(), Wi = Array.Empty<double>();
        public int Len;
        public bool HasEsc;
        public double EscR, EscI;
        public double Cr, Ci;
        public double OffX, OffY;   // in units of 2^S0 (#1957)
    }

    /// <summary>Render a TwoStatePlan frame; the pixel layout and the sink are <see cref="Render"/>'s,
    /// and so are <paramref name="pixelScale"/> / <paramref name="derivExp2"/> (#1957).</summary>
    public static PerturbationStats RenderTwoState(
        TwoStatePlan plan, ExactCentre centre, double scale, int width, int height, int maxIter,
        PixelSink sink, CancellationToken ct,
        int[]? rowMaxIter = null, Func<int, bool>? marked = null,
        Func<double, double, (double X, double Y)>? warp = null,
        FloatExp? pixelScale = null, int derivExp2 = 0, int fxBelowLog2 = FxScaleLog2, bool linearWarp = false)
    {
        int prec = centre.Precision;
        int s0 = 0;
        if (pixelScale is { } ps)
        {
            if (ps.Log2Abs() >= fxBelowLog2) scale = ps.ToDouble();
            else
            {
                if (warp != null && !linearWarp) throw new ArgumentException("A non-linear warp needs a double pixel scale.", nameof(warp));
                s0 = (int)ps.E; scale = ps.M;
            }
        }
        var frame = new Frame(s0, derivExp2);

        TwoStateReference Build(double offX, double offY)
        {
            var cr = BigFloat.Add(centre.Re, BigFloat.FromDouble(offX, prec).ScaleB(s0), prec);
            var ci = BigFloat.Add(centre.Im, BigFloat.FromDouble(offY, prec).ScaleB(s0), prec);
            var r = new TwoStateReference
            {
                Zr = new double[maxIter + 1], Zi = new double[maxIter + 1], Wr = new double[maxIter + 1], Wi = new double[maxIter + 1],
                OffX = offX, OffY = offY, Cr = cr.ToDouble(), Ci = ci.ToDouble(),
            };
            BigFloat zr = BigFloat.Zero(prec), zi = BigFloat.Zero(prec);
            BigFloat wr = plan.WStartsAtC ? cr : BigFloat.Zero(prec), wi = plan.WStartsAtC ? ci : BigFloat.Zero(prec);
            int n = 0;
            for (; n <= maxIter; n++)
            {
                if ((n & 1023) == 0) ct.ThrowIfCancellationRequested();
                double a = zr.ToDouble(), b = zi.ToDouble();
                if (a * a + b * b >= plan.Bailout2) { r.HasEsc = true; r.EscR = a; r.EscI = b; break; }
                r.Zr[n] = a; r.Zi[n] = b; r.Wr[n] = wr.ToDouble(); r.Wi[n] = wi.ToDouble();
                if (n == maxIter) { n++; break; }
                plan.RefStep(ref zr, ref zi, ref wr, ref wi, cr, ci, prec);
            }
            r.Len = n;
            return r;
        }

        (double X, double Y) Offset(int x, int y)
        {
            double ox = (x - width * 0.5) * scale, oy = (y - height * 0.5) * scale;
            return warp != null ? warp(ox, oy) : (ox, oy);
        }

        int RowMax(int y) => rowMaxIter != null && y < rowMaxIter.Length && rowMaxIter[y] > 0 ? rowMaxIter[y] : maxIter;

        long poleFlags = 0, scaledSteps = 0;
        var glitched = new ConcurrentBag<(int Idx, int Iter)>();
        var main = Build(0, 0);
        var po = new ParallelOptions { CancellationToken = ct };
        Parallel.For(0, height, po, y =>
        {
            long pf = 0, sc = 0;
            for (int x = 0; x < width; x++)
            {
                int idx = y * width + x;
                if (marked != null && !marked(idx)) continue;
                var (ox, oy) = Offset(x, y);
                int g = TwoStatePixel(plan, main, frame, ox, oy, RowMax(y), idx, sink, ref pf, ref sc);
                if (g >= 0) glitched.Add((idx, g));
            }
            Interlocked.Add(ref poleFlags, pf);
            Interlocked.Add(ref scaledSteps, sc);
        });

        int glitchedCount = glitched.Count, refs = 1, direct = 0;
        var pending = new List<(int Idx, int Iter)>(glitched);
        while (pending.Count > 0 && refs < MaxReferences)
        {
            ct.ThrowIfCancellationRequested();
            var best = pending[0];
            foreach (var p in pending)
                if (p.Iter > best.Iter || (p.Iter == best.Iter && p.Idx < best.Idx)) best = p;
            var (bx, by) = Offset(best.Idx % width, best.Idx / width);
            var reference = Build(bx, by);
            refs++;
            var next = new ConcurrentBag<(int, int)>();
            Parallel.ForEach(pending, po, p =>
            {
                long pf = 0, sc = 0;
                var (ox, oy) = Offset(p.Idx % width, p.Idx / width);
                int g = TwoStatePixel(plan, reference, frame, ox, oy, RowMax(p.Idx / width), p.Idx, sink, ref pf, ref sc);
                Interlocked.Add(ref poleFlags, pf);
                Interlocked.Add(ref scaledSteps, sc);
                if (g >= 0) next.Add((p.Idx, g));
            });
            pending = new List<(int Idx, int Iter)>(next);
        }

        if (pending.Count > 0)
        {
            Parallel.ForEach(pending, po, p =>
            {
                var (ox, oy) = Offset(p.Idx % width, p.Idx / width);
                TwoStateDirect(plan, centre, prec, frame, ox, oy, RowMax(p.Idx / width), p.Idx, sink, ct);
            });
            direct = pending.Count;
        }
        return new PerturbationStats(main.Len, refs, 0, 0, glitchedCount, direct, Interlocked.Read(ref poleFlags),
                                     Interlocked.Read(ref scaledSteps));
    }

    // #1957 — the tangent (both states) held as t·2^k: rescaled past 2^DerivRenormLog2.
    private static void Renorm4(ref double a, ref double b, ref double c, ref double d, ref int k)
    {
        int l = Math.Max(LogB(a, b), LogB(c, d));
        if (l <= DerivRenormLog2 || l == int.MaxValue) return;
        int s = l - 500;
        a = Math.ScaleB(a, -s); b = Math.ScaleB(b, -s); c = Math.ScaleB(c, -s); d = Math.ScaleB(d, -s);
        k += s;
    }

    private static void TangentAny(TwoStatePlan plan, double zr, double zi, double wr, double wi, double cr, double ci,
                                   ref double tzr, ref double tzi, ref double twr, ref double twi, ref int k)
    {
        if (k == 0) plan.TangentStep(zr, zi, wr, wi, cr, ci, ref tzr, ref tzi, ref twr, ref twi);
        else plan.TangentStepScaled(zr, zi, wr, wi, cr, ci, ref tzr, ref tzi, ref twr, ref twi, Math.ScaleB(1.0, -k));
        Renorm4(ref tzr, ref tzi, ref twr, ref twi, ref k);
    }

    // One step of both scaled δs (d·2^s; ε = e·2^s0): the plan's DeltaStep on δ, ε rescaled to ~2^−500.
    private static void ScaledDelta2(TwoStatePlan plan, double Zr, double Zi, double Wr, double Wi, double cr, double ci,
                                     ref double dzr, ref double dzi, ref double dwr, ref double dwi, double er, double ei, int s0, int s)
    {
        int ld = Math.Max(LogB(dzr, dzi), LogB(dwr, dwi)), le = LogB(er, ei);
        int top = Math.Max(ld, le == int.MinValue ? int.MinValue : le + (s0 - s));
        if (top == int.MinValue) return;
        int q = -500 - top;
        double a = Math.ScaleB(dzr, q), b = Math.ScaleB(dzi, q), c = Math.ScaleB(dwr, q), d = Math.ScaleB(dwi, q);
        plan.DeltaStep(Zr, Zi, Wr, Wi, cr, ci, ref a, ref b, ref c, ref d, Math.ScaleB(er, s0 - s + q), Math.ScaleB(ei, s0 - s + q));
        dzr = Math.ScaleB(a, -q); dzi = Math.ScaleB(b, -q); dwr = Math.ScaleB(c, -q); dwi = Math.ScaleB(d, -q);
    }

    private static int TwoStatePixel(TwoStatePlan plan, TwoStateReference r, in Frame f, double ox, double oy, int maxIter, int idx,
                                     PixelSink sink, ref long poleFlags, ref long scaledSteps)
    {
        double er = ox - r.OffX, ei = oy - r.OffY;   // units of 2^s0
        double dzr = 0, dzi = 0, dwr = plan.WStartsAtC ? er : 0, dwi = plan.WStartsAtC ? ei : 0;
        double tzr = 0, tzi = 0, twr = plan.WStartsAtC ? 1 : 0, twi = 0;   // ∂(z, w)/∂Re(c) at n = 0
        int k = 0;
        double bail = plan.Bailout2;
        bool poles = plan.HasPoles;
        double zr = 0, zi = 0;
        int m = 0, iter = 0;
        int s0 = f.S0;

        if (s0 != 0)
        {
            // #1957 — the scaled-δ prelude: δz, δw = d·2^s while both are below 2^FxExitLog2.
            int s = s0;
            double pcr0 = r.Cr + Math.ScaleB(er, s0), pci0 = r.Ci + Math.ScaleB(ei, s0);
            for (; iter < maxIter; iter++)
            {
                int ld = Math.Max(LogB(dzr, dzi), LogB(dwr, dwi));
                bool nz = dzr != 0 || dzi != 0 || dwr != 0 || dwi != 0;
                if (nz)
                {
                    if (s + ld >= FxExitLog2) break;
                    if (ld > 64 || ld < -64)
                    {
                        dzr = Math.ScaleB(dzr, -ld); dzi = Math.ScaleB(dzi, -ld); dwr = Math.ScaleB(dwr, -ld); dwi = Math.ScaleB(dwi, -ld);
                        s += ld;
                    }
                }
                double Zr = r.Zr[m], Zi = r.Zi[m], Wr = r.Wr[m], Wi = r.Wi[m];
                zr = Zr + Math.ScaleB(dzr, s); zi = Zi + Math.ScaleB(dzi, s);
                double wr = Wr + Math.ScaleB(dwr, s), wi = Wi + Math.ScaleB(dwi, s);
                TangentAny(plan, zr, zi, wr, wi, pcr0, pci0, ref tzr, ref tzi, ref twr, ref twi, ref k);
                if (m + 1 >= r.Len)
                {
                    // The reference ends: one step onto its escaped point, kept when it escapes; else glitched.
                    if (r.HasEsc && iter + 1 < maxIter)
                    {
                        double ar = dzr, ai = dzi, br = dwr, bi = dwi;
                        ScaledDelta2(plan, Zr, Zi, Wr, Wi, r.Cr, r.Ci, ref ar, ref ai, ref br, ref bi, er, ei, s0, s);
                        double ezr = r.EscR + Math.ScaleB(ar, s), ezi = r.EscI + Math.ScaleB(ai, s);
                        if (ezr * ezr + ezi * ezi >= bail)
                        {
                            var (ovr, ovi) = Deliver(tzr, tzi, k, f);
                            sink(idx, iter + 1, ezr, ezi, ovr, ovi);
                            return -1;
                        }
                    }
                    return iter;
                }
                ScaledDelta2(plan, Zr, Zi, Wr, Wi, r.Cr, r.Ci, ref dzr, ref dzi, ref dwr, ref dwi, er, ei, s0, s);
                m++;
                scaledSteps++;
            }
            if (iter >= maxIter)
            {
                int mm = Math.Min(m, r.Len - 1);
                var (vr, vi) = Deliver(tzr, tzi, k, f);
                sink(idx, iter, r.Zr[mm] + Math.ScaleB(dzr, s), r.Zi[mm] + Math.ScaleB(dzi, s), vr, vi);
                return -1;
            }
            dzr = Math.ScaleB(dzr, s); dzi = Math.ScaleB(dzi, s); dwr = Math.ScaleB(dwr, s); dwi = Math.ScaleB(dwi, s);
            er = Math.ScaleB(er, s0); ei = Math.ScaleB(ei, s0);
        }

        double pcr = r.Cr + er, pci = r.Ci + ei;
        for (; iter < maxIter; iter++)
        {
            double Zr = r.Zr[m], Zi = r.Zi[m], Wr = r.Wr[m], Wi = r.Wi[m];
            zr = Zr + dzr; zi = Zi + dzi;
            double wr = Wr + dwr, wi = Wi + dwi;
            if (zr * zr + zi * zi >= bail) break;
            TangentAny(plan, zr, zi, wr, wi, pcr, pci, ref tzr, ref tzi, ref twr, ref twi, ref k);
            bool glitch = plan.IsGlitch(zr, zi, wr, wi, Zr, Zi, Wr, Wi, r.Cr, r.Ci, GlitchTolerance2);
            if (m + 1 >= r.Len || glitch)
            {
                // The reference ends here: one step onto its escaped point, kept when it escapes.
                if (m + 1 >= r.Len && r.HasEsc && iter + 1 < maxIter && !glitch)
                {
                    double ar = dzr, ai = dzi, br = dwr, bi = dwi;
                    plan.DeltaStep(Zr, Zi, Wr, Wi, r.Cr, r.Ci, ref ar, ref ai, ref br, ref bi, er, ei);
                    double ezr = r.EscR + ar, ezi = r.EscI + ai;
                    if (ezr * ezr + ezi * ezi >= bail)
                    {
                        var (ovr, ovi) = Deliver(tzr, tzi, k, f);
                        sink(idx, iter + 1, ezr, ezi, ovr, ovi);
                        return -1;
                    }
                }
                return iter;
            }
            if (poles && plan.PoleRatio2(Zr, Zi, Wr, Wi, r.Cr, r.Ci, dzr, dzi, dwr, dwi, er, ei) < GlitchTolerance2)
            {
                poleFlags++;
                return iter;
            }
            plan.DeltaStep(Zr, Zi, Wr, Wi, r.Cr, r.Ci, ref dzr, ref dzi, ref dwr, ref dwi, er, ei);
            m++;
        }
        if (iter >= maxIter && m < r.Len) { zr = r.Zr[m] + dzr; zi = r.Zi[m] + dzi; }
        var (dvr, dvi) = Deliver(tzr, tzi, k, f);
        sink(idx, iter, zr, zi, dvr, dvi);
        return -1;
    }

    private static void TwoStateDirect(TwoStatePlan plan, ExactCentre centre, int prec, in Frame f, double ox, double oy, int maxIter,
                                       int idx, PixelSink sink, CancellationToken ct)
    {
        var cr = BigFloat.Add(centre.Re, BigFloat.FromDouble(ox, prec).ScaleB(f.S0), prec);
        var ci = BigFloat.Add(centre.Im, BigFloat.FromDouble(oy, prec).ScaleB(f.S0), prec);
        BigFloat zr = BigFloat.Zero(prec), zi = BigFloat.Zero(prec);
        BigFloat wr = plan.WStartsAtC ? cr : BigFloat.Zero(prec), wi = plan.WStartsAtC ? ci : BigFloat.Zero(prec);
        double tzr = 0, tzi = 0, twr = plan.WStartsAtC ? 1 : 0, twi = 0, pcr = cr.ToDouble(), pci = ci.ToDouble();
        int k = 0;
        double a = 0, b = 0;
        int iter = 0;
        for (; iter < maxIter; iter++)
        {
            if ((iter & 1023) == 0) ct.ThrowIfCancellationRequested();
            a = zr.ToDouble(); b = zi.ToDouble();
            if (a * a + b * b >= plan.Bailout2) break;
            TangentAny(plan, a, b, wr.ToDouble(), wi.ToDouble(), pcr, pci, ref tzr, ref tzi, ref twr, ref twi, ref k);
            plan.RefStep(ref zr, ref zi, ref wr, ref wi, cr, ci, prec);
        }
        if (iter >= maxIter) { a = zr.ToDouble(); b = zi.ToDouble(); }
        var (vr, vi) = Deliver(tzr, tzi, k, f);
        sink(idx, iter, a, b, vr, vi);
    }
}
