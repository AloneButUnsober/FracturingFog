// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// VolumePaletteBaker.cs
//
// Vol-color slice D (#180) — bakes the active 3D color theme's gradient into a
// small packed-ARGB LUT once per frame, so the volumetric in-scatter can be
// hue-remapped through the same palette as the fractal surface
// (ShadingPipeline.VolumetricInScatter reads LightingFxData.VolumePalette).
//
// This lives on the Engine side (not in ShadingPipeline) because it needs the
// IColorMap contract, which the lighting layer deliberately doesn't reference —
// ShadingPipeline only ever sees the baked uint[] ramp, keeping the shading
// kernel decoupled from the color-map/theme machinery.

using System;
using FracturingFog.Interefaces;
using FracturingFog.Rendering.Lighting;

namespace FracturingFog.Rendering.Lighting;

/// <summary>Bakes an <see cref="IColorMap"/> into the runtime gradient LUT the
/// slice-D volumetric palette remap consumes. No-op (clears the LUT) unless
/// <see cref="LightingFxData.VolumePaletteStrength"/> &gt; 0, so a default scene
/// pays nothing and stays bit-identical.</summary>
public static class VolumePaletteBaker
{
    /// <summary>Default LUT resolution — 256 entries gives a smooth ramp with a
    /// negligible one-time cost (256 <c>Map</c> calls per frame, only when the
    /// feature is on).</summary>
    public const int DefaultSize = 256;

    /// <summary>Bake <paramref name="colorMap"/>'s iteration sweep into
    /// <c>fx.VolumePalette</c> when slice-D is active; otherwise clear it. Call
    /// once per <c>Calculate</c>, after the local <see cref="LightingFxData"/>
    /// is taken and before the per-pixel render loop.
    ///
    /// The ramp sweeps the theme's smooth-iteration axis at a fixed, gently
    /// tilted surface normal (the same convention as <c>IColorMap.SwatchSample</c>)
    /// so 3D themes return their shaded hue progression rather than an edge-on
    /// black. Brightness is re-imposed by the in-scatter energy at sample time,
    /// so only the hue progression matters here.</summary>
    public static void Bake(ref LightingFxData fx, IColorMap? colorMap, int size = DefaultSize)
    {
        if (fx.VolumePaletteStrength <= 0.0 || colorMap is null || size < 2)
        {
            fx.VolumePalette = null;
            return;
        }

        int iters = colorMap.MaxIterations > 0 ? colorMap.MaxIterations : 256;
        var lut = new uint[size];
        double denom = size - 1;
        for (int i = 0; i < size; i++)
        {
            float smooth = (float)(i / denom * iters);
            // nx/ny match SwatchSample's gentle tilt so relief themes shade.
            lut[i] = (uint)colorMap.Map(smooth, 0f, iters, 0.30f, 0.20f);
        }
        fx.VolumePalette = lut;
    }
}
