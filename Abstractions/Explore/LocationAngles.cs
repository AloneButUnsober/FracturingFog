// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Explore/LocationAngles.cs
//
// Finder S4b (#1237): location → external angles — the angle pair of a found
// minibrot (S2) and the angles of a snapped Misiurewicz point (S3).
//
//  • Component: its root is where the cycle's multiplier reaches 1. Walk the
//    interior coordinate b from 0 (nucleus) to 1 with Newton on
//    (z, c): f^p(z) = z, (f^p)′(z) = b — perturbed about the nucleus orbit, so
//    any depth works. The rays land at the root from outside.
//  • Probes: points a little way from the root (or the Misiurewicz point) in
//    several directions; the escaping ones are traced OUTWARD along their own
//    ray (Newton on c for f^{k+1}(0) = r·e^{2πiφ} with r rising to the escape
//    radius, then k → k−1 and φ ← (φ + b)/2, the bit b chosen so the phase
//    matches f^k(0)), which reads off the binary digits of the probe's angle.
//    The probe's angle agrees with the landing ray's for its first digits.
//  • Each candidate (.(p digits) for a component; .(k−1 digits)(p·n digits)
//    for M(k, p)) is landed with ExternalRay.Land and kept only if it lands
//    back on the same component / point — no candidate is trusted unverified.

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using System.Threading;

using FracturingFog.FFMath;

using SMath = System.Math;

namespace FracturingFog.Abstractions.Explore;

public static class LocationAngles
{
    /// <summary>Probe ring radii, as fractions of the nucleus–root distance.</summary>
    private static readonly double[] ProbeScales = { 0.3, 0.1, 0.03, 0.01 };
    /// <summary>Probes that escape later than this are skipped (outward cost ~ k²).</summary>
    public const int MaxProbeDepth = 20000;
    /// <summary>Candidate angles landed per search, at most.</summary>
    public const int MaxCandidates = 8;

    /// <summary>The verified external angles of the component (one for the
    /// main cardioid, else two, ascending). Empty when none verify.</summary>
    public static List<ExternalAngle> ComponentAngles(in NucleusResult n, CancellationToken ct = default)
    {
        var found = new List<ExternalAngle>();
        if (!n.Found) return found;
        if (n.Period == 1) { found.Add(ExternalAngle.FromBits("", "0")); return found; }

        if (!Root(n.Nucleus, n.Period, out var rootOffset)) return found;
        var root = n.Nucleus.Translate(rootOffset.Real, rootOffset.Imaginary);
        double dist = rootOffset.Magnitude;
        var outward = rootOffset / dist;
        double sizeTol = 0.01 * n.Size.Magnitude;
        // The exterior near a root is thin (a cusp between tangent
        // components), so probe a dense ring at several radii, escape-tested
        // cheaply by perturbation about the root's own (bounded) orbit. Trace
        // the shallowest probes that are still deep enough (≥ ProbeMinDepth
        // periods) first: the outward trace costs ~ depth².
        var Z = ExternalRay.ReferenceOrbit(root, RingMaxDepth, Array.Empty<Complex>());
        var probes = new List<(Complex Off, int Depth)>();
        foreach (double f in ProbeScales)
            for (int a = 0; a < RingDirections; a++)
            {
                ct.ThrowIfCancellationRequested();
                var off = f * dist * outward * Complex.FromPolarCoordinates(1, 2 * SMath.PI * (a + 0.5) / RingDirections);
                int depth = EscapeDepth(Z, off);
                if (depth >= ProbeMinDepth * n.Period) probes.Add((off, depth));
            }
        probes.Sort((x, y) => x.Depth.CompareTo(y.Depth));
        var tried = new HashSet<ExternalAngle>();
        foreach (var (off, _) in probes)
        {
            ct.ThrowIfCancellationRequested();
            if (found.Count >= 2 || tried.Count >= MaxCandidates) break;
            string? bits = TraceOut(root.Translate(off.Real, off.Imaginary), ct);
            if (bits == null || bits.Length < n.Period) continue;
            var cand = ExternalAngle.FromBits("", bits[..n.Period]);
            if (cand.Period != n.Period || !tried.Add(cand)) continue;
            var land = ExternalRay.Land(cand, ct: ct);
            if (land.Status == RayLandingStatus.Component
                && ExternalRay.Dist(land.Nucleus.Nucleus, n.Nucleus) <= sizeTol
                && !found.Contains(cand))
                found.Add(cand);
        }
        return Sorted(found);
    }

    /// <summary>Probe directions per ring.</summary>
    public const int RingDirections = 128;
    /// <summary>Ring probes escaping later than this are not used.</summary>
    public const int RingMaxDepth = 4000;
    /// <summary>A probe must escape no earlier than this many periods.</summary>
    public const int ProbeMinDepth = 4;

    // First n with |Z_n + ε_n| ≥ √R for c = reference + off (perturbed about
    // the reference orbit Z); 0 when it does not escape within the orbit.
    private static int EscapeDepth(Complex[] Z, Complex off)
    {
        double rs2 = ExternalRay.StartRadius;   // (√R)²
        Complex e = Complex.Zero;
        for (int n = 0; n + 1 < Z.Length; n++)
        {
            e = 2.0 * Z[n] * e + e * e + off;
            Complex z = Z[n + 1] + e;
            if (z.Real * z.Real + z.Imaginary * z.Imaginary >= rs2) return n + 1;
        }
        return 0;
    }

    /// <summary>The verified external angles landing at the Misiurewicz point
    /// (preperiod k − 1; period a multiple of p), ascending. <paramref name="scale"/>
    /// is a length the point's surroundings are resolved at (e.g. the view
    /// radius); probes start a little way inside it.</summary>
    public static List<ExternalAngle> MisiurewiczAngles(in MisiurewiczResult m, double scale, CancellationToken ct = default)
    {
        var found = new List<ExternalAngle>();
        if (!m.Found || m.Preperiod < 1 || m.Period < 1) return found;
        int pre = m.Preperiod - 1;
        double tol = 1e-6 * scale;
        var tried = new HashSet<ExternalAngle>();
        foreach (double f in new[] { 1e-3, 1e-5 })
            for (int a = 0; a < 12; a++)
            {
                ct.ThrowIfCancellationRequested();
                if (tried.Count >= 3 * MaxCandidates) return Sorted(found);
                double th = 2 * SMath.PI * (a + 0.5) / 12;
                var probe = m.Point.Translate(f * scale * SMath.Cos(th), f * scale * SMath.Sin(th));
                string? bits = TraceOut(probe, ct);
                if (bits == null) continue;
                for (int mult = 1; mult <= 4; mult++)
                {
                    int per = m.Period * mult;
                    if (bits.Length < pre + per) break;
                    var cand = ExternalAngle.FromBits(bits[..pre], bits.Substring(pre, per));
                    if (!tried.Add(cand) || found.Contains(cand)) continue;
                    var land = ExternalRay.Land(cand, ct: ct);
                    if (land.Status == RayLandingStatus.Misiurewicz
                        && ExternalRay.Dist(land.Misiurewicz.Point, m.Point) <= tol)
                    {
                        found.Add(cand);
                        break;
                    }
                }
            }
        return Sorted(found);
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

    // ── Root of a component ────────────────────────────────────────────────

    /// <summary>The root of the period-p component with this nucleus, as an
    /// offset from the nucleus: walk the interior coordinate b = 0 → 1 with
    /// Newton on (z₀, c) for f^p(z₀) = z₀, (f^p)′(z₀) = b, perturbed about the
    /// nucleus orbit (exact in OD, stored as doubles).</summary>
    public static bool Root(DeepComplex nucleus, int p, out Complex offset, int steps = 32)
    {
        var Z = ExternalRay.ReferenceOrbit(nucleus, p, Array.Empty<Complex>());
        Complex e0 = Complex.Zero, dcOff = Complex.Zero;
        for (int s = 1; s <= steps; s++)
        {
            double b = (double)s / steps;
            bool ok = false;
            // At b = 1 the cycle is parabolic (a double root): Newton slows to
            // linear convergence, so allow more steps and a looser stop there —
            // far tighter than the probe offsets need.
            double tol = s == steps ? 1e-9 : 1e-13;
            for (int it = 0; it < 64; it++)
            {
                Complex e = e0, dz = Complex.One, dc = Complex.Zero, dzz = Complex.Zero, dzc = Complex.Zero;
                for (int n = 0; n < p; n++)
                {
                    Complex zn = Z[n] + e;
                    dzz = 2.0 * (dz * dz + zn * dzz);
                    dzc = 2.0 * (dc * dz + zn * dzc);
                    dz = 2.0 * zn * dz;
                    dc = 2.0 * zn * dc + Complex.One;
                    e = 2.0 * Z[n] * e + e * e + dcOff;
                }
                Complex f1 = Z[p] + e - e0, f2 = dz - b;
                Complex j11 = dz - Complex.One, j12 = dc, j21 = dzz, j22 = dzc;
                Complex det = j11 * j22 - j12 * j21;
                if (det == Complex.Zero || !double.IsFinite(det.Real)) { offset = default; return false; }
                Complex d0 = (f1 * j22 - j12 * f2) / det, d1 = (j11 * f2 - j21 * f1) / det;
                e0 -= d0;
                dcOff -= d1;
                if (!double.IsFinite(dcOff.Real) || !double.IsFinite(e0.Real)) { offset = default; return false; }
                if (d1.Magnitude <= 1e-15 * dcOff.Magnitude && d0.Magnitude <= 1e-15 * SMath.Max(e0.Magnitude, 1e-300)) { ok = true; break; }
                if (d1.Magnitude <= tol * dcOff.Magnitude) ok = true;
            }
            if (!ok) { offset = default; return false; }
        }
        offset = dcOff;
        return dcOff != Complex.Zero;
    }

    // ── Outward ray trace ─────────────────────────────────────────────────

    /// <summary>The binary digits b₀b₁… of the external angle of the ray
    /// through <paramref name="c"/> (an escaping point), as far as its escape
    /// depth, by tracing the ray outward; null when c does not escape within
    /// <see cref="MaxProbeDepth"/> iterations or the trace fails.</summary>
    public static string? TraceOut(DeepComplex c, CancellationToken ct = default, int substeps = 4)
    {
        double R = ExternalRay.StartRadius, rs = SMath.Sqrt(R);
        // Escape depth: first n with |z_n| ≥ √R, from an exact OD orbit.
        OD x = new(0), y = new(0);
        int nEsc = -1;
        var zs = new List<Complex> { Complex.Zero };
        for (int n = 1; n <= MaxProbeDepth; n++)
        {
            (x, y) = OdExact.SquareAdd(x, y, c.Re, c.Im);
            var zn = new Complex(x.X0, y.X0);
            zs.Add(zn);
            if (zn.Magnitude >= rs) { nEsc = n; break; }
        }
        if (nEsc < 1) return null;
        var Z = zs.ToArray();
        int k = nEsc - 1;
        double phi = Frac01(Z[nEsc].Phase / (2 * SMath.PI));
        Complex delta = Complex.Zero;
        double rNow = Z[nEsc].Magnitude;
        var bits = new char[k];
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            // Raise the radius to R at depth k (log-spaced substeps).
            if (rNow < R)
            {
                double l0 = SMath.Log(rNow), l1 = SMath.Log(R);
                for (int j = 1; j <= substeps; j++)
                {
                    double r = SMath.Exp(l0 + (l1 - l0) * j / substeps);
                    if (!ExternalRay.SolveDepth(Z, ref delta, k, Complex.FromPolarCoordinates(r, 2 * SMath.PI * phi)))
                        return null;
                }
            }
            if (k == 0) break;
            // f^k(0) at the current point: its phase picks the digit b_{k−1}.
            Complex e = Complex.Zero;
            for (int n = 0; n < k; n++) e = 2.0 * Z[n] * e + e * e + delta;
            Complex w = Z[k] + e;
            double psi = Frac01(w.Phase / (2 * SMath.PI));
            int bit = CircDist(phi / 2 + 0.5, psi) < CircDist(phi / 2, psi) ? 1 : 0;
            bits[k - 1] = bit == 1 ? '1' : '0';
            phi = (phi + bit) / 2;
            rNow = w.Magnitude;
            k--;
        }
        return new string(bits);
    }

    private static double Frac01(double v) { v -= SMath.Floor(v); return v >= 1 ? 0 : v; }

    private static double CircDist(double a, double b)
    {
        double d = SMath.Abs(Frac01(a) - Frac01(b));
        return SMath.Min(d, 1 - d);
    }
}
