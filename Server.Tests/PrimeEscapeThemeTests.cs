// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1486 — Prime Escape (Gold-Cyan). The invariant: every rendered pixel's colour
// class (gold / cyan / white / dim / black) is the one an independent sieve of
// Eratosthenes gives for that pixel's own escape count, read back from the
// calculator's IterationBuffer — on the native Mandelbrot path (shallow and
// perturbation depth), the escape-time path, and the GPU palette path.

using System;
using System.Threading;

using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Render;
using FracturingFog.Rendering.Vulkan;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class PrimeEscapeThemeTests
{
    private const int W = 200, H = 150, MaxIter = 400;

    private static bool[] Sieve(int max)
    {
        var p = new bool[max + 1];
        for (int i = 2; i <= max; i++) p[i] = true;
        for (int i = 2; (long)i * i <= max; i++)
            if (p[i]) for (int j = i * i; j <= max; j += i) p[j] = false;
        return p;
    }

    private static readonly bool[] Primes = Sieve(200_000);

    private static string Expected(int n, int maxIter)
    {
        if (n >= maxIter) return "black";
        if (!Primes[n]) return "dim";
        return n == 2 ? "white" : n % 4 == 1 ? "gold" : "cyan";
    }

    // Classifies by channel relations only (not by the theme's own constants).
    private static string Classify(uint argb)
    {
        int r = (int)((argb >> 16) & 0xFF), g = (int)((argb >> 8) & 0xFF), b = (int)(argb & 0xFF);
        if (r == 0 && g == 0 && b == 0) return "black";
        if (Math.Max(r, Math.Max(g, b)) <= 72) return "dim";
        if (Math.Abs(r - g) <= 2 && Math.Abs(g - b) <= 2) return "white";
        if (r >= 130 && b <= 8 && g > r / 2 && g < r) return "gold";
        if (b >= 130 && r < b / 4 && g > r && g < b) return "cyan";
        return $"other({r},{g},{b})";
    }

    private static void AssertEveryPixel(uint[] color, int[] iter, int maxIter, string label)
    {
        int lit = 0;
        for (int i = 0; i < color.Length; i++)
        {
            string want = Expected(iter[i], maxIter), got = Classify(color[i]);
            Assert.True(want == got, $"[{label}] pixel {i}: n={iter[i]} want {want}, got {got}");
            if (want is "gold" or "cyan" or "white") lit++;
        }
        Assert.True(lit > color.Length / 50, $"[{label}] too few prime pixels ({lit}) to mean anything");
    }

    [Fact]
    public void IsPrime_MatchesTheSieve()
    {
        for (uint n = 0; n < Primes.Length; n++)
            Assert.True(PrimeEscapeGoldCyanMap.IsPrime(n) == Primes[n], $"n={n}");
    }

    [Theory]
    [InlineData(2, "white")]
    [InlineData(5, "gold")]
    [InlineData(13, "gold")]
    [InlineData(3, "cyan")]
    [InlineData(7, "cyan")]
    [InlineData(0, "dim")]
    [InlineData(1, "dim")]
    [InlineData(9, "dim")]
    [InlineData(4489, "dim")]   // 67², the first composite past the old DSL bound
    public void Map_SplitsPrimesByMod4(int n, string want)
    {
        var map = new PrimeEscapeGoldCyanMap { MaxIterations = 10_000 };
        Assert.Equal(want, Classify(unchecked((uint)map.Map(n + 0.3f, 0f, n))));
    }

    [Fact]
    public void InSetSentinel_IsBlack_AndMaxIterFallback_UsesSmooth()
    {
        var map = new PrimeEscapeGoldCyanMap { MaxIterations = 100 };
        Assert.Equal("black", Classify(unchecked((uint)map.Map(0f, 0f, 100))));
        // A calculator that passes maxIter for an escaped pixel: n = floor(smooth).
        Assert.Equal("cyan", Classify(unchecked((uint)map.Map(7.4f, 0f, 100))));
        Assert.Equal("dim", Classify(unchecked((uint)map.Map(8.4f, 0f, 100))));
    }

    [Fact]
    public void IsABuiltInTheme()
    {
        Assert.Contains(ColorPalette.BuiltIns, m => m is PrimeEscapeGoldCyanMap);
        Assert.IsType<PrimeEscapeGoldCyanMap>(ColorPalette.GetPaletteByName("Prime Escape (Gold-Cyan)"));
    }

    [Theory]
    [InlineData(-0.75, 0.0, 1.0, MaxIter)]                 // whole set
    [InlineData(-0.7453, 0.1127, 300.0, MaxIter)]          // seahorse valley
    [InlineData(-0.743643887, 0.131825904, 1e7, 5000)]     // deep enough for perturbation
    public void NativeMandelbrot_EveryPixelMatchesTheSieve(double cx, double cy, double zoom, int maxIter)
    {
        var calc = new MandelbrotCalculator(W, H)
        {
            CenterX = cx, CenterY = cy, Zoom = zoom, MaxIterations = maxIter,
            ColorMap = new PrimeEscapeGoldCyanMap(),
        };
        calc.Calculate(CancellationToken.None);
        AssertEveryPixel(calc.ColorBuffer, calc.IterationBuffer, maxIter, $"mandelbrot zoom {zoom}");
    }

    [Theory]
    [InlineData(FractalType.Julia)]
    [InlineData(FractalType.BurningShip)]
    public void EscapeTime_EveryPixelMatchesTheSieve(FractalType kind)
    {
        var calc = new EscapeTimeCalculator(W, H)
        {
            FractalType = kind, ColorMap = new PrimeEscapeGoldCyanMap(), MaxIterations = MaxIter, Zoom = 1.0,
        };
        calc.Calculate(CancellationToken.None);
        AssertEveryPixel(calc.ColorBuffer, calc.IterationBuffer, MaxIter, kind.ToString());
    }

    [Theory]
    [InlineData(FractalType.Mandelbrot)]
    [InlineData(FractalType.Julia)]
    public void Gpu_EveryPixelMatchesTheSieve(FractalType kind)
    {
        using var gpu = VulkanComputeKernel.TryCreateWithOwnContext();
        if (gpu is null) Assert.Skip("no Vulkan device on this host");
        var calc = new EscapeTimeCalculator(W, H)
        {
            FractalType = kind, ColorMap = new PrimeEscapeGoldCyanMap(), MaxIterations = MaxIter, Zoom = 1.0,
            UseGpuCompute = true, GpuKernel = gpu,
        };
        calc.Calculate(CancellationToken.None);
        // On the GPU and coloured there (an empty Detail means no CPU colouring fallback).
        Assert.Equal(GpuRouteState.Gpu, calc.LastGpuRoute.State);
        Assert.True(string.IsNullOrEmpty(calc.LastGpuRoute.Detail), $"GPU route detail: {calc.LastGpuRoute.Detail}");
        AssertEveryPixel(calc.ColorBuffer, calc.IterationBuffer, MaxIter, $"gpu {kind}");
    }
}
