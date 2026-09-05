// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Roadmap slice S11 (3D-Rendering-Roadmap.md, #592) — relief height from an
// orbit-trap distance field. ReliefHeightField.Build selects / blends the per-pixel
// scalar that drives the relief height: smooth iteration count (default, byte-
// identical), orbit-trap min-distance (normalised into smooth's raw range, inverted
// so near-trap = high ridge), or a blend. These lock the contract:
//   * Smooth / no-trap / all-zero-trap → the smooth array UNCHANGED (same reference);
//   * Trap → in-set (trap 0) flat, near-trap high, far-trap low, scaled to smooth's max;
//   * Blend → a lerp of smooth and the trap height by the blend weight.

using FracturingFog;                     // ReliefHeightSource, MandelbrotCalculator
using FracturingFog.Models;              // OrbitTrapCircleMap
using FracturingFog.Rendering.Lighting;  // ReliefHeightField
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class ReliefHeightFieldTests
{
    [Fact]
    public void OrbitTrapTheme_Populates_TrapBuffer_And_Drives_Distinct_Height()
    {
        // Integration: with an orbit-trap theme active the Mandelbrot calc fills
        // TrapBuffer, so a Trap height source produces a field distinct from smooth —
        // the plumbing the relief capture reads (host / poster).
        int w = 96, h = 72;
        var calc = new MandelbrotCalculator(w, h)
        {
            CenterX = -0.75, CenterY = 0.0, Zoom = 1.0, MaxIterations = 300,
            ColorMap = new OrbitTrapCircleMap(),
        };
        calc.Calculate(default);

        Assert.Equal(w * h, calc.TrapBuffer.Length);
        bool anyTrap = false;
        foreach (var t in calc.TrapBuffer) if (t > 0f) { anyTrap = true; break; }
        Assert.True(anyTrap, "orbit-trap theme must populate TrapBuffer");

        var trapHeight = ReliefHeightField.Build(
            calc.SmoothBuffer, calc.TrapBuffer, w * h, ReliefHeightSource.Trap, 1.0);
        Assert.NotSame(calc.SmoothBuffer, trapHeight);
        int diff = 0;
        for (int i = 0; i < w * h; i++) if (trapHeight[i] != calc.SmoothBuffer[i]) diff++;
        Assert.True(diff > (w * h) / 10, $"trap height should differ from smooth ({diff} px)");
    }

    [Fact]
    public void HiResTrap_Twin_Config_Fills_TrapBuffer_CpuForced()
    {
        // S11 hi-res trap tail (#592): the hi-res field twin runs the orbit-trap theme
        // with GPU forced OFF (the GPU orbit path does not emit TrapBuffer), so a
        // CPU-forced MandelbrotCalculator at a hi-res-floor size fills TrapBuffer and
        // yields a distinct trap height — the exact config the twin uses.
        int w = 320, h = 240;   // stand-in for a hi-res field-floor render
        var calc = new MandelbrotCalculator(w, h)
        {
            CenterX = -0.75, CenterY = 0.0, Zoom = 1.0, MaxIterations = 300,
            ColorMap = new OrbitTrapCircleMap(),
            UseGpuCompute = false,   // GPU orbit path fills no TrapBuffer → force CPU
        };
        calc.Calculate(default);

        bool anyTrap = false;
        foreach (var t in calc.TrapBuffer) if (t > 0f) { anyTrap = true; break; }
        Assert.True(anyTrap, "CPU orbit path must fill TrapBuffer at hi-res size");

        var eff = ReliefHeightField.Build(
            calc.SmoothBuffer, calc.TrapBuffer, w * h, ReliefHeightSource.Trap, 1.0);
        int diff = 0;
        for (int i = 0; i < w * h; i++) if (eff[i] != calc.SmoothBuffer[i]) diff++;
        Assert.True(diff > (w * h) / 10, $"hi-res trap height should differ from smooth ({diff} px)");
    }

    [Fact]
    public void Smooth_Source_Returns_Same_Reference()
    {
        var smooth = new[] { 1f, 2f, 3f, 4f };
        var trap = new[] { 0.1f, 0.2f, 0.3f, 0.4f };
        var outp = ReliefHeightField.Build(smooth, trap, 4, ReliefHeightSource.Smooth, 0.5);
        Assert.Same(smooth, outp);   // byte-identical default: no copy, no change
    }

    [Fact]
    public void Trap_Null_Or_Empty_Falls_Back_To_Smooth()
    {
        var smooth = new[] { 1f, 2f, 3f, 4f };
        Assert.Same(smooth, ReliefHeightField.Build(smooth, null, 4, ReliefHeightSource.Trap, 1.0));
        Assert.Same(smooth, ReliefHeightField.Build(smooth, new float[0], 4, ReliefHeightSource.Trap, 1.0));
    }

    [Fact]
    public void Trap_AllZero_Falls_Back_To_Smooth()
    {
        // No orbit-trap theme ran → trap all 0 (no active pixels) → fall back.
        var smooth = new[] { 1f, 2f, 3f, 4f };
        var trap = new[] { 0f, 0f, 0f, 0f };
        Assert.Same(smooth, ReliefHeightField.Build(smooth, trap, 4, ReliefHeightSource.Trap, 1.0));
    }

    [Fact]
    public void Trap_Source_Inverts_And_Scales_To_Smooth_Max()
    {
        // smoothMax = 10. trap: idx0 in-set (0) → flat; idx1 nearest (0.1) → high;
        // idx3 farthest (0.5) → low(0); idx2 mid.
        var smooth = new[] { 5f, 10f, 2f, 7f };
        var trap = new[] { 0f, 0.1f, 0.3f, 0.5f };
        var outp = ReliefHeightField.Build(smooth, trap, 4, ReliefHeightSource.Trap, 1.0);

        Assert.NotSame(smooth, outp);
        Assert.Equal(0f, outp[0], 4);                 // in-set → flat
        Assert.Equal(10f, outp[1], 3);                // nearest trap → full smoothMax
        Assert.Equal(0f, outp[3], 3);                 // farthest trap → 0
        Assert.True(outp[2] > 0f && outp[2] < 10f);   // mid
        // Monotone: nearer trap (smaller distance) → higher ridge.
        Assert.True(outp[1] > outp[2] && outp[2] > outp[3]);
    }

    [Fact]
    public void Blend_Lerps_Smooth_And_TrapHeight()
    {
        var smooth = new[] { 5f, 10f, 2f, 7f };
        var trap = new[] { 0f, 0.1f, 0.3f, 0.5f };
        var full = ReliefHeightField.Build(smooth, trap, 4, ReliefHeightSource.Trap, 1.0);

        var b0 = ReliefHeightField.Build(smooth, trap, 4, ReliefHeightSource.Blend, 0.0);
        var b1 = ReliefHeightField.Build(smooth, trap, 4, ReliefHeightSource.Blend, 1.0);
        var bMid = ReliefHeightField.Build(smooth, trap, 4, ReliefHeightSource.Blend, 0.5);

        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(smooth[i], b0[i], 3);                          // blend 0 = smooth
            Assert.Equal(full[i], b1[i], 3);                            // blend 1 = trap height
            Assert.Equal(0.5f * smooth[i] + 0.5f * full[i], bMid[i], 3); // lerp
        }
    }
}
