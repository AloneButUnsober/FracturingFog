// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Explore/DeepLocationAngles.cs
//
// #1873 (epic #1428, C7e) — LocationAngles past octuple-double: the external angles of a
// minibrot (DeepNucleusFinder) or Misiurewicz point (DeepMisiurewiczFinder) found past
// 1e100. The same method as LocationAngles (its header; external rays and the root walk
// credited in Docs/Resources-Bibliography.md: Douady–Hubbard, Claude Heiland-Allen),
// carried in BigFloat and floatexp:
//
//   • Root: the interior coordinate b walks 0 → 1 with Newton on (z₀, c) for f^p(z₀) = z₀,
//     (f^p)′(z₀) = b, perturbed about the nucleus orbit (BigFloat, stored as double), the
//     offsets in CFx (a deep component's root is far below double's range from its nucleus).
//   • Probes on rings around the root (or the point), escape-tested by perturbation in CFx
//     about the root's own orbit; the shallowest deep-enough ones are traced OUTWARD:
//     the probe's exact orbit in BigFloat to its escape, then Newton on the offset (CFx) for
//     f^{k+1}(0) = r·e^{2πiφ} with r rising to the escape radius, k stepping down, the
//     digit read off f^k(0)'s phase.
//   • Each candidate angle is landed with DeepExternalRay.Land and kept only if it lands
//     back on the same component (within 1% of its size) or point.

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using System.Threading;

using FracturingFog.FFMath;

using SMath = System.Math;

namespace FracturingFog.Abstractions.Explore;

/// <summary>#1873 — external angles of locations found past OD. See the file header.</summary>
public static class DeepLocationAngles
{
    private static readonly double[] ProbeScales = { 0.3, 0.1, 0.03, 0.01 };
    private const double Log2Of10 = 3.3219280948873622;

    /// <summary>The verified external angles of the component (one for the main cardioid,
    /// else two, ascending). Empty when none verify.</summary>
    public static List<ExternalAngle> ComponentAngles(in DeepNucleusResult n, CancellationToken ct = default)
    {
        var found = new List<ExternalAngle>();
        if (!n.Found) return found;
        if (n.Period == 1) { found.Add(ExternalAngle.FromBits("", "0")); return found; }

        if (!Root(n.Nucleus, n.Period, out var rootOffset, ct: ct)) return found;
        int prec = n.Nucleus.Precision;
        var root = Add(n.Nucleus, rootOffset, prec);
        var outward = Unit(rootOffset);
        double sizeTolLog2 = n.Size.Log2Abs() + SMath.Log2(0.01);

        var Z = Orbit(root, LocationAngles.RingMaxDepth, prec, ct);
        var probes = new List<(CFx Off, int Depth)>();
        foreach (double f in ProbeScales)
            for (int a = 0; a < LocationAngles.RingDirections; a++)
            {
                ct.ThrowIfCancellationRequested();
                var dir = outward * Complex.FromPolarCoordinates(f, 2 * SMath.PI * (a + 0.5) / LocationAngles.RingDirections);
                var off = rootOffset.Magnitude() is var m ? new CFx(m, FloatExp.Zero) * CFx.Of(dir.Real, dir.Imaginary) : default;
                int depth = EscapeDepth(Z, off);
                if (depth >= LocationAngles.ProbeMinDepth * n.Period) probes.Add((off, depth));
            }
        probes.Sort((x, y) => x.Depth.CompareTo(y.Depth));
        var tried = new HashSet<ExternalAngle>();
        foreach (var (off, _) in probes)
        {
            ct.ThrowIfCancellationRequested();
            if (found.Count >= 2 || tried.Count >= LocationAngles.MaxCandidates) break;
            string? bits = TraceOut(Add(root, off, prec), ct);
            if (bits == null || bits.Length < n.Period) continue;
            var cand = ExternalAngle.FromBits("", bits[..n.Period]);
            if (cand.Period != n.Period || !tried.Add(cand)) continue;
            var land = DeepExternalRay.Land(cand, ct: ct);
            if (land.Status == RayLandingStatus.Component
                && DistLog2(land.Nucleus.Nucleus, n.Nucleus) <= sizeTolLog2
                && !found.Contains(cand))
                found.Add(cand);
        }
        return Sorted(found);
    }

    /// <summary>The verified external angles landing at the Misiurewicz point (preperiod
    /// k − 1; period a multiple of p), ascending. 10^<paramref name="scaleLog10"/> is a length
    /// its surroundings are resolved at (e.g. the snap's reach); probes start inside it.</summary>
    public static List<ExternalAngle> MisiurewiczAngles(in DeepMisiurewiczResult m, double scaleLog10, CancellationToken ct = default)
    {
        var found = new List<ExternalAngle>();
        if (!m.Found || m.Preperiod < 1 || m.Period < 1) return found;
        int pre = m.Preperiod - 1;
        int prec = m.Point.Precision;
        double tolLog2 = scaleLog10 * Log2Of10 + SMath.Log2(1e-6);
        var tried = new HashSet<ExternalAngle>();
        foreach (double f in new[] { 1e-3, 1e-5 })
            for (int a = 0; a < 12; a++)
            {
                ct.ThrowIfCancellationRequested();
                if (tried.Count >= 3 * LocationAngles.MaxCandidates) return Sorted(found);
                double th = 2 * SMath.PI * (a + 0.5) / 12;
                var off = new CFx(FloatExp.FromLog10(scaleLog10) * f, FloatExp.Zero) * CFx.Of(SMath.Cos(th), SMath.Sin(th));
                string? bits = TraceOut(Add(m.Point, off, prec), ct);
                if (bits == null) continue;
                for (int mult = 1; mult <= 4; mult++)
                {
                    int per = m.Period * mult;
                    if (bits.Length < pre + per) break;
                    var cand = ExternalAngle.FromBits(bits[..pre], bits.Substring(pre, per));
                    if (!tried.Add(cand) || found.Contains(cand)) continue;
                    var land = DeepExternalRay.Land(cand, ct: ct);
                    if (land.Status == RayLandingStatus.Misiurewicz
                        && DistLog2(land.Misiurewicz.Point, m.Point) <= tolLog2)
                    {
                        found.Add(cand);
                        break;
                    }
                }
            }
        return Sorted(found);
    }

    // ── Root of a component ────────────────────────────────────────────────

    /// <summary>As <see cref="LocationAngles.Root"/>: the root of the period-p component
    /// with this nucleus, as an offset from it (CFx).</summary>
    public static bool Root(ExactCentre nucleus, int p, out CFx offset, int steps = 32, CancellationToken ct = default)
    {
        var Z = Orbit(nucleus, p, nucleus.Precision, ct);
        CFx e0 = CFx.Zero, dcOff = CFx.Zero;
        var one = CFx.One;
        for (int s = 1; s <= steps; s++)
        {
            ct.ThrowIfCancellationRequested();
            var b = CFx.Of((double)s / steps, 0);
            bool ok = false;
            double tolLog2 = s == steps ? -29.9 : -43.2;   // 1e-9 at the parabolic end, else 1e-13
            for (int it = 0; it < 64; it++)
            {
                CFx e = e0, dz = one, dc = CFx.Zero, dzz = CFx.Zero, dzc = CFx.Zero;
                for (int n = 0; n < p; n++)
                {
                    var Zn = CFx.Of(Z[n].Real, Z[n].Imaginary);
                    CFx zn = Zn + e;
                    dzz = (dz * dz + zn * dzz).Twice();
                    dzc = (dc * dz + zn * dzc).Twice();
                    dz = (zn * dz).Twice();
                    dc = (zn * dc).Twice().PlusOne();
                    e = (Zn.Twice() + e) * e + dcOff;
                }
                CFx f1 = CFx.Of(Z[p].Real, Z[p].Imaginary) + e - e0, f2 = dz - b;
                CFx j11 = dz - one, j12 = dc, j21 = dzz, j22 = dzc;
                CFx det = j11 * j22 - j12 * j21;
                if (det.IsZero || !det.IsFinite) { offset = default; return false; }
                CFx d0 = (f1 * j22 - j12 * f2) / det, d1 = (j11 * f2 - j21 * f1) / det;
                e0 -= d0;
                dcOff -= d1;
                if (!dcOff.IsFinite || !e0.IsFinite) { offset = default; return false; }
                double dcLog2 = dcOff.Log2Abs();
                if (d1.Log2Abs() <= dcLog2 - 49.8 && d0.Log2Abs() <= SMath.Max(e0.Log2Abs(), dcLog2 - 200) - 49.8) { ok = true; break; }
                if (d1.Log2Abs() <= dcLog2 + tolLog2) ok = true;
            }
            if (!ok) { offset = default; return false; }
        }
        offset = dcOff;
        return !dcOff.IsZero;
    }

    // ── Probes and the outward trace ─────────────────────────────────────

    // First n with |Z_n + ε_n| ≥ √R for c = reference + off (perturbed in CFx); 0 if none.
    private static int EscapeDepth(Complex[] Z, CFx off)
    {
        double rs2 = ExternalRay.StartRadius;   // (√R)²
        CFx e = CFx.Zero;
        for (int n = 0; n + 1 < Z.Length; n++)
        {
            var Zn = CFx.Of(Z[n].Real, Z[n].Imaginary);
            e = (Zn.Twice() + e) * e + off;
            var z = new Complex(Z[n + 1].Real + e.Re.ToDouble(), Z[n + 1].Imaginary + e.Im.ToDouble());
            if (z.Real * z.Real + z.Imaginary * z.Imaginary >= rs2) return n + 1;
        }
        return 0;
    }

    /// <summary>As <see cref="LocationAngles.TraceOut"/>: the binary digits of the external
    /// angle of the ray through <paramref name="c"/> (an escaping point) as far as its escape
    /// depth, traced outward; null when it does not escape within
    /// <see cref="LocationAngles.MaxProbeDepth"/> or the trace fails.</summary>
    public static string? TraceOut(ExactCentre c, CancellationToken ct = default, int substeps = 4)
    {
        double R = ExternalRay.StartRadius, rs = SMath.Sqrt(R);
        int prec = c.Precision;
        BigFloat x = BigFloat.Zero(prec), y = BigFloat.Zero(prec);
        int nEsc = -1;
        var zs = new List<Complex> { Complex.Zero };
        for (int n = 1; n <= LocationAngles.MaxProbeDepth; n++)
        {
            if ((n & 0xFF) == 0) ct.ThrowIfCancellationRequested();
            var x2 = BigFloat.Mul(x, x, prec); var y2 = BigFloat.Mul(y, y, prec); var xy = BigFloat.Mul(x, y, prec);
            x = BigFloat.Add(BigFloat.Sub(x2, y2, prec), c.Re, prec);
            y = BigFloat.Add(xy.ScaleB(1), c.Im, prec);
            var zn = new Complex(x.ToDouble(), y.ToDouble());
            zs.Add(zn);
            if (zn.Magnitude >= rs) { nEsc = n; break; }
        }
        if (nEsc < 1) return null;
        var Z = zs.ToArray();
        int k = nEsc - 1;
        double phi = Frac01(Z[nEsc].Phase / (2 * SMath.PI));
        CFx delta = CFx.Zero;
        double rNow = Z[nEsc].Magnitude;
        var bits = new char[k];
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (rNow < R)
            {
                double l0 = SMath.Log(rNow), l1 = SMath.Log(R);
                for (int j = 1; j <= substeps; j++)
                {
                    double r = SMath.Exp(l0 + (l1 - l0) * j / substeps);
                    if (!DeepExternalRay.SolveDepth(Z, ref delta, k, Complex.FromPolarCoordinates(r, 2 * SMath.PI * phi)))
                        return null;
                }
            }
            if (k == 0) break;
            CFx e = CFx.Zero;
            for (int n = 0; n < k; n++)
            {
                var Zn = CFx.Of(Z[n].Real, Z[n].Imaginary);
                e = (Zn.Twice() + e) * e + delta;
            }
            var w = new Complex(Z[k].Real + e.Re.ToDouble(), Z[k].Imaginary + e.Im.ToDouble());
            double psi = Frac01(w.Phase / (2 * SMath.PI));
            int bit = CircDist(phi / 2 + 0.5, psi) < CircDist(phi / 2, psi) ? 1 : 0;
            bits[k - 1] = bit == 1 ? '1' : '0';
            phi = (phi + bit) / 2;
            rNow = w.Magnitude;
            k--;
        }
        return new string(bits);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    // Z_0..Z_n at c in BigFloat, stored as double.
    private static Complex[] Orbit(ExactCentre c, int n, int prec, CancellationToken ct)
    {
        var z = new Complex[n + 1];
        BigFloat x = BigFloat.Zero(prec), y = BigFloat.Zero(prec);
        var cx = c.Re.WithPrecision(prec); var cy = c.Im.WithPrecision(prec);
        for (int i = 1; i <= n; i++)
        {
            if ((i & 0xFF) == 0) ct.ThrowIfCancellationRequested();
            var x2 = BigFloat.Mul(x, x, prec); var y2 = BigFloat.Mul(y, y, prec); var xy = BigFloat.Mul(x, y, prec);
            x = BigFloat.Add(BigFloat.Sub(x2, y2, prec), cx, prec);
            y = BigFloat.Add(xy.ScaleB(1), cy, prec);
            z[i] = new Complex(x.ToDouble(), y.ToDouble());
        }
        return z;
    }

    private static Complex Unit(CFx v)
    {
        var m = v.Magnitude();
        return new Complex((v.Re / m).ToDouble(), (v.Im / m).ToDouble());
    }

    private static ExactCentre Add(ExactCentre c, CFx d, int prec)
        => new(BigFloat.Add(c.Re.WithPrecision(prec), BigFloat.FromFloatExp(d.Re, prec), prec),
               BigFloat.Add(c.Im.WithPrecision(prec), BigFloat.FromFloatExp(d.Im, prec), prec));

    private static double DistLog2(ExactCentre a, ExactCentre b)
    {
        int prec = SMath.Max(a.Precision, b.Precision);
        return new CFx(BigFloat.Sub(a.Re.WithPrecision(prec), b.Re.WithPrecision(prec), prec).ToFloatExp(),
                       BigFloat.Sub(a.Im.WithPrecision(prec), b.Im.WithPrecision(prec), prec).ToFloatExp()).Log2Abs();
    }

    private static List<ExternalAngle> Sorted(List<ExternalAngle> list)
    {
        list.Sort((x, y) =>
        {
            var (xn, xd) = x.Fraction; var (yn, yd) = y.Fraction;
            return (xn * yd).CompareTo(yn * xd);
        });
        return list;
    }

    private static double Frac01(double v) { v -= SMath.Floor(v); return v >= 1 ? 0 : v; }

    private static double CircDist(double a, double b)
    {
        double d = SMath.Abs(Frac01(a) - Frac01(b));
        return SMath.Min(d, 1 - d);
    }
}
