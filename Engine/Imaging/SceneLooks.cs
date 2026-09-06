// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Imaging/SceneLooks.cs
//
// Roadmap slice S10.10 (PaletteBuilder-Design.md, #392) — "LOOKS" (scene colour
// scripts). The on-brand way the palette tool reaches into 3D WITHOUT becoming a
// material editor: pair a ramp with a small MATERIAL preset (roughness + metallic)
// and key-light / sky tint, saved as one unit — a "look". Gold = a warm ramp + low
// roughness + metallic; ice = a cool ramp + mid roughness + dielectric.
//
// The discipline (design §6, "not a worse Photoshop"): a look is a tiny descriptor,
// NOT a node graph. It carries only the colour-adjacent knobs the palette naturally
// owns — the ramp, two scalar material parameters, and lights DRAWN FROM THE PALETTE
// (the highlight and shadow ends). Emission / transmission tints are reserved as a
// forward-hook for roadmap S5 (they default to null = unused, so a look stays a pure
// colour object until S5 gives them a render meaning).
//
// This is a pure, self-contained descriptor + a catalog + a composer that derives a
// coherent look from any ramp. Applying a look to the live render (writing material /
// LightingFxData) is the UI/apply tail, deferred with the rest of the S10 UI. Pure +
// deterministic → asserted in tests. Reuses PerceptualRamp (OKLCH) for the composer's
// hue / chroma / lightness reads.

using System;
using System.Collections.Generic;

namespace FracturingFog.Imaging;

/// <summary>A look's material preset (roadmap S10.10, #392): the two scalar knobs the
/// palette tool owns, both in [0,1]. Not a material graph — just roughness + metallic.</summary>
public readonly record struct LookMaterial(float Roughness, float Metallic)
{
    /// <summary>Clamp both scalars into [0,1].</summary>
    public LookMaterial Clamped() => new(Math.Clamp(Roughness, 0f, 1f), Math.Clamp(Metallic, 0f, 1f));
}

/// <summary>A look's lighting tints (roadmap S10.10, #392): the key-light and sky/ambient
/// colours, drawn from the palette so the lights stay on-brand with the ramp.</summary>
public readonly record struct LookLighting(
    (byte r, byte g, byte b) KeyTint, (byte r, byte g, byte b) SkyTint);

/// <summary>A scene colour script (roadmap S10.10, #392): a ramp + a material preset +
/// lighting tints, saved as one recallable unit. <see cref="EmissionTint"/> /
/// <see cref="TransmissionTint"/> are a forward-hook for roadmap S5 (null = unused).</summary>
public sealed record Look(
    string Name,
    IReadOnlyList<(byte r, byte g, byte b)> Ramp,
    LookMaterial Material,
    LookLighting Lighting,
    (byte r, byte g, byte b)? EmissionTint = null,
    (byte r, byte g, byte b)? TransmissionTint = null)
{
    /// <summary>A look is well-formed when it has a name, a ramp of at least two stops,
    /// and material scalars in [0,1].</summary>
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(Name) &&
        Ramp is { Count: >= 2 } &&
        Material.Roughness is >= 0f and <= 1f &&
        Material.Metallic is >= 0f and <= 1f;
}

/// <summary>Derives and catalogs scene "looks" (roadmap S10.10, #392): pair a ramp with
/// a coherent material + palette-drawn lights, without a material node graph.</summary>
public static class SceneLooks
{
    /// <summary>Compose a coherent look from a ramp: derive a material preset and
    /// lighting tints straight from the palette's own colour statistics — high-chroma
    /// ramps read as glossier / more metallic (gold, copper); flat / desaturated ramps
    /// read as rough dielectrics (stone, ash). The key light is the ramp's brightest
    /// stop and the sky/ambient its darkest, so the lights stay on-brand.</summary>
    public static Look FromRamp(string name, IReadOnlyList<(byte r, byte g, byte b)> stops)
    {
        if (stops == null || stops.Count == 0)
            throw new ArgumentException("ramp is empty", nameof(stops));

        // Mean OKLCH chroma + the brightest / darkest stops.
        double sumC = 0;
        int loI = 0, hiI = 0;
        float loL = float.MaxValue, hiL = float.MinValue;
        for (int i = 0; i < stops.Count; i++)
        {
            var (L, a, b) = PerceptualRamp.RgbToOkLab(stops[i].r, stops[i].g, stops[i].b);
            var (_, C, _) = PerceptualRamp.OkLabToOklch(L, a, b);
            sumC += C;
            if (L < loL) { loL = L; loI = i; }
            if (L > hiL) { hiL = L; hiI = i; }
        }
        float meanChroma = (float)(sumC / stops.Count);

        // Chroma → metallic / gloss. OkLab chroma tops out ~0.3 for saturated sRGB, so
        // scale it into [0,1]. High chroma → metallic + low roughness (a glossy metal);
        // low chroma → dielectric + rough.
        float chromaN = Math.Clamp(meanChroma / 0.20f, 0f, 1f);
        var material = new LookMaterial(
            Roughness: 0.85f - 0.6f * chromaN,   // 0.85 (matte) → 0.25 (glossy)
            Metallic: chromaN).Clamped();

        var lighting = new LookLighting(KeyTint: stops[hiI], SkyTint: stops[loI]);
        return new Look(name, stops, material, lighting);
    }

    /// <summary>Re-skin a look with a new ramp: keep the material preset (the "gold is
    /// glossy metal" intent) but re-derive the palette-drawn lights from the new ramp,
    /// so a scene colour script can be recoloured without losing its material identity.</summary>
    public static Look Recolor(Look look, IReadOnlyList<(byte r, byte g, byte b)> newStops)
    {
        if (look == null) throw new ArgumentNullException(nameof(look));
        if (newStops == null || newStops.Count == 0)
            throw new ArgumentException("ramp is empty", nameof(newStops));

        int loI = 0, hiI = 0;
        float loL = float.MaxValue, hiL = float.MinValue;
        for (int i = 0; i < newStops.Count; i++)
        {
            float L = PerceptualRamp.RgbToOkLab(newStops[i].r, newStops[i].g, newStops[i].b).L;
            if (L < loL) { loL = L; loI = i; }
            if (L > hiL) { hiL = L; hiI = i; }
        }
        var lighting = new LookLighting(KeyTint: newStops[hiI], SkyTint: newStops[loI]);
        return look with { Ramp = newStops, Lighting = lighting };
    }

    /// <summary>The built-in on-brand look catalog — hand-authored ramp + material +
    /// lights, each a recallable scene colour script.</summary>
    public static IReadOnlyList<Look> Catalog { get; } = BuildCatalog();

    private static Look[] BuildCatalog()
    {
        return new[]
        {
            // Gold — warm amber ramp, glossy metal.
            new Look("Gold",
                new (byte, byte, byte)[] { (40, 24, 4), (140, 92, 20), (216, 168, 60), (255, 236, 170) },
                new LookMaterial(0.28f, 0.95f),
                new LookLighting((255, 244, 214), (28, 20, 10))),

            // Copper — red-brown ramp, metal.
            new Look("Copper",
                new (byte, byte, byte)[] { (32, 12, 8), (120, 52, 32), (196, 104, 66), (245, 190, 150) },
                new LookMaterial(0.34f, 0.9f),
                new LookLighting((255, 226, 200), (26, 12, 8))),

            // Ice — cool blue ramp, glossy dielectric.
            new Look("Ice",
                new (byte, byte, byte)[] { (8, 18, 40), (40, 96, 150), (130, 190, 230), (232, 246, 255) },
                new LookMaterial(0.2f, 0.0f),
                new LookLighting((224, 240, 255), (10, 18, 36))),

            // Ember — deep-red to yellow, matte-hot.
            new Look("Ember",
                new (byte, byte, byte)[] { (18, 4, 4), (110, 20, 12), (210, 78, 24), (252, 208, 96) },
                new LookMaterial(0.7f, 0.1f),
                new LookLighting((255, 214, 150), (16, 6, 6)),
                EmissionTint: (255, 96, 24)),

            // Jade — green stone, mid roughness.
            new Look("Jade",
                new (byte, byte, byte)[] { (6, 26, 18), (28, 92, 66), (86, 156, 116), (206, 238, 214) },
                new LookMaterial(0.45f, 0.15f),
                new LookLighting((230, 250, 236), (8, 22, 16))),

            // Obsidian — near-neutral dark ramp, matte stone.
            new Look("Obsidian",
                new (byte, byte, byte)[] { (6, 6, 8), (40, 40, 46), (96, 96, 104), (196, 198, 206) },
                new LookMaterial(0.82f, 0.05f),
                new LookLighting((236, 238, 244), (6, 6, 8))),
        };
    }
}
