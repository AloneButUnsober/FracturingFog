// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Models/ColorSchemes/WoodColorThemes.cs
//
// Three colour map themes inspired by natural wood grain.
//
// Visual philosophy
//   Wood grain moves through warm dark heartwood browns → mid-tone amber →
//   golden sapwood → pale cream highlight, then cycles back.  The grain rings
//   give natural wood its characteristic banding — the cycling palette
//   reproduces this with concentric warm-tone rings at every zoom level.
//
//   The 3D Phong theme adds a warm side-light and a cool shadow fill to make
//   the fractal ridges look like carved or turned wood — raised grain catches
//   warm amber highlights; grooves fall into cool umber shadow.
//
// Themes provided:
//   WoodGrainGradient        — linear gradient (t = smooth / maxIter)
//   WoodGrainCycling         — cycling gradient (repeating grain rings)
//   WoodGrainPhong3D         — cycling gradient + dual-light Phong (realistic wood)

using FracturingFog.Interefaces;
using System.Drawing;

namespace FracturingFog.Models
{
    // ── Shared stop factory ───────────────────────────────────────────────────

    internal static class WoodStops
    {
        /// <summary>
        /// Builds the canonical wood-grain gradient stops.
        ///
        /// Stop map (position → colour description):
        ///   0.00  very dark espresso heartwood
        ///   0.08  deep walnut brown
        ///   0.18  rich mahogany
        ///   0.28  warm mid-brown (oak heartwood)
        ///   0.38  amber-brown (teak)
        ///   0.48  golden amber (fresh-cut oak)
        ///   0.58  warm honey-gold
        ///   0.68  pale gold / light maple
        ///   0.78  cream-wheat sapwood
        ///   0.86  very pale ash / birch highlight
        ///   0.93  warm ivory (grain highlight peak)
        ///   1.00  back to dark espresso (grain ring boundary — good for cycling)
        /// </summary>
        internal static System.Collections.Generic.List<ColorStop> Build()
        {
            return new System.Collections.Generic.List<ColorStop>
            {
                new(0.00f, Color.FromArgb( 18,  10,   5)),   // espresso heartwood
                new(0.08f, Color.FromArgb( 42,  22,   9)),   // deep walnut
                new(0.18f, Color.FromArgb( 82,  38,  14)),   // rich mahogany
                new(0.28f, Color.FromArgb(115,  60,  20)),   // oak heartwood
                new(0.38f, Color.FromArgb(150,  88,  30)),   // teak amber-brown
                new(0.48f, Color.FromArgb(185, 120,  40)),   // fresh-cut oak
                new(0.58f, Color.FromArgb(210, 155,  55)),   // honey-gold
                new(0.68f, Color.FromArgb(225, 185,  90)),   // light maple gold
                new(0.78f, Color.FromArgb(235, 210, 140)),   // cream-wheat sapwood
                new(0.86f, Color.FromArgb(242, 228, 180)),   // pale ash highlight
                new(0.93f, Color.FromArgb(248, 238, 205)),   // warm ivory peak
                new(1.00f, Color.FromArgb( 22,  12,   6)),   // back to espresso (wrap)
            };
        }
    }

    // =========================================================================
    // 1. Linear gradient — WoodGrainGradient
    // =========================================================================

    /// <summary>
    /// Linear gradient across natural wood-grain tones.
    /// Low-iteration (deep) pixels are dark espresso heartwood; high-iteration
    /// pixels sweep through mahogany → amber → honey-gold → pale ivory sapwood.
    /// The gradient stretches once across the full iteration range.
    /// </summary>
    public sealed class WoodGrainGradient : GradientColorMap
    {
        public static string Name        => "Wood Grain";
        public static string Category    => "Natural";
        public static string Description =>
            "Linear gradient across natural wood tones — " +
            "dark espresso heartwood to pale ivory sapwood highlight.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.GradientBased |
            ColorMapFeatures.Perceptual;

        public new ColorPaletteType Type => ColorPaletteType.GradientLinear;

        public WoodGrainGradient()
        {
            Stops.AddRange(WoodStops.Build());
        }
    }

    // =========================================================================
    // 2. Cycling gradient — WoodGrainCycling
    // =========================================================================

    /// <summary>
    /// Cycling variant of the wood grain gradient.
    /// The palette repeats every ~50 smooth-iteration units, producing concentric
    /// warm-tone rings that resemble annual growth rings in a cross-section of wood.
    /// Deep-zoom images remain richly coloured rather than collapsing to a single hue.
    /// </summary>
    public sealed class WoodGrainCycling : CyclingGradientColorMap
    {
        public static string Name        => "Wood Grain Cycling";
        public static string Category    => "Natural";
        public static string Description =>
            "Cycling wood-grain palette — repeating amber-brown growth rings at all zoom depths.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.GradientBased |
            ColorMapFeatures.Cyclic;

        public new ColorPaletteType Type => ColorPaletteType.GradientCyclic;

        // One full cycle every ~50 smooth units  (1/0.02 = 50)
        protected override float CycleSpeed => 0.020f;

        public WoodGrainCycling()
        {
            Stops.AddRange(WoodStops.Build());
        }
    }

    // =========================================================================
    // 3. 3D Phong — WoodGrainPhong3D
    // =========================================================================

    /// <summary>
    /// 3D Phong relief version of the wood grain cycling theme.
    ///
    /// Lighting design — "carved and polished hardwood" look:
    ///
    ///   Key light  — positioned high-left, warm incandescent (2 900 K).
    ///               Warm amber-white diffuse casts natural workshop lighting
    ///               across raised grain ridges.  Tight specular (shininess 160)
    ///               produces the small, brilliant highlight seen on lacquered
    ///               or oiled wood surfaces.
    ///
    ///   Fill light — low-right, cool umber-brown shadow fill.
    ///               Prevents grooves from going flat black; instead they read
    ///               as deep cool umber — the colour of wood in open shadow.
    ///               Broad, soft specular (shininess 18) adds a faint waxy sheen
    ///               across shadowed faces.
    ///
    ///   Steepness  — 1.4  (moderate carving — clear relief without being harsh).
    ///   Ambient    — 0.14 (enough to keep dark grain readable; wood is opaque).
    ///   KeySpecScale — 0.70 (controlled gloss — oiled wood, not mirror-polished).
    ///   FillDiffScale — 0.40 (visible shadow-side illumination from a second window).
    /// </summary>
    public sealed class WoodGrainPhong3D : GradientPhong3DBase
    {
        public static string Name        => "Wood Grain 3D";
        public static string Category    => "Natural";
        public static string Description =>
            "Wood grain with 3D Phong lighting — warm amber highlights on raised " +
            "ridges, deep umber shadows in grooves.  Resembles carved hardwood.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.GradientBased |
            ColorMapFeatures.UsesNormals | ColorMapFeatures.Cyclic |
            ColorMapFeatures.ThreeDEffect;

        public new ColorPaletteType Type => ColorPaletteType.Relief3D;

        // Match the flat cycling sibling's ring frequency.
        protected override float CycleSpeed   => 0.020f;

        // Moderate depth — grain relief is visible but not overly dramatic.
        protected override float Steepness    => 1.4f;

        // Readable ambient — wood absorbs and scatters light; deep shadows still show grain.
        protected override float Ambient      => 0.14f;

        // Controlled gloss — oiled/waxed wood, not lacquered.
        protected override float KeySpecScale => 0.70f;

        // Subtle fill specular — soft waxy sheen in shadow.
        protected override float FillSpecScale => 0.22f;

        // Visible fill diffuse — secondary window light on the shadow side.
        protected override float FillDiffScale => 0.40f;

        public WoodGrainPhong3D()
        {
            Stops.AddRange(WoodStops.Build());

            // ── Key light: high-left, warm incandescent workshop lamp ─────────
            // Direction: upper-left, angled comfortably toward the viewer.
            // Diffuse: warm white-amber (tungsten, ~2900 K) to enrich the gold tones.
            // Specular: warm white with a slight amber tint — lacquered wood highlight.
            // Shininess 160: tight but not needle-sharp — polished, not mirrored.
            KeyLight = new LightSource(
                lx: -0.50f, ly:  0.65f, lz: 0.70f,        // upper-left, toward viewer
                diffR: 1.00f, diffG: 0.88f, diffB: 0.68f,  // warm amber-white diffuse
                specR: 1.00f, specG: 0.92f, specB: 0.78f,  // warm white specular
                shininess: 160f);

            // ── Fill light: low-right, cool umber shadow fill ─────────────────
            // Direction: lower-right, barely angled toward the viewer.
            // Diffuse: desaturated cool umber — open-shade shadow colour on wood.
            // Specular: muted brown-grey — barely-visible waxy sheen.
            // Shininess 18: very broad and soft; just enough to lift shadow detail.
            FillLight = new LightSource(
                lx:  0.55f, ly: -0.45f, lz: 0.50f,        // lower-right
                diffR: 0.38f, diffG: 0.28f, diffB: 0.18f,  // cool umber diffuse
                specR: 0.30f, specG: 0.24f, specB: 0.18f,  // muted brown-grey specular
                shininess: 18f);
        }
    }
}
