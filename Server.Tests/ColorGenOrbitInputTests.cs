// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// F15 (#591) — ColorGen orbit-accumulator inputs. A ColorGen program that
// references trapMin / stripeAvg / tiaAvg becomes orbit-aware: the host samples
// the orbit per iteration and binds those values at escape. CPU-only (the orbit
// map advertises no GPU palette). These tests pin: type selection (only
// orbit-using programs become orbit-aware — no regression for normal themes),
// the render actually reflects the accumulator, and the GPU palette is off.

using System.Collections.Generic;
using FracturingFog;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class ColorGenOrbitInputTests
{
    private const int W = 48, H = 36;

    private static InterpretedColorMap Make(string src)
    {
        var m = InterpretedColorMap.TryCreate(src, null, out string? err);
        Assert.Null(err);
        Assert.NotNull(m);
        return m!;
    }

    [Fact]
    public void OrbitInput_Program_IsOrbitAware()
    {
        var m = Make("return hsv(saturate(trapMin), 0.8, 1.0);");
        Assert.IsType<InterpretedOrbitColorMap>(m);
        Assert.IsAssignableFrom<IOrbitAwareColorMap>(m);
    }

    [Theory]
    [InlineData("return hsv(saturate(stripeAvg), 0.8, 1.0);")]
    [InlineData("return hsv(saturate(tiaAvg), 0.8, 1.0);")]
    [InlineData("let k = trapMin * 2.0; return rgb(k, k, k);")]
    [InlineData("return hsv(saturate(trapCross * 3.0), 0.8, 1.0);")]
    [InlineData("return hsv(saturate(curvature), 0.8, 1.0);")]
    [InlineData("return hsv(saturate(lyapunov * 0.2), 0.8, 1.0);")]
    [InlineData("return hsv(saturate(gaussian * 1.4), 0.8, 1.0);")]
    [InlineData("return hsv(saturate(expSmooth), 0.8, 1.0);")]
    [InlineData("return hsv(saturate(trapRing * 3.0), 0.8, 1.0);")]
    [InlineData("return hsv(saturate(trapHyperbola), 0.8, 1.0);")]
    [InlineData("return hsv(saturate(trapHexagon), 0.8, 1.0);")]
    public void EachOrbitInput_TriggersOrbitAware(string src)
        => Assert.IsAssignableFrom<IOrbitAwareColorMap>(Make(src));

    [Fact]
    public void NonOrbit_Program_StaysFastPath()
    {
        var m = Make("return hsv(smooth * 0.03, 0.85, 1.0);");
        Assert.IsType<InterpretedColorMap>(m);          // base type, not the orbit subclass
        Assert.False(m is IOrbitAwareColorMap);          // no per-iteration sampling
    }

    [Fact]
    public void OrbitMap_AdvertisesNoGpuPalette()
    {
        var m = (InterpretedOrbitColorMap)Make("return rgb(saturate(trapMin), 0, 0);");
        Assert.Equal("", m.HlslPaletteBody);             // GPU escape-only path can't do orbit
    }

    private static uint[] RenderNative(string src)
    {
        var calc = new MandelbrotCalculator(W, H)
        {
            CenterX = -0.75, CenterY = 0.0, Zoom = 1.0, MaxIterations = 200,
            ColorMap = Make(src),
        };
        calc.Calculate(default);
        return (uint[])calc.ColorBuffer.Clone();
    }

    // The native Mandelbrot path dispatches the orbit map through its orbit-aware
    // loop (interface fallback), so Sample runs and trapMin actually varies.
    [Fact]
    public void NativeRender_TrapMin_IsNonUniform()
    {
        uint[] trap = RenderNative("return hsv(saturate(trapMin), 0.9, 1.0);");
        var distinct = new HashSet<uint>(trap);
        Assert.True(distinct.Count >= 3, $"trapMin should vary, saw {distinct.Count}");
    }

    // Different accumulators drive different images (proves each is really bound,
    // not a shared constant).
    [Fact]
    public void NativeRender_DifferentAccumulators_DifferentImages()
    {
        uint[] trap = RenderNative("return hsv(saturate(trapMin), 0.9, 1.0);");
        uint[] stripe = RenderNative("return hsv(saturate(stripeAvg), 0.9, 1.0);");
        Assert.NotEqual(trap, stripe);
    }

    // Each of the extended accumulator inputs actually binds (renders non-uniform)
    // and drives a distinct image.
    [Theory]
    [InlineData("return hsv(saturate(lyapunov * 0.2), 0.9, 1.0);")]
    [InlineData("return hsv(saturate(curvature), 0.9, 1.0);")]
    [InlineData("return hsv(saturate(gaussian * 1.4), 0.9, 1.0);")]
    [InlineData("return hsv(saturate(expSmooth), 0.9, 1.0);")]
    [InlineData("return hsv(saturate(trapCross * 3.0), 0.9, 1.0);")]
    [InlineData("return hsv(saturate(trapRing * 3.0), 0.9, 1.0);")]
    [InlineData("return hsv(saturate(trapHyperbola), 0.9, 1.0);")]
    [InlineData("return hsv(saturate(trapHexagon), 0.9, 1.0);")]
    public void NativeRender_ExtendedAccumulators_AreNonUniform(string src)
    {
        var distinct = new HashSet<uint>(RenderNative(src));
        Assert.True(distinct.Count >= 3, $"accumulator should vary, saw {distinct.Count}");
    }

    // Two distinct shape traps in one program bind independently (separate
    // accumulator slots), so they produce different images.
    [Fact]
    public void NativeRender_ShapeTraps_AreIndependent()
    {
        uint[] ring = RenderNative("return hsv(saturate(trapRing * 3.0), 0.9, 1.0);");
        uint[] hex  = RenderNative("return hsv(saturate(trapHexagon), 0.9, 1.0);");
        Assert.NotEqual(ring, hex);
    }

    // "Generate via ColorGen" (C# export) now emits an orbit-aware class for
    // programs that reference orbit inputs — an IOrbitAwareColorMap with baked
    // InitOrbit / Sample gates. It must succeed and emit that interface.
    [Fact]
    public void GenerateCSharp_OrbitInputs_EmitsOrbitAwareClass()
    {
        var r = FracturingFog.ColorGen.ColorGenApi.Generate("return rgb(saturate(trapMin), 0, 0);", "TrapTheme");
        Assert.True(r.Ok, r.Error);
        Assert.Contains("IOrbitAwareColorMap", r.Source);
        Assert.Contains("public void Sample(", r.Source);
        Assert.Contains("F_trapMin       = true", r.Source);   // gate baked on
        Assert.Contains("F_stripe        = false", r.Source);  // unused gate off
    }

    // A normal (non-orbit) program still exports the escape-only GPU-capable class.
    [Fact]
    public void GenerateCSharp_NonOrbit_Succeeds()
    {
        var r = FracturingFog.ColorGen.ColorGenApi.Generate("return hsv(smooth * 0.03, 0.85, 1.0);", "PlainTheme");
        Assert.True(r.Ok);
        Assert.Contains("IGpuHlslPalette", r.Source);          // escape path keeps GPU
        Assert.DoesNotContain("IOrbitAwareColorMap", r.Source);
    }

    // End-to-end: the generated orbit C# Roslyn-compiles, loads, and its
    // MapWithOrbit matches the interpreter bit-for-bit over a sampled orbit.
    // This is the correctness guarantee for the C# export path.
    [Theory]
    [InlineData("return hsv(saturate(trapMin), 0.9, 1.0);")]
    [InlineData("return hsv(saturate(stripeAvg), 0.9, 1.0);")]
    [InlineData("let k = trapCross*3.0 + trapRing; return hsv(saturate(k), 0.9, 1.0);")]
    [InlineData("return hsv(saturate(lyapunov*0.2 + curvature), 0.8, 1.0);")]
    [InlineData("return hsv(saturate(gaussian*1.4 + expSmooth), 0.8, 1.0);")]
    [InlineData("return hsv(saturate(trapHexagon + trapHyperbola + tiaAvg), 0.8, 1.0);")]
    public void GeneratedOrbit_CompilesAndMatchesInterpreter(string dsl)
    {
        const int maxIter = 300;
        var opts = new FracturingFog.ColorGen.GenerateOptions { ThemeName = "OrbitCmp", Category = "Test" };

        var hot = FracturingFog.ColorGen.ColorGenHotLoad.TryCompileAndLoad(dsl, "OrbitCmp", opts);
        Assert.True(hot.Ok, $"compile failed: {hot.Error}");
        var compiled = (IColorMap)System.Activator.CreateInstance(hot.ColorMapType!)!;
        compiled.MaxIterations = maxIter;
        var compiledOrbit = Assert.IsAssignableFrom<IOrbitAwareColorMap>(compiled);

        var interp = InterpretedColorMap.TryCreate(dsl, opts, out string? err);
        Assert.True(interp != null, err);
        interp!.MaxIterations = maxIter;
        var interpOrbit = Assert.IsAssignableFrom<IOrbitAwareColorMap>(interp);

        // Run one representative orbit through both Sample paths, then compare
        // MapWithOrbit at a few escape states.
        static void RunOrbit(IOrbitAwareColorMap m, out OrbitAccumulator acc)
        {
            m.InitOrbit(out acc);
            double zr = 0, zi = 0; const double cr = -0.75, ci = 0.12;
            for (int i = 1; i <= 40; i++)
            {
                double nzr = zr * zr - zi * zi + cr;
                double nzi = 2 * zr * zi + ci;
                zr = nzr; zi = nzi;
                m.Sample(ref acc, zr, zi, cr, ci, i);
            }
        }

        RunOrbit(compiledOrbit, out var accC);
        RunOrbit(interpOrbit, out var accI);

        foreach (var (smooth, dist, iter) in new[] { (5.5f, 0.01f, 40), (120.3f, 0.5f, 200), (300f, 0f, 300) })
        {
            int want = interpOrbit.MapWithOrbit(smooth, dist, iter, 0f, 0f, in accI);
            int got  = compiledOrbit.MapWithOrbit(smooth, dist, iter, 0f, 0f, in accC);
            Assert.True(want == got,
                $"MapWithOrbit mismatch @ iter={iter}: interp 0x{want:X8} != generated 0x{got:X8}");
        }
    }
}
