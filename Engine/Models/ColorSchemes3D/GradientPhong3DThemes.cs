// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Models/ColorSchemes/GradientPhong3DThemes.cs
//
// All 13 gradient-based 3D colour themes.
//
// Each class:
//   • Inherits GradientPhong3DBase (which handles all Phong maths and
//     correct interface routing — no LitMap boilerplate needed here).
//   • Sets gradient stops matching the flat counterpart exactly.
//   • Configures KeyLight and FillLight to match each palette's atmosphere.
//   • Overrides CycleSpeed to match the flat counterpart's cycle rate.
//   • Overrides Steepness and Shininess as appropriate for the surface feel.
//
// Lighting design rationale per theme is documented inline.

using FracturingFog.Interefaces;
using System.Drawing;

namespace FracturingFog.Models
{
    /// <summary>
    /// Arctic polar night with Phong 3D relief shading.
    /// Cold blue-white key light from upper-right; faint warm amber fill from
    /// lower-left.  Colour distribution matches the flat PolarNight theme.
    /// </summary>
    public class PolarNight3DMap : GradientPhong3DBase
    {
        public static string Name => "Polar Night 3D";
        public new static ColorPaletteType Type => ColorPaletteType.Relief3D;
        public static string Category => "3D Relief";
        public static string Description => "PolarNight gradient as a 3D Phong relief — cold key light, warm amber fill.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth |
            ColorMapFeatures.UsesNormals |
            ColorMapFeatures.GradientBased |
            ColorMapFeatures.ThreeDEffect;


        protected override float CycleSpeed => 0.02f;

        //protected override float Steepness => 1.4f;   // moderately carved

        //protected override float KeySpecScale => 1.10f;  // hot metal gleam — slightly boosted

        //protected override float FillDiffScale => 0.30f;

        // ── Gradient stops — identical to the flat PolarNight theme ───────────

        public PolarNight3DMap()
        {
            // ── Light setup ───────────────────────────────────────────────────────

            // Cold blue-white key light: upper-right, ~40° above horizontal.
            KeyLight = new LightSource(
                 lx: 0.60f, ly: 0.45f, lz: 0.80f,
                 diffR: 0.70f, diffG: 0.80f, diffB: 1.00f,
                 specR: 0.80f, specG: 0.90f, specB: 1.00f,
                 shininess: 55f);

            // Warm amber fill light: lower-left, dim.
            FillLight = new LightSource(
                lx: -0.70f, ly: -0.40f, lz: 0.45f,
                diffR: 0.55f, diffG: 0.35f, diffB: 0.10f,
                specR: 0.30f, specG: 0.20f, specB: 0.05f,
                shininess: 12f);

            Stops.Add(new ColorStop(0.00f, Color.FromArgb(2, 4, 15)));  // near-black navy
            Stops.Add(new ColorStop(0.12f, Color.FromArgb(8, 20, 55)));  // midnight blue
            Stops.Add(new ColorStop(0.28f, Color.FromArgb(25, 40, 100)));  // deep blue
            Stops.Add(new ColorStop(0.45f, Color.FromArgb(50, 60, 140)));  // blue-violet
            Stops.Add(new ColorStop(0.60f, Color.FromArgb(80, 90, 170)));  // periwinkle
            Stops.Add(new ColorStop(0.74f, Color.FromArgb(130, 160, 210)));  // dusty blue
            Stops.Add(new ColorStop(0.88f, Color.FromArgb(190, 220, 240)));  // pale aqua
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(225, 245, 255)));  // icy white-blue
        }
    }
    
        // ─────────────────────────────────────────────────────────────────────────
        // BLACK BODY RADIATION 3D
        // Palette: black → very dark brown → deep red → bright orange → gold → white
        // Character: incandescent metal heating from cool dark to white-hot
        // Lighting: warm forge/furnace light from upper-left; complementary cool
        //           blue shadow from lower-right; tight specular for hot metal sheen
        // ─────────────────────────────────────────────────────────────────────────
        public class Blackbody3DMap : GradientPhong3DBase
    {
        public static string Name        => "BB Rad 3D";
        public static string Category    => "3D Relief";
        public static string Description => "Black Body Radiation with forge lighting — hot metal 3D relief.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesNormals |
            ColorMapFeatures.GradientBased | ColorMapFeatures.ThreeDEffect;

        protected override float CycleSpeed  => 0.02f;
        protected override float Steepness   => 1.4f;   // moderately carved
        protected override float KeySpecScale => 1.10f;  // hot metal gleam — slightly boosted
        protected override float FillDiffScale => 0.30f;

        public Blackbody3DMap()
        {
            // Warm orange-white forge/furnace light from upper-left.
            // The orange tint amplifies the hot end of the gradient;
            // the pale blue tip picks up specular highlights nicely.
            KeyLight = new LightSource(
                lx: -0.55f, ly:  0.50f, lz: 0.85f,
                diffR: 1.00f, diffG: 0.65f, diffB: 0.25f,  // orange-white diffuse
                specR: 1.00f, specG: 0.90f, specB: 0.70f,  // warm specular
                shininess: 80f);

            // Cool blue-violet shadow fill from lower-right — complementary
            // to the hot orange, deepens the dark maroon areas dramatically.
            FillLight = new LightSource(
                lx:  0.65f, ly: -0.45f, lz: 0.40f,
                diffR: 0.15f, diffG: 0.20f, diffB: 0.55f,  // cool blue fill
                specR: 0.10f, specG: 0.15f, specB: 0.40f,
                shininess: 20f);

            Stops.Add(new ColorStop(0.0f, Color.Black));
            Stops.Add(new ColorStop(0.2f, ColorTranslator.FromHtml("#1F0C00")));
            Stops.Add(new ColorStop(0.4f, ColorTranslator.FromHtml("#7A1E00")));
            Stops.Add(new ColorStop(0.6f, ColorTranslator.FromHtml("#FF6A00")));
            Stops.Add(new ColorStop(0.8f, ColorTranslator.FromHtml("#FFD700")));
            Stops.Add(new ColorStop(1.0f, Color.White));
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // COSMIC LATTE 3D
    // Palette: near-black warm → dark coffee → caramel → honey → cream → gold → amber
    // Character: warm, nostalgic, cosy — coffee and candlelight
    // Lighting: golden candlelight key from upper-right; cool blue-grey shadow fill
    //           Soft shininess — warm wax or aged wood surface
    // ─────────────────────────────────────────────────────────────────────────
    public class CosmicLatte3DMap : GradientPhong3DBase
    {
        public static string Name        => "Cosmic Latte 3D";
        public static string Category    => "3D Relief";
        public static string Description => "Cosmic Latte with golden candlelight — warm 3D relief.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesNormals |
            ColorMapFeatures.GradientBased | ColorMapFeatures.ThreeDEffect;

        protected override float CycleSpeed  => 0.025f;
        protected override float Steepness   => 1.8f;   // gentle carving — cosy, not harsh
        protected override float Ambient     => 0.18f;  // slightly lifted ambient — warm shadows
        protected override float KeySpecScale => 0.55f;  // soft specular — not shiny
        protected override float FillDiffScale => 0.40f;

        public CosmicLatte3DMap()
        {
            // Golden candlelight from upper-right — warm, soft-edged.
            KeyLight = new LightSource(
                lx:  0.55f, ly:  0.45f, lz: 0.75f,
                diffR: 1.00f, diffG: 0.80f, diffB: 0.40f,  // golden diffuse
                specR: 1.00f, specG: 0.90f, specB: 0.60f,  // warm specular
                shininess: 30f);  // broad highlight — soft surface

            // Cool blue-grey shadow fill from lower-left.
            FillLight = new LightSource(
                lx: -0.60f, ly: -0.35f, lz: 0.50f,
                diffR: 0.30f, diffG: 0.35f, diffB: 0.50f,  // cool grey-blue
                specR: 0.10f, specG: 0.12f, specB: 0.20f,
                shininess: 10f);

            Stops.Add(new ColorStop(0.00f, Color.FromArgb( 15,  10,   5)));
            Stops.Add(new ColorStop(0.15f, Color.FromArgb( 80,  50,  20)));
            Stops.Add(new ColorStop(0.35f, Color.FromArgb(180, 120,  50)));
            Stops.Add(new ColorStop(0.55f, Color.FromArgb(240, 200, 130)));
            Stops.Add(new ColorStop(0.75f, Color.FromArgb(255, 240, 200)));
            Stops.Add(new ColorStop(0.90f, Color.FromArgb(255, 220, 100)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(200, 160,  40)));
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // DEEP SPACE 3D
    // Palette: near-black void → deep navy → bright blue → white starlight
    // Character: deep space — void to brilliant starlight
    // Lighting: brilliant hard cold-blue starlight from upper-right; faint
    //           purple-violet nebula glow fill; very tight specular
    // ─────────────────────────────────────────────────────────────────────────
    public class DeepSpaceBlue3DMap : GradientPhong3DBase
    {
        public static string Name        => "Deep Space 3D";
        public static string Category    => "3D Relief";
        public static string Description => "Deep Space with starlight — crystalline void 3D relief.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesNormals |
            ColorMapFeatures.GradientBased | ColorMapFeatures.ThreeDEffect;

        protected override float CycleSpeed  => 0.02f;
        protected override float Steepness   => 1.2f;   // sharper carving — hard crystalline void
        protected override float Ambient     => 0.08f;  // very dark ambient — space is dark
        protected override float KeySpecScale => 1.20f;  // brilliant starlight specular
        protected override float FillDiffScale => 0.25f;
        protected override float FillSpecScale => 0.20f;

        public DeepSpaceBlue3DMap()
        {
            // Brilliant cold blue-white starlight from upper-right.
            KeyLight = new LightSource(
                lx:  0.60f, ly:  0.45f, lz: 0.80f,
                diffR: 0.75f, diffG: 0.88f, diffB: 1.00f,  // cold blue-white
                specR: 0.85f, specG: 0.92f, specB: 1.00f,
                shininess: 90f);  // tight highlight — hard crystalline surface

            // Faint purple-violet nebula glow from lower-left.
            FillLight = new LightSource(
                lx: -0.65f, ly: -0.40f, lz: 0.35f,
                diffR: 0.25f, diffG: 0.10f, diffB: 0.40f,  // purple-violet
                specR: 0.15f, specG: 0.05f, specB: 0.25f,
                shininess: 15f);

            Stops.Add(new ColorStop(0.0f, ColorTranslator.FromHtml("#000010")));
            Stops.Add(new ColorStop(0.3f, ColorTranslator.FromHtml("#001060")));
            Stops.Add(new ColorStop(0.6f, ColorTranslator.FromHtml("#00A0FF")));
            Stops.Add(new ColorStop(1.0f, ColorTranslator.FromHtml("#FFFFFF")));
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // EARTH TONES 3D
    // Palette: dark bark → mid brown → sandy tan → pale linen
    // Character: natural earth, sandstone, soil — warm neutrals
    // Lighting: warm afternoon sunlight from upper-right; soft open-shade
    //           sky fill from opposite; very low shininess for rough matte earth
    // ─────────────────────────────────────────────────────────────────────────
    public class EarthTone3DMap : GradientPhong3DBase
    {
        public static string Name        => "Earth Tones 3D";
        public static string Category    => "3D Relief";
        public static string Description => "Earth Tones with afternoon sunlight — natural stone 3D relief.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesNormals |
            ColorMapFeatures.GradientBased | ColorMapFeatures.ThreeDEffect;

        protected override float CycleSpeed  => 0.02f;
        protected override float Steepness   => 2.0f;   // flatter — eroded landscape feel
        protected override float Ambient     => 0.22f;  // strong ambient — open sky scatters light
        protected override float KeySpecScale => 0.35f;  // very low specular — rough earth
        protected override float FillDiffScale => 0.55f; // strong fill — open-shade sky is bright

        public EarthTone3DMap()
        {
            // Warm yellow-white afternoon sun from upper-right.
            KeyLight = new LightSource(
                lx:  0.50f, ly:  0.55f, lz: 0.80f,
                diffR: 1.00f, diffG: 0.90f, diffB: 0.65f,  // warm yellow-white sunlight
                specR: 1.00f, specG: 0.95f, specB: 0.80f,
                shininess: 20f);  // wide highlight — rough sandstone

            // Soft sky-blue fill from upper-left (open shade).
            FillLight = new LightSource(
                lx: -0.70f, ly:  0.30f, lz: 0.55f,
                diffR: 0.40f, diffG: 0.55f, diffB: 0.75f,  // sky blue
                specR: 0.05f, specG: 0.08f, specB: 0.15f,
                shininess: 8f);

            Stops.Add(new ColorStop(0.0f, ColorTranslator.FromHtml("#2B1B0E")));
            Stops.Add(new ColorStop(0.3f, ColorTranslator.FromHtml("#705438")));
            Stops.Add(new ColorStop(0.6f, ColorTranslator.FromHtml("#C9A66B")));
            Stops.Add(new ColorStop(1.0f, ColorTranslator.FromHtml("#F2E9D8")));
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ICE FIRE 3D
    // Palette: Black → blue → cyan → orange-red → white
    // Character: maximum contrast between ice-cold and fire-hot
    // Lighting: pure neutral white key — doesn't bias either extreme;
    //           very high shininess so both ice facets and hot metal gleam
    // ─────────────────────────────────────────────────────────────────────────
    public class Icefire3DMap : GradientPhong3DBase
    {
        public static string Name        => "Ice Fire 3D";
        public static string Category    => "3D Relief";
        public static string Description => "Ice Fire with neutral white light — balanced extremes 3D relief.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesNormals |
            ColorMapFeatures.GradientBased | ColorMapFeatures.ThreeDEffect;

        protected override float CycleSpeed  => 0.02f;
        protected override float Steepness   => 1.3f;  // fairly sharp for contrast
        protected override float Ambient     => 0.10f;
        protected override float KeySpecScale => 1.15f; // strong specular to pop off both ice and fire
        protected override float FillDiffScale => 0.45f;

        public Icefire3DMap()
        {
            // Pure neutral white — preserves colour identity of both ice and fire.
            // Slightly elevated from above-right so shadows clearly define structure.
            KeyLight = new LightSource(
                lx:  0.50f, ly:  0.50f, lz: 0.90f,
                diffR: 1.00f, diffG: 1.00f, diffB: 1.00f,  // pure white
                specR: 1.00f, specG: 1.00f, specB: 1.00f,
                shininess: 120f);  // very tight — both crystal ice and polished metal gleam

            // Neutral mid-grey fill from lower-left — no colour bias.
            FillLight = new LightSource(
                lx: -0.65f, ly: -0.40f, lz: 0.45f,
                diffR: 0.40f, diffG: 0.40f, diffB: 0.40f,  // neutral grey
                specR: 0.20f, specG: 0.20f, specB: 0.20f,
                shininess: 30f);

            Stops.Add(new ColorStop(0.0f,  Color.Black));
            Stops.Add(new ColorStop(0.3f,  ColorTranslator.FromHtml("#0055FF")));
            Stops.Add(new ColorStop(0.5f,  ColorTranslator.FromHtml("#00FFFF")));
            Stops.Add(new ColorStop(0.7f,  ColorTranslator.FromHtml("#FF5500")));
            Stops.Add(new ColorStop(1.0f,  Color.White));
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // INFERNO 3D
    // Palette: black void → deep purple → magenta → burnt orange → amber → pale yellow
    // Character: volcanic — cool dark void heating through magma to white-hot flame
    // Lighting: hot orange forge light from upper-left; deep violet shadow fill
    //           from lower-right (dark magma cavern)
    // ─────────────────────────────────────────────────────────────────────────
    public class Inferno3DMap : GradientPhong3DBase
    {
        public static string Name        => "Inferno 3D";
        public static string Category    => "3D Relief";
        public static string Description => "Inferno with volcanic forge light — magma 3D relief.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesNormals |
            ColorMapFeatures.GradientBased | ColorMapFeatures.ThreeDEffect;

        protected override float CycleSpeed  => 0.02f;
        protected override float Steepness   => 1.3f;
        protected override float Ambient     => 0.10f;
        protected override float KeySpecScale => 0.95f;
        protected override float FillDiffScale => 0.35f;

        public Inferno3DMap()
        {
            // Hot orange-white volcanic forge light from upper-left.
            KeyLight = new LightSource(
                lx: -0.50f, ly:  0.55f, lz: 0.80f,
                diffR: 1.00f, diffG: 0.60f, diffB: 0.20f,  // hot orange-white
                specR: 1.00f, specG: 0.85f, specB: 0.55f,
                shininess: 70f);

            // Deep violet cavern fill from lower-right.
            FillLight = new LightSource(
                lx:  0.60f, ly: -0.50f, lz: 0.35f,
                diffR: 0.30f, diffG: 0.05f, diffB: 0.45f,  // deep violet
                specR: 0.20f, specG: 0.02f, specB: 0.30f,
                shininess: 15f);

            Stops.Add(new ColorStop(0.0f, ColorTranslator.FromHtml("#000004")));
            Stops.Add(new ColorStop(0.2f, ColorTranslator.FromHtml("#420A68")));
            Stops.Add(new ColorStop(0.4f, ColorTranslator.FromHtml("#932667")));
            Stops.Add(new ColorStop(0.6f, ColorTranslator.FromHtml("#DD513A")));
            Stops.Add(new ColorStop(0.8f, ColorTranslator.FromHtml("#FCA50A")));
            Stops.Add(new ColorStop(1.0f, ColorTranslator.FromHtml("#FCFFA4")));
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // LAVA LAMP 3D
    // Palette: near-black → dark maroon → deep red-orange → orange → amber → pale yellow → near-black
    // Character: warm, saturated, cycling molten wax — intimate, rich reds and ambers
    // Lighting: orange-red lamp warmth from upper-right; deep red-violet shadow fill;
    //           medium shininess for slightly glossy wax
    // ─────────────────────────────────────────────────────────────────────────
    public class LavaLamp3DMap : GradientPhong3DBase
    {
        public static string Name        => "Lava Lamp 3D";
        public static string Category    => "3D Relief";
        public static string Description => "Lava Lamp with warm lamp lighting — molten wax 3D relief.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesNormals |
            ColorMapFeatures.GradientBased | ColorMapFeatures.ThreeDEffect;

        protected override float CycleSpeed  => 0.018f;
        protected override float Steepness   => 1.5f;
        protected override float Ambient     => 0.15f;
        protected override float KeySpecScale => 0.75f;
        protected override float FillDiffScale => 0.40f;

        public LavaLamp3DMap()
        {
            // Warm orange-red lamp light from upper-right.
            KeyLight = new LightSource(
                lx:  0.55f, ly:  0.45f, lz: 0.75f,
                diffR: 1.00f, diffG: 0.50f, diffB: 0.15f,  // orange-red
                specR: 1.00f, specG: 0.75f, specB: 0.40f,
                shininess: 50f);

            // Deep red-violet shadow fill from lower-left.
            FillLight = new LightSource(
                lx: -0.60f, ly: -0.45f, lz: 0.35f,
                diffR: 0.35f, diffG: 0.05f, diffB: 0.25f,  // deep red-violet
                specR: 0.20f, specG: 0.02f, specB: 0.15f,
                shininess: 12f);

            Stops.Add(new ColorStop(0.00f, Color.FromArgb( 10,   2,   0)));
            Stops.Add(new ColorStop(0.15f, Color.FromArgb( 80,  10,   5)));
            Stops.Add(new ColorStop(0.30f, Color.FromArgb(180,  40,   0)));
            Stops.Add(new ColorStop(0.50f, Color.FromArgb(240, 100,  10)));
            Stops.Add(new ColorStop(0.68f, Color.FromArgb(255, 185,  30)));
            Stops.Add(new ColorStop(0.83f, Color.FromArgb(255, 240, 130)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb( 10,   2,   0)));
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // OCEAN DEPTH 3D
    // Palette: deep ocean black → deep blue → mid blue → pale cyan surface
    // Character: deep water — abyss to sunlit surface
    // Lighting: aqua-white caustic light from upper-right (sunlight through water);
    //           deep teal bioluminescent fill from lower-left; very high shininess
    // ─────────────────────────────────────────────────────────────────────────
    public class OceanDepth3DMap : GradientPhong3DBase
    {
        public static string Name        => "Ocean Depth 3D";
        public static string Category    => "3D Relief";
        public static string Description => "Ocean Depth with caustic water light — underwater 3D relief.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesNormals |
            ColorMapFeatures.GradientBased | ColorMapFeatures.ThreeDEffect;

        protected override float CycleSpeed  => 0.02f;
        protected override float Steepness   => 1.4f;
        protected override float Ambient     => 0.10f;
        protected override float KeySpecScale => 1.30f;  // caustic water gives brilliant specular
        protected override float FillDiffScale => 0.45f;
        protected override float FillSpecScale => 0.35f;

        public OceanDepth3DMap()
        {
            // Aqua-white caustic sunlight through water from upper-right.
            KeyLight = new LightSource(
                lx:  0.55f, ly:  0.50f, lz: 0.85f,
                diffR: 0.60f, diffG: 0.90f, diffB: 1.00f,  // aqua-white
                specR: 0.70f, specG: 0.95f, specB: 1.00f,
                shininess: 100f);  // very tight — water caustic

            // Deep teal bioluminescent fill from lower-left.
            FillLight = new LightSource(
                lx: -0.60f, ly: -0.40f, lz: 0.35f,
                diffR: 0.05f, diffG: 0.35f, diffB: 0.40f,  // deep teal
                specR: 0.02f, specG: 0.20f, specB: 0.25f,
                shininess: 20f);

            Stops.Add(new ColorStop(0.0f, ColorTranslator.FromHtml("#001F33")));
            Stops.Add(new ColorStop(0.4f, ColorTranslator.FromHtml("#004F7C")));
            Stops.Add(new ColorStop(0.7f, ColorTranslator.FromHtml("#00A0C6")));
            Stops.Add(new ColorStop(1.0f, ColorTranslator.FromHtml("#E0FFFF")));
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // PLASMA 3D
    // Palette: deep blue → violet → magenta → hot pink → salmon → orange → yellow
    // Character: high-energy plasma — perceptually uniform, very saturated
    // Lighting: pure brilliant white key — high-energy uniform illumination;
    //           deep violet fill; very tight specular for ionised-gas feel
    // ─────────────────────────────────────────────────────────────────────────
    public class Plasma3DMap : GradientPhong3DBase
    {
        public static string Name        => "Plasma 3D";
        public static string Category    => "3D Relief";
        public static string Description => "Plasma with high-energy white light — ionised gas 3D relief.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesNormals |
            ColorMapFeatures.GradientBased | ColorMapFeatures.ThreeDEffect;

        protected override float CycleSpeed  => 0.02f;
        protected override float Steepness   => 1.4f;
        protected override float Ambient     => 0.12f;
        protected override float KeySpecScale => 1.10f;
        protected override float FillDiffScale => 0.30f;

        public Plasma3DMap()
        {
            // Brilliant pure white from upper-right — doesn't bias colour direction.
            KeyLight = new LightSource(
                lx:  0.60f, ly:  0.45f, lz: 0.80f,
                diffR: 1.00f, diffG: 1.00f, diffB: 1.00f,  // pure white
                specR: 1.00f, specG: 1.00f, specB: 1.00f,
                shininess: 100f);

            // Deep violet fill from lower-left.
            FillLight = new LightSource(
                lx: -0.65f, ly: -0.40f, lz: 0.35f,
                diffR: 0.20f, diffG: 0.05f, diffB: 0.40f,  // deep violet
                specR: 0.12f, specG: 0.02f, specB: 0.25f,
                shininess: 20f);

            Stops.Add(new ColorStop(0.00f, Color.FromArgb( 13,   8, 135)));
            Stops.Add(new ColorStop(0.14f, Color.FromArgb( 84,   2, 163)));
            Stops.Add(new ColorStop(0.29f, Color.FromArgb(139,  10, 165)));
            Stops.Add(new ColorStop(0.43f, Color.FromArgb(185,  50, 137)));
            Stops.Add(new ColorStop(0.57f, Color.FromArgb(219,  92, 104)));
            Stops.Add(new ColorStop(0.71f, Color.FromArgb(244, 136,  73)));
            Stops.Add(new ColorStop(0.86f, Color.FromArgb(254, 188,  43)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(240, 249,  33)));
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // PURPLE BODY RADIATION 3D
    // Palette: medium purple → deep navy → teal → deep green → deep red → back to purple
    // Character: alien/cosmic cycling — geological with sci-fi overtones
    // Lighting: purple-white twilight key from upper-right; teal-green
    //           bioluminescent fill from lower-left
    // ─────────────────────────────────────────────────────────────────────────
    public class Purplebody3DMap : GradientPhong3DBase
    {
        public static string Name        => "Purplebody Radiant 3D";
        public static string Category    => "3D Relief";
        public static string Description => "Purplebody Radiant with twilight/bioluminescent lighting — cosmic 3D relief.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesNormals |
            ColorMapFeatures.GradientBased | ColorMapFeatures.ThreeDEffect;

        protected override float CycleSpeed  => 0.02f;
        protected override float Steepness   => 1.6f;
        protected override float Ambient     => 0.14f;
        protected override float KeySpecScale => 0.80f;
        protected override float FillDiffScale => 0.45f;

        public Purplebody3DMap()
        {
            // Purple-white twilight/cosmic key from upper-right.
            KeyLight = new LightSource(
                lx:  0.55f, ly:  0.45f, lz: 0.80f,
                diffR: 0.80f, diffG: 0.65f, diffB: 1.00f,  // purple-white
                specR: 0.90f, specG: 0.80f, specB: 1.00f,
                shininess: 45f);

            // Teal-green bioluminescent fill from lower-left.
            FillLight = new LightSource(
                lx: -0.65f, ly: -0.40f, lz: 0.40f,
                diffR: 0.05f, diffG: 0.40f, diffB: 0.35f,  // teal-green
                specR: 0.02f, specG: 0.25f, specB: 0.20f,
                shininess: 15f);

            Stops.Add(new ColorStop(0.0f, ColorTranslator.FromHtml("#a464a8")));
            Stops.Add(new ColorStop(0.2f, ColorTranslator.FromHtml("#013472")));
            Stops.Add(new ColorStop(0.4f, ColorTranslator.FromHtml("#016d72")));
            Stops.Add(new ColorStop(0.6f, ColorTranslator.FromHtml("#017206")));
            Stops.Add(new ColorStop(0.8f, ColorTranslator.FromHtml("#720601")));
            Stops.Add(new ColorStop(1.0f, ColorTranslator.FromHtml("#a464a8")));
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // TRI-COLOR STRIPE 3D
    // Palette: Black → Red (full) → Lime (full) → Blue (full)
    // Character: pure saturated RGB primaries — graphic, high contrast
    // Lighting: pure neutral white key — preserves each primary's colour identity;
    //           neutral grey fill; medium shininess (matte plastic)
    // ─────────────────────────────────────────────────────────────────────────
    public class TriColor3DMap : GradientPhong3DBase
    {
        public static string Name        => "TriColor Stripe 3D";
        public static string Category    => "3D Relief";
        public static string Description => "TriColor Stripe with neutral white light — graphic RGB 3D relief.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesNormals |
            ColorMapFeatures.GradientBased | ColorMapFeatures.ThreeDEffect;

        protected override float CycleSpeed  => 0.02f;
        protected override float Steepness   => 1.6f;
        protected override float Ambient     => 0.15f;
        protected override float KeySpecScale => 0.70f;
        protected override float FillDiffScale => 0.40f;

        public TriColor3DMap()
        {
            // Pure neutral white key — doesn't tint any of the three primaries.
            KeyLight = new LightSource(
                lx:  0.55f, ly:  0.45f, lz: 0.80f,
                diffR: 1.00f, diffG: 1.00f, diffB: 1.00f,
                specR: 1.00f, specG: 1.00f, specB: 1.00f,
                shininess: 60f);

            // Dim neutral grey fill — no colour cast.
            FillLight = new LightSource(
                lx: -0.65f, ly: -0.40f, lz: 0.40f,
                diffR: 0.30f, diffG: 0.30f, diffB: 0.30f,
                specR: 0.10f, specG: 0.10f, specB: 0.10f,
                shininess: 15f);

            Stops.Add(new ColorStop(0.00f, Color.Black));
            Stops.Add(new ColorStop(0.33f, Color.Red));
            Stops.Add(new ColorStop(0.66f, Color.Lime));
            Stops.Add(new ColorStop(1.00f, Color.Blue));
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // TROPICAL 3D
    // Palette: deep ocean → turquoise → lime → bright yellow → hot pink → coral → deep ocean
    // Character: tropical reef — vivid, all-saturated cycling
    // Lighting: warm white-yellow tropical noon sun from upper-right;
    //           soft aqua ocean-reflection fill from lower-left
    // ─────────────────────────────────────────────────────────────────────────
    public class Tropical3DMap : GradientPhong3DBase
    {
        public static string Name        => "Tropical 3D";
        public static string Category    => "3D Relief";
        public static string Description => "Tropical with tropical noon lighting — reef 3D relief.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesNormals |
            ColorMapFeatures.GradientBased | ColorMapFeatures.ThreeDEffect;

        protected override float CycleSpeed  => 0.022f;
        protected override float Steepness   => 1.5f;
        protected override float Ambient     => 0.16f;
        protected override float KeySpecScale => 0.90f;
        protected override float FillDiffScale => 0.50f;

        public Tropical3DMap()
        {
            // White-yellow tropical noon sunlight from upper-right.
            KeyLight = new LightSource(
                lx:  0.55f, ly:  0.50f, lz: 0.85f,
                diffR: 1.00f, diffG: 0.95f, diffB: 0.70f,  // warm white-yellow
                specR: 1.00f, specG: 1.00f, specB: 0.85f,
                shininess: 70f);  // wet tropical surface — moderately sharp

            // Soft aqua ocean-water reflection fill from lower-left.
            FillLight = new LightSource(
                lx: -0.60f, ly: -0.35f, lz: 0.50f,
                diffR: 0.20f, diffG: 0.55f, diffB: 0.65f,  // aqua-teal
                specR: 0.10f, specG: 0.30f, specB: 0.35f,
                shininess: 20f);

            Stops.Add(new ColorStop(0.00f, Color.FromArgb(  0,  20,  30)));
            Stops.Add(new ColorStop(0.18f, Color.FromArgb(  0, 180, 200)));
            Stops.Add(new ColorStop(0.35f, Color.FromArgb( 50, 240, 120)));
            Stops.Add(new ColorStop(0.52f, Color.FromArgb(255, 240,  50)));
            Stops.Add(new ColorStop(0.68f, Color.FromArgb(255,  80, 160)));
            Stops.Add(new ColorStop(0.83f, Color.FromArgb(255, 120,  60)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(  0,  20,  30)));
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // AURORA BOREALIS 3D
    // Palette: Black → deep night blue → blue → neon green → pale green → white
    // Character: aurora borealis — night sky lit by vivid green aurora curtains
    // Lighting: cold green-white aurora glow from upper-right;
    //           deep night-blue shadow fill from lower-left
    // ─────────────────────────────────────────────────────────────────────────
    public class Aurora3DMap : GradientPhong3DBase
    {
        public static string Name        => "Aurora 3D";
        public static string Category    => "3D Relief";
        public static string Description => "Aurora Borealis with aurora lighting — polar night sky 3D relief.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesNormals |
            ColorMapFeatures.GradientBased | ColorMapFeatures.ThreeDEffect;

        protected override float CycleSpeed  => 0.02f;
        protected override float Steepness   => 1.7f;
        protected override float Ambient     => 0.10f;
        protected override float KeySpecScale => 0.80f;
        protected override float FillDiffScale => 0.35f;

        public Aurora3DMap()
        {
            // Cold green-white aurora glow from upper-right.
            KeyLight = new LightSource(
                lx:  0.55f, ly:  0.45f, lz: 0.80f,
                diffR: 0.50f, diffG: 1.00f, diffB: 0.70f,  // cold green-white
                specR: 0.70f, specG: 1.00f, specB: 0.80f,
                shininess: 65f);

            // Deep night-blue shadow fill from lower-left (polar sky).
            FillLight = new LightSource(
                lx: -0.65f, ly: -0.40f, lz: 0.35f,
                diffR: 0.05f, diffG: 0.10f, diffB: 0.45f,  // deep night blue
                specR: 0.02f, specG: 0.05f, specB: 0.25f,
                shininess: 12f);

            Stops.Add(new ColorStop(0.0f, Color.Black));
            Stops.Add(new ColorStop(0.2f, ColorTranslator.FromHtml("#002040")));
            Stops.Add(new ColorStop(0.4f, ColorTranslator.FromHtml("#004080")));
            Stops.Add(new ColorStop(0.6f, ColorTranslator.FromHtml("#00FF80")));
            Stops.Add(new ColorStop(0.8f, ColorTranslator.FromHtml("#80FF80")));
            Stops.Add(new ColorStop(1.0f, Color.White));
        }
    }
}
