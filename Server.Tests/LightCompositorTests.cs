// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Roadmap slice S1 (3D-Rendering-Roadmap.md, #389 / #398) — the light compositor.
// Recombines the captured diffuse / specular / AO lighting-component AOV with the
// surface albedo under per-component gains/tints, relighting in post. Contract:
// the default recombine follows albedo·diffuse (+ specular); a diffuse gain scales
// the key; ambient lifts the shadows; AoStrength 0 ignores the captured AO;
// specular adds on top; a diffuse tint colours the fill; deterministic; alpha kept.

using FracturingFog.Imaging;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class LightCompositorTests
{
    private static uint[] Fill(int n, uint v)
    {
        var b = new uint[n];
        for (int i = 0; i < n; i++) b[i] = v;
        return b;
    }

    private static ShadingPipeline.ShadeComponents[] Comp(int n,
        float diff, float spec, float ao)
    {
        var c = new ShadingPipeline.ShadeComponents[n];
        for (int i = 0; i < n; i++)
            c[i] = new ShadingPipeline.ShadeComponents(diff, diff, diff, spec, spec, spec, ao, 1f);
        return c;
    }

    private static int R(uint c) => (int)((c >> 16) & 0xFF);

    [Fact]
    public void Default_Recombines_Albedo_Times_Diffuse()
    {
        int w = 4, h = 4, n = w * h;
        var albedo = Fill(n, 0xFF808080u);                 // 128 → 0.50196
        var comp = Comp(n, diff: 0.8f, spec: 0f, ao: 1f);
        var outp = LightCompositor.Composite(albedo, comp, w, h, new LightCompositeParams());
        // 0.50196 * 0.8 * 255 + 0.5 = 102.9 → 102
        Assert.Equal(102, R(outp[0]));
    }

    [Fact]
    public void DiffuseGain_Scales_The_Key()
    {
        int w = 4, h = 4, n = w * h;
        var albedo = Fill(n, 0xFF808080u);
        var comp = Comp(n, 0.4f, 0f, 1f);
        var lo = LightCompositor.Composite(albedo, comp, w, h, new LightCompositeParams { DiffuseGain = 1.0 });
        var hi = LightCompositor.Composite(albedo, comp, w, h, new LightCompositeParams { DiffuseGain = 2.0 });
        Assert.True(R(hi[0]) > R(lo[0]) + 30, $"gain did not brighten ({R(lo[0])} → {R(hi[0])})");
    }

    [Fact]
    public void Ambient_Lifts_A_Fully_Shadowed_Pixel()
    {
        int w = 4, h = 4, n = w * h;
        var albedo = Fill(n, 0xFFFFFFFFu);                 // white albedo
        var comp = Comp(n, diff: 0f, spec: 0f, ao: 1f);    // no direct diffuse
        var dark = LightCompositor.Composite(albedo, comp, w, h, new LightCompositeParams { Ambient = 0.0 });
        var fill = LightCompositor.Composite(albedo, comp, w, h, new LightCompositeParams { Ambient = 0.3 });
        Assert.Equal(0, R(dark[0]));                       // no light at all → black
        Assert.InRange(R(fill[0]), 70, 82);               // 1.0 * 0.3 * 255 ≈ 76
    }

    [Fact]
    public void AoStrength_Zero_Ignores_Occlusion()
    {
        int w = 4, h = 4, n = w * h;
        var albedo = Fill(n, 0xFFFFFFFFu);
        var comp = Comp(n, diff: 1f, spec: 0f, ao: 0.2f);  // heavily occluded
        var occluded = LightCompositor.Composite(albedo, comp, w, h, new LightCompositeParams { AoStrength = 1.0 });
        var flat = LightCompositor.Composite(albedo, comp, w, h, new LightCompositeParams { AoStrength = 0.0 });
        Assert.True(R(flat[0]) > R(occluded[0]) + 100, $"AO not removed ({R(occluded[0])} → {R(flat[0])})");
    }

    [Fact]
    public void Specular_Adds_On_Top()
    {
        int w = 4, h = 4, n = w * h;
        var albedo = Fill(n, 0xFF000000u);                 // black albedo → only spec shows
        var comp = Comp(n, diff: 0f, spec: 0.5f, ao: 1f);
        var none = LightCompositor.Composite(albedo, comp, w, h, new LightCompositeParams { SpecularGain = 0.0 });
        var spec = LightCompositor.Composite(albedo, comp, w, h, new LightCompositeParams { SpecularGain = 1.0 });
        Assert.Equal(0, R(none[0]));
        Assert.InRange(R(spec[0]), 120, 135);             // 0.5 * 255 ≈ 128
    }

    [Fact]
    public void Diffuse_Tint_Colours_The_Fill()
    {
        int w = 4, h = 4, n = w * h;
        var albedo = Fill(n, 0xFFFFFFFFu);
        var comp = Comp(n, diff: 1f, spec: 0f, ao: 1f);
        // Pure-red diffuse tint → the green/blue diffuse is zeroed.
        var outp = LightCompositor.Composite(albedo, comp, w, h,
            new LightCompositeParams { DiffuseTint = 0xFFFF0000u });
        uint c = outp[0];
        Assert.Equal(255, (int)((c >> 16) & 0xFF));        // red kept
        Assert.Equal(0, (int)((c >> 8) & 0xFF));           // green off
        Assert.Equal(0, (int)(c & 0xFF));                  // blue off
    }

    [Fact]
    public void Is_Deterministic_And_Keeps_Alpha()
    {
        int w = 8, h = 8, n = w * h;
        var albedo = Fill(n, 0x80402010u);                 // non-opaque alpha
        var comp = Comp(n, 0.6f, 0.2f, 0.7f);
        var p = new LightCompositeParams { DiffuseGain = 1.3, Ambient = 0.1 };
        var a = LightCompositor.Composite(albedo, comp, w, h, p);
        var b = LightCompositor.Composite(albedo, comp, w, h, p);
        Assert.Equal(a, b);
        Assert.Equal(0x80u, (a[0] >> 24) & 0xFF);          // alpha carried from albedo
    }
}
