// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Engine/CosmicWeb/Fft3D.cs (#2022, Cosmic Web epic #2020)
//
// In-place complex FFT of an N x N x N grid (N a power of two), as three passes of 1D transforms
// along x, y and z. Each 1D transform is the iterative radix-2 decimation-in-time algorithm of
// Cooley & Tukey ("An algorithm for the machine calculation of complex Fourier series", Math. Comp.
// 19, 297-301, 1965): bit-reversal permutation, then log2 N butterfly stages. Lines are independent,
// so they run in parallel; every line is computed the same way whatever the thread count, so the
// result is deterministic.
//
// Convention: Forward computes X_k = sum_m x_m exp(-2 pi i k m / N) per axis (no scaling);
// Inverse uses exp(+...) and divides by N^3, so Inverse(Forward(x)) = x.
// Layout: index = (z * N + y) * N + x, real and imaginary parts in separate arrays.

using System;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace FracturingFog.CosmicWeb;

public static class Fft3D
{
    public static bool IsPowerOfTwo(int n) => n > 0 && (n & (n - 1)) == 0;

    public static void Forward(double[] re, double[] im, int n, CancellationToken ct = default) => Transform(re, im, n, -1, ct);

    public static void Inverse(double[] re, double[] im, int n, CancellationToken ct = default)
    {
        Transform(re, im, n, +1, ct);
        double s = 1.0 / ((double)n * n * n);
        Parallel.For(0, n, new ParallelOptions { CancellationToken = ct }, z =>
        {
            int o = z * n * n;
            for (int i = o; i < o + n * n; i++) { re[i] *= s; im[i] *= s; }
        });
    }

    private static void Transform(double[] re, double[] im, int n, int sign, CancellationToken ct)
    {
        if (!IsPowerOfTwo(n)) throw new ArgumentException("FFT size must be a power of two.", nameof(n));
        if (re.Length != n * n * n || im.Length != re.Length) throw new ArgumentException("Grid size mismatch.");
        var (cosT, sinT) = Twiddles(n, sign);
        var rev = BitReverse(n);
        var po = new ParallelOptions { CancellationToken = ct };
        // Axis x: stride 1; axis y: stride n; axis z: stride n^2. One task per (other two indices) plane row.
        foreach (int stride in new[] { 1, n, n * n })
        {
            Parallel.For(0, n * n, po, () => (new double[n], new double[n]), (line, _, buf) =>
            {
                int a = line / n, b = line % n;
                int start = stride switch
                {
                    1 => (a * n + b) * n,          // fixed z = a, y = b
                    _ when stride == n => a * n * n + b,      // fixed z = a, x = b
                    _ => a * n + b,                // fixed y = a, x = b
                };
                var (lr, li) = buf;
                for (int k = 0; k < n; k++) { int idx = start + k * stride; lr[rev[k]] = re[idx]; li[rev[k]] = im[idx]; }
                Butterflies(lr, li, n, cosT, sinT);
                for (int k = 0; k < n; k++) { int idx = start + k * stride; re[idx] = lr[k]; im[idx] = li[k]; }
                return buf;
            }, _ => { });
            ct.ThrowIfCancellationRequested();
        }
    }

    /// <summary>In-place radix-2 butterflies on a bit-reversed line.</summary>
    private static void Butterflies(double[] r, double[] i, int n, double[] cosT, double[] sinT)
    {
        for (int len = 2; len <= n; len <<= 1)
        {
            int half = len >> 1, step = n / len;
            for (int s = 0; s < n; s += len)
            {
                for (int k = 0; k < half; k++)
                {
                    double wr = cosT[k * step], wi = sinT[k * step];
                    int p = s + k, q = p + half;
                    double tr = wr * r[q] - wi * i[q];
                    double ti = wr * i[q] + wi * r[q];
                    r[q] = r[p] - tr; i[q] = i[p] - ti;
                    r[p] += tr; i[p] += ti;
                }
            }
        }
    }

    private static (double[] Cos, double[] Sin) Twiddles(int n, int sign)
    {
        var c = new double[n / 2];
        var s = new double[n / 2];
        for (int k = 0; k < n / 2; k++)
        {
            double a = sign * 2.0 * Math.PI * k / n;
            c[k] = Math.Cos(a);
            s[k] = Math.Sin(a);
        }
        return (c, s);
    }

    private static int[] BitReverse(int n)
    {
        int bits = BitOperations.Log2((uint)n);
        var r = new int[n];
        for (int k = 0; k < n; k++)
        {
            int v = 0;
            for (int b = 0; b < bits; b++) if ((k & (1 << b)) != 0) v |= 1 << (bits - 1 - b);
            r[k] = v;
        }
        return r;
    }

    /// <summary>Signed frequency of grid index <paramref name="k"/> (0..n-1): k for k &lt;= n/2, else k - n.</summary>
    public static int Freq(int k, int n) => k <= n / 2 ? k : k - n;
}
