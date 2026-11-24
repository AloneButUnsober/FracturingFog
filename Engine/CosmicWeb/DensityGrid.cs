// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// DensityGrid.cs (#2024, Cosmic Web epic #2020)
//
// The particles deposited on a periodic G^3 density grid for the volume renderer.
//
// Deposit: cloud-in-cell (CIC) assignment, each particle a uniform cube one cell wide whose mass
// is shared among the 8 nearest cell centres by the overlap volumes (trilinear weights). CIC is the
// "clouds-in-cells" scheme of Birdsall & Fuss (J. Comput. Phys. 3, 494, 1969) as used in particle
// simulation codes; FF does not claim it. FF's own choices: the weights are fixed-point integers
// that sum exactly to MassOne per particle (the eighth weight takes the remainder), added with
// atomic integer adds, so the grid is the same for any thread count or order and its total is
// exactly count * MassOne (the mass-conservation check of #2024).
//
// Smoothing (optional): a separable periodic Gaussian of sigma cells on the density.
// Mapping: v = log(1 + rho / rho_mean) / log(1 + rho_max / rho_mean) in [0, 1], the log-density
// display the point splat uses, as floats for the raymarch (CPU and GPU sample the same array).

using System;
using System.Threading;
using System.Threading.Tasks;

namespace FracturingFog.CosmicWeb;

/// <summary>#2024 — a deposited, mapped density grid (x fastest: index (z G + y) G + x).</summary>
public sealed class CosmicWebDensityGrid
{
    public int Grid { get; init; }
    /// <summary>log(1 + rho/mean) / log(1 + max/mean) per cell, 0..1.</summary>
    public float[] Value { get; init; } = Array.Empty<float>();
    public double MaxOverMean { get; init; }
    public int Particles { get; init; }
}

public static class DensityGrid
{
    /// <summary>Fixed-point mass of one particle.</summary>
    public const long MassOne = 1L << 20;

    /// <summary>A power of two in 32..256 (nearest at or below, floor 32).</summary>
    public static int SanitiseGrid(int g)
    {
        int c = 32;
        while (c * 2 <= g && c < 256) c *= 2;
        return c;
    }

    /// <summary>CIC deposit: per-cell mass in units of 1/MassOne particles. Sum = count * MassOne exactly.</summary>
    public static long[] DepositCic(CosmicWebPoints pts, int g, CancellationToken ct = default)
    {
        var mass = new long[(long)g * g * g];
        double scale = g / pts.BoxSize;
        var xyz = pts.Xyz;
        const int chunk = 1 << 15;
        int chunks = (pts.Count + chunk - 1) / chunk;
        Parallel.For(0, chunks, new ParallelOptions { CancellationToken = ct }, c =>
        {
            Span<long> w = stackalloc long[8];
            Span<int> idx = stackalloc int[8];
            int end = Math.Min(pts.Count, (c + 1) * chunk);
            for (int i = c * chunk; i < end; i++)
            {
                // Cell centres at (k + 0.5) / g of the box: u = x g / box - 0.5.
                double ux = xyz[3 * i] * scale - 0.5, uy = xyz[3 * i + 1] * scale - 0.5, uz = xyz[3 * i + 2] * scale - 0.5;
                int ix = (int)Math.Floor(ux), iy = (int)Math.Floor(uy), iz = (int)Math.Floor(uz);
                double fx = ux - ix, fy = uy - iy, fz = uz - iz;
                long sum = 0;
                int n = 0;
                for (int dz = 0; dz < 2; dz++)
                {
                    double wz = dz == 0 ? 1 - fz : fz;
                    int z = Wrap(iz + dz, g);
                    for (int dy = 0; dy < 2; dy++)
                    {
                        double wy = dy == 0 ? 1 - fy : fy;
                        int y = Wrap(iy + dy, g);
                        for (int dx = 0; dx < 2; dx++)
                        {
                            double wx = dx == 0 ? 1 - fx : fx;
                            int x = Wrap(ix + dx, g);
                            idx[n] = (z * g + y) * g + x;
                            if (n < 7) { w[n] = (long)Math.Round(wx * wy * wz * MassOne); sum += w[n]; }
                            n++;
                        }
                    }
                }
                w[7] = MassOne - sum;   // exact total per particle
                for (int k = 0; k < 8; k++) if (w[k] != 0) Interlocked.Add(ref mass[idx[k]], w[k]);
            }
        });
        return mass;
    }

    private static int Wrap(int i, int g) { i %= g; return i < 0 ? i + g : i; }

    /// <summary>Separable periodic Gaussian smoothing (sigma in cells) of a G^3 float field, in place.</summary>
    public static void Smooth(float[] f, int g, double sigma, CancellationToken ct = default)
    {
        if (!(sigma > 0.05)) return;
        int r = Math.Min(g / 2 - 1, (int)Math.Ceiling(3 * sigma));
        var k = new float[2 * r + 1];
        double s = 0;
        for (int i = -r; i <= r; i++) { double v = Math.Exp(-0.5 * i * i / (sigma * sigma)); k[i + r] = (float)v; s += v; }
        for (int i = 0; i < k.Length; i++) k[i] = (float)(k[i] / s);
        for (int axis = 0; axis < 3; axis++)
        {
            int stride = axis == 0 ? 1 : axis == 1 ? g : g * g;
            var src = (float[])f.Clone();
            // One line per (other two coordinates).
            Parallel.For(0, g * g, new ParallelOptions { CancellationToken = ct }, line =>
            {
                int a = line % g, b = line / g;
                int baseIdx = axis switch
                {
                    0 => (b * g + a) * g,        // x line at y = a, z = b
                    1 => b * g * g + a,          // y line at x = a, z = b
                    _ => b * g + a,              // z line at x = a, y = b
                };
                for (int i = 0; i < g; i++)
                {
                    float acc = 0;
                    for (int j = -r; j <= r; j++) acc += k[j + r] * src[baseIdx + Wrap(i + j, g) * stride];
                    f[baseIdx + i * stride] = acc;
                }
            });
        }
    }

    /// <summary>Deposit, smooth and map a point set (the volume renderer's input).</summary>
    public static CosmicWebDensityGrid Build(CosmicWebPoints pts, int grid, double smoothing, CancellationToken ct = default)
    {
        int g = SanitiseGrid(grid);
        var mass = DepositCic(pts, g, ct);
        long cells = (long)g * g * g;
        double mean = pts.Count > 0 ? (double)pts.Count * MassOne / cells : 1.0;
        var rho = new float[cells];
        for (long i = 0; i < cells; i++) rho[i] = (float)(mass[i] / mean);
        Smooth(rho, g, smoothing, ct);
        float max = 0;
        foreach (float v in rho) if (v > max) max = v;
        double inv = max > 0 ? 1.0 / Math.Log(1.0 + max) : 0.0;
        Parallel.For(0, g, new ParallelOptions { CancellationToken = ct }, z =>
        {
            long o = (long)z * g * g;
            for (long i = o; i < o + (long)g * g; i++) rho[i] = (float)(Math.Log(1.0 + Math.Max(0f, rho[i])) * inv);
        });
        return new CosmicWebDensityGrid { Grid = g, Value = rho, MaxOverMean = max, Particles = pts.Count };
    }
}
