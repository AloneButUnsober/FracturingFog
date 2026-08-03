// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #27 Phase 4 — the ColorGen DSL interpreter (InterpretedColorMap) must produce
// the EXACT same colour as the old Roslyn-compiled path for every theme. This
// is the correctness guarantee for retiring codegen from the theme render path.
//
// For a corpus spanning the whole DSL surface (hsv/hsl/oklab/oklch, palette,
// cosine, brightness/contrast/gamma, mix/mix_oklab, all scalar math, ternary,
// channel access, let-bindings, every built-in input), each theme is both
// Roslyn-compiled (ColorGenHotLoad) and interpreted (InterpretedColorMap). Over
// a grid of sample inputs the two Map() results must be bit-identical — the DSL
// runs the same double arithmetic and the same ARGB packer on both paths.

using System;
using FracturingFog.ColorGen;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class ColorGenInterpreterParityTests
{
    // (label, DSL source). Every entry must return a Vec3.
    public static TheoryData<string, string> Corpus() => new()
    {
        { "hsv_t", "return hsv(t, 1.0, 1.0);" },

        { "hsl_math",
          "let l = 0.4 + 0.2*sin(smooth*0.3); return hsl(fract(arg/6.2831853 + 0.5), 0.7, l);" },

        { "palette_stops",
          "return palette(fract(smooth*0.1), rgb(0,0,0), rgb(1,0,0), rgb(1,1,0), rgb(1,1,1));" },

        { "cosine_iq",
          "return cosine(t, rgb(0.5,0.5,0.5), rgb(0.5,0.5,0.5), rgb(1,1,1), rgb(0.0,0.33,0.67));" },

        { "oklab_oklch",
          "let a = oklab(t, 0.1, -0.05); let b = oklch(t, 0.12, arg); return mix_oklab(a, b, saturate(dist*10));" },

        { "postfx_chain",
          "let base = hsv(t, 0.9, 0.8); return gamma(contrast(brightness(base, 0.05), 0.2), 1.8);" },

        { "channels_ternary",
          "let cvec = hsv(t, 1.0, 1.0); let lum = cvec.r*0.3 + cvec.g*0.59 + cvec.b*0.11; " +
          "return isInSet > 0.5 ? rgb(0,0,0) : rgb(lum, lum, lum);" },

        { "scalar_kitchen_sink",
          "let x = clamp(smoothstep(0.0, 1.0, t) + mod(smooth, 3.0)*0.1, 0.0, 1.0); " +
          "let y = max(min(x, 0.9), 0.1); let h = hash(iter) * 0.15; " +
          "return hsv(y + h, pow(0.8, 1.2), step(0.2, x)*0.5 + 0.5);" },

        { "final_state_inputs",
          "let m = mag / (1.0 + mag); let ang = atan2(zi, zr); " +
          "return hsv(fract(ang/6.2831853), m, hypot(dzr, dzi) / (1.0 + hypot(dzr, dzi)));" },

        { "mix_scalar_pxscale",
          "let k = mix(nx, ny, 0.5) + pxScale*100.0; return rgb(saturate(k), saturate(1.0-k), 0.5);" },
    };

    // Sample grid — exterior + in-set, tilted normals, non-trivial final state.
    private static readonly (float smooth, float dist, int iter)[] Escapes =
    {
        (0f, 0f, 0), (5.5f, 0.001f, 50), (120.3f, 0.5f, 300), (512f, 0f, 512), // last = in-set
    };
    private static readonly (float nx, float ny)[] Normals = { (0f, 0f), (0.3f, -0.2f) };
    private static readonly (float zr, float zi, float dzr, float dzi)[] Finals =
    {
        (0f, 0f, 0f, 0f), (1.5f, -0.7f, 0.2f, 0.9f),
    };

    private const int MaxIter = 512;
    private const double PixelScale = 0.0025;

    [Theory]
    [MemberData(nameof(Corpus))]
    public void Interpreted_MatchesRoslynCompiled_PerPixel(string label, string dsl)
    {
        var opts = new GenerateOptions { ThemeName = label, Category = "Test", Description = "" };

        // Compiled reference (the old runtime path).
        var hot = ColorGenHotLoad.TryCompileAndLoad(dsl, label + "Cmp", opts);
        Assert.True(hot.Ok, $"[{label}] compile failed: {hot.Error}");
        var compiled = (IColorMap)Activator.CreateInstance(hot.ColorMapType!)!;
        compiled.MaxIterations = MaxIter;
        ((IColorMapWithPixelScale)compiled).PixelScale = PixelScale;

        // Interpreter under test.
        var interp = InterpretedColorMap.TryCreate(dsl, opts, out string? err);
        Assert.True(interp != null, $"[{label}] interpreter parse failed: {err}");
        interp!.MaxIterations = MaxIter;
        ((IColorMapWithPixelScale)interp).PixelScale = PixelScale;

        foreach (var (smooth, dist, iter) in Escapes)
        foreach (var (nx, ny) in Normals)
        foreach (var (zr, zi, dzr, dzi) in Finals)
        {
            int want = compiled.Map(smooth, dist, iter, nx, ny, zr, zi, dzr, dzi);
            int got  = interp.Map(smooth, dist, iter, nx, ny, zr, zi, dzr, dzi);
            Assert.True(want == got,
                $"[{label}] smooth={smooth} dist={dist} iter={iter} n=({nx},{ny}) " +
                $"z=({zr},{zi}) dz=({dzr},{dzi}): compiled 0x{want:X8} != interpreted 0x{got:X8}");
        }
    }

    [Fact]
    public void TryCreate_ParseError_ReturnsNullWithMessage()
    {
        var map = InterpretedColorMap.TryCreate("return 1.0;", null, out string? err); // scalar return
        Assert.Null(map);
        Assert.False(string.IsNullOrEmpty(err));
    }

    [Fact]
    public void TryCreate_ImplementsGpuAndInSetInterfaces()
    {
        var map = InterpretedColorMap.TryCreate("return hsv(t, 1.0, 1.0);", null, out _);
        Assert.NotNull(map);
        Assert.IsAssignableFrom<IGpuHlslPalette>(map!);
        Assert.IsAssignableFrom<IColorMapHandlesInSet>(map!);
        Assert.IsAssignableFrom<INamedColorMap>(map!);
        Assert.False(string.IsNullOrEmpty(((IGpuHlslPalette)map!).HlslPaletteBody));
    }
}
