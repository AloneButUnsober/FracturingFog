// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// ColorCore/RandomThemeGenerator.cs
//
// #434 slice 1 — headless random colour-theme generator. Produces a complete
// ColorThemeDef for a given Kind from a seed, mirroring the artful/experimental
// ranges of the Color Theme Editor's 🎲 (ColorThemeEditorViewModel.RandomizePalette,
// #83 / #524). Pure + deterministic by seed so it is unit-testable and callable
// off the UI thread — the slideshow (#434 slices 2/3) drives it per theme-slot.
//
// Lives in FracturingFog.ColorCore (refs only Abstractions) so both the shell
// and headless callers can use it without the UI VM. The editor's own 🎲 still
// has its private copy of this algorithm; folding it onto this core (single
// source of truth) is the slice-1b follow-up — kept separate here to avoid
// re-touching the recently-fixed editor in the same change.

using System;
using System.Collections.Generic;
using FracturingFog.Models;

namespace FracturingFog.Imaging
{
    /// <summary>Generates a random <see cref="ColorThemeDef"/> for a Kind.</summary>
    public static class RandomThemeGenerator
    {
        /// <summary>Build a random theme of <paramref name="kind"/> from
        /// <paramref name="seed"/>. <paramref name="experimental"/> off = "artful"
        /// (clamped, palette-coherent ranges); on = full-range "wild". The same
        /// seed + kind + mode reproduces the same theme. Post-FX defaults are left
        /// neutral (null) — a slideshow theme should not hijack global grading.</summary>
        public static ColorThemeDef Generate(
            ColorThemeKindDef kind, int seed, bool experimental = false, string? name = null)
        {
            var rng = new Random(seed);
            bool wild = experimental;

            var def = new ColorThemeDef
            {
                Name = string.IsNullOrWhiteSpace(name)
                    ? $"Random {kind} (seed {seed}, {(wild ? "experimental" : "artful")})"
                    : name!,
                Category = "Random",
                Description = $"Random theme (seed {seed}, {(wild ? "experimental" : "artful")})",
                Kind = kind,
            };

            var pal = BuildStops(def, rng, wild);
            BuildInterpolation(def, rng, wild);

            if (kind != ColorThemeKindDef.Gradient)
                BuildCycle(def, rng, wild);

            if (kind == ColorThemeKindDef.Phong3D || kind == ColorThemeKindDef.Pbr3D)
                BuildLights(def, rng, wild, pal);

            if (kind == ColorThemeKindDef.Phong3D)
                BuildPhongExtras(def, rng, wild);

            if (kind == ColorThemeKindDef.Pbr3D)
                BuildPbr(def, rng, wild);

            if (kind == ColorThemeKindDef.OrbitTrap)
                BuildOrbitTrap(def, rng, wild);

            BuildInSet(def, rng, wild, pal);

            return def;
        }

        // ── Section builders (mirror ColorThemeEditorViewModel.Randomize*) ──────

        private static List<(byte R, byte G, byte B)> BuildStops(ColorThemeDef def, Random rng, bool wild)
        {
            const double golden = 0.6180339887498949;
            int n = wild ? rng.Next(3, 9) : 5;
            double hue = rng.NextDouble();
            double satLo = wild ? 0.10 : 0.55, satHi = wild ? 1.00 : 0.95;
            double valLo = wild ? 0.20 : 0.65, valHi = 1.00;

            var pal = new List<(byte, byte, byte)>(n);
            def.Stops = new List<ColorStopDef>(n);
            for (int i = 0; i < n; i++)
            {
                hue = (hue + golden) % 1.0;
                double sat = Rng(rng, satLo, satHi);
                double val = Rng(rng, valLo, valHi);
                var (r, g, b) = HsvToRgb(hue * 360.0, sat, val);
                float pos = n > 1 ? i / (float)(n - 1) : 0.5f;
                def.Stops.Add(new ColorStopDef { Position = pos, R = r, G = g, B = b });
                pal.Add((r, g, b));
            }
            return pal;
        }

        private static void BuildInterpolation(ColorThemeDef def, Random rng, bool wild)
        {
            def.InterpolationSpace = Pick<GradientColorSpaceDef>(rng);
            def.InterpolationCurve = Pick<InterpolationCurveDef>(rng);
            def.TransferFunction = Pick<TransferFunctionDef>(rng);
            def.TransferStrength = (float)(wild ? Rng(rng, 0, 1) : Rng(rng, 0.3, 1.0));
            def.PaletteGamma = (float)(wild ? Rng(rng, 0.2, 3.0) : Rng(rng, 0.7, 1.6));
        }

        private static void BuildCycle(ColorThemeDef def, Random rng, bool wild)
        {
            def.ColorOffset = (float)(wild ? Rng(rng, -10, 10) : Rng(rng, -1, 1));
            def.ColorDensity = (float)(wild ? Rng(rng, 0, 20) : Rng(rng, 0.5, 4));
            def.CycleSpeed = (float)(wild ? Rng(rng, 0.0001, 10) : Rng(rng, 0.005, 0.1));
            def.WrapMode = Pick<ColorWrapModeDef>(rng);
            def.SeamlessCycle = wild ? rng.NextDouble() < 0.5 : true;

            if (wild && rng.NextDouble() < 0.3)
            {
                def.SparkleStride = rng.Next(4, 25);
                def.SparkleBoost = (float)Rng(rng, 0.3, 0.8);
            }
            else { def.SparkleStride = 0; def.SparkleBoost = 0f; }

            if (wild && rng.NextDouble() < 0.15)
            {
                def.XorLevels = rng.Next(4, 33);
                def.XorMask = rng.Next(1, def.XorLevels);
            }
            else { def.XorLevels = 0; def.XorMask = 0; }
        }

        private static void BuildLights(ColorThemeDef def, Random rng, bool wild, List<(byte R, byte G, byte B)> pal)
        {
            def.Steepness = (float)(wild ? Rng(rng, 0.1, 10) : Rng(rng, 0.8, 3.0));
            def.Ambient = (float)(wild ? Rng(rng, 0, 1) : Rng(rng, 0.05, 0.30));

            def.KeyLight = MakeLight(rng, wild, pal, shinLo: 16, shinHi: 128);
            def.FillLight = MakeLight(rng, wild, pal, shinLo: 8, shinHi: 64);

            bool useRim = rng.NextDouble() < (wild ? 0.70 : 0.40);
            def.RimLight = useRim ? MakeLight(rng, wild, pal, shinLo: 64, shinHi: 256) : null;
        }

        private static LightSourceDef MakeLight(Random rng, bool wild, List<(byte R, byte G, byte B)> pal, int shinLo, int shinHi)
        {
            var light = new LightSourceDef
            {
                Lx = (float)Rng(rng, -1, 1),
                Ly = (float)Rng(rng, -1, 1),
                Lz = (float)(wild ? Rng(rng, -1, 1) : Rng(rng, 0.3, 1.0)),
            };

            if (wild)
            {
                light.DiffR = RandUnit(rng); light.DiffG = RandUnit(rng); light.DiffB = RandUnit(rng);
                light.SpecR = RandUnit(rng); light.SpecG = RandUnit(rng); light.SpecB = RandUnit(rng);
            }
            else
            {
                var (r, g, b) = pal.Count > 0 ? pal[rng.Next(pal.Count)] : ((byte)255, (byte)255, (byte)255);
                light.DiffR = (float)Lerp(r, 255, 0.35) / 255f;
                light.DiffG = (float)Lerp(g, 255, 0.35) / 255f;
                light.DiffB = (float)Lerp(b, 255, 0.35) / 255f;
                float s = rng.Next(200, 256) / 255f;
                light.SpecR = s; light.SpecG = s; light.SpecB = s;
            }

            light.Shininess = wild ? rng.Next(1, 513) : rng.Next(shinLo, shinHi + 1);
            return light;
        }

        private static void BuildPhongExtras(ColorThemeDef def, Random rng, bool wild)
        {
            def.KeySpecScale = (float)(wild ? Rng(rng, 0, 10) : Rng(rng, 0.4, 1.2));
            def.FillSpecScale = (float)(wild ? Rng(rng, 0, 10) : Rng(rng, 0.1, 0.5));
            def.FillDiffScale = (float)(wild ? Rng(rng, 0, 10) : Rng(rng, 0.2, 0.6));
            def.RimSpecScale = (float)(wild ? Rng(rng, 0, 10) : Rng(rng, 0.5, 1.5));
            def.RimDiffScale = (float)(wild ? Rng(rng, 0, 10) : Rng(rng, 0.1, 0.4));
        }

        private static void BuildPbr(ColorThemeDef def, Random rng, bool wild)
        {
            def.PbrLightingMode = Pick<PbrLightingModeDef>(rng);
            def.GlowBoostExponent = (float)(wild ? Rng(rng, 0, 50) : Rng(rng, 2, 16));
            def.GlowBoostScale = (float)(wild ? Rng(rng, 0, 10) : Rng(rng, 0, 2));

            int bandCount = wild ? rng.Next(1, 9) : rng.Next(2, 5);
            def.MaterialBands = new List<PbrMaterialBandDef>(bandCount);
            for (int i = 0; i < bandCount; i++)
            {
                float upper = (i == bandCount - 1) ? 1f : (i + 1) / (float)bandCount;
                float metal = (float)(wild ? rng.NextDouble() : Rng(rng, 0, 1));
                float rough = (float)(wild ? rng.NextDouble() : Rng(rng, 0.2, 0.9));
                def.MaterialBands.Add(new PbrMaterialBandDef { UpperT = upper, Metal = metal, Roughness = rough });
            }
        }

        private static void BuildOrbitTrap(ColorThemeDef def, Random rng, bool wild)
        {
            def.TrapShape = Pick<OrbitTrapShapeDef>(rng);
            def.TrapScale = (float)(wild ? Rng(rng, 0.1, 100) : Rng(rng, 0.8, 4));
            def.TrapPower = (float)(wild ? Rng(rng, 0.05, 8) : Rng(rng, 0.2, 1.2));
            def.ColorInterior = rng.NextDouble() < (wild ? 0.5 : 0.3);
        }

        private static void BuildInSet(ColorThemeDef def, Random rng, bool wild, List<(byte R, byte G, byte B)> pal)
        {
            if (wild)
            {
                def.InSetColor = new InSetColorDef
                {
                    R = RandByte(rng), G = RandByte(rng), B = RandByte(rng), A = RandByte(rng),
                };
            }
            else
            {
                var (r, g, b) = pal.Count > 0 ? pal[rng.Next(pal.Count)] : ((byte)0, (byte)0, (byte)0);
                def.InSetColor = new InSetColorDef
                {
                    R = (byte)Lerp(r, 0, 0.75),
                    G = (byte)Lerp(g, 0, 0.75),
                    B = (byte)Lerp(b, 0, 0.75),
                    A = 255,
                };
            }
        }

        // ── Helpers ─────────────────────────────────────────────────────────

        private static double Lerp(double a, double b, double t) => a + (b - a) * t;
        private static double Rng(Random r, double lo, double hi) => Lerp(lo, hi, r.NextDouble());
        private static byte RandByte(Random r) => (byte)r.Next(0, 256);
        private static float RandUnit(Random r) => r.Next(0, 256) / 255f;

        private static T Pick<T>(Random r) where T : struct, Enum
        {
            var vals = Enum.GetValues<T>();
            return vals[r.Next(vals.Length)];
        }

        /// <summary>HSV (h in degrees [0,360), s,v in [0,1]) → 8-bit RGB. Matches
        /// the editor's HsvColor.ToRgb for the ranges used here.</summary>
        public static (byte R, byte G, byte B) HsvToRgb(double h, double s, double v)
        {
            h = ((h % 360.0) + 360.0) % 360.0;
            s = Math.Clamp(s, 0.0, 1.0);
            v = Math.Clamp(v, 0.0, 1.0);

            double c = v * s;
            double x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
            double m = v - c;
            double r1, g1, b1;
            if (h < 60) { r1 = c; g1 = x; b1 = 0; }
            else if (h < 120) { r1 = x; g1 = c; b1 = 0; }
            else if (h < 180) { r1 = 0; g1 = c; b1 = x; }
            else if (h < 240) { r1 = 0; g1 = x; b1 = c; }
            else if (h < 300) { r1 = x; g1 = 0; b1 = c; }
            else { r1 = c; g1 = 0; b1 = x; }

            byte To(double u) => (byte)Math.Clamp((int)Math.Round((u + m) * 255.0), 0, 255);
            return (To(r1), To(g1), To(b1));
        }
    }
}
