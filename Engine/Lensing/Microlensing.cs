// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Microlensing.cs (#1948, microlensing magnification maps)
//
// Gravitational microlensing of a background source by the stars of a lensing galaxy, by
// INVERSE RAY SHOOTING (Kayser, Refsdal & Stabell 1986): a dense uniform grid of rays is shot
// through the lens plane, each ray is mapped to the source plane by the lens equation, and the
// rays landing in each source-plane pixel are counted. Counts per pixel, divided by the count an
// unlensed pixel would get, are the magnification. Caustics (where the map folds) show as bright
// curves with cusps. NOT a fractal: the networks are piecewise smooth, without self-similarity.
//
// Lens equation, dimensionless (lengths in Einstein radii of a unit mass), N point masses m_i at
// x_i, a smooth convergence κ_c and an external shear γ along the axes (Schneider, Ehlers &
// Falco 1992, the point-mass lens with convergence and shear):
//
//     y1 = (1 − κ_c − γ) x1 − Σ m_i (x1 − x_i1) / |x − x_i|²
//     y2 = (1 − κ_c + γ) x2 − Σ m_i (x2 − x_i2) / |x − x_i|²
//
// Star field (FF's own set-up): stars uniformly in a disc of radius R about the origin until
// their total mass reaches κ_* R² (a uniform disc of point masses with that mass has mean
// convergence κ_*), masses equal (1) or uniform in [m_min, 1], positions from the seed. A single
// lens (one unit mass at the origin, no field) is the check case: its magnification is
// Paczyński's A(u) = (u² + 2) / (u √(u² + 4)) (ApJ 304, 1, 1986). Over a large region the mean
// magnification is 1 / ((1 − κ)² − γ²), κ = κ_* + κ_c (Schneider, Ehlers & Falco 1992).
//
// Shooting region (FF's own): the preimage of the source view under the mean map
// diag(1 − κ − γ, 1 − κ + γ), padded by `pad` Einstein radii on each side. Rays from further out
// that stars scatter into the view are lost; measured for κ_* = 0.3, κ_c = 0.1, γ = 0.1 the mean
// magnification comes out about 13% low at pad 2 and 4% at pad 6 (the lost share falls roughly
// as 1 / pad), so the default pad is 4. Ray spacing = pitch / √(rays per pixel), so an unlensed pixel gets
// `rays per pixel` rays; the total is capped, raising the spacing. Each ray is jittered inside its
// cell by a hash of its cell and the seed (no moiré, still deterministic); counts are integers, so
// the parallel sum is exact in any order.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.Models;

namespace FracturingFog.Lensing;

/// <summary>#1948 — one lens field: stars, smooth convergence and shear.</summary>
public sealed class MicrolensField
{
    public double[] X { get; }
    public double[] Y { get; }
    public double[] M { get; }
    public double KappaStars { get; }
    public double KappaSmooth { get; }
    public double Shear { get; }
    public double FieldRadius { get; }
    public int Count => M.Length;
    /// <summary>Total convergence κ = κ_* + κ_c.</summary>
    public double Kappa => KappaStars + KappaSmooth;

    public const int MaxStars = 20000;

    public MicrolensField(double[] x, double[] y, double[] m, double kappaStars, double kappaSmooth, double shear, double radius)
    {
        X = x; Y = y; M = m; KappaStars = kappaStars; KappaSmooth = kappaSmooth; Shear = shear; FieldRadius = radius;
    }

    public static MicrolensField Build(FractalParameters p)
    {
        double ks = double.IsFinite(p.MicroKappaSmooth) ? Math.Clamp(p.MicroKappaSmooth, -5, 5) : 0;
        double g = double.IsFinite(p.MicroShear) ? Math.Clamp(p.MicroShear, -5, 5) : 0;
        if (p.MicroLayout == MicrolensLayout.Single)
            return new MicrolensField(new[] { 0.0 }, new[] { 0.0 }, new[] { 1.0 }, 0, ks, g, 0);
        double kstar = double.IsFinite(p.MicroKappaStars) ? Math.Clamp(p.MicroKappaStars, 0, 5) : 0.4;
        double R = double.IsFinite(p.MicroFieldRadius) ? Math.Clamp(p.MicroFieldRadius, 1, 200) : 12;
        double mmin = double.IsFinite(p.MicroMassMin) ? Math.Clamp(p.MicroMassMin, 1e-3, 1) : 0.1;
        double target = kstar * R * R;
        var rng = new Random(p.MicroSeed);
        var xs = new List<double>(); var ys = new List<double>(); var ms = new List<double>();
        double total = 0;
        while (total < target && ms.Count < MaxStars)
        {
            double m = p.MicroMassFunction == MicrolensMassFunction.UniformRange ? mmin + (1 - mmin) * rng.NextDouble() : 1.0;
            double r = R * Math.Sqrt(rng.NextDouble()), t = 2 * Math.PI * rng.NextDouble();
            xs.Add(r * Math.Cos(t)); ys.Add(r * Math.Sin(t)); ms.Add(m);
            total += m;
        }
        // Rescale the masses so the field's convergence is κ_* exactly (the last star overshoots).
        if (total > 0) for (int i = 0; i < ms.Count; i++) ms[i] *= target / total;
        return new MicrolensField(xs.ToArray(), ys.ToArray(), ms.ToArray(), kstar, ks, g, R);
    }

    /// <summary>The lens equation: source position of the ray through lens-plane point (x1, x2).</summary>
    public (double Y1, double Y2) Map(double x1, double x2)
    {
        double s1 = 0, s2 = 0;
        var X = this.X; var Y = this.Y; var M = this.M;
        for (int i = 0; i < M.Length; i++)
        {
            double d1 = x1 - X[i], d2 = x2 - Y[i];
            double r2 = d1 * d1 + d2 * d2;
            if (r2 <= 0) return (double.NaN, double.NaN);
            double k = M[i] / r2;
            s1 += k * d1; s2 += k * d2;
        }
        return ((1 - KappaSmooth - Shear) * x1 - s1, (1 - KappaSmooth + Shear) * x2 - s2);
    }

    /// <summary>Mean magnification 1 / ((1 − κ)² − γ²) (Schneider, Ehlers &amp; Falco 1992).</summary>
    public double MeanMagnification => 1.0 / ((1 - Kappa) * (1 - Kappa) - Shear * Shear);
}

/// <summary>#1948 — the result of one ray-shooting pass.</summary>
public sealed class MicrolensShot
{
    public int[] Counts { get; init; } = Array.Empty<int>();
    /// <summary>Lens-plane ray spacing (rays sit on a grid of this pitch, jittered within cells).</summary>
    public double Spacing { get; init; }
    public long RaysShot { get; init; }
    public long RaysLanded { get; init; }
    /// <summary>Source-plane pixel pitch.</summary>
    public double Pitch { get; init; }
    /// <summary>True when the shooting region reaches past the star disc (edge effects: raise the field radius).</summary>
    public bool OutsideField { get; init; }
    /// <summary>Magnification of a pixel from its count: count · spacing² / pitch².</summary>
    public double MagnificationOf(int count) => count * Spacing * Spacing / (Pitch * Pitch);
}

/// <summary>#1948 — inverse ray shooting (see the file header).</summary>
public static class MicrolensShooter
{
    public const long MaxRays = 300_000_000;

    /// <summary>Shoot rays for a <paramref name="w"/> x <paramref name="h"/> source view centred on
    /// view point (<paramref name="cx"/>, <paramref name="cy"/>) (the 2D convention: source b = −(cy + ...)).</summary>
    public static MicrolensShot Shoot(MicrolensField f, int w, int h, double cx, double cy, double zoom,
                                      double raysPerPixel, double pad, int seed, CancellationToken ct = default)
    {
        double pitch = FracturingFog.ViewState.ViewCamera.PixelPitch(w, h, zoom);
        double rpp = double.IsFinite(raysPerPixel) ? Math.Clamp(raysPerPixel, 0.01, 10000) : 8;
        pad = double.IsFinite(pad) ? Math.Clamp(pad, 0, 100) : 1;
        // Source window (world, b up): a in [a0, a1], b in [b0, b1].
        double a0 = cx - 0.5 * w * pitch, a1 = cx + 0.5 * w * pitch;
        double b1 = -(cy - 0.5 * h * pitch), b0 = -(cy + 0.5 * h * pitch);
        // Preimage under the mean map, padded.
        double k = f.Kappa;
        double lx = Clamp1(1 - k - f.Shear), ly = Clamp1(1 - k + f.Shear);
        double xs0 = Math.Min(a0 / lx, a1 / lx) - pad, xs1 = Math.Max(a0 / lx, a1 / lx) + pad;
        double ys0 = Math.Min(b0 / ly, b1 / ly) - pad, ys1 = Math.Max(b0 / ly, b1 / ly) + pad;
        double spacing = pitch / Math.Sqrt(rpp);
        double nx = Math.Ceiling((xs1 - xs0) / spacing), ny = Math.Ceiling((ys1 - ys0) / spacing);
        if (nx * ny > MaxRays)
        {
            spacing *= Math.Sqrt(nx * ny / MaxRays);
            nx = Math.Ceiling((xs1 - xs0) / spacing); ny = Math.Ceiling((ys1 - ys0) / spacing);
        }
        int NX = (int)nx, NY = (int)ny;
        var counts = new int[w * h];
        long landed = 0;
        double invPitch = 1.0 / pitch;
        Parallel.For(0, NY, new ParallelOptions { CancellationToken = ct }, j =>
        {
            long local = 0;
            for (int i = 0; i < NX; i++)
            {
                var (jx, jy) = Jitter(i, j, seed);
                double x1 = xs0 + (i + jx) * spacing, x2 = ys0 + (j + jy) * spacing;
                var (y1, y2) = f.Map(x1, x2);
                if (!double.IsFinite(y1) || !double.IsFinite(y2)) continue;
                double px = (y1 - cx) * invPitch + 0.5 * w;
                double py = (-y2 - cy) * invPitch + 0.5 * h;
                if (px < 0 || py < 0 || px >= w || py >= h) continue;
                int ix = (int)px, iy = (int)py;
                Interlocked.Increment(ref counts[iy * w + ix]);
                local++;
            }
            Interlocked.Add(ref landed, local);
        });
        // The shooting region should lie inside the star disc, or its outer part sees no stars.
        double reach = Math.Max(Math.Max(Math.Abs(xs0), Math.Abs(xs1)), Math.Max(Math.Abs(ys0), Math.Abs(ys1)));
        return new MicrolensShot
        {
            Counts = counts, Spacing = spacing, RaysShot = (long)NX * NY, RaysLanded = landed, Pitch = pitch,
            OutsideField = f.FieldRadius > 0 && reach > f.FieldRadius,
        };
    }

    private static double Clamp1(double v) => Math.Abs(v) < 0.05 ? (v < 0 ? -0.05 : 0.05) : v;

    /// <summary>A deterministic offset in [0, 1)² for ray cell (i, j).</summary>
    public static (double, double) Jitter(int i, int j, int seed)
    {
        ulong h = (ulong)(uint)i * 0x9E3779B97F4A7C15UL ^ ((ulong)(uint)j * 0xC2B2AE3D27D4EB4FUL) ^ ((ulong)(uint)seed * 0x165667B19E3779F9UL);
        h ^= h >> 33; h *= 0xFF51AFD7ED558CCDUL; h ^= h >> 33; h *= 0xC4CEB9FE1A85EC53UL; h ^= h >> 33;
        return ((h & 0xFFFFFFFF) / 4294967296.0, (h >> 32) / 4294967296.0);
    }
}
