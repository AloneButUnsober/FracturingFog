// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Calculators/RotationDomainColoring.cs
//
// #1981 — rotation-domain colouring, an opt-in colour option for the pixels a render left
// non-converged (in-set for Julia / Phoenix / Magnet, no root for Newton / Nova).
//
// Idea credited to pauldelbrot, fractalforums.org topic 5693 (Docs/Resources-Bibliography.md
// → pauldelbrot-early-outs-5693): a non-converging orbit's Lyapunov exponent
// λ = log|Π f′(z_n)| / N, from the running first derivative, is ≈ 0 in a rotation domain
// (Siegel disk, Herman ring: neither converging nor chaotic), > 0 on a chaotic orbit and < 0 in
// an attracting cycle's basin; and |f′| / |f″| of the iterate (the running first and second
// derivatives) estimates the distance to the domain's boundary.
//
// FF's form, measured on the golden-mean Siegel disk (Server.Tests/S1981RotationDomainTests.cs):
//  • λ in a rotation domain is not 0 at a finite N but O(1/N): |Π f′| stays bounded both ways
//    (f^N is conjugate to a rotation), so log|Π f′| is bounded, not 0. At N = 2000 the disk's
//    |λ| reaches ~7e-3 near its edge, so the post's ε ≈ 1e-5 alone would need ~10⁶ iterations.
//    A pixel classifies as rotation when |λ| < max(ε, K / N): ε (RotationDomainEpsilon, default the
//    post's 1e-5) and the bound K on |log|Π f′|| in nats (RotationDomainBound, default 16).
//  • the distance is the minimum over the orbit of |(f^n)′| / |(f^n)″|. Inside the disk each f^n is
//    univalent, so by the Koebe distortion theorem every term is at least a quarter of the true
//    distance; the minimum is the tightest. Measured: ≈ 2× the distance to the critical-orbit
//    closure (the disk's boundary) for most of the disk.
//  • a parameter-plane family starts at a critical point (Nova z₀ = 1: N′ = 0), so its product and
//    derivatives start at the first iterate (as #1980's chaos early-out does).
//  • Phoenix is a two-variable map: λ is its largest Lyapunov exponent (a tangent vector carried
//    through the Jacobian [[2z, p], [1, 0]]), and it has no |f′| / |f″| distance (flat shade).
//
// It is a post-pass over the finished frame's non-converged pixels, so every compute path (SIMD,
// scalar, GPU, solid regions, adaptive AA) gets it, and it re-iterates each pixel from its own
// coordinate in double. It stays off past the family's double tier (the deep engines). Off is
// byte-identical: nothing here runs.

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.Models;
using FracturingFog.Perturbation;

namespace FracturingFog;

public static class RotationDomainColoring
{
    public enum OrbitClass : byte { Attracting, Rotation, Chaotic }

    /// <summary>One orbit's verdict: its class, Lyapunov exponent (nats per step) and the
    /// rotation-domain distance estimate in plane units (+∞ when none).</summary>
    public readonly record struct Result(OrbitClass Class, double Lyapunov, double Distance);

    /// <summary>The colour option's settings, from <see cref="FractalParameters"/>.</summary>
    public readonly record struct Settings(double Epsilon, double Bound, double ShadeWidthPx,
                                           uint Centre, uint Edge, uint Chaos)
    {
        public static Settings From(FractalParameters p) => new(
            Math.Max(0, p.RotationDomainEpsilon), Math.Max(0, p.RotationDomainBound),
            Math.Max(1e-3, p.RotationDomainShadeWidth),
            p.RotationDomainColor, p.RotationDomainEdgeColor, p.RotationDomainChaosColor);

        /// <summary>The |λ| threshold for an N-step orbit: max(ε, K / N).</summary>
        public double Threshold(int n) => Math.Max(Epsilon, n > 0 ? Bound / n : double.PositiveInfinity);
    }

    /// <summary>Pixels per class in the last pass (for the probe and tests).</summary>
    public readonly record struct Stats(long Rotation, long Chaotic, long Attracting);

    // ── second-order jets: a value with its first and second derivative in z ──

    public readonly struct Jet
    {
        public readonly Complex V, D1, D2;
        public Jet(Complex v, Complex d1, Complex d2) { V = v; D1 = d1; D2 = d2; }
        public static Jet Var(Complex z) => new(z, Complex.One, Complex.Zero);
        public static Jet Const(Complex c) => new(c, Complex.Zero, Complex.Zero);
        public static Jet operator +(Jet a, Jet b) => new(a.V + b.V, a.D1 + b.D1, a.D2 + b.D2);
        public static Jet operator -(Jet a, Jet b) => new(a.V - b.V, a.D1 - b.D1, a.D2 - b.D2);
        public static Jet operator +(Jet a, Complex c) => new(a.V + c, a.D1, a.D2);
        public static Jet operator -(Jet a, Complex c) => new(a.V - c, a.D1, a.D2);
        public static Jet operator *(Complex k, Jet a) => new(k * a.V, k * a.D1, k * a.D2);
        public static Jet operator *(Jet a, Jet b)
            => new(a.V * b.V, a.D1 * b.V + a.V * b.D1, a.D2 * b.V + 2.0 * a.D1 * b.D1 + a.V * b.D2);
        // q = a / b: q′ = (a′ − q b′) / b, q″ = (a″ − 2 q′ b′ − q b″) / b.
        public static Jet operator /(Jet a, Jet b)
        {
            Complex q = a.V / b.V;
            Complex q1 = (a.D1 - q * b.D1) / b.V;
            return new(q, q1, (a.D2 - 2.0 * q1 * b.D1 - q * b.D2) / b.V);
        }
        public Jet Pow(int n)
        {
            Jet r = this;
            for (int i = 1; i < n; i++) r *= this;
            return r;
        }
    }

    /// <summary>A one-variable step z → f(z) for a fixed parameter, on jets, and the family's own
    /// stop test (escaped or converged ⇒ the orbit is not a rotation domain's).</summary>
    public interface IStep
    {
        Jet Apply(Complex z);
        bool Stops(Complex z, Complex next);
    }

    public readonly struct JuliaStep : IStep
    {
        private readonly Complex _c; private readonly double _bail2;
        public JuliaStep(Complex c, double bailout2) { _c = c; _bail2 = bailout2; }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Jet Apply(Complex z) => new(z * z + _c, 2.0 * z, 2.0);
        public bool Stops(Complex z, Complex next) => next.Real * next.Real + next.Imaginary * next.Imaginary >= _bail2;
    }

    /// <summary>Newton / Nova: N(z) = z − R·(z^d − 1)/(d·z^(d−1)) + add.</summary>
    public readonly struct NewtonStep : IStep
    {
        private readonly int _d; private readonly double _r; private readonly Complex _add;
        public NewtonStep(int d, double r, Complex add) { _d = d; _r = r; _add = add; }
        public Jet Apply(Complex z)
        {
            var j = Jet.Var(z);
            Jet zd1 = j.Pow(_d - 1);
            Jet p = zd1 * j - Complex.One;
            Jet dp = (Complex)_d * zd1;
            return j - (Complex)_r * (p / dp) + _add;
        }
        public bool Stops(Complex z, Complex next)
        {
            Complex s = next - z;
            return s.Real * s.Real + s.Imaginary * s.Imaginary < 1e-12;   // the renders' tolerance
        }
    }

    /// <summary>Magnet type 1: z → ((z² + c − 1) / (2z + c − 2))².</summary>
    public readonly struct Magnet1Step : IStep
    {
        private readonly Complex _c; private readonly double _bail2, _eps;
        public Magnet1Step(Complex c, double bailout2, double convEps) { _c = c; _bail2 = bailout2; _eps = convEps; }
        public Jet Apply(Complex z)
        {
            var j = Jet.Var(z);
            Jet g = (j * j + (_c - 1)) / ((Complex)2 * j + (_c - 2));
            return g * g;
        }
        public bool Stops(Complex z, Complex next) => MagnetStops(next, _bail2, _eps);
    }

    /// <summary>Magnet type 2: z → ((z³ + 3(c−1)z + (c−1)(c−2)) / (3z² + 3(c−2)z + c² − 3c + 3))².</summary>
    public readonly struct Magnet2Step : IStep
    {
        private readonly Complex _c; private readonly double _bail2, _eps;
        public Magnet2Step(Complex c, double bailout2, double convEps) { _c = c; _bail2 = bailout2; _eps = convEps; }
        public Jet Apply(Complex z)
        {
            var j = Jet.Var(z);
            Complex a = _c - 1, b = _c - 2;
            Jet num = j.Pow(3) + (3 * a) * j + a * b;
            Jet den = (Complex)3 * (j * j) + (3 * b) * j + (_c * _c - 3 * _c + 3);
            Jet g = num / den;
            return g * g;
        }
        public bool Stops(Complex z, Complex next) => MagnetStops(next, _bail2, _eps);
    }

    private static bool MagnetStops(Complex z, double bail2, double eps)
        => z.Real * z.Real + z.Imaginary * z.Imaginary >= bail2 || (z - 1).Magnitude <= eps;

    /// <summary>Classify an N-step orbit of a one-variable map from <paramref name="z0"/>.
    /// <paramref name="fromFirst"/>: z₀ is a critical point (a parameter plane), so the product and
    /// the derivatives start at the first iterate.</summary>
    public static Result Classify<T>(in T step, Complex z0, int n, bool fromFirst, in Settings s)
        where T : struct, IStep
    {
        Complex z = z0, d1 = Complex.One, d2 = Complex.Zero;
        double mul = 1, dist = double.PositiveInfinity;
        long mulE = 0;
        int counted = 0;
        bool track = true;   // the distance, until the derivatives leave double's range
        for (int i = 0; i < n; i++)
        {
            Jet f = step.Apply(z);
            Complex next = f.V;
            if (!double.IsFinite(next.Real) || !double.IsFinite(next.Imaginary) || step.Stops(z, next))
                return new(OrbitClass.Attracting, double.NegativeInfinity, double.PositiveInfinity);
            if (i > 0 || !fromFirst)
            {
                PerturbationEngine.MulIn(ref mul, ref mulE, f.D1.Real * f.D1.Real + f.D1.Imaginary * f.D1.Imaginary);
                counted++;
                if (track)
                {
                    d2 = f.D2 * d1 * d1 + f.D1 * d2;
                    d1 = f.D1 * d1;
                    double m1 = d1.Magnitude, m2 = d2.Magnitude;
                    if (!(m1 < 1e150 && m1 > 1e-150 && m2 < 1e300)) track = false;
                    else if (m2 > 0) dist = Math.Min(dist, m1 / m2);
                }
            }
            z = next;
        }
        if (counted == 0) return new(OrbitClass.Attracting, 0, double.PositiveInfinity);
        double lambda = mul > 0 ? 0.5 * (Math.Log2(mul) + mulE) * Math.Log(2) / counted : double.NegativeInfinity;
        return Verdict(lambda, counted, track ? dist : double.PositiveInfinity, s);
    }

    /// <summary>Phoenix z → z² + c + p·z_prev from (z, z_prev) = (0, 0): the largest Lyapunov
    /// exponent of the two-variable map, from a tangent vector; no distance.</summary>
    public static Result ClassifyPhoenix(Complex c, Complex p, double bailout2, int n, in Settings s)
    {
        Complex z = Complex.Zero, w = Complex.Zero, a = Complex.One, b = Complex.Zero;
        double mul = 1; long mulE = 0;
        for (int i = 0; i < n; i++)
        {
            if (z.Real * z.Real + z.Imaginary * z.Imaginary >= bailout2)
                return new(OrbitClass.Attracting, double.NegativeInfinity, double.PositiveInfinity);
            // J = [[2z, p], [1, 0]] at (z, w).
            Complex na = 2.0 * z * a + p * b, nb = a;
            double n2 = na.Real * na.Real + na.Imaginary * na.Imaginary + nb.Real * nb.Real + nb.Imaginary * nb.Imaginary;
            if (!(n2 > 0) || !double.IsFinite(n2))
                return new(OrbitClass.Attracting, double.NegativeInfinity, double.PositiveInfinity);
            PerturbationEngine.MulIn(ref mul, ref mulE, n2);
            double inv = 1 / Math.Sqrt(n2);
            a = na * inv; b = nb * inv;
            Complex nz = z * z + c + p * w;
            w = z; z = nz;
        }
        double lambda = 0.5 * (Math.Log2(mul) + mulE) * Math.Log(2) / n;
        return Verdict(lambda, n, double.PositiveInfinity, s);
    }

    private static Result Verdict(double lambda, int n, double dist, in Settings s)
    {
        double t = s.Threshold(n);
        var cls = Math.Abs(lambda) < t ? OrbitClass.Rotation : lambda > 0 ? OrbitClass.Chaotic : OrbitClass.Attracting;
        return new(cls, lambda, cls == OrbitClass.Rotation ? dist : double.PositiveInfinity);
    }

    /// <summary>The colour for a verdict, 0 to keep the pixel's own: rotation shades from the edge
    /// colour at the boundary to the centre colour over ShadeWidthPx pixels of distance (the centre
    /// colour without a distance), chaos is flat, an attracting orbit keeps its colour.</summary>
    public static uint ColorFor(in Result r, double pixelScale, in Settings s)
    {
        switch (r.Class)
        {
            case OrbitClass.Chaotic: return s.Chaos;
            case OrbitClass.Rotation:
                double t = double.IsFinite(r.Distance) && pixelScale > 0
                    ? 1 - Math.Exp(-(r.Distance / pixelScale) / s.ShadeWidthPx) : 1;
                return Lerp(s.Edge, s.Centre, t);
            default: return 0;
        }
    }

    internal static uint Lerp(uint a, uint b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        uint ch(int sh) => (uint)Math.Round(((a >> sh) & 0xFF) + (((b >> sh) & 0xFF) - (double)((a >> sh) & 0xFF)) * t) << sh;
        return ch(24) | ch(16) | ch(8) | ch(0);
    }

    /// <summary>Run the pass: for each candidate pixel (masked, when a mask is given) classify it and
    /// write its colour into <paramref name="stamp"/> (0 = keep). Returns per-class counts.</summary>
    public static Stats Fill(uint[] stamp, int w, int h, byte[]? mask, Func<int, bool> candidate,
                             Func<int, int, Result> classify, double pixelScale, Settings s, CancellationToken ct)
    {
        long rot = 0, cha = 0, att = 0;
        Parallel.For(0, h, new ParallelOptions { CancellationToken = ct }, y =>
        {
            long lr = 0, lc = 0, la = 0;
            int rb = y * w;
            for (int x = 0; x < w; x++)
            {
                int idx = rb + x;
                stamp[idx] = 0;
                if (mask != null && mask[idx] == 0) continue;
                if (!candidate(idx)) continue;
                var r = classify(x, y);
                stamp[idx] = ColorFor(r, pixelScale, s);
                if (r.Class == OrbitClass.Rotation) lr++; else if (r.Class == OrbitClass.Chaotic) lc++; else la++;
            }
            Interlocked.Add(ref rot, lr); Interlocked.Add(ref cha, lc); Interlocked.Add(ref att, la);
        });
        return new(rot, cha, att);
    }

    /// <summary>Write the stamp's colours (non-zero entries) over <paramref name="color"/>, alpha scaled
    /// by <paramref name="interiorAlpha"/> (255 = as is).</summary>
    public static void Stamp(uint[] stamp, uint[] color, byte[]? mask, int interiorAlpha)
    {
        int n = Math.Min(stamp.Length, color.Length);
        for (int i = 0; i < n; i++)
        {
            if (stamp[i] == 0 || (mask != null && mask[i] == 0)) continue;
            color[i] = interiorAlpha < 255 ? InteriorAlphaStamp.ScaleArgbAlpha(stamp[i], interiorAlpha) : stamp[i];
        }
    }
}
