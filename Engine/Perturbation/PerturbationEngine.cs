// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1907 (epic #1253, D2) — the generic perturbation engine, double-δ tier. One reference orbit in
// BigFloat at the view's precision (stored as doubles: |Z| stays below the bailout), a per-pixel δ
// in double, the plan's bilinear approximation, and the glitch handling the plan allows:
//   • parameter plane (critical point z₀ = 0): Zhuoran's rebase — when |Z_m + δ| < |δ| or the
//     reference runs out, δ ← Z_m + δ and m ← 0;
//   • otherwise (Julia: z₀ is the pixel; or a formula whose critical point is not z₀ = 0, #1908):
//     the plan's glitch test (Pauldelbrot's |Z_m + δ|² < τ·|Z_m|², generalised to |f′(z)|² <
//     τ·|f′(Z)|²) and the reference running out mark the pixel glitched; up to MaxReferences secondary
//     references, each at the glitched pixel that lasted longest, re-render the glitched pixels; any
//     left are iterated directly in BigFloat.
// Sources (Docs/Resources-Bibliography.md): K. I. Martin (perturbation), Pauldelbrot (glitch test),
// Zhuoran (rebasing, BLA). Design: Docs/Technical/Deep-Zoom-All-Families-DesignPlan.md §3, §4.1.
//
// #1921 (D2b) — past double:
//   • the pixel scale may be given as a FloatExp (a log zoom past ~1e290). Pixel offsets are then
//     held as doubles in units of 2^s0 (s0 the scale's exponent), and while a pixel's δ is below
//     2^FxExitLog2 it is held as d·2^s (the "scaled-δ prelude"): the BLA applies to the scaled d
//     (A·d + B·e·2^(s0−s)), and a single step evaluates the plan's own DeltaStep on δ, ε rescaled to
//     ~2^−500 and scales the result back. That is exact to ~2^−500 relative wherever the step is
//     positively homogeneous of degree 1 at small δ (differentiable, or an abs fold); the dropped
//     second-order terms are ~2^−500 of the kept ones. Once |δ| ≥ 2^FxExitLog2 the double loop
//     takes over (ε, below 2^−1000 there, then contributes under 2^−100 of δ and may underflow).
//   • the derivative is held as d·2^t once |d| passes 2^DerivRenormLog2 (t = 0 until then, so a
//     shallower frame is unchanged bit for bit); the sink gets it times 2^−derivExp2 (the caller's
//     distance unit, MandelbrotCalculator's #1783 / #1843 convention), finite at any depth.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FracturingFog.FFMath;

namespace FracturingFog.Perturbation;

/// <summary>What one frame did (diagnostics, tests).</summary>
public readonly record struct PerturbationStats(
    int ReferenceLength, int References, long Rebases, long BlaSkips, int GlitchedPixels, int DirectPixels,
    long PoleGlitches = 0,   // #1911 — pixel steps flagged by the quotient's pole test
    long ScaledSteps = 0);   // #1921 — pixel steps (single or skipped) taken in the scaled-δ prelude

public static partial class PerturbationEngine
{
    /// <summary>Pauldelbrot's glitch tolerance τ on |z|² / |Z|² (dynamic plane).</summary>
    public const double GlitchTolerance2 = 1e-6;

    /// <summary>The BLA tolerance ε the engine uses by default. Every pixel here resumes after a rebase
    /// or a glitch with BLA, the case #1613 measured for Mandelbrot's rows: at the default 1e-6 a
    /// period-448 minibrot was ~2.5 % off no-BLA rebasing, at 1e-10 ~0.2 % (MandelbrotCalculator.
    /// QdBandBlaTolerance). Here too: at 1e-6 z² + c at p448 1e30 was 2.8 % off MandelbrotCalculator and
    /// Julia's derivative off on ~10 % of pixels; with BLA off both were exact.</summary>
    public const double DefaultBlaEpsilon = 1e-10;

    /// <summary>Secondary references tried before the remaining glitched pixels are iterated directly.</summary>
    public const int MaxReferences = 8;

    /// <summary>#1921 — a pixel scale below 2^this (zoom past ~1e286) renders with the scaled-δ prelude.</summary>
    public const int FxScaleLog2 = -950;

    /// <summary>#1921 — the scaled-δ prelude hands a pixel to the double loop once |δ| ≥ 2^this.</summary>
    public const int FxExitLog2 = -900;

    /// <summary>#1921 — the derivative is rescaled (d·2^t) once |d| passes 2^this.</summary>
    public const int DerivRenormLog2 = 600;

    /// <summary>Receives one finished pixel: index, iterations, final z, final derivative (times
    /// 2^−derivExp2 when <see cref="Render"/> was given one).</summary>
    public delegate void PixelSink(int idx, int iter, double zr, double zi, double dvr, double dvi);

    /// <summary>#1911 — receives a pixel that converged: index, the iteration, z there, and |z − target|
    /// one step before (NaN when a BLA skip came just before; the caller falls back to the count).</summary>
    public delegate void ConvergedSink(int idx, int iter, double zr, double zi, double dPrev);

    /// <summary>#1911 — a second stop: |z − (Re, Im)| ≤ Eps (Magnet's attracting fixed point z = 1).
    /// Real plans only. The BLA does not skip where the reference is within 4·Eps of the target, so a
    /// pixel cannot step over its own convergence there.</summary>
    public sealed record Convergence(double Re, double Im, double Eps, ConvergedSink Sink)
    {
        /// <summary>#1959 — the step stop instead (Nova's): |z_{n+1} − z_n|² &lt; Eps² stops at n, with
        /// z_{n+1}; the target (Re, Im) is unused. The BLA does not skip where the reference's own step is
        /// under <see cref="StepBlaGuard"/>.</summary>
        public StepConvergedSink? StepSink { get; init; }

        /// <summary>#1959 — a step stop at |step|² &lt; <paramref name="eps2"/>.</summary>
        public static Convergence OnStep(double eps2, StepConvergedSink sink)
            => new(0, 0, Math.Sqrt(eps2), static (_, _, _, _, _) => { }) { StepSink = sink };
    }

    /// <summary>#1959 — receives a pixel whose step converged at iteration n: z_{n+1}, the step² one before
    /// (NaN when unknown: a BLA skip came just before, or n = 0) and this step².</summary>
    public delegate void StepConvergedSink(int idx, int iter, double zr, double zi, double prevStep2, double step2);

    /// <summary>#1959 — no BLA skip where the reference's step is shorter than this: a pixel converging
    /// there could take its own sub-tolerance step inside the skip.</summary>
    public const double StepBlaGuard = 1e-2;

    private sealed class Reference
    {
        public double[] Zr = Array.Empty<double>(), Zi = Array.Empty<double>();
        public int Len;           // valid Z_0 .. Z_{Len−1}
        public bool HasEsc;       // the reference escaped: EscR / EscI is Z_{Len} (outside the bailout)
        public double EscR, EscI;
        public double Cr, Ci;     // the reference's c, rounded to double (a coefficient in the δ step)
        public BlaTable? Bla;
        public Bla2Table? Bla2;   // #1909 — a real (non-conformal) plan's matrix BLA
        public double OffX, OffY; // the reference's offset from the view centre, in units of 2^S0
    }

    // #1921 — per-frame constants a pixel needs: the offset unit's exponent (0: complex units) and the
    // sink's derivative unit.
    private readonly record struct Frame(int S0, int DerivExp2);

    /// <summary>Render a frame. Pixel (x, y) sits at the centre + ((x − w/2)·scale, (y − h/2)·scale)
    /// (EscapeTimeCalculator's convention), moved by <paramref name="warp"/> when given.
    /// #1921: <paramref name="pixelScale"/>, when given, is the scale (any depth; <paramref name="scale"/>
    /// is then ignored), and the sink gets the derivative times 2^−<paramref name="derivExp2"/>.</summary>
    public static PerturbationStats Render(
        IPerturbationPlan plan, ExactCentre centre, double scale, int width, int height, int maxIter,
        PixelSink sink, CancellationToken ct,
        int[]? rowMaxIter = null, Func<int, bool>? marked = null,
        bool useBla = true, double blaEpsilon = DefaultBlaEpsilon,
        Func<double, double, (double X, double Y)>? warp = null,
        Convergence? converge = null,
        FloatExp? pixelScale = null, int derivExp2 = 0, int fxBelowLog2 = FxScaleLog2, bool linearWarp = false)
    {
        int prec = centre.Precision;
        if (converge != null && plan is not IRealPerturbationPlan)
            throw new ArgumentException("Convergence needs a real (R2) plan.", nameof(converge));
        // #1921 — the offset unit: complex units while the scale is a comfortable double, else 2^s0.
        int s0 = 0;
        if (pixelScale is { } ps)
        {
            if (ps.Log2Abs() >= fxBelowLog2) scale = ps.ToDouble();   // fxBelowLog2: a test hook (the prelude at a double depth)
            else
            {
                // #1957 — a linear warp (a rotation) acts on the 2^s0-unit offsets as on complex ones.
                if (warp != null && !linearWarp) throw new ArgumentException("A non-linear warp needs a double pixel scale.", nameof(warp));
                s0 = (int)ps.E; scale = ps.M;
            }
        }
        var frame = new Frame(s0, derivExp2);
        var (jcr, jci) = plan is PowerPlan pp && !plan.ParameterPlane ? pp.JuliaC(prec) : (BigFloat.Zero(prec), BigFloat.Zero(prec));
        double dcMax = Math.ScaleB(Math.Sqrt(width * width + height * height) * 0.5 * scale, s0);   // 0 past double: see the header

        Reference Build(double offX, double offY)
        {
            var cr = BigFloat.Add(centre.Re, BigFloat.FromDouble(offX, prec).ScaleB(s0), prec);
            var ci = BigFloat.Add(centre.Im, BigFloat.FromDouble(offY, prec).ScaleB(s0), prec);
            var r = BuildReference(plan, cr, ci, jcr, jci, prec, maxIter, ct);
            r.OffX = offX; r.OffY = offY;
            (r.Cr, r.Ci) = plan.ParameterPlane ? (cr.ToDouble(), ci.ToDouble()) : (jcr.ToDouble(), jci.ToDouble());
            if (useBla && r.Len > 2 && plan is IRealPerturbationPlan rp)
            {
                var level0 = new Bla2[r.Len];
                for (int n = 0; n < r.Len; n++) level0[n] = rp.Level0R2(r.Zr[n], r.Zi[n], r.Cr, r.Ci, blaEpsilon, dcMax);
                if (converge?.StepSink != null)
                {
                    // #1959 — no skip where the reference's step is short (the pixels converge there).
                    for (int n = 0; n + 1 < r.Len; n++)
                    {
                        double ar = r.Zr[n + 1] - r.Zr[n], ai = r.Zi[n + 1] - r.Zi[n];
                        if (ar * ar + ai * ai < StepBlaGuard * StepBlaGuard) level0[n] = level0[n] with { R2 = 0 };
                    }
                    level0[r.Len - 1] = level0[r.Len - 1] with { R2 = 0 };
                }
                else if (converge != null)
                {
                    // #1911 — no skip where the reference is near the target (a zero radius poisons
                    // every merged skip that covers the step).
                    double near2 = 16 * converge.Eps * converge.Eps;
                    for (int n = 0; n < r.Len; n++)
                    {
                        double ar = r.Zr[n] - converge.Re, ai = r.Zi[n] - converge.Im;
                        if (ar * ar + ai * ai < near2) level0[n] = level0[n] with { R2 = 0 };
                    }
                }
                r.Bla2 = new Bla2Table(level0, r.Len, dcMax);
            }
            else if (useBla && r.Len > 2)
            {
                var level0 = new Bla[r.Len];
                for (int n = 0; n < r.Len; n++) level0[n] = plan.Level0(r.Zr[n], r.Zi[n], r.Cr, r.Ci, blaEpsilon);
                r.Bla = new BlaTable(level0, r.Len, dcMax, blaEpsilon);
            }
            return r;
        }

        (double X, double Y) Offset(int x, int y)
        {
            double ox = (x - width * 0.5) * scale, oy = (y - height * 0.5) * scale;
            return warp != null ? warp(ox, oy) : (ox, oy);
        }

        long rebases = 0, skips = 0, poleFlags = 0, scaled = 0;
        // Glitched pixels (dynamic plane): index → the iteration it glitched at.
        var glitched = new ConcurrentBag<(int Idx, int Iter)>();

        var main = Build(0, 0);
        var po = new ParallelOptions { CancellationToken = ct };
        Parallel.For(0, height, po, y =>
        {
            int rowMax = rowMaxIter != null && y < rowMaxIter.Length && rowMaxIter[y] > 0 ? rowMaxIter[y] : maxIter;
            var st = new Counters();
            for (int x = 0; x < width; x++)
            {
                int idx = y * width + x;
                if (marked != null && !marked(idx)) continue;
                var (ox, oy) = Offset(x, y);
                int g = PixelAny(plan, main, frame, ox, oy, rowMax, idx, sink, converge, ref st);
                if (g >= 0) glitched.Add((idx, g));
            }
            Interlocked.Add(ref rebases, st.Rebases);
            Interlocked.Add(ref skips, st.Skips);
            Interlocked.Add(ref poleFlags, st.PoleFlags);
            Interlocked.Add(ref scaled, st.Scaled);
        });

        int glitchedCount = glitched.Count, refs = 1, direct = 0;
        var pending = new List<(int Idx, int Iter)>(glitched);
        while (pending.Count > 0 && refs < MaxReferences)
        {
            ct.ThrowIfCancellationRequested();
            // The next reference: the glitched pixel that lasted longest (its orbit covers the others'),
            // the lowest index on a tie, so a frame renders the same every time.
            var best = pending[0];
            foreach (var p in pending)
                if (p.Iter > best.Iter || (p.Iter == best.Iter && p.Idx < best.Idx)) best = p;
            var (bx, by) = Offset(best.Idx % width, best.Idx / width);
            var reference = Build(bx, by);
            refs++;
            var next = new ConcurrentBag<(int, int)>();
            Parallel.ForEach(pending, po, p =>
            {
                int y = p.Idx / width;
                int rowMax = rowMaxIter != null && y < rowMaxIter.Length && rowMaxIter[y] > 0 ? rowMaxIter[y] : maxIter;
                var (ox, oy) = Offset(p.Idx % width, y);
                var st = new Counters();
                int g = PixelAny(plan, reference, frame, ox, oy, rowMax, p.Idx, sink, converge, ref st);
                Interlocked.Add(ref rebases, st.Rebases);
                Interlocked.Add(ref skips, st.Skips);
                Interlocked.Add(ref poleFlags, st.PoleFlags);
                Interlocked.Add(ref scaled, st.Scaled);
                if (g >= 0) next.Add((p.Idx, g));
            });
            pending = new List<(int Idx, int Iter)>(next);
        }

        // Whatever no reference could carry: direct iteration in BigFloat.
        if (pending.Count > 0)
        {
            Parallel.ForEach(pending, po, p =>
            {
                int y = p.Idx / width;
                int rowMax = rowMaxIter != null && y < rowMaxIter.Length && rowMaxIter[y] > 0 ? rowMaxIter[y] : maxIter;
                var (ox, oy) = Offset(p.Idx % width, y);
                Direct(plan, centre, jcr, jci, prec, frame, ox, oy, rowMax, p.Idx, sink, converge, ct);
            });
            direct = pending.Count;
        }
        return new PerturbationStats(main.Len, refs, rebases, skips, glitchedCount, direct, Interlocked.Read(ref poleFlags),
                                     Interlocked.Read(ref scaled));
    }

    private struct Counters { public long Rebases, Skips, PoleFlags, Scaled; }

    private static Reference BuildReference(IPerturbationPlan plan, BigFloat cr, BigFloat ci, BigFloat jcr, BigFloat jci,
                                            int prec, int maxIter, CancellationToken ct)
    {
        // Parameter plane: z₀ = 0, c = the reference point. Dynamic plane: z₀ = the reference point, c = Julia's.
        BigFloat zr, zi, kr, ki;
        if (plan.ParameterPlane)
        {
            var (z0r, z0i) = plan.InitialZ;   // #1959: Nova starts at z₀ ≠ 0
            zr = BigFloat.FromDouble(z0r, prec); zi = BigFloat.FromDouble(z0i, prec); kr = cr; ki = ci;
        }
        else { zr = cr; zi = ci; kr = jcr; ki = jci; }
        // Z_0 .. Z_{Len−1}, all inside the bailout. The escaped point is not kept: a pixel that
        // stepped onto it would carry |δ| ≈ |Z| ≥ the bailout while its own z may be small — the
        // large-minus-large loss perturbation must avoid. It rebases (or glitches) at Len − 1
        // instead, as MandelbrotCalculator's reference ends. (A dynamic-plane pixel may take the one
        // step onto it when that step escapes: see Pixel.)
        var r = new Reference { Zr = new double[maxIter + 1], Zi = new double[maxIter + 1] };
        int n = 0;
        for (; n <= maxIter; n++)
        {
            if ((n & 1023) == 0) ct.ThrowIfCancellationRequested();
            double a = zr.ToDouble(), b = zi.ToDouble();
            if (a * a + b * b >= plan.Bailout2) { r.HasEsc = true; r.EscR = a; r.EscI = b; break; }
            r.Zr[n] = a; r.Zi[n] = b;
            if (n == maxIter) { n++; break; }
            plan.RefStep(ref zr, ref zi, kr, ki, prec);
        }
        r.Len = n;
        return r;
    }

    // ── #1921: the derivative held as d·2^t ──

    // ILogB of the larger component; int.MinValue for zero (Math.ILogB(0) is int.MinValue).
    private static int LogB(double a, double b) => Math.ILogB(Math.Max(Math.Abs(a), Math.Abs(b)));

    // Rescale (a, b)·2^t when |·| passed 2^DerivRenormLog2 (to ~2^500). Leaves t = 0 alone below.
    private static void Renorm(ref double a, ref double b, ref int t)
    {
        int l = LogB(a, b);
        if (l <= DerivRenormLog2 || l == int.MaxValue) return;   // int.MaxValue: ∞ / NaN
        int k = l - 500;
        a = Math.ScaleB(a, -k); b = Math.ScaleB(b, -k); t += k;
    }

    private static void Renorm(ref RealMat2 j, ref int t)
    {
        int l = Math.Max(LogB(j.M11, j.M21), LogB(j.M12, j.M22));
        if (l <= DerivRenormLog2 || l == int.MaxValue) return;
        int k = l - 500;
        j = new RealMat2(Math.ScaleB(j.M11, -k), Math.ScaleB(j.M12, -k), Math.ScaleB(j.M21, -k), Math.ScaleB(j.M22, -k));
        t += k;
    }

    // The derivative as the sink takes it: d·2^(t − derivExp2). Unchanged when both are 0.
    private static (double, double) Deliver(double a, double b, int t, in Frame f)
    {
        int k = t - f.DerivExp2;
        return k == 0 ? (a, b) : (Math.ScaleB(a, k), Math.ScaleB(b, k));
    }

    private static void DerivAny(IPerturbationPlan plan, double zr, double zi, double cr, double ci, ref double dvr, ref double dvi, int t)
    {
        if (t == 0) plan.DerivStep(zr, zi, cr, ci, ref dvr, ref dvi);
        else plan.DerivStepScaled(zr, zi, cr, ci, ref dvr, ref dvi, Math.ScaleB(1.0, -t));
    }

    private static void JacAny(IRealPerturbationPlan plan, double zr, double zi, double cr, double ci, ref RealMat2 j, int t)
    {
        if (t == 0) plan.JacobianStep(zr, zi, cr, ci, ref j);
        else plan.JacobianStepScaled(zr, zi, cr, ci, ref j, Math.ScaleB(1.0, -t));
    }

    // ── #1921: the scaled-δ prelude ──

    // The pixel's state between the prelude and the double loop.
    private struct Start
    {
        public int M, Iter;
        public double Dr, Di, Er, Ei;   // δ, ε in complex units (the double loop's)
        public RealMat2 Jac;            // the derivative (a complex plan: column 1 of OfComplex), times 2^T
        public int T;
        public double DPrev;            // #1911 convergence: |z − target| one step before
    }

    // Runs the pixel while |δ| < 2^FxExitLog2 (offsets in units of 2^f.S0). Returns −2 to continue in
    // the double loop from <paramref name="st"/>, −1 when the pixel finished (sink called), else the
    // iteration it glitched at. <paramref name="rp"/>: the plan when it is real (R²).
    private static int Prelude(IPerturbationPlan plan, IRealPerturbationPlan? rp, Reference r, in Frame f,
                               double ox, double oy, int maxIter, int idx, PixelSink sink, Convergence? converge,
                               ref Counters c, out Start st)
    {
        bool pp = plan.ParameterPlane;
        // ε = e·2^s0 (constant); δ = d·2^s.
        double er = ox - r.OffX, ei = oy - r.OffY, dr = 0, di = 0;
        if (!pp) { dr = er; di = ei; er = 0; ei = 0; }
        int s0 = f.S0, s = s0;
        var (ivr, ivi) = plan.InitialDerivative;
        var jac = RealMat2.OfComplex(ivr, ivi);
        int t = 0;
        bool rebase = rp != null ? rp.CanRebase : pp && plan.CanRebase;
        double cEps2 = converge != null ? converge.Eps * converge.Eps : 0, dPrev = double.NaN;
        var bla2 = r.Bla2;
        var bla = r.Bla;
        int m = 0, iter = 0;
        for (; iter < maxIter; iter++)
        {
            int ld = LogB(dr, di);
            if (dr != 0 || di != 0)
            {
                if (s + ld >= FxExitLog2) break;   // big enough for the double loop
                if (ld > 64 || ld < -64) { dr = Math.ScaleB(dr, -ld); di = Math.ScaleB(di, -ld); s += ld; ld = 0; }
            }
            double Zr = r.Zr[m], Zi = r.Zi[m];
            // Z as small as δ (|Z| < 8·|δ|): rebasing and z = Z + δ need the scaled sum.
            int lz = LogB(Zr, Zi);
            bool near = (dr != 0 || di != 0) && (lz == int.MinValue || lz <= s + ld + 2);
            if (m + 1 >= r.Len)
            {
                if (!rebase)
                {
                    // The reference ends (Pixel's rule): a pixel this close escapes with it — one δ
                    // step onto the escaped point, kept when that escapes; else it is glitched.
                    if (r.HasEsc && iter + 1 < maxIter)
                    {
                        double ezr0 = Zr + Math.ScaleB(dr, s), ezi0 = Zi + Math.ScaleB(di, s);
                        double qcr = r.Cr + Math.ScaleB(er, s0), qci = r.Ci + Math.ScaleB(ei, s0);
                        var vj = jac; int vt = t;
                        if (rp != null) { JacAny(rp, ezr0, ezi0, qcr, qci, ref vj, vt); Renorm(ref vj, ref vt); }
                        else
                        {
                            double vr = vj.M11, vi = vj.M21;
                            DerivAny(plan, ezr0, ezi0, qcr, qci, ref vr, ref vi, vt);
                            Renorm(ref vr, ref vi, ref vt);
                            vj = RealMat2.OfComplex(vr, vi);
                        }
                        double tr = dr, ti = di;
                        ScaledDelta(plan, Zr, Zi, r.Cr, r.Ci, ref tr, ref ti, er, ei, s0, s);
                        double ezr = r.EscR + Math.ScaleB(tr, s), ezi = r.EscI + Math.ScaleB(ti, s);
                        if (ezr * ezr + ezi * ezi >= plan.Bailout2)
                        {
                            var (ovr, ovi) = Deliver(vj.M11, vj.M21, vt, f);
                            sink(idx, iter + 1, ezr, ezi, ovr, ovi);
                            st = default; return -1;
                        }
                    }
                    st = default; return iter;
                }
                if (!near) break;   // rebase to Z_m (|Z| ≫ |δ|): the double loop does it
            }

            // The BLA on the scaled δ: d′ = A·d + B·e·2^(s0 − s).
            double k = Math.ScaleB(1.0, s0 - s);
            double dMag2 = Math.ScaleB(dr * dr + di * di, 2 * s);   // underflows to 0: see the header
            int lim = Math.Min(r.Len - 1, m + (maxIter - iter));
            if (bla2 != null)
            {
                int bi = bla2.Lookup(m, dMag2, lim);
                if (bi >= 0 && bla2.Data[bi].L >= 2)
                {
                    ref readonly var e = ref bla2.Data[bi];
                    ApplyBla(e.A, e.B, ref dr, ref di, er * k, ei * k, ref jac, ref t);
                    m += e.L; iter += e.L - 1; c.Skips++; c.Scaled++; dPrev = double.NaN;
                    continue;
                }
            }
            else if (bla != null)
            {
                int bi = bla.Lookup(m, dMag2, lim);
                if (bi >= 0 && bla.Data[bi].L >= 2)
                {
                    ref readonly var e = ref bla.Data[bi];
                    ApplyBla(RealMat2.OfComplex(e.ARe, e.AIm), RealMat2.OfComplex(e.BRe, e.BIm), ref dr, ref di, er * k, ei * k, ref jac, ref t);
                    m += e.L; iter += e.L - 1; c.Skips++; c.Scaled++;
                    continue;
                }
            }

            // z = Z + δ: ≈ Z unless Z is as small as δ (then exactly, scaled).
            double zr = Zr + Math.ScaleB(dr, s), zi = Zi + Math.ScaleB(di, s);
            if (converge != null && converge.StepSink == null)
            {
                double ur = zr - converge.Re, ui = zi - converge.Im, d2 = ur * ur + ui * ui;
                if (d2 <= cEps2)
                {
                    converge.Sink(idx, iter, zr, zi, dPrev);
                    st = default; return -1;
                }
                dPrev = Math.Sqrt(d2);
            }
            double pcr = r.Cr + Math.ScaleB(er, s0), pci = r.Ci + Math.ScaleB(ei, s0);
            if (rp != null) { JacAny(rp, zr, zi, pcr, pci, ref jac, t); Renorm(ref jac, ref t); }
            else
            {
                double vr = jac.M11, vi = jac.M21;
                DerivAny(plan, zr, zi, pcr, pci, ref vr, ref vi, t);
                Renorm(ref vr, ref vi, ref t);
                jac = RealMat2.OfComplex(vr, vi);
            }

            if (rebase && near)
            {
                double zsr = Math.ScaleB(Zr, -s) + dr, zsi = Math.ScaleB(Zi, -s) + di;
                if (zsr * zsr + zsi * zsi < dr * dr + di * di || m + 1 >= r.Len)
                {
                    dr = zsr; di = zsi; Zr = 0; Zi = 0; m = 0;   // rebase, scaled
                    c.Rebases++;
                }
            }

            double odr = dr, odi = di;
            ScaledDelta(plan, Zr, Zi, r.Cr, r.Ci, ref dr, ref di, er, ei, s0, s);
            m++;
            c.Scaled++;
            if (converge?.StepSink is { } ss && m < r.Len)
            {
                // #1959 — the pixel's step (Z_{m} − Z_{m−1}) + (δ′ − δ), the reference's step exact in double.
                double sr = (r.Zr[m] - Zr) + Math.ScaleB(dr - odr, s), si = (r.Zi[m] - Zi) + Math.ScaleB(di - odi, s);
                double step2 = sr * sr + si * si;
                if (step2 < cEps2)
                {
                    ss(idx, iter, r.Zr[m] + Math.ScaleB(dr, s), r.Zi[m] + Math.ScaleB(di, s), dPrev, step2);
                    st = default; return -1;
                }
                dPrev = step2;
            }
        }
        if (iter >= maxIter)
        {
            // In set (or the row's cap) inside the prelude: z = Z_m + δ.
            double zr = r.Zr[Math.Min(m, r.Len - 1)] + Math.ScaleB(dr, s), zi = r.Zi[Math.Min(m, r.Len - 1)] + Math.ScaleB(di, s);
            var (vr, vi) = Deliver(jac.M11, jac.M21, t, f);
            sink(idx, iter, zr, zi, vr, vi);
            st = default; return -1;
        }
        st = new Start
        {
            M = m, Iter = iter, Dr = Math.ScaleB(dr, s), Di = Math.ScaleB(di, s),
            Er = Math.ScaleB(er, s0), Ei = Math.ScaleB(ei, s0), Jac = jac, T = t, DPrev = dPrev,
        };
        return -2;
    }

    // One step on the scaled δ (d·2^s; ε = e·2^s0): the plan's DeltaStep on δ, ε rescaled to ~2^−500,
    // scaled back by the same power of two. δ = ε = 0 (the reference itself) stays 0.
    private static void ScaledDelta(IPerturbationPlan plan, double Zr, double Zi, double cr, double ci,
                                    ref double dr, ref double di, double er, double ei, int s0, int s)
    {
        int ld = LogB(dr, di), le = LogB(er, ei);
        int top = Math.Max(ld, le == int.MinValue ? int.MinValue : le + (s0 - s));
        if (top == int.MinValue) return;
        int q = -500 - top;
        double a = Math.ScaleB(dr, q), b = Math.ScaleB(di, q);
        plan.DeltaStep(Zr, Zi, cr, ci, ref a, ref b, Math.ScaleB(er, s0 - s + q), Math.ScaleB(ei, s0 - s + q));
        dr = Math.ScaleB(a, -q); di = Math.ScaleB(b, -q);
    }

    private static void ApplyBla(in RealMat2 A, in RealMat2 B, ref double dr, ref double di, double er, double ei, ref RealMat2 jac, ref int t)
    {
        double ndr = A.M11 * dr + A.M12 * di + (B.M11 * er + B.M12 * ei);
        double ndi = A.M21 * dr + A.M22 * di + (B.M21 * er + B.M22 * ei);
        dr = ndr; di = ndi;
        jac = t == 0 ? A * jac + B : A * jac + Scale(B, Math.ScaleB(1.0, -t));
        Renorm(ref jac, ref t);
    }

    private static RealMat2 Scale(in RealMat2 m, double w) => new(m.M11 * w, m.M12 * w, m.M21 * w, m.M22 * w);

    private static int PixelAny(IPerturbationPlan plan, Reference r, in Frame f, double ox, double oy, int maxIter, int idx,
                                PixelSink sink, Convergence? converge, ref Counters c)
    {
        var rp = plan as IRealPerturbationPlan;
        Start st;
        if (f.S0 != 0)
        {
            int g = Prelude(plan, rp, r, f, ox, oy, maxIter, idx, sink, converge, ref c, out st);
            if (g != -2) return g;
        }
        else
        {
            double er = ox - r.OffX, ei = oy - r.OffY, dr = 0, di = 0;
            if (!plan.ParameterPlane) { dr = er; di = ei; er = 0; ei = 0; }
            var (ivr, ivi) = plan.InitialDerivative;
            st = new Start { Dr = dr, Di = di, Er = er, Ei = ei, Jac = RealMat2.OfComplex(ivr, ivi), DPrev = double.NaN };
        }
        return rp != null
            ? PixelR2(rp, r, f, st, maxIter, idx, sink, converge, ref c)
            : Pixel(plan, r, f, st, maxIter, idx, sink, ref c);
    }

    // One pixel against a reference, from <paramref name="st"/> (iteration 0, or where the scaled-δ
    // prelude left it). Returns −1 when the pixel finished (sink called), else the iteration it
    // glitched at (dynamic plane only).
    private static int Pixel(IPerturbationPlan plan, Reference r, in Frame f, in Start st, int maxIter, int idx,
                             PixelSink sink, ref Counters c)
    {
        bool pp = plan.ParameterPlane;
        double er = st.Er, ei = st.Ei, dr = st.Dr, di = st.Di;
        double dvr = st.Jac.M11, dvi = st.Jac.M21;   // the derivative of the full orbit
        int t = st.T;
        bool rebase = pp && plan.CanRebase;
        double pcr = pp ? r.Cr + er : r.Cr, pci = pp ? r.Ci + ei : r.Ci;   // the pixel's c, to double
        double zr = 0, zi = 0;
        double bail = plan.Bailout2;
        var bla = r.Bla;
        int m = st.M, iter = st.Iter;
        for (; iter < maxIter; iter++)
        {
            if (bla != null)
            {
                int bi = bla.Lookup(m, dr * dr + di * di, Math.Min(r.Len - 1, m + (maxIter - iter)));
                if (bi >= 0)
                {
                    ref readonly var e = ref bla.Data[bi];
                    if (e.L >= 2)
                    {
                        double ar = e.ARe, ai = e.AIm, br = e.BRe, bim = e.BIm;
                        double ndr = ar * dr - ai * di + (br * er - bim * ei);
                        double ndi = ar * di + ai * dr + (br * ei + bim * er);
                        // d′ = A·d + B (B is ∂/∂c of the skipped steps; 0 in the dynamic plane) — #1782.
                        if (t != 0) { double w = Math.ScaleB(1.0, -t); br *= w; bim *= w; }   // #1921
                        double nvr = ar * dvr - ai * dvi + br, nvi = ar * dvi + ai * dvr + bim;
                        dr = ndr; di = ndi; dvr = nvr; dvi = nvi;
                        Renorm(ref dvr, ref dvi, ref t);
                        m += e.L;
                        iter += e.L - 1;
                        c.Skips++;
                        continue;
                    }
                }
            }

            double Zr = r.Zr[m], Zi = r.Zi[m];
            zr = Zr + dr; zi = Zi + di;
            double z2 = zr * zr + zi * zi;
            if (z2 >= bail) break;
            DerivAny(plan, zr, zi, pcr, pci, ref dvr, ref dvi, t);
            Renorm(ref dvr, ref dvi, ref t);

            if (rebase)
            {
                if (z2 < dr * dr + di * di || m + 1 >= r.Len)
                {
                    dr = zr; di = zi; Zr = 0; Zi = 0; m = 0;   // rebase: Z₀ = 0 (the critical point)
                    c.Rebases++;
                }
            }
            else if (m + 1 >= r.Len || plan.IsGlitch(zr, zi, Zr, Zi, r.Cr, r.Ci, GlitchTolerance2))
            {
                // The reference ends here. A pixel near it escapes with it: one δ step onto the
                // escaped point, kept only when it escapes (then |δ| ≪ |Z| and nothing cancelled).
                if (m + 1 >= r.Len && r.HasEsc && iter + 1 < maxIter && !plan.IsGlitch(zr, zi, Zr, Zi, r.Cr, r.Ci, GlitchTolerance2))
                {
                    double tr = dr, ti = di;
                    plan.DeltaStep(Zr, Zi, r.Cr, r.Ci, ref tr, ref ti, er, ei);
                    double ezr = r.EscR + tr, ezi = r.EscI + ti;
                    if (ezr * ezr + ezi * ezi >= bail)
                    {
                        var (ovr, ovi) = Deliver(dvr, dvi, t, f);
                        sink(idx, iter + 1, ezr, ezi, ovr, ovi);
                        return -1;
                    }
                }
                return iter;   // glitched: another reference, or direct iteration, takes it
            }
            plan.DeltaStep(Zr, Zi, r.Cr, r.Ci, ref dr, ref di, er, ei);
            m++;
        }
        // In set: z after the last step, as the direct loop leaves it.
        if (iter >= maxIter && m < r.Len) { zr = r.Zr[m] + dr; zi = r.Zi[m] + di; }
        var (vr, vi) = Deliver(dvr, dvi, t, f);
        sink(idx, iter, zr, zi, vr, vi);
        return -1;
    }

    // #1909 — Pixel for a real (non-conformal) plan: δ, ε as R² columns, the matrix BLA, and the
    // orbit's 2×2 Jacobian (the sink gets its first column, ∂z/∂Re(pixel)), with the same rebase /
    // glitch / escape-step rules as Pixel. #1913: the dynamic plane too (δ₀ = the offset, ε = 0).
    private static int PixelR2(IRealPerturbationPlan plan, Reference r, in Frame f, in Start st, int maxIter, int idx,
                               PixelSink sink, Convergence? converge, ref Counters c)
    {
        bool poles = plan.HasPoles;   // #1911
        double dPrev = st.DPrev, cEps2 = converge != null ? converge.Eps * converge.Eps : 0;
        double er = st.Er, ei = st.Ei, dr = st.Dr, di = st.Di;
        var jac = st.Jac;
        int t = st.T;
        bool rebase = plan.CanRebase;
        double pcr = r.Cr + er, pci = r.Ci + ei;
        double zr = 0, zi = 0;
        double bail = plan.Bailout2;
        var bla = r.Bla2;
        int m = st.M, iter = st.Iter;
        for (; iter < maxIter; iter++)
        {
            if (bla != null)
            {
                int bi = bla.Lookup(m, dr * dr + di * di, Math.Min(r.Len - 1, m + (maxIter - iter)));
                if (bi >= 0)
                {
                    ref readonly var e = ref bla.Data[bi];
                    if (e.L >= 2)
                    {
                        var A = e.A; var B = e.B;
                        double ndr = A.M11 * dr + A.M12 * di + (B.M11 * er + B.M12 * ei);
                        double ndi = A.M21 * dr + A.M22 * di + (B.M21 * er + B.M22 * ei);
                        jac = t == 0 ? A * jac + B : A * jac + Scale(B, Math.ScaleB(1.0, -t));   // J′ = A·J + B
                        Renorm(ref jac, ref t);
                        dr = ndr; di = ndi;
                        m += e.L;
                        iter += e.L - 1;
                        c.Skips++;
                        dPrev = double.NaN;
                        continue;
                    }
                }
            }

            double Zr = r.Zr[m], Zi = r.Zi[m];
            zr = Zr + dr; zi = Zi + di;
            double z2 = zr * zr + zi * zi;
            if (z2 >= bail) break;
            if (converge != null && converge.StepSink == null)
            {
                double ur = zr - converge.Re, ui = zi - converge.Im, d2 = ur * ur + ui * ui;
                if (d2 <= cEps2) { converge.Sink(idx, iter, zr, zi, dPrev); return -1; }
                dPrev = Math.Sqrt(d2);
            }
            JacAny(plan, zr, zi, pcr, pci, ref jac, t);
            Renorm(ref jac, ref t);

            if (rebase)
            {
                if (z2 < dr * dr + di * di || m + 1 >= r.Len)
                {
                    dr = zr; di = zi; Zr = 0; Zi = 0; m = 0;
                    c.Rebases++;
                }
            }
            else if (m + 1 >= r.Len || plan.IsGlitch(zr, zi, Zr, Zi, r.Cr, r.Ci, GlitchTolerance2))
            {
                if (m + 1 >= r.Len && r.HasEsc && iter + 1 < maxIter && !plan.IsGlitch(zr, zi, Zr, Zi, r.Cr, r.Ci, GlitchTolerance2))
                {
                    double tr = dr, ti = di;
                    plan.DeltaStep(Zr, Zi, r.Cr, r.Ci, ref tr, ref ti, er, ei);
                    double ezr = r.EscR + tr, ezi = r.EscI + ti;
                    if (ezr * ezr + ezi * ezi >= bail)
                    {
                        var (ovr, ovi) = Deliver(jac.M11, jac.M21, t, f);
                        sink(idx, iter + 1, ezr, ezi, ovr, ovi);
                        return -1;
                    }
                }
                return iter;
            }
            // #1911 — the pixel's denominator cancels against the reference's: precision lost in
            // Q + ΔQ. Another reference, or direct iteration, takes the pixel.
            if (poles && plan.PoleRatio2(Zr, Zi, r.Cr, r.Ci, dr, di, er, ei) < GlitchTolerance2)
            {
                c.PoleFlags++;
                return iter;
            }
            plan.DeltaStep(Zr, Zi, r.Cr, r.Ci, ref dr, ref di, er, ei);
            m++;
            if (converge?.StepSink is { } ss && m < r.Len)
            {
                // #1959 — the step to z_{n+1} = Z_{m} + δ′, against z_n (both double: the tolerance is far
                // above their rounding).
                double nzr = r.Zr[m] + dr, nzi = r.Zi[m] + di, sr = nzr - zr, si = nzi - zi, step2 = sr * sr + si * si;
                if (step2 < cEps2) { ss(idx, iter, nzr, nzi, dPrev, step2); return -1; }
                dPrev = step2;
            }
        }
        if (iter >= maxIter && m < r.Len) { zr = r.Zr[m] + dr; zi = r.Zi[m] + di; }
        var (vr, vi) = Deliver(jac.M11, jac.M21, t, f);
        sink(idx, iter, zr, zi, vr, vi);
        return -1;
    }

    // Direct iteration in BigFloat (the last resort for glitched pixels). The derivative runs in
    // double on the rounded z, as the perturbed path's does (scaled past 2^DerivRenormLog2, #1921).
    private static void Direct(IPerturbationPlan plan, ExactCentre centre, BigFloat jcr, BigFloat jci, int prec, in Frame f,
                               double ox, double oy, int maxIter, int idx, PixelSink sink, Convergence? converge, CancellationToken ct)
    {
        var pr = BigFloat.Add(centre.Re, BigFloat.FromDouble(ox, prec).ScaleB(f.S0), prec);
        var pi = BigFloat.Add(centre.Im, BigFloat.FromDouble(oy, prec).ScaleB(f.S0), prec);
        BigFloat zr, zi, kr, ki;
        if (plan.ParameterPlane)
        {
            var (z0r, z0i) = plan.InitialZ;   // #1959
            zr = BigFloat.FromDouble(z0r, prec); zi = BigFloat.FromDouble(z0i, prec); kr = pr; ki = pi;
        }
        else { zr = pr; zi = pi; kr = jcr; ki = jci; }
        var (dvr, dvi) = plan.InitialDerivative;
        var rp = plan as IRealPerturbationPlan;
        var jac = RealMat2.OfComplex(dvr, dvi);   // #1909 — a real plan's 2×2 Jacobian
        int t = 0;
        double dPrev = double.NaN;
        double a = 0, b = 0, pcr = kr.ToDouble(), pci = ki.ToDouble();
        int iter = 0;
        for (; iter < maxIter; iter++)
        {
            if ((iter & 1023) == 0) ct.ThrowIfCancellationRequested();
            a = zr.ToDouble(); b = zi.ToDouble();
            if (a * a + b * b >= plan.Bailout2) break;
            if (converge != null && converge.StepSink == null)
            {
                double ur = a - converge.Re, ui = b - converge.Im, d2 = ur * ur + ui * ui;
                if (d2 <= converge.Eps * converge.Eps) { converge.Sink(idx, iter, a, b, dPrev); return; }
                dPrev = Math.Sqrt(d2);
            }
            if (rp != null) { JacAny(rp, a, b, pcr, pci, ref jac, t); Renorm(ref jac, ref t); }
            else { DerivAny(plan, a, b, pcr, pci, ref dvr, ref dvi, t); Renorm(ref dvr, ref dvi, ref t); }
            plan.RefStep(ref zr, ref zi, kr, ki, prec);
            if (converge?.StepSink is { } ss)
            {
                double na = zr.ToDouble(), nb = zi.ToDouble(), sr = na - a, si = nb - b, step2 = sr * sr + si * si;   // #1959
                if (step2 < converge.Eps * converge.Eps) { ss(idx, iter, na, nb, dPrev, step2); return; }
                dPrev = step2;
            }
        }
        if (iter >= maxIter) { a = zr.ToDouble(); b = zi.ToDouble(); }
        if (rp != null) { dvr = jac.M11; dvi = jac.M21; }
        var (vr, vi) = Deliver(dvr, dvi, t, f);
        sink(idx, iter, a, b, vr, vi);
    }
}
