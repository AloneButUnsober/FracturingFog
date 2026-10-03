// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Cli/LightingFidelity.cs
// #998 (CB6 of #64) — which parts of the live Lighting & FX block a batch
// command can carry. The individual lighting flags cover the three lights, the
// curated fog knobs and glass; everything else (AO, shadows, materials, sky,
// tone map, lens, reflections, caustics, edges, ...) travels only inside a saved
// Lighting & FX preset (--lighting-preset). A live block with such settings and
// no matching preset would be silently dropped — the command builder reports it.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;

namespace FracturingFog.Cli
{
    public static class LightingFidelity
    {
        // LightingFxPresetData property names the batch flags carry (the --lightN-*,
        // curated fog (#373/#408) and glass (#406) flags).
        private static readonly HashSet<string> s_flagCovered = new(StringComparer.Ordinal)
        {
            "FogDensity", "FogHeightFalloff", "VolumeSteps", "VolumeLightMask", "VolumeAnisotropy",
            "FogColor", "VolumePaletteStrength", "FogBackground", "FogBackgroundDistance",
            "Froxel3D", "Froxel3DQuality", "Froxel3DShadowSteps",
            "Transmission", "Ior", "AbsorptionColor", "AbsorptionDistance",
            "RefractInternalMarch", "RefractInternalBounces",
        };

        /// <summary>Not a look: debug / backend switches and the animation clock.
        /// Stereo is reported as its own gap.</summary>
        private static bool IsIgnored(string name)
            => name.StartsWith("Stereo", StringComparison.Ordinal)
            || name.StartsWith("Debug", StringComparison.Ordinal)
            || name is "UseGpuPost" or "UseGpuRender" or "SceneTime";

        private static bool IsCovered(string name)
            => s_flagCovered.Contains(name)
            || (name.Length > 6 && name.StartsWith("Light", StringComparison.Ordinal)
                && name[5] is >= '1' and <= '3');   // Light1Theta, Light2Color, ...

        private static JsonObject Json(in LightingFxData fx)
            => (JsonObject)JsonSerializer.SerializeToNode(LightingFxPresetData.FromFx(fx))!;

        /// <summary>The Lighting &amp; FX settings in <paramref name="live"/> that
        /// differ from the defaults and have no batch flag (property names).</summary>
        public static IReadOnlyList<string> UnexpressedFields(in LightingFxData live)
        {
            var now = Json(live);
            var stock = Json(LightingFxData.CreateDefault());
            var diff = new List<string>();
            foreach (var (name, node) in now)
            {
                if (IsCovered(name) || IsIgnored(name)) continue;
                stock.TryGetPropertyValue(name, out var d);
                if (!JsonNode.DeepEquals(node, d)) diff.Add(name);
            }
            return diff;
        }

        /// <summary>The saved preset whose settings equal <paramref name="live"/>
        /// exactly (ignoring debug / backend switches), or null.</summary>
        public static string? MatchPreset(in LightingFxData live, IEnumerable<LightingFxPreset> presets)
        {
            var now = Strip(Json(live));
            foreach (var p in presets)
            {
                if (p?.Data == null || string.IsNullOrWhiteSpace(p.Name)) continue;
                var candidate = Strip(Json(p.Data.ToFx()));
                if (JsonNode.DeepEquals(now, candidate)) return p.Name;
            }
            return null;
        }

        private static JsonObject Strip(JsonObject o)
        {
            foreach (var name in o.Select(kv => kv.Key).Where(k => IsIgnored(k) && !k.StartsWith("Stereo", StringComparison.Ordinal)).ToList())
                o.Remove(name);
            return o;
        }
    }
}
