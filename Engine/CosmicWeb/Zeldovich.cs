// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Engine/CosmicWeb/Zeldovich.cs (#2022, Cosmic Web epic #2020)
//
// The Zel'dovich approximation (Ya. B. Zeldovich, A&A 5, 84-89, 1970) in a periodic box: particles start
// on a uniform grid at Lagrangian positions q and move along straight lines,
//   x(q) = q + D s(q),   with  div s = -delta_L,  s = -grad phi,  laplacian phi = delta_L,
// where delta_L is the linear density contrast and D the growth factor. In Fourier space
// s_k = i k delta_k / k^2. Where the map q -> x folds over, sheets ("pancakes") and then filaments form.
// This is his Eq. 1, r = a(t) q + b(t) p(q), in comoving units (divided by a): D = b/a, s = p. His Eq. 2,
// rho (a - alpha b)(a - beta b)(a - gamma b) = rho_mean a^3 (alpha, beta, gamma the eigenvalues of the
// deformation), linearised gives delta = -D div s. Checked by the tests: div s = -delta exactly in
// Fourier space, and the particle density contrast = D delta_L at small D. Read from page 84 of the paper.
//
// The linear field (after E. Bertschinger, "Multiscale Gaussian random fields for cosmological
// simulations", ApJS 137, 1, 2001, Sec. II.1, Eqs. 7-9): Gaussian white noise on the grid, Fourier
// transformed, multiplied by the transfer function T(k) = sqrt(P(k)), transformed back. FF uses
// P(k) proportional to k^n times a Gaussian smoothing exp(-k^2 R^2) (so T has exp(-k^2 R^2 / 2)), with
// the k = 0 mode removed. White noise from Box-Muller (Box & Muller, Ann. Math. Statist. 29, 610,
// 1958) on SplitMix64 streams, one per z plane, so the field does not depend on the thread count.
//
// Normalisation (FF's own): the field is scaled so its rms over the grid is exactly 1, so D is the rms
// linear density contrast: shell crossing starts around D ~ 1. Lengths in grid cells; the calculator
// scales the grid to the box.
//
// FFT: Fft3D (radix-2 Cooley-Tukey). At N = 256 a --batch render peaked at 1.8 GB (process working set).

using System;
using System.Threading;
using System.Threading.Tasks;

namespace FracturingFog.CosmicWeb;

/// <summary>#2022 — what changes the linear field and its displacement (the field cache key).</summary>
public sealed record ZeldovichFieldSettings(int Seed, int Grid, double Index, double Smoothing);

/// <summary>#2022 — the linear field and its Zel'dovich displacement at D = 1, both on the N^3 grid.</summary>
public sealed class ZeldovichField
{
    public int N { get; init; }
    /// <summary>Linear density contrast, rms 1.</summary>
    public float[] Delta { get; init; } = Array.Empty<float>();
    /// <summary>Displacement components in grid cells at D = 1 (index (z N + y) N + x).</summary>
    public float[] Sx { get; init; } = Array.Empty<float>();
    public float[] Sy { get; init; } = Array.Empty<float>();
    public float[] Sz { get; init; } = Array.Empty<float>();
}

public static class Zeldovich
{
    public const int MinGrid = 16, MaxGrid = 256;

    /// <summary>Grid size: a power of two in [16, 256].</summary>
    public static int SanitiseGrid(int n)
    {
        n = Math.Clamp(n, MinGrid, MaxGrid);
        int p = MinGrid;
        while (p * 2 <= n) p *= 2;
        return p;
    }

    public static ZeldovichFieldSettings Sanitise(ZeldovichFieldSettings s) => s with
    {
        Grid = SanitiseGrid(s.Grid),
        Index = double.IsFinite(s.Index) ? Math.Clamp(s.Index, -3.0, 4.0) : -1.0,
        Smoothing = double.IsFinite(s.Smoothing) ? Math.Clamp(s.Smoothing, 0.0, 32.0) : 1.0,
    };

    /// <summary>|k| in radians per grid cell of grid index (x, y, z).</summary>
    public static double WaveNumber(int x, int y, int z, int n)
    {
        double f = 2 * Math.PI / n;
        double kx = Fft3D.Freq(x, n) * f, ky = Fft3D.Freq(y, n) * f, kz = Fft3D.Freq(z, n) * f;
        return Math.Sqrt(kx * kx + ky * ky + kz * kz);
    }

    /// <summary>The transfer function T(k) = sqrt(k^n exp(-k^2 R^2)); 0 at k = 0.</summary>
    public static double Transfer(double k, double index, double smoothing)
        => k <= 0 ? 0.0 : Math.Sqrt(Math.Pow(k, index) * Math.Exp(-k * k * smoothing * smoothing));

    /// <summary>Gaussian white noise, unit variance, one SplitMix64 stream per z plane.</summary>
    public static double[] WhiteNoise(int seed, int n, CancellationToken ct = default)
    {
        var w = new double[n * n * n];
        Parallel.For(0, n, new ParallelOptions { CancellationToken = ct }, z =>
        {
            var rng = new SplitMix64(SplitMix64.Mix(unchecked((ulong)(uint)seed ^ 0x2E1D0717UL), (ulong)z));
            int o = z * n * n;
            for (int i = 0; i < n * n; i += 2)
            {
                // Box-Muller: two independent unit normals from two uniforms.
                double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble();
                double r = Math.Sqrt(-2.0 * Math.Log(u1)), a = 2.0 * Math.PI * u2;
                w[o + i] = r * Math.Cos(a);
                if (i + 1 < n * n) w[o + i + 1] = r * Math.Sin(a);
            }
        });
        return w;
    }

    /// <summary>Builds the linear field (rms 1) and its displacement at D = 1.</summary>
    public static ZeldovichField Build(ZeldovichFieldSettings settings, CancellationToken ct = default)
    {
        var s = Sanitise(settings);
        int n = s.Grid, total = n * n * n;
        var po = new ParallelOptions { CancellationToken = ct };

        // White noise -> Fourier space -> times T(k).
        var dRe = WhiteNoise(s.Seed, n, ct);
        var dIm = new double[total];
        Fft3D.Forward(dRe, dIm, n, ct);
        Parallel.For(0, n, po, z =>
        {
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    int i = (z * n + y) * n + x;
                    double t = Transfer(WaveNumber(x, y, z, n), s.Index, s.Smoothing);
                    dRe[i] *= t; dIm[i] *= t;
                }
        });

        // Normalise to rms 1: with the inverse's 1/N^3, sum_x delta^2 = N^-3 sum_k |delta_k|^2 (Parseval),
        // so the mean of delta^2 is N^-6 sum_k |delta_k|^2.
        double power = 0;
        for (int i = 0; i < total; i++) power += dRe[i] * dRe[i] + dIm[i] * dIm[i];
        double rms = Math.Sqrt(power) / ((double)n * n * n);
        double norm = rms > 0 ? 1.0 / rms : 0.0;
        for (int i = 0; i < total; i++) { dRe[i] *= norm; dIm[i] *= norm; }

        var delta = InverseReal(dRe, dIm, n, (re, im, x, y, z) => (re, im), ct);
        var sx = InverseReal(dRe, dIm, n, (re, im, x, y, z) => Displace(re, im, x, y, z, n, 0), ct);
        var sy = InverseReal(dRe, dIm, n, (re, im, x, y, z) => Displace(re, im, x, y, z, n, 1), ct);
        var sz = InverseReal(dRe, dIm, n, (re, im, x, y, z) => Displace(re, im, x, y, z, n, 2), ct);
        return new ZeldovichField { N = n, Delta = delta, Sx = sx, Sy = sy, Sz = sz };
    }

    /// <summary>s_k = i k_j delta_k / k^2 for component <paramref name="axis"/>. k_j is 0 on the Nyquist plane
    /// of that axis (its sign is ambiguous there, and a real field needs it to vanish).</summary>
    private static (double Re, double Im) Displace(double re, double im, int x, int y, int z, int n, int axis)
    {
        int idx = axis == 0 ? x : axis == 1 ? y : z;
        if (idx == n / 2) return (0, 0);
        double k = WaveNumber(x, y, z, n);
        if (k <= 0) return (0, 0);
        double kj = Fft3D.Freq(idx, n) * 2 * Math.PI / n;
        double c = kj / (k * k);
        return (-im * c, re * c);   // i c (re + i im)
    }

    private delegate (double Re, double Im) Mode(double re, double im, int x, int y, int z);

    /// <summary>Applies <paramref name="mode"/> to a copy of the spectrum and returns the real part of its inverse.</summary>
    private static float[] InverseReal(double[] kRe, double[] kIm, int n, Mode mode, CancellationToken ct)
    {
        int total = n * n * n;
        var re = new double[total];
        var im = new double[total];
        Parallel.For(0, n, new ParallelOptions { CancellationToken = ct }, z =>
        {
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    int i = (z * n + y) * n + x;
                    (re[i], im[i]) = mode(kRe[i], kIm[i], x, y, z);
                }
        });
        Fft3D.Inverse(re, im, n, ct);
        var o = new float[total];
        for (int i = 0; i < total; i++) o[i] = (float)re[i];
        return o;
    }

    /// <summary>Particle positions x = q + D s(q) in a box of side <paramref name="box"/> (periodic), q on the grid nodes.</summary>
    public static CosmicWebPoints Displace(ZeldovichField f, double growth, double box, CancellationToken ct = default)
    {
        int n = f.N, total = n * n * n;
        double cell = box / n;
        double d = double.IsFinite(growth) ? growth : 0.0;
        var xyz = new float[3 * total];
        Parallel.For(0, n, new ParallelOptions { CancellationToken = ct }, z =>
        {
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    int i = (z * n + y) * n + x;
                    xyz[3 * i] = (float)SoneiraPeebles.Wrap((x + d * f.Sx[i]) * cell, box);
                    xyz[3 * i + 1] = (float)SoneiraPeebles.Wrap((y + d * f.Sy[i]) * cell, box);
                    xyz[3 * i + 2] = (float)SoneiraPeebles.Wrap((z + d * f.Sz[i]) * cell, box);
                }
        });
        return new CosmicWebPoints { Xyz = xyz, Count = total, BoxSize = box, ClumpsRequested = 0, ClumpsUsed = 0, Capped = false };
    }
}
