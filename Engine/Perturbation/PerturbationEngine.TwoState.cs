// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1912 (epic #1253, D7) — the engine for TwoStatePlan (Phoenix, Spider, User Equation prev): one
// BigFloat reference of both states (stored as doubles), a per-pixel δ for each, the plan's glitch and
// pole tests with secondary references and the direct BigFloat fallback, as Render does for one state.
// No BLA and no rebase yet (TwoStatePlan's header says why).

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
        public double OffX, OffY;
    }

    /// <summary>Render a TwoStatePlan frame; the pixel layout and the sink are <see cref="Render"/>'s.</summary>
    public static PerturbationStats RenderTwoState(
        TwoStatePlan plan, ExactCentre centre, double scale, int width, int height, int maxIter,
        PixelSink sink, CancellationToken ct,
        int[]? rowMaxIter = null, Func<int, bool>? marked = null,
        Func<double, double, (double X, double Y)>? warp = null)
    {
        int prec = centre.Precision;

        TwoStateReference Build(double offX, double offY)
        {
            var cr = BigFloat.Add(centre.Re, BigFloat.FromDouble(offX, prec), prec);
            var ci = BigFloat.Add(centre.Im, BigFloat.FromDouble(offY, prec), prec);
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

        long poleFlags = 0;
        var glitched = new ConcurrentBag<(int Idx, int Iter)>();
        var main = Build(0, 0);
        var po = new ParallelOptions { CancellationToken = ct };
        Parallel.For(0, height, po, y =>
        {
            long pf = 0;
            for (int x = 0; x < width; x++)
            {
                int idx = y * width + x;
                if (marked != null && !marked(idx)) continue;
                var (ox, oy) = Offset(x, y);
                int g = TwoStatePixel(plan, main, ox, oy, RowMax(y), idx, sink, ref pf);
                if (g >= 0) glitched.Add((idx, g));
            }
            Interlocked.Add(ref poleFlags, pf);
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
                long pf = 0;
                var (ox, oy) = Offset(p.Idx % width, p.Idx / width);
                int g = TwoStatePixel(plan, reference, ox, oy, RowMax(p.Idx / width), p.Idx, sink, ref pf);
                Interlocked.Add(ref poleFlags, pf);
                if (g >= 0) next.Add((p.Idx, g));
            });
            pending = new List<(int Idx, int Iter)>(next);
        }

        if (pending.Count > 0)
        {
            Parallel.ForEach(pending, po, p =>
            {
                var (ox, oy) = Offset(p.Idx % width, p.Idx / width);
                TwoStateDirect(plan, centre, prec, ox, oy, RowMax(p.Idx / width), p.Idx, sink, ct);
            });
            direct = pending.Count;
        }
        return new PerturbationStats(main.Len, refs, 0, 0, glitchedCount, direct, Interlocked.Read(ref poleFlags));
    }

    private static int TwoStatePixel(TwoStatePlan plan, TwoStateReference r, double ox, double oy, int maxIter, int idx,
                                     PixelSink sink, ref long poleFlags)
    {
        double er = ox - r.OffX, ei = oy - r.OffY;
        double dzr = 0, dzi = 0, dwr = plan.WStartsAtC ? er : 0, dwi = plan.WStartsAtC ? ei : 0;
        double tzr = 0, tzi = 0, twr = plan.WStartsAtC ? 1 : 0, twi = 0;   // ∂(z, w)/∂Re(c) at n = 0
        double pcr = r.Cr + er, pci = r.Ci + ei, bail = plan.Bailout2;
        bool poles = plan.HasPoles;
        double zr = 0, zi = 0;
        int m = 0, iter = 0;
        for (; iter < maxIter; iter++)
        {
            double Zr = r.Zr[m], Zi = r.Zi[m], Wr = r.Wr[m], Wi = r.Wi[m];
            zr = Zr + dzr; zi = Zi + dzi;
            double wr = Wr + dwr, wi = Wi + dwi;
            if (zr * zr + zi * zi >= bail) break;
            plan.TangentStep(zr, zi, wr, wi, pcr, pci, ref tzr, ref tzi, ref twr, ref twi);
            bool glitch = plan.IsGlitch(zr, zi, wr, wi, Zr, Zi, Wr, Wi, r.Cr, r.Ci, GlitchTolerance2);
            if (m + 1 >= r.Len || glitch)
            {
                // The reference ends here: one step onto its escaped point, kept when it escapes.
                if (m + 1 >= r.Len && r.HasEsc && iter + 1 < maxIter && !glitch)
                {
                    double ar = dzr, ai = dzi, br = dwr, bi = dwi;
                    plan.DeltaStep(Zr, Zi, Wr, Wi, r.Cr, r.Ci, ref ar, ref ai, ref br, ref bi, er, ei);
                    double ezr = r.EscR + ar, ezi = r.EscI + ai;
                    if (ezr * ezr + ezi * ezi >= bail) { sink(idx, iter + 1, ezr, ezi, tzr, tzi); return -1; }
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
        sink(idx, iter, zr, zi, tzr, tzi);
        return -1;
    }

    private static void TwoStateDirect(TwoStatePlan plan, ExactCentre centre, int prec, double ox, double oy, int maxIter,
                                       int idx, PixelSink sink, CancellationToken ct)
    {
        var cr = BigFloat.Add(centre.Re, BigFloat.FromDouble(ox, prec), prec);
        var ci = BigFloat.Add(centre.Im, BigFloat.FromDouble(oy, prec), prec);
        BigFloat zr = BigFloat.Zero(prec), zi = BigFloat.Zero(prec);
        BigFloat wr = plan.WStartsAtC ? cr : BigFloat.Zero(prec), wi = plan.WStartsAtC ? ci : BigFloat.Zero(prec);
        double tzr = 0, tzi = 0, twr = plan.WStartsAtC ? 1 : 0, twi = 0, pcr = cr.ToDouble(), pci = ci.ToDouble();
        double a = 0, b = 0;
        int iter = 0;
        for (; iter < maxIter; iter++)
        {
            if ((iter & 1023) == 0) ct.ThrowIfCancellationRequested();
            a = zr.ToDouble(); b = zi.ToDouble();
            if (a * a + b * b >= plan.Bailout2) break;
            plan.TangentStep(a, b, wr.ToDouble(), wi.ToDouble(), pcr, pci, ref tzr, ref tzi, ref twr, ref twi);
            plan.RefStep(ref zr, ref zi, ref wr, ref wi, cr, ci, prec);
        }
        if (iter >= maxIter) { a = zr.ToDouble(); b = zi.ToDouble(); }
        sink(idx, iter, a, b, tzr, tzi);
    }
}
