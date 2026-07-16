// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Models/ColorSchemes/ChromostereopsisFamilyThemes.cs
//
// Wide-spectrum family of Chromostereopsis-driven palettes.  Every theme
// here exploits chromatic aberration in the human eye: long-wavelength
// hues (red / orange / amber) refract differently than short-wavelength
// hues (blue / violet / cyan), so when both poles are placed against a
// dark void the warm pole appears to float forward and the cool pole
// recedes.  Themes vary four design axes:
//
//   * Hue pair    — which warm/cool pair drives the depth illusion
//                   (red/blue, orange/cyan, yellow/violet, magenta/teal,
//                   amber/ice, green/pink, etc.)
//   * Void colour — pure black, charcoal, midnight navy, cream, deep
//                   maroon, etc.  Sets overall mood.
//   * Saturation  — full-primary acid neon → muted jewel tones → dusty
//                   pastel whispers
//   * Cycle speed — slow (contemplative) → mid (active) → fast (alarm)
//
// All themes inherit CyclingGradientColorMap so deep zoom never locks
// onto a single hue.  HighContrast + Cyclic + GradientBased feature
// flags applied uniformly.

using FracturingFog.Interefaces;
using System.Drawing;

namespace FracturingFog.Models
{
    // ── Shared metadata helper ───────────────────────────────────────────────
    internal static class ChromostereopsisFamily
    {
        public const ColorMapFeatures CommonFeatures =
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.Cyclic |
            ColorMapFeatures.HighContrast | ColorMapFeatures.GradientBased;
    }

    // =========================================================================
    // INVERTED DEPTH — blue forward, red back.  Same pure primaries as the
    // classic but reversed order: exploits expectation-violation for a subtle
    // optical "lift" of the usually-receding pole.
    // =========================================================================
    public sealed class ChromostereopsisInvertedMap : CyclingGradientColorMap
    {
        public static string Name => "Chromostereopsis Inverted";
        public static string Category => "Eye Trick";
        public static string Description =>
            "Pure blue / red on black with the depth order swapped.  Eye still " +
            "perceives the red layer forward, fighting the gradient's nominal " +
            "ordering — produces a soft binocular tug between the two layers.";
        public static ColorMapFeatures Features => ChromostereopsisFamily.CommonFeatures;

        protected override float CycleSpeed => 0.045f;

        public ChromostereopsisInvertedMap()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb(  0,   0, 255)));
            Stops.Add(new ColorStop(0.10f, Color.FromArgb(  0,   0,   0)));
            Stops.Add(new ColorStop(0.18f, Color.FromArgb(255,   0,   0)));
            Stops.Add(new ColorStop(0.30f, Color.FromArgb(  0,   0,   0)));
            Stops.Add(new ColorStop(0.38f, Color.FromArgb(  0,   0, 255)));
            Stops.Add(new ColorStop(0.48f, Color.FromArgb(  0, 255,   0)));
            Stops.Add(new ColorStop(0.58f, Color.FromArgb(255,   0,   0)));
            Stops.Add(new ColorStop(0.70f, Color.FromArgb(  0,   0,   0)));
            Stops.Add(new ColorStop(0.78f, Color.FromArgb(  0,   0, 255)));
            Stops.Add(new ColorStop(0.88f, Color.FromArgb(255,   0,   0)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(  0,   0, 255)));
        }
    }

    // =========================================================================
    // ORANGE / CYAN — warm-cool complementary pair, classic anaglyph adjacent.
    // Sunset-ember orange floats above icy-cyan voids.  Less harsh than pure
    // R/B but still strong chromostereopsis.
    // =========================================================================
    public sealed class ChromostereopsisOrangeCyanMap : CyclingGradientColorMap
    {
        public static string Name => "Chromostereopsis Orange/Cyan";
        public static string Category => "Eye Trick";
        public static string Description =>
            "Sunset orange against cyan over black voids.  Warm-cool " +
            "complementary pair delivers softer chromostereopsis than pure " +
            "RGB, with an ember-glow warmth that reads less alarming.";
        public static ColorMapFeatures Features => ChromostereopsisFamily.CommonFeatures;

        protected override float CycleSpeed => 0.040f;

        public ChromostereopsisOrangeCyanMap()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb(255, 110,   0)));
            Stops.Add(new ColorStop(0.12f, Color.FromArgb(  0,   0,   0)));
            Stops.Add(new ColorStop(0.22f, Color.FromArgb(  0, 220, 255)));
            Stops.Add(new ColorStop(0.34f, Color.FromArgb(  0,   0,   0)));
            Stops.Add(new ColorStop(0.44f, Color.FromArgb(255, 140,  20)));
            Stops.Add(new ColorStop(0.56f, Color.FromArgb(  0,   0,   0)));
            Stops.Add(new ColorStop(0.66f, Color.FromArgb(  0, 200, 240)));
            Stops.Add(new ColorStop(0.78f, Color.FromArgb(  0,   0,   0)));
            Stops.Add(new ColorStop(0.88f, Color.FromArgb(255, 110,   0)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(  0, 220, 255)));
        }
    }

    // =========================================================================
    // YELLOW / VIOLET — second-order complementary pair.  Royal / regal mood:
    // gold flares forward, deep violet recedes into a black field.
    // =========================================================================
    public sealed class ChromostereopsisYellowVioletMap : CyclingGradientColorMap
    {
        public static string Name => "Chromostereopsis Yellow/Violet";
        public static string Category => "Eye Trick";
        public static string Description =>
            "Saturated gold against deep violet on black voids.  The yellow " +
            "edge advances aggressively while violet sinks back — regal mood " +
            "with strong long-wavelength / short-wavelength depth pull.";
        public static ColorMapFeatures Features => ChromostereopsisFamily.CommonFeatures;

        protected override float CycleSpeed => 0.042f;

        public ChromostereopsisYellowVioletMap()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb(255, 230,   0)));
            Stops.Add(new ColorStop(0.12f, Color.FromArgb(  0,   0,   0)));
            Stops.Add(new ColorStop(0.22f, Color.FromArgb( 90,   0, 200)));
            Stops.Add(new ColorStop(0.34f, Color.FromArgb(  0,   0,   0)));
            Stops.Add(new ColorStop(0.44f, Color.FromArgb(255, 240,  40)));
            Stops.Add(new ColorStop(0.56f, Color.FromArgb(  0,   0,   0)));
            Stops.Add(new ColorStop(0.66f, Color.FromArgb(120,   0, 220)));
            Stops.Add(new ColorStop(0.78f, Color.FromArgb(  0,   0,   0)));
            Stops.Add(new ColorStop(0.88f, Color.FromArgb(255, 220,   0)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb( 90,   0, 200)));
        }
    }

    // =========================================================================
    // MAGENTA / TEAL — high-chroma cosmetic pair.  Vaporwave mood; magenta
    // floats forward, teal recedes.
    // =========================================================================
    public sealed class ChromostereopsisMagentaTealMap : CyclingGradientColorMap
    {
        public static string Name => "Chromostereopsis Magenta/Teal";
        public static string Category => "Eye Trick";
        public static string Description =>
            "Hot magenta against deep teal on near-black voids.  Vaporwave " +
            "palette with extreme chroma — magenta lifts off the surface, " +
            "teal sinks into shadow.";
        public static ColorMapFeatures Features => ChromostereopsisFamily.CommonFeatures;

        protected override float CycleSpeed => 0.050f;

        public ChromostereopsisMagentaTealMap()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb(255,   0, 200)));
            Stops.Add(new ColorStop(0.11f, Color.FromArgb(  5,   0,   8)));
            Stops.Add(new ColorStop(0.20f, Color.FromArgb(  0, 180, 180)));
            Stops.Add(new ColorStop(0.32f, Color.FromArgb(  5,   0,   8)));
            Stops.Add(new ColorStop(0.42f, Color.FromArgb(255,  20, 220)));
            Stops.Add(new ColorStop(0.54f, Color.FromArgb(  5,   0,   8)));
            Stops.Add(new ColorStop(0.64f, Color.FromArgb(  0, 200, 200)));
            Stops.Add(new ColorStop(0.76f, Color.FromArgb(  5,   0,   8)));
            Stops.Add(new ColorStop(0.86f, Color.FromArgb(255,   0, 220)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(  0, 180, 180)));
        }
    }

    // =========================================================================
    // ANAGLYPH — exact red/cyan pair used in stereoscopic anaglyph 3D glasses.
    // No middle hues; raw bichromatic depth.
    // =========================================================================
    public sealed class ChromostereopsisAnaglyphMap : CyclingGradientColorMap
    {
        public static string Name => "Chromostereopsis Anaglyph";
        public static string Category => "Eye Trick";
        public static string Description =>
            "Pure red against pure cyan on black — the exact pair used in " +
            "anaglyph 3D glasses.  Bichromatic with no spacer hues, so the " +
            "depth pull is maximally direct.";
        public static ColorMapFeatures Features => ChromostereopsisFamily.CommonFeatures;

        protected override float CycleSpeed => 0.048f;

        public ChromostereopsisAnaglyphMap()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb(255,   0,   0)));
            Stops.Add(new ColorStop(0.15f, Color.FromArgb(  0,   0,   0)));
            Stops.Add(new ColorStop(0.30f, Color.FromArgb(  0, 255, 255)));
            Stops.Add(new ColorStop(0.45f, Color.FromArgb(  0,   0,   0)));
            Stops.Add(new ColorStop(0.60f, Color.FromArgb(255,   0,   0)));
            Stops.Add(new ColorStop(0.75f, Color.FromArgb(  0,   0,   0)));
            Stops.Add(new ColorStop(0.90f, Color.FromArgb(  0, 255, 255)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(255,   0,   0)));
        }
    }

    // =========================================================================
    // HALLOWEEN — saturated orange / deep purple on black.  Spooky mood.
    // =========================================================================
    public sealed class ChromostereopsisHalloweenMap : CyclingGradientColorMap
    {
        public static string Name => "Chromostereopsis Halloween";
        public static string Category => "Eye Trick";
        public static string Description =>
            "Pumpkin orange against deep purple on black voids.  Classic " +
            "Halloween chroma pair — orange jumps forward, purple recedes " +
            "into a haunted void.";
        public static ColorMapFeatures Features => ChromostereopsisFamily.CommonFeatures;

        protected override float CycleSpeed => 0.038f;

        public ChromostereopsisHalloweenMap()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb(255, 130,   0)));
            Stops.Add(new ColorStop(0.12f, Color.FromArgb(  0,   0,   0)));
            Stops.Add(new ColorStop(0.22f, Color.FromArgb( 80,   0, 130)));
            Stops.Add(new ColorStop(0.34f, Color.FromArgb(  0,   0,   0)));
            Stops.Add(new ColorStop(0.44f, Color.FromArgb(255, 100,   0)));
            Stops.Add(new ColorStop(0.56f, Color.FromArgb(  0,   0,   0)));
            Stops.Add(new ColorStop(0.66f, Color.FromArgb( 60,   0, 110)));
            Stops.Add(new ColorStop(0.78f, Color.FromArgb(  0,   0,   0)));
            Stops.Add(new ColorStop(0.88f, Color.FromArgb(255, 130,   0)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb( 80,   0, 130)));
        }
    }

    // =========================================================================
    // TOXIC — acid green / hot pink.  Hazard / radioactive mood.
    // =========================================================================
    public sealed class ChromostereopsisToxicMap : CyclingGradientColorMap
    {
        public static string Name => "Chromostereopsis Toxic";
        public static string Category => "Eye Trick";
        public static string Description =>
            "Acid green against hot pink on black voids.  Both poles are " +
            "near-fluorescent, creating a hazard-sign vibration; the green " +
            "edge feels chemical, the pink feels alarming.";
        public static ColorMapFeatures Features => ChromostereopsisFamily.CommonFeatures;

        protected override float CycleSpeed => 0.060f;

        public ChromostereopsisToxicMap()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb(180, 255,   0)));
            Stops.Add(new ColorStop(0.11f, Color.FromArgb(  0,   0,   0)));
            Stops.Add(new ColorStop(0.20f, Color.FromArgb(255,   0, 140)));
            Stops.Add(new ColorStop(0.32f, Color.FromArgb(  0,   0,   0)));
            Stops.Add(new ColorStop(0.42f, Color.FromArgb(160, 255,  40)));
            Stops.Add(new ColorStop(0.54f, Color.FromArgb(  0,   0,   0)));
            Stops.Add(new ColorStop(0.64f, Color.FromArgb(255,  40, 160)));
            Stops.Add(new ColorStop(0.76f, Color.FromArgb(  0,   0,   0)));
            Stops.Add(new ColorStop(0.86f, Color.FromArgb(180, 255,   0)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(255,   0, 140)));
        }
    }

    // =========================================================================
    // VOLTAGE — red/blue with thin white-hot edges.  Electric alarm mood.
    // Fastest cycle in the family.
    // =========================================================================
    public sealed class ChromostereopsisVoltageMap : CyclingGradientColorMap
    {
        public static string Name => "Chromostereopsis Voltage";
        public static string Category => "Eye Trick";
        public static string Description =>
            "Red and blue primaries with thin white-hot incandescent edges, " +
            "rapid cycle.  Reads like a high-voltage warning strobe — " +
            "primaries snap, white edges sting.";
        public static ColorMapFeatures Features => ChromostereopsisFamily.CommonFeatures;

        protected override float CycleSpeed => 0.090f;

        public ChromostereopsisVoltageMap()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb(255,   0,   0)));
            Stops.Add(new ColorStop(0.04f, Color.FromArgb(255, 240, 240)));
            Stops.Add(new ColorStop(0.10f, Color.FromArgb(  0,   0,   0)));
            Stops.Add(new ColorStop(0.20f, Color.FromArgb(240, 240, 255)));
            Stops.Add(new ColorStop(0.24f, Color.FromArgb(  0,   0, 255)));
            Stops.Add(new ColorStop(0.36f, Color.FromArgb(  0,   0,   0)));
            Stops.Add(new ColorStop(0.46f, Color.FromArgb(255, 240, 240)));
            Stops.Add(new ColorStop(0.50f, Color.FromArgb(255,   0,   0)));
            Stops.Add(new ColorStop(0.62f, Color.FromArgb(  0,   0,   0)));
            Stops.Add(new ColorStop(0.72f, Color.FromArgb(240, 240, 255)));
            Stops.Add(new ColorStop(0.76f, Color.FromArgb(  0,   0, 255)));
            Stops.Add(new ColorStop(0.88f, Color.FromArgb(  0,   0,   0)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(255,   0,   0)));
        }
    }

    // =========================================================================
    // BLOOD MOON — crimson / midnight navy on charcoal.  Visceral, slow cycle.
    // Lower saturation than pure RGB; saturated enough to still trigger
    // chromostereopsis but mood reads ominous rather than clinical.
    // =========================================================================
    public sealed class ChromostereopsisBloodMoonMap : CyclingGradientColorMap
    {
        public static string Name => "Chromostereopsis Blood Moon";
        public static string Category => "Eye Trick";
        public static string Description =>
            "Deep crimson against midnight navy on charcoal voids.  Slow " +
            "cycle, oxblood saturation — visceral and ominous; the red " +
            "still floats forward but reads as blood, not neon.";
        public static ColorMapFeatures Features => ChromostereopsisFamily.CommonFeatures;

        protected override float CycleSpeed => 0.028f;

        public ChromostereopsisBloodMoonMap()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb(180,  10,  20)));
            Stops.Add(new ColorStop(0.12f, Color.FromArgb( 14,  10,  14)));
            Stops.Add(new ColorStop(0.22f, Color.FromArgb( 20,  20, 130)));
            Stops.Add(new ColorStop(0.34f, Color.FromArgb( 14,  10,  14)));
            Stops.Add(new ColorStop(0.44f, Color.FromArgb(200,  20,  30)));
            Stops.Add(new ColorStop(0.56f, Color.FromArgb( 14,  10,  14)));
            Stops.Add(new ColorStop(0.66f, Color.FromArgb( 30,  30, 150)));
            Stops.Add(new ColorStop(0.78f, Color.FromArgb( 14,  10,  14)));
            Stops.Add(new ColorStop(0.88f, Color.FromArgb(180,  10,  20)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb( 20,  20, 130)));
        }
    }

    // =========================================================================
    // STAINED GLASS — rich jewel-tone trichroma: ruby, sapphire, emerald
    // (with deep amethyst lead-lines instead of black).  Contemplative mood.
    // =========================================================================
    public sealed class ChromostereopsisStainedGlassMap : CyclingGradientColorMap
    {
        public static string Name => "Chromostereopsis Stained Glass";
        public static string Category => "Eye Trick";
        public static string Description =>
            "Ruby, emerald, and sapphire jewel tones separated by deep " +
            "amethyst lead-lines.  Contemplative cathedral-window mood — " +
            "depth illusion intact but softened by jewel-rich saturation.";
        public static ColorMapFeatures Features => ChromostereopsisFamily.CommonFeatures;

        protected override float CycleSpeed => 0.030f;

        public ChromostereopsisStainedGlassMap()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb(220,  20,  60)));
            Stops.Add(new ColorStop(0.10f, Color.FromArgb( 20,   8,  30)));
            Stops.Add(new ColorStop(0.20f, Color.FromArgb( 30,  40, 200)));
            Stops.Add(new ColorStop(0.32f, Color.FromArgb( 20,   8,  30)));
            Stops.Add(new ColorStop(0.40f, Color.FromArgb(220,  30,  70)));
            Stops.Add(new ColorStop(0.50f, Color.FromArgb( 30, 160,  60)));
            Stops.Add(new ColorStop(0.60f, Color.FromArgb( 30,  40, 200)));
            Stops.Add(new ColorStop(0.72f, Color.FromArgb( 20,   8,  30)));
            Stops.Add(new ColorStop(0.80f, Color.FromArgb(220,  20,  60)));
            Stops.Add(new ColorStop(0.90f, Color.FromArgb( 30,  40, 200)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(220,  20,  60)));
        }
    }

    // =========================================================================
    // FUNERAL — desaturated maroon / muted indigo on warm grey.  Somber.
    // Saturation barely high enough to trigger any chromostereopsis at all —
    // the illusion is a whisper rather than a punch.
    // =========================================================================
    public sealed class ChromostereopsisFuneralMap : CyclingGradientColorMap
    {
        public static string Name => "Chromostereopsis Funeral";
        public static string Category => "Eye Trick";
        public static string Description =>
            "Dusty maroon against muted indigo on warm grey voids.  Saturation " +
            "deliberately low so the chromostereopsis depth pull becomes a " +
            "whisper — a quiet, mournful 3D effect.";
        public static ColorMapFeatures Features => ChromostereopsisFamily.CommonFeatures;

        protected override float CycleSpeed => 0.025f;

        public ChromostereopsisFuneralMap()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb(120,  50,  60)));
            Stops.Add(new ColorStop(0.12f, Color.FromArgb( 35,  32,  34)));
            Stops.Add(new ColorStop(0.22f, Color.FromArgb( 60,  65, 100)));
            Stops.Add(new ColorStop(0.34f, Color.FromArgb( 35,  32,  34)));
            Stops.Add(new ColorStop(0.44f, Color.FromArgb(130,  55,  65)));
            Stops.Add(new ColorStop(0.56f, Color.FromArgb( 35,  32,  34)));
            Stops.Add(new ColorStop(0.66f, Color.FromArgb( 70,  75, 110)));
            Stops.Add(new ColorStop(0.78f, Color.FromArgb( 35,  32,  34)));
            Stops.Add(new ColorStop(0.88f, Color.FromArgb(120,  50,  60)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb( 60,  65, 100)));
        }
    }

    // =========================================================================
    // CANDY SHOCK — saturated bubblegum pink and bright sky blue with lemon
    // accents; cream-white voids instead of black.  Playful, sugary mood.
    // =========================================================================
    public sealed class ChromostereopsisCandyShockMap : CyclingGradientColorMap
    {
        public static string Name => "Chromostereopsis Candy Shock";
        public static string Category => "Eye Trick";
        public static string Description =>
            "Bubblegum pink and sky blue with lemon accents on cream voids.  " +
            "Inverts the usual black-void convention — pink lifts, blue " +
            "recedes against pale paper for a sugary, playful pop.";
        public static ColorMapFeatures Features => ChromostereopsisFamily.CommonFeatures;

        protected override float CycleSpeed => 0.050f;

        public ChromostereopsisCandyShockMap()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb(255,  60, 180)));
            Stops.Add(new ColorStop(0.11f, Color.FromArgb(252, 248, 230)));
            Stops.Add(new ColorStop(0.22f, Color.FromArgb( 60, 180, 255)));
            Stops.Add(new ColorStop(0.32f, Color.FromArgb(252, 248, 230)));
            Stops.Add(new ColorStop(0.42f, Color.FromArgb(255,  90, 200)));
            Stops.Add(new ColorStop(0.50f, Color.FromArgb(255, 240, 100)));
            Stops.Add(new ColorStop(0.60f, Color.FromArgb( 80, 200, 255)));
            Stops.Add(new ColorStop(0.72f, Color.FromArgb(252, 248, 230)));
            Stops.Add(new ColorStop(0.82f, Color.FromArgb(255,  60, 180)));
            Stops.Add(new ColorStop(0.92f, Color.FromArgb( 60, 180, 255)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(255,  60, 180)));
        }
    }

    // =========================================================================
    // EMBER / FROST — molten amber / glacier ice-blue on black.  Hot/cold
    // thermal-vision feel.
    // =========================================================================
    public sealed class ChromostereopsisEmberFrostMap : CyclingGradientColorMap
    {
        public static string Name => "Chromostereopsis Ember/Frost";
        public static string Category => "Eye Trick";
        public static string Description =>
            "Molten amber against glacier ice-blue on black voids.  Reads " +
            "like thermal vision — heat radiates forward, cold sinks back; " +
            "evokes furnace / freezer tension.";
        public static ColorMapFeatures Features => ChromostereopsisFamily.CommonFeatures;

        protected override float CycleSpeed => 0.038f;

        public ChromostereopsisEmberFrostMap()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb(255, 170,  20)));
            Stops.Add(new ColorStop(0.12f, Color.FromArgb(  0,   0,   0)));
            Stops.Add(new ColorStop(0.22f, Color.FromArgb(150, 220, 255)));
            Stops.Add(new ColorStop(0.34f, Color.FromArgb(  0,   0,   0)));
            Stops.Add(new ColorStop(0.44f, Color.FromArgb(255, 140,   0)));
            Stops.Add(new ColorStop(0.56f, Color.FromArgb(  0,   0,   0)));
            Stops.Add(new ColorStop(0.66f, Color.FromArgb(180, 230, 255)));
            Stops.Add(new ColorStop(0.78f, Color.FromArgb(  0,   0,   0)));
            Stops.Add(new ColorStop(0.88f, Color.FromArgb(255, 170,  20)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(150, 220, 255)));
        }
    }

    // =========================================================================
    // FESTIVAL — rotating multi-hue depth-pairs anchored by R/B poles.  Each
    // black void bounded by a fresh warm/cool pair — chromostereopsis remains
    // intact across the cycle while hue churn maximises variation.
    // =========================================================================
    public sealed class ChromostereopsisFestivalMap : CyclingGradientColorMap
    {
        public static string Name => "Chromostereopsis Festival";
        public static string Category => "Eye Trick";
        public static string Description =>
            "Rotating warm/cool depth-pairs separated by black voids: " +
            "red/blue, orange/cyan, yellow/violet, magenta/teal.  Eye sees a " +
            "different colour pop forward at each band — maximal variation " +
            "while preserving chromostereopsis.";
        public static ColorMapFeatures Features => ChromostereopsisFamily.CommonFeatures;

        protected override float CycleSpeed => 0.045f;

        public ChromostereopsisFestivalMap()
        {
            // Pair 1: red / blue
            Stops.Add(new ColorStop(0.000f, Color.FromArgb(255,   0,   0)));
            Stops.Add(new ColorStop(0.060f, Color.FromArgb(  0,   0,   0)));
            Stops.Add(new ColorStop(0.120f, Color.FromArgb(  0,   0, 255)));
            Stops.Add(new ColorStop(0.180f, Color.FromArgb(  0,   0,   0)));
            // Pair 2: orange / cyan
            Stops.Add(new ColorStop(0.240f, Color.FromArgb(255, 130,   0)));
            Stops.Add(new ColorStop(0.300f, Color.FromArgb(  0,   0,   0)));
            Stops.Add(new ColorStop(0.360f, Color.FromArgb(  0, 220, 255)));
            Stops.Add(new ColorStop(0.420f, Color.FromArgb(  0,   0,   0)));
            // Pair 3: yellow / violet
            Stops.Add(new ColorStop(0.480f, Color.FromArgb(255, 230,   0)));
            Stops.Add(new ColorStop(0.540f, Color.FromArgb(  0,   0,   0)));
            Stops.Add(new ColorStop(0.600f, Color.FromArgb(100,   0, 220)));
            Stops.Add(new ColorStop(0.660f, Color.FromArgb(  0,   0,   0)));
            // Pair 4: magenta / teal
            Stops.Add(new ColorStop(0.720f, Color.FromArgb(255,   0, 200)));
            Stops.Add(new ColorStop(0.780f, Color.FromArgb(  0,   0,   0)));
            Stops.Add(new ColorStop(0.840f, Color.FromArgb(  0, 200, 200)));
            Stops.Add(new ColorStop(0.900f, Color.FromArgb(  0,   0,   0)));
            // Wrap back to pair 1
            Stops.Add(new ColorStop(1.000f, Color.FromArgb(255,   0,   0)));
        }
    }

    // =========================================================================
    // INFRARED — near-monochrome deep crimson with the faintest cool-blue
    // glimmer.  Mood: thermal night-vision, oppressive heat.  Most of the
    // depth pull comes from sheer brightness contrast against the faint blue.
    // =========================================================================
    public sealed class ChromostereopsisInfraredMap : CyclingGradientColorMap
    {
        public static string Name => "Chromostereopsis Infrared";
        public static string Category => "Eye Trick";
        public static string Description =>
            "Deep crimson dominates with the faintest cool-blue glimmer on " +
            "near-black voids.  Reads like long-wavelength thermal imaging — " +
            "oppressive heat, depth pull from a single warm pole.";
        public static ColorMapFeatures Features => ChromostereopsisFamily.CommonFeatures;

        protected override float CycleSpeed => 0.032f;

        public ChromostereopsisInfraredMap()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb(220,   0,   0)));
            Stops.Add(new ColorStop(0.12f, Color.FromArgb(  6,   0,   8)));
            Stops.Add(new ColorStop(0.22f, Color.FromArgb( 30,  40, 130)));
            Stops.Add(new ColorStop(0.34f, Color.FromArgb(  6,   0,   8)));
            Stops.Add(new ColorStop(0.44f, Color.FromArgb(255,  40,  10)));
            Stops.Add(new ColorStop(0.56f, Color.FromArgb(  6,   0,   8)));
            Stops.Add(new ColorStop(0.66f, Color.FromArgb( 40,  50, 150)));
            Stops.Add(new ColorStop(0.78f, Color.FromArgb(  6,   0,   8)));
            Stops.Add(new ColorStop(0.88f, Color.FromArgb(200,   0,   0)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb( 30,  40, 130)));
        }
    }

    // =========================================================================
    // PASTEL WHISPER — dusty rose / dusty blue on cream.  Lowest-energy theme
    // in the family.  Chromostereopsis is barely present — a dream rather
    // than a hit.
    // =========================================================================
    public sealed class ChromostereopsisPastelWhisperMap : CyclingGradientColorMap
    {
        public static string Name => "Chromostereopsis Pastel Whisper";
        public static string Category => "Eye Trick";
        public static string Description =>
            "Dusty rose and dusty blue on cream voids.  Saturation pulled " +
            "way back — the chromostereopsis effect becomes a daydream, " +
            "soft and barely there.";
        public static ColorMapFeatures Features => ChromostereopsisFamily.CommonFeatures;

        protected override float CycleSpeed => 0.024f;

        public ChromostereopsisPastelWhisperMap()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb(230, 170, 180)));
            Stops.Add(new ColorStop(0.12f, Color.FromArgb(248, 244, 232)));
            Stops.Add(new ColorStop(0.22f, Color.FromArgb(170, 195, 230)));
            Stops.Add(new ColorStop(0.34f, Color.FromArgb(248, 244, 232)));
            Stops.Add(new ColorStop(0.44f, Color.FromArgb(235, 175, 190)));
            Stops.Add(new ColorStop(0.56f, Color.FromArgb(248, 244, 232)));
            Stops.Add(new ColorStop(0.66f, Color.FromArgb(180, 205, 235)));
            Stops.Add(new ColorStop(0.78f, Color.FromArgb(248, 244, 232)));
            Stops.Add(new ColorStop(0.88f, Color.FromArgb(230, 170, 180)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(170, 195, 230)));
        }
    }

    // =========================================================================
    // ALARM STRIPES — high-frequency red/blue bands separated only by thin
    // black slices.  Looks like hazard tape; chromostereopsis at maximum
    // because the depth poles touch with minimal void.
    // =========================================================================
    public sealed class ChromostereopsisAlarmStripesMap : CyclingGradientColorMap
    {
        public static string Name => "Chromostereopsis Alarm Stripes";
        public static string Category => "Eye Trick";
        public static string Description =>
            "Tight red/blue hazard-tape stripes separated by thin black " +
            "slices — depth poles packed close together drive the strongest, " +
            "most aggressive chromostereopsis vibration in the family.";
        public static ColorMapFeatures Features => ChromostereopsisFamily.CommonFeatures;

        protected override float CycleSpeed => 0.075f;

        public ChromostereopsisAlarmStripesMap()
        {
            Stops.Add(new ColorStop(0.00f, Color.FromArgb(255,   0,   0)));
            Stops.Add(new ColorStop(0.06f, Color.FromArgb(  0,   0,   0)));
            Stops.Add(new ColorStop(0.12f, Color.FromArgb(  0,   0, 255)));
            Stops.Add(new ColorStop(0.18f, Color.FromArgb(  0,   0,   0)));
            Stops.Add(new ColorStop(0.24f, Color.FromArgb(255,   0,   0)));
            Stops.Add(new ColorStop(0.30f, Color.FromArgb(  0,   0,   0)));
            Stops.Add(new ColorStop(0.36f, Color.FromArgb(  0,   0, 255)));
            Stops.Add(new ColorStop(0.42f, Color.FromArgb(  0,   0,   0)));
            Stops.Add(new ColorStop(0.48f, Color.FromArgb(255,   0,   0)));
            Stops.Add(new ColorStop(0.54f, Color.FromArgb(  0,   0,   0)));
            Stops.Add(new ColorStop(0.60f, Color.FromArgb(  0,   0, 255)));
            Stops.Add(new ColorStop(0.66f, Color.FromArgb(  0,   0,   0)));
            Stops.Add(new ColorStop(0.72f, Color.FromArgb(255,   0,   0)));
            Stops.Add(new ColorStop(0.78f, Color.FromArgb(  0,   0,   0)));
            Stops.Add(new ColorStop(0.84f, Color.FromArgb(  0,   0, 255)));
            Stops.Add(new ColorStop(0.90f, Color.FromArgb(  0,   0,   0)));
            Stops.Add(new ColorStop(1.00f, Color.FromArgb(255,   0,   0)));
        }
    }
}
