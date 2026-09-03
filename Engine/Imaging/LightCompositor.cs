// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Imaging/LightCompositor.cs
//
// Roadmap slice S1 (3D-Rendering-Roadmap.md, parent #389 / #398): the light
// compositor. The raymarch already resolves, per primary hit, the separate
// lighting components — diffuse, specular, ambient occlusion, shadow — and FF now
// captures them into a ShadeComponents AOV (the same float layers the AOV EXR
// exports). This recombines those layers with the surface albedo under per-
// component gains/tints to RELIGHT in post, without re-rendering: brighten the
// diffuse, warm the key, lift the shadows with fill, dial the AO — the actual
// superpower of Blender's compositor, driven by data FF already produces.
//
// Pure, deterministic, parallel per-pixel recombination (no RNG, no device state)
// → it runs identically on the live path and under --batch (the CPU-parity
// discipline the roadmap requires). It composites the DIRECT-lighting layers
// (diffuse × albedo + specular), modulated by ambient fill and AO; the additive
// passes the beauty adds on top (SSS, reflections) are separate layers a fuller
// compositor would carry — out of scope for this operator, which relights the
// captured components.

using System;
using System.Threading.Tasks;
using FracturingFog.Rendering.Lighting;

namespace FracturingFog.Imaging;

/// <summary>Per-component relight controls for <see cref="LightCompositor"/>.
/// Defaults (gain 1, ambient 0, AO full, white tints) recombine the captured
/// components as shaded.</summary>
public sealed class LightCompositeParams
{
    /// <summary>Scale on the direct diffuse contribution (1 = as rendered).</summary>
    public double DiffuseGain { get; set; } = 1.0;

    /// <summary>Scale on the specular highlight (1 = as rendered).</summary>
    public double SpecularGain { get; set; } = 1.0;

    /// <summary>How much of the captured ambient-occlusion darkening to apply:
    /// 0 = ignore AO (flat), 1 = full captured AO. Values &gt;1 exaggerate it.</summary>
    public double AoStrength { get; set; } = 1.0;

    /// <summary>Flat ambient fill added to the diffuse term before AO — lifts the
    /// shadows toward the albedo without a light. 0 = none.</summary>
    public double Ambient { get; set; } = 0.0;

    /// <summary>Multiplies the diffuse component (a relight colour). 0xFFFFFFFF =
    /// neutral. Packed 0xAARRGGBB; alpha ignored.</summary>
    public uint DiffuseTint { get; set; } = 0xFFFFFFFFu;

    /// <summary>Multiplies the specular component. 0xFFFFFFFF = neutral.</summary>
    public uint SpecularTint { get; set; } = 0xFFFFFFFFu;
}

/// <summary>Relight from the captured lighting-component AOV (roadmap S1, #398).</summary>
public static class LightCompositor
{
    /// <summary>Recombine the per-pixel <paramref name="components"/> (diffuse /
    /// specular / AO from <see cref="ShadingPipeline.ShadeComponents"/>) with the
    /// straight-alpha BGRA <paramref name="albedo"/> under <paramref name="p"/>,
    /// producing a relit BGRA buffer. Per pixel:
    /// <c>lit = (Ambient + diffuse·DiffuseGain·tint) · aoEff</c>,
    /// <c>out = albedo·lit + specular·SpecularGain·tint</c>, where
    /// <c>aoEff = 1 − (1 − AO)·AoStrength</c>. Alpha is carried from the albedo.
    /// Returns a new buffer; inputs are not modified.</summary>
    public static uint[] Composite(uint[] albedo, ShadingPipeline.ShadeComponents[] components,
        int w, int h, LightCompositeParams p)
    {
        if (albedo == null) throw new ArgumentNullException(nameof(albedo));
        if (components == null) throw new ArgumentNullException(nameof(components));
        if (p == null) throw new ArgumentNullException(nameof(p));
        long n = (long)w * h;
        if (albedo.Length < n) throw new ArgumentException("Composite: albedo smaller than width*height.");
        if (components.Length < n) throw new ArgumentException("Composite: components smaller than width*height.");

        var outp = new uint[n];
        double dtR = ((p.DiffuseTint >> 16) & 0xFF) / 255.0;
        double dtG = ((p.DiffuseTint >> 8) & 0xFF) / 255.0;
        double dtB = (p.DiffuseTint & 0xFF) / 255.0;
        double stR = ((p.SpecularTint >> 16) & 0xFF) / 255.0;
        double stG = ((p.SpecularTint >> 8) & 0xFF) / 255.0;
        double stB = (p.SpecularTint & 0xFF) / 255.0;

        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                uint alb = albedo[i];
                double aR = ((alb >> 16) & 0xFF) / 255.0;
                double aG = ((alb >> 8) & 0xFF) / 255.0;
                double aB = (alb & 0xFF) / 255.0;

                var c = components[i];
                double aoEff = 1.0 - (1.0 - c.Ao) * p.AoStrength;

                double litR = (p.Ambient + c.DiffR * p.DiffuseGain * dtR) * aoEff;
                double litG = (p.Ambient + c.DiffG * p.DiffuseGain * dtG) * aoEff;
                double litB = (p.Ambient + c.DiffB * p.DiffuseGain * dtB) * aoEff;

                double outR = aR * litR + c.SpecR * p.SpecularGain * stR;
                double outG = aG * litG + c.SpecG * p.SpecularGain * stG;
                double outB = aB * litB + c.SpecB * p.SpecularGain * stB;

                uint R = (uint)Math.Clamp(outR * 255.0 + 0.5, 0, 255);
                uint G = (uint)Math.Clamp(outG * 255.0 + 0.5, 0, 255);
                uint B = (uint)Math.Clamp(outB * 255.0 + 0.5, 0, 255);
                outp[i] = (alb & 0xFF000000u) | (R << 16) | (G << 8) | B;
            }
        });
        return outp;
    }
}
