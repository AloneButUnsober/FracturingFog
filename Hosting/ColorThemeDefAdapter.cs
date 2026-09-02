// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Hosting/ColorThemeDefAdapter.cs
//
// Translates between the UI-neutral ColorThemeDef hierarchy (lives in
// FracturingFog.Abstractions / Models) and the legacy ColorThemeData
// hierarchy (lives in the main FracturingFog WinExe alongside the
// runtime LightSource / PbrLightingMode classes). Used by
// HostColorThemeService and the Avalonia shell bootstrap to keep the
// UI.Avalonia assembly free of System.Drawing and runtime renderer
// types.

using System.Collections.Generic;
using System.Linq;

using FracturingFog.Models;

namespace FracturingFog.Hosting
{
    internal static class ColorThemeDefAdapter
    {
        // ── Def ──→ Data ────────────────────────────────────────────────────

        public static ColorThemeData ToData(ColorThemeDef def)
        {
            return new ColorThemeData
            {
                Name = def.Name,
                Category = def.Category,
                Description = def.Description,
                MaxRecommendedZoom = def.MaxRecommendedZoom,
                Kind = ToKind(def.Kind),

                Stops = def.Stops.Select(ToData).ToList(),

                TrapShape = (OrbitTrapShape)def.TrapShape,
                TrapScale = def.TrapScale,
                TrapPower = def.TrapPower,
                ColorInterior = def.ColorInterior,

                InterpolationSpace = (GradientColorSpace)def.InterpolationSpace,
                InterpolationCurve = (InterpolationCurve)def.InterpolationCurve,
                TransferFunction = (TransferFunction)def.TransferFunction,
                TransferStrength = def.TransferStrength,
                PaletteGamma = def.PaletteGamma,

                CycleSpeed = def.CycleSpeed,

                ColorOffset = def.ColorOffset,
                ColorDensity = def.ColorDensity,
                WrapMode = (ColorWrapMode)def.WrapMode,
                SparkleStride = def.SparkleStride,
                SparkleBoost = def.SparkleBoost,
                SeamlessCycle = def.SeamlessCycle,
                XorLevels = def.XorLevels,
                XorMask = def.XorMask,

                Steepness = def.Steepness,
                Ambient = def.Ambient,
                KeyLight = ToData(def.KeyLight),
                FillLight = ToData(def.FillLight),
                RimLight = ToData(def.RimLight),

                KeySpecScale = def.KeySpecScale,
                FillSpecScale = def.FillSpecScale,
                FillDiffScale = def.FillDiffScale,
                RimSpecScale = def.RimSpecScale,
                RimDiffScale = def.RimDiffScale,

                PbrLightingMode = ToMode(def.PbrLightingMode),
                GlowBoostExponent = def.GlowBoostExponent,
                GlowBoostScale = def.GlowBoostScale,
                MaterialBands = def.MaterialBands.Select(ToData).ToList(),

                InSetColor = ToData(def.InSetColor),

                Brightness = def.Brightness,
                Contrast = def.Contrast,
                Adaptive = def.Adaptive,
            };
        }

        private static ColorStopData ToData(ColorStopDef s) => new ColorStopData
        {
            Position = s.Position,
            R = s.R, G = s.G, B = s.B, A = s.A,
            Midpoint = s.Midpoint,
        };

        private static LightSourceData? ToData(LightSourceDef? d) => d == null ? null : new LightSourceData
        {
            Lx = d.Lx, Ly = d.Ly, Lz = d.Lz,
            DiffR = d.DiffR, DiffG = d.DiffG, DiffB = d.DiffB,
            SpecR = d.SpecR, SpecG = d.SpecG, SpecB = d.SpecB,
            Shininess = d.Shininess,
        };

        private static PbrMaterialBandData ToData(PbrMaterialBandDef b) => new PbrMaterialBandData
        {
            UpperT = b.UpperT,
            Metal = b.Metal,
            Roughness = b.Roughness,
        };

        private static InSetColorData? ToData(InSetColorDef? c) => c == null ? null : new InSetColorData(c.R, c.G, c.B) { A = c.A };

        private static ColorThemeKind ToKind(ColorThemeKindDef k) => k switch
        {
            ColorThemeKindDef.Gradient  => ColorThemeKind.Gradient,
            ColorThemeKindDef.Cycling   => ColorThemeKind.Cycling,
            ColorThemeKindDef.Phong3D   => ColorThemeKind.Phong3D,
            ColorThemeKindDef.Pbr3D     => ColorThemeKind.Pbr3D,
            ColorThemeKindDef.OrbitTrap => ColorThemeKind.OrbitTrap,
            _ => ColorThemeKind.Gradient,
        };

        private static PbrLightingMode ToMode(PbrLightingModeDef m) => m switch
        {
            PbrLightingModeDef.PBRRealistic => PbrLightingMode.PBRRealistic,
            PbrLightingModeDef.PBRBright    => PbrLightingMode.PBRBright,
            _ => PbrLightingMode.PBRRealistic,
        };

        // ── Data ──→ Def ────────────────────────────────────────────────────

        public static ColorThemeDef ToDef(ColorThemeData data)
        {
            return new ColorThemeDef
            {
                Name = data.Name,
                Category = data.Category,
                Description = data.Description,
                MaxRecommendedZoom = data.MaxRecommendedZoom,
                Kind = ToKindDef(data.Kind),

                Stops = (data.Stops ?? new List<ColorStopData>()).Select(ToDef).ToList(),

                TrapShape = (OrbitTrapShapeDef)data.TrapShape,
                TrapScale = data.TrapScale,
                TrapPower = data.TrapPower,
                ColorInterior = data.ColorInterior,

                InterpolationSpace = (GradientColorSpaceDef)data.InterpolationSpace,
                InterpolationCurve = (InterpolationCurveDef)data.InterpolationCurve,
                TransferFunction = (TransferFunctionDef)data.TransferFunction,
                TransferStrength = data.TransferStrength,
                PaletteGamma = data.PaletteGamma,

                CycleSpeed = data.CycleSpeed,

                ColorOffset = data.ColorOffset,
                ColorDensity = data.ColorDensity,
                WrapMode = (ColorWrapModeDef)data.WrapMode,
                SparkleStride = data.SparkleStride,
                SparkleBoost = data.SparkleBoost,
                SeamlessCycle = data.SeamlessCycle,
                XorLevels = data.XorLevels,
                XorMask = data.XorMask,

                Steepness = data.Steepness,
                Ambient = data.Ambient,
                KeyLight = ToDef(data.KeyLight),
                FillLight = ToDef(data.FillLight),
                RimLight = ToDef(data.RimLight),

                KeySpecScale = data.KeySpecScale,
                FillSpecScale = data.FillSpecScale,
                FillDiffScale = data.FillDiffScale,
                RimSpecScale = data.RimSpecScale,
                RimDiffScale = data.RimDiffScale,

                PbrLightingMode = ToModeDef(data.PbrLightingMode),
                GlowBoostExponent = data.GlowBoostExponent,
                GlowBoostScale = data.GlowBoostScale,
                MaterialBands = (data.MaterialBands ?? new List<PbrMaterialBandData>()).Select(ToDef).ToList(),

                InSetColor = ToDef(data.InSetColor),

                Brightness = data.Brightness,
                Contrast = data.Contrast,
                Adaptive = data.Adaptive,
            };
        }

        private static ColorStopDef ToDef(ColorStopData s) => new ColorStopDef
        {
            Position = s.Position,
            R = s.R, G = s.G, B = s.B, A = s.A,
            Midpoint = s.Midpoint <= 0f ? 0.5f : s.Midpoint,
        };

        private static LightSourceDef? ToDef(LightSourceData? d) => d == null ? null : new LightSourceDef
        {
            Lx = d.Lx, Ly = d.Ly, Lz = d.Lz,
            DiffR = d.DiffR, DiffG = d.DiffG, DiffB = d.DiffB,
            SpecR = d.SpecR, SpecG = d.SpecG, SpecB = d.SpecB,
            Shininess = d.Shininess,
        };

        private static PbrMaterialBandDef ToDef(PbrMaterialBandData b) => new PbrMaterialBandDef
        {
            UpperT = b.UpperT,
            Metal = b.Metal,
            Roughness = b.Roughness,
        };

        private static InSetColorDef? ToDef(InSetColorData? c) => c == null ? null : new InSetColorDef
        {
            R = c.R, G = c.G, B = c.B, A = c.A,
        };

        private static ColorThemeKindDef ToKindDef(ColorThemeKind k) => k switch
        {
            ColorThemeKind.Gradient  => ColorThemeKindDef.Gradient,
            ColorThemeKind.Cycling   => ColorThemeKindDef.Cycling,
            ColorThemeKind.Phong3D   => ColorThemeKindDef.Phong3D,
            ColorThemeKind.Pbr3D     => ColorThemeKindDef.Pbr3D,
            ColorThemeKind.OrbitTrap => ColorThemeKindDef.OrbitTrap,
            _ => ColorThemeKindDef.Gradient,
        };

        private static PbrLightingModeDef ToModeDef(PbrLightingMode m) => m switch
        {
            PbrLightingMode.PBRRealistic => PbrLightingModeDef.PBRRealistic,
            PbrLightingMode.PBRBright    => PbrLightingModeDef.PBRBright,
            _ => PbrLightingModeDef.PBRRealistic,
        };
    }
}
