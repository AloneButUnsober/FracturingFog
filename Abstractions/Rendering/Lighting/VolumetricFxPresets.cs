// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Rendering/Lighting/VolumetricFxPresets.cs
//
// #306 — curated volumetric-fog starting points for the Lighting & FX dialog.
// A small named set (haze / god-rays / storm clouds / amber / teal + a clear)
// the UI offers as a droplist so the user has artistic jumping-off points
// instead of tuning a dozen fog knobs from zero.
//
// Each preset sets the fog / volumetric subset of LightingFxData and returns a
// modified copy — lights, material, camera, sky and post knobs the user already
// set are preserved. Presets are examples, not locks: applying one populates the
// sliders, which the user then tunes (and a region save then snapshots, #295).
// Mirrors AsciiFxPresets so the catalogue reads the same way.
//
// GOD-RAY SHAFTS need occlusion. The in-scatter walk only casts terrain shadows
// into the fog when ShadowSteps > 0 (both CPU ShadingPipeline and the GPU relief
// kernel gate the per-step SoftShadow on it); with ShadowSteps == 0 the medium
// scatters uniformly and the fog only brightens/darkens — no shafts. So the
// shaft-forming presets (God rays / Storm clouds / Amber / Teal) set ShadowSteps
// explicitly rather than leaning on Relief2DAutoShade — that auto-fill is relief-
// only (off on the 3D raymarchers and on the still/poster path), so a preset that
// relied on it would silently degrade to flat glow everywhere else. Shafts also
// need a directional key light (Light1.Intensity > 0, default 1.0) and something
// to occlude it (the relief terrain / 3D surface); the preset leaves the lights
// alone so it respects the user's key-light direction. "Soft haze" deliberately
// leaves shadows off — even glow is the intent.
//
// Lives in Abstractions next to LightingFxData so shell and headless share one
// catalogue. LightingFxData is a struct, so Apply is Func<in,out> (copy-return),
// not the ref-mutating Action<> that AsciiFxSettings (a class) can use.

using System;
using System.Collections.Generic;

namespace FracturingFog.Rendering.Lighting
{
    /// <summary>A named volumetric-fog look and the fog-subset mutation that
    /// produces it (over the current lighting).</summary>
    public sealed class VolumetricFxPreset
    {
        public string Name { get; }
        /// <summary>Return <paramref name="fx"/> with the fog / volumetric fields
        /// set for this look; all other fields pass through unchanged.</summary>
        public Func<LightingFxData, LightingFxData> Apply { get; }
        public VolumetricFxPreset(string name, Func<LightingFxData, LightingFxData> apply)
        { Name = name; Apply = apply; }
    }

    /// <summary>Catalogue of one-click volumetric-fog looks.</summary>
    public static class VolumetricFxPresets
    {
        /// <summary>The "no preset" sentinel name (leaves lighting untouched).</summary>
        public const string NoneName = "—"; // em dash

        /// <summary>All presets (excluding <see cref="NoneName"/>), in menu order.</summary>
        public static IReadOnlyList<VolumetricFxPreset> All { get; } = new[]
        {
            // Even, gentle depth haze — no clouds, no directional shafts.
            new VolumetricFxPreset("Soft haze", fx =>
            {
                fx.FogDensity = 0.25;
                fx.FogHeightFalloff = 0.5;
                fx.VolumeSteps = 16;
                fx.VolumeNoiseAmount = 0.0;
                fx.VolumeSelfShadow = 0.0;
                fx.VolumeAnisotropy = 0.0;
                fx.FogColor = 0xFFFFFFFFu;
                fx.VolumePaletteStrength = 0.0;
                return fx;
            }),
            // Forward-scatter light shafts through the medium (god rays). Needs
            // ShadowSteps>0 so the terrain casts shadow bands into the fog.
            new VolumetricFxPreset("God rays", fx =>
            {
                fx.FogDensity = 0.45;
                fx.FogHeightFalloff = 0.3;
                fx.VolumeSteps = 40;
                fx.VolumeNoiseAmount = 0.2;
                fx.VolumeNoiseScale = 1.0;
                fx.VolumeNoiseOctaves = 3;
                fx.VolumeSelfShadow = 1.5;
                fx.VolumeSelfShadowSteps = 6;
                fx.VolumeAnisotropy = 0.7;   // strong forward halo toward the light
                fx.ShadowSteps = 24;         // terrain-cast shafts (the actual rays)
                fx.ShadowSoftK = 16.0;
                fx.FogColor = 0xFFFFFFFFu;
                return fx;
            }),
            // Dense, noisy, self-shadowing cloud bank.
            new VolumetricFxPreset("Storm clouds", fx =>
            {
                fx.FogDensity = 0.6;
                fx.FogHeightFalloff = 0.2;
                fx.VolumeSteps = 40;
                fx.VolumeNoiseAmount = 0.9;
                fx.VolumeNoiseScale = 1.5;
                fx.VolumeNoiseOctaves = 4;
                fx.VolumeSelfShadow = 2.5;
                fx.VolumeSelfShadowSteps = 8;
                fx.VolumeAnisotropy = 0.3;
                fx.ShadowSteps = 24;         // terrain shadows break up the bank
                fx.ShadowSoftK = 12.0;
                fx.FogColor = 0xFFB4B4C0u;   // cool storm grey
                return fx;
            }),
            // Warm amber haze (also the colorblind-safe accent hue).
            new VolumetricFxPreset("Amber fog", fx =>
            {
                fx.FogDensity = 0.4;
                fx.FogHeightFalloff = 0.6;
                fx.VolumeSteps = 24;
                fx.VolumeNoiseAmount = 0.3;
                fx.VolumeNoiseScale = 1.0;
                fx.VolumeSelfShadow = 0.5;
                fx.VolumeSelfShadowSteps = 4;
                fx.VolumeAnisotropy = 0.4;
                fx.ShadowSteps = 16;         // soft shafts
                fx.ShadowSoftK = 16.0;
                fx.FogColor = 0xFFFFCC00u;   // amber
                return fx;
            }),
            // Cool teal mist hugging the ground.
            new VolumetricFxPreset("Teal mist", fx =>
            {
                fx.FogDensity = 0.3;
                fx.FogHeightFalloff = 1.0;
                fx.VolumeSteps = 24;
                fx.VolumeNoiseAmount = 0.25;
                fx.VolumeNoiseScale = 1.2;
                fx.VolumeSelfShadow = 0.3;
                fx.VolumeSelfShadowSteps = 4;
                fx.VolumeAnisotropy = 0.2;
                fx.ShadowSteps = 16;         // gentle ground-hugging shafts
                fx.ShadowSoftK = 20.0;
                fx.FogColor = 0xFF66CCFFu;   // teal
                return fx;
            }),
            // Wipe the fog back to nothing (fast way to A/B against no medium).
            new VolumetricFxPreset("Clear (no fog)", fx =>
            {
                fx.FogDensity = 0.0;
                fx.FogHeightFalloff = 0.0;
                fx.VolumeSteps = 0;
                fx.VolumeNoiseAmount = 0.0;
                fx.VolumeSelfShadow = 0.0;
                fx.VolumeSelfShadowSteps = 0;
                fx.VolumeAnisotropy = 0.0;
                fx.VolumePaletteStrength = 0.0;
                fx.FogColor = 0xFFFFFFFFu;
                return fx;
            }),
        };

        /// <summary>Menu names: <see cref="NoneName"/> then every preset.</summary>
        public static IReadOnlyList<string> Names
        {
            get
            {
                var names = new List<string>(All.Count + 1) { NoneName };
                foreach (var p in All) names.Add(p.Name);
                return names;
            }
        }

        /// <summary>Return <paramref name="fx"/> with the named preset's fog subset
        /// applied (unchanged copy for null / unknown / <see cref="NoneName"/>).</summary>
        public static LightingFxData ApplyByName(string? name, LightingFxData fx)
        {
            if (string.IsNullOrEmpty(name) || name == NoneName) return fx;
            foreach (var p in All)
                if (p.Name == name) return p.Apply(fx);
            return fx;
        }
    }
}
