// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1608 — Gaussian Prime Hits. Inside the main cardioid the attracting cycle is the
// fixed point z* = (1 − √(1 − 4c)) / 2, so the window holds only (copies of) z* and
// the hit fraction is 1 or 0 by whether round(6·z*) is a Gaussian prime — checked
// here from the closed form and an independent primality test, not the theme's grid.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;

using FracturingFog.Interefaces;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class GaussianPrimeHitsThemeTests
{
    private const int W = 160, H = 120;

    private static bool IsPrime(long n)
    {
        if (n < 2) return false;
        for (long d = 2; d * d <= n; d++) if (n % d == 0) return false;
        return true;
    }

    private static bool GaussianPrime(long a, long b)
    {
        a = Math.Abs(a); b = Math.Abs(b);
        return a != 0 && b != 0 ? IsPrime(a * a + b * b) : a + b != 0 && IsPrime(a + b) && (a + b) % 4 == 3;
    }

    // Window = the samples of the current block (iter ≥ 10·⌊last/10⌋) and the one
    // before it; feed iterations 1..25 where only 10, 11, 12 land on 1+i (prime).
    [Fact]
    public void HitFraction_CountsTheTailWindow()
    {
        var map = new GaussianPrimeHitsMap();
        map.InitOrbit(out var acc);
        for (int k = 1; k <= 25; k++)
        {
            bool hit = k is 2 or 10 or 11 or 12;   // 2 falls outside the window
            double v = hit ? 1.0 / 6 : 0.0;         // round(6·(1/6 + i/6)) = 1+i, prime; 0 is not
            map.Sample(ref acc, v, v, 0, 0, k);
        }
        // last = 25 → blocks [20, 25] and [10, 19]: 16 samples, 3 hits.
        Assert.Equal(3f / 16f, GaussianPrimeHitsMap.HitFraction(acc), 6);
    }

    private static MandelbrotCalculator Render(IColorMap map, double cx, double cy, double zoom, int maxIter)
    {
        var calc = new MandelbrotCalculator(W, H)
        {
            CenterX = cx, CenterY = cy, Zoom = zoom, MaxIterations = maxIter, ColorMap = map,
        };
        calc.Calculate(CancellationToken.None);
        return calc;
    }

    private static bool Near(uint argb, int r, int g, int b)
        => Math.Abs((int)((argb >> 16) & 0xFF) - r) <= 2 && Math.Abs((int)((argb >> 8) & 0xFF) - g) <= 2
        && Math.Abs((int)(argb & 0xFF) - b) <= 2;

    [Fact]
    public void MainCardioid_ColoursMatchTheFixedPointOracle()
    {
        const double cx = -0.2, cy = 0.0, zoom = 3.0;
        var calc = Render(new GaussianPrimeHitsMap(), cx, cy, zoom, 400);
        double scale = (3.5 / Math.Max(W, H)) / zoom;
        int lit = 0, dark = 0;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                var c = new Complex(cx + (x - W * 0.5) * scale, cy + (y - H * 0.5) * scale);
                var z = (1 - Complex.Sqrt(1 - 4 * c)) / 2;
                if ((2 * z).Magnitude >= 0.5) continue;                     // fast convergence only
                double fr = 6 * z.Real, fi = 6 * z.Imaginary;
                if (Math.Abs(fr - Math.Floor(fr) - 0.5) < 1e-3 || Math.Abs(fi - Math.Floor(fi) - 0.5) < 1e-3) continue;
                bool prime = GaussianPrime((long)Math.Round(fr), (long)Math.Round(fi));
                uint px = calc.ColorBuffer[y * W + x];
                Assert.True(calc.IterationBuffer[y * W + x] >= 400, $"({x},{y}) escaped");
                Assert.True(prime ? Near(px, 255, 250, 230) : Near(px, 10, 14, 30),
                    $"({x},{y}) z*={z} prime={prime}: got {px:X8}");
                if (prime) lit++; else dark++;
            }
        Assert.True(lit > 50 && dark > 50, $"lit {lit}, dark {dark}: the view should hold both");
    }

    [Fact]
    public void Exterior_IsTheGaussianPrimeTrap()
    {
        var hits = Render(new GaussianPrimeHitsMap(), -0.6, 0.0, 1.0, 300);
        var trap = Render(new GaussianPrimeTrapMap(), -0.6, 0.0, 1.0, 300);
        int n = 0;
        for (int i = 0; i < W * H; i++)
        {
            if (hits.IterationBuffer[i] >= 300) continue;
            Assert.Equal(trap.ColorBuffer[i], hits.ColorBuffer[i]);
            n++;
        }
        Assert.True(n > W * H / 4);
    }

    // The interior differs from the trap theme's (domes) and is carved into blocks:
    // large areas at both ends of the gradient.
    [Fact]
    public void Interior_IsCarvedIntoBlocks()
    {
        var calc = Render(new GaussianPrimeHitsMap(), -0.6, 0.0, 1.0, 300);
        var inside = Enumerable.Range(0, W * H).Where(i => calc.IterationBuffer[i] >= 300).ToArray();
        int cream = inside.Count(i => Near(calc.ColorBuffer[i], 255, 250, 230));
        int navy = inside.Count(i => Near(calc.ColorBuffer[i], 10, 14, 30));
        Assert.True(cream > inside.Length / 10 && navy > inside.Length / 10,
            $"interior {inside.Length}: cream {cream}, navy {navy}");
    }

    [Fact]
    public void IsABuiltInTheme()
    {
        Assert.Contains(ColorPalette.BuiltIns, m => m is GaussianPrimeHitsMap);
        Assert.IsType<GaussianPrimeHitsMap>(ColorPalette.GetPaletteByName("Gaussian Prime Hits"));
    }
}
