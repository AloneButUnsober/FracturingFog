// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Animation/SceneShotLighting.cs
//
// #1059 — a scene shot's Lighting & FX from a Volumetric Lighting & FX preset
// instead of borrowing another region's lighting. One rule shared by live
// playback (ShellViewModel) and the offline exporter (SceneVideoRenderer), so a
// shot is lit the same in Preview / Play and in its export.
//
//  - User preset (lighting-fx-presets.json): the full saved Lighting & FX block,
//    replacing the shot's lighting wholesale (same as the dialog's Recall).
//  - Built-in curated preset (VolumetricFxPresets): its fog / volume subset
//    overlaid on the shot's current lighting (same as picking it in the dialog).
//
// Precedence: scene global track > shot preset > (legacy) lighting region >
// shot region > default. The preset is applied after the legacy
// LightingRegionName borrow, so an old scene that still carries one keeps it as
// the base and a preset — if both are set — wins.

using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;

namespace FracturingFog.Abstractions.Animation;

public static class SceneShotLighting
{
    /// <summary>True when the shot names a lighting preset.</summary>
    public static bool HasPreset(SceneShot shot)
        => !string.IsNullOrWhiteSpace(shot?.LightingPresetName);

    /// <summary>The shot's lighting after its preset is applied over
    /// <paramref name="current"/>. Returns false (and <paramref name="result"/> =
    /// <paramref name="current"/>) when the shot names no preset or the named
    /// preset no longer exists — the shot then keeps its region lighting.</summary>
    /// <param name="userPresets">The user preset library (null = not loaded / none).</param>
    public static bool TryApplyPreset(SceneShot shot, in LightingFxData current,
        LightingFxPresetFile? userPresets, out LightingFxData result)
    {
        result = current;
        if (!HasPreset(shot)) return false;
        string name = shot.LightingPresetName!;

        if (shot.LightingPresetIsBuiltIn)
        {
            if (!IsBuiltIn(name)) return false;
            result = VolumetricFxPresets.ApplyByName(name, current);
            return true;
        }

        var preset = userPresets == null ? null : LightingFxPresetLibrary.Get(userPresets, name);
        if (preset == null) return false;
        result = preset.Data.ToFx();
        return true;
    }

    /// <summary>True when <paramref name="name"/> is a built-in curated preset
    /// (not the "—" none entry).</summary>
    public static bool IsBuiltIn(string? name)
    {
        if (string.IsNullOrEmpty(name) || name == VolumetricFxPresets.NoneName) return false;
        foreach (var p in VolumetricFxPresets.All)
            if (p.Name == name) return true;
        return false;
    }
}
