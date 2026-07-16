// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Models/ColorTheme/ColorThemeDef.cs
//
// UI-neutral mirror of the WinForms ColorThemeData hierarchy. Used by the
// Avalonia ColorThemeEditor VM so UI.Avalonia stays free of System.Drawing
// and of the runtime LightSource / PbrLightingMode classes (which live next
// to the hot-path renderer in the main project).
//
// The host's IColorThemeService bridges these neutral DTOs to the legacy
// ColorThemeData/LightSourceData/PbrMaterialBandData/InSetColorData types
// in FracturingFog.Models, so existing JSON, library, and C# export
// machinery keeps working untouched.
//
// Naming convention: legacy types end in "Data", neutral mirrors end in
// "Def", so both can live in the same FracturingFog.Models namespace.

using System.Collections.Generic;

namespace FracturingFog.Models
{
    /// <summary>UI-neutral mirror of <see cref="ColorThemeKind"/>.</summary>
    public enum ColorThemeKindDef
    {
        Gradient,
        Cycling,
        Phong3D,
        Pbr3D,
    }

    /// <summary>UI-neutral mirror of the PBR lighting profile enum.</summary>
    public enum PbrLightingModeDef
    {
        PBRRealistic,
        PBRBright,
    }

    /// <summary>One gradient stop: position in [0,1] plus opaque RGB.</summary>
    public sealed class ColorStopDef
    {
        public float Position { get; set; }
        public byte R { get; set; }
        public byte G { get; set; }
        public byte B { get; set; }
    }

    /// <summary>
    /// Directional light source. Direction (Lx,Ly,Lz) is normalised by the
    /// runtime; the editor stores it raw and lets the renderer normalise.
    /// Diffuse / specular channels are 0..1 floats (matches the runtime
    /// LightSource class in FracturingFog.Models).
    /// </summary>
    public sealed class LightSourceDef
    {
        public float Lx { get; set; }
        public float Ly { get; set; }
        public float Lz { get; set; }
        public float DiffR { get; set; }
        public float DiffG { get; set; }
        public float DiffB { get; set; }
        public float SpecR { get; set; }
        public float SpecG { get; set; }
        public float SpecB { get; set; }
        public float Shininess { get; set; } = 32f;
    }

    /// <summary>One band of the PBR piecewise metal/roughness function.</summary>
    public sealed class PbrMaterialBandDef
    {
        public float UpperT { get; set; } = 1.0f;
        public float Metal { get; set; } = 0.0f;
        public float Roughness { get; set; } = 0.7f;
    }

    /// <summary>Optional override colour for in-set pixels.</summary>
    public sealed class InSetColorDef
    {
        public byte R { get; set; }
        public byte G { get; set; }
        public byte B { get; set; }
    }

    /// <summary>
    /// Full UI-neutral colour theme definition. Mirrors <c>ColorThemeData</c>
    /// 1:1 but stores everything in primitives — no <c>System.Drawing.Color</c>,
    /// no runtime <c>LightSource</c> / <c>PbrLightingMode</c> references.
    /// </summary>
    public sealed class ColorThemeDef
    {
        // ── Identity / display ────────────────────────────────────────────
        public string Name { get; set; } = "Unnamed Theme";
        public string Category { get; set; } = "User";
        public string Description { get; set; } = "";
        public double? MaxRecommendedZoom { get; set; }
        public ColorThemeKindDef Kind { get; set; } = ColorThemeKindDef.Gradient;

        // ── Gradient ──────────────────────────────────────────────────────
        public List<ColorStopDef> Stops { get; set; } = new();

        // ── Cycling / 3D ──────────────────────────────────────────────────
        public float CycleSpeed { get; set; } = 0.02f;

        // ── 3D shared (Phong + PBR) ──────────────────────────────────────
        public float Steepness { get; set; } = 1.6f;
        public float Ambient { get; set; } = 0.12f;
        public LightSourceDef? KeyLight { get; set; }
        public LightSourceDef? FillLight { get; set; }
        public LightSourceDef? RimLight { get; set; }

        // ── Phong3D extras ───────────────────────────────────────────────
        public float KeySpecScale { get; set; } = 0.85f;
        public float FillSpecScale { get; set; } = 0.25f;
        public float FillDiffScale { get; set; } = 0.35f;
        public float RimSpecScale { get; set; } = 1.0f;
        public float RimDiffScale { get; set; } = 0.20f;

        // ── PBR extras ───────────────────────────────────────────────────
        public PbrLightingModeDef PbrLightingMode { get; set; } = PbrLightingModeDef.PBRRealistic;
        public float GlowBoostExponent { get; set; } = 8f;
        public float GlowBoostScale { get; set; } = 0f;
        public List<PbrMaterialBandDef> MaterialBands { get; set; } = new();

        // ── In-set override ──────────────────────────────────────────────
        public InSetColorDef? InSetColor { get; set; }

        // ── Post-FX defaults (optional) ──────────────────────────────────
        public int? Brightness { get; set; }
        public int? Contrast { get; set; }
        public int? Adaptive { get; set; }
    }
}
