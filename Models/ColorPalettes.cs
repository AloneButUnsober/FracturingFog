// Models/ColorSchemes/ColorPalettes.cs  — v5 (3D themes added)
//
// Add new themes to the Palettes list; they appear automatically in the UI.
// The "3D Relief" category groups all normal-mapped colour maps together at
// the top of the list so users can find them immediately.
using FracturingFog.Interefaces;

using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace FracturingFog.Models
{
    public static class ColorPalette
    {
        // ── Master palette list ───────────────────────────────────────────────
        // Order here controls the order in the UI combo box.

        public static readonly List<IColorMap> Palettes = new()
        {
            // ── 3D Relief (normal-mapped) ─────────────────────────────────────
            new PhongStoneMap(),
            new MoltenMetalMap(),
            new CrystalCaveMap(),
            new GoldReliefMap(),
            new MarbleReliefMap(),
            new VolcanicRockMap(),
            new LunarSurfaceMap(),
            new AncientBronzeMap(),
            new NeonReliefMap(),
            new PolarNight3DMap(),
            new Inferno3DMap(),
            new Blackbody3DMap(),
            new CosmicLatte3DMap(),
            new Aurora3DMap(),
            new DeepSpaceBlue3DMap(),
            new EarthTone3DMap(),
            new Icefire3DMap(),
            new LavaLamp3DMap(),
            new Plasma3DMap(),
            new Purplebody3DMap(),
            new TriColor3DMap(),
            new Tropical3DMap(),
            new OceanDepth3DMap(),

            // ── Classic / algorithmic ─────────────────────────────────────────
            new HsvPalette(),
            new HsvModified(),
            new HsvCLD(),
            new HsvTst(),
            new WarpedHsvMap(),
            new RainbowColorMap(),
            new GoldenRatioMap(),
            new MonoBandMap(),
            new BernsteinMap(),

            // ── Gradient — linear ─────────────────────────────────────────────
            new BlackbodyColorMap(),
            new PurplebodyColorMap(),
            new DeepSpaceBlueMap(),
            new EarthToneMap(),
            new IcefireColorMap(),
            new InfernoColorMap(),
            new OceanDepthMap(),
            new AuroraColorMap(),
            new PolarNightMap(),

            // ── Gradient — cycling ────────────────────────────────────────────
            new FirePalette(),
            new CosmicLatteMap(),
            new TropicalMap(),
            new LavaLampMap(),
            new TriColorMap(),

            // ── Algorithmic / artistic ────────────────────────────────────────
            new NebulaDustMap(),
            //new DistanceGlowMap(),
            new DigitalMatrixMap(),
            new PsychedelicMap(),
            new TwilightCyclicMap(),
            new SolarWindMap(),
            new SolarWindMapMOD(),

            // ── Metallic / texture ────────────────────────────────────────────
            new CopperSheenMap(),
            new VintageSepiaMap(),
            new GrayscalePalette(),
            new RedAndBlack(),

            // ── Scientific / perceptual ───────────────────────────────────────
            new ViridisColorMap(),
            new PlasmaColorMap(),
        };

        // ── Lookup helpers ────────────────────────────────────────────────────

        /// <summary>
        /// Returns the <see cref="IColorMap"/> whose static <c>Name</c> property
        /// matches <paramref name="name"/>, or a new <see cref="HsvPalette"/> if
        /// not found.
        /// </summary>
        public static IColorMap GetPaletteByName(string name)
        {
            foreach (var p in Palettes)
                if (GetStaticName(p) == name) return p;
            return new HsvPalette();
        }

        /// <summary>Returns the display names of all registered palettes, in list order.</summary>
        public static List<string> GetPaletteNames()
        {
            var names = new List<string>();
            foreach (var p in Palettes)
            {
                var n = GetStaticName(p);
                if (!string.IsNullOrEmpty(n) &&
                    n != HsvPalette.Name)
                {
                    names.Add(n);
                }
            }

            names.Sort();
            names.Insert(0, HsvPalette.Name);
            return names;
        }

        /// <summary>
        /// Returns palettes grouped by their static <c>Category</c> property.
        /// Key = category string, Value = ordered list of palettes in that category.
        /// </summary>
        public static Dictionary<string, List<IColorMap>> GetPalettesByCategory()
        {
            var groups = new Dictionary<string, List<IColorMap>>(StringComparer.Ordinal);
            foreach (var p in Palettes)
            {
                string cat = GetStaticCategory(p);
                if (!groups.TryGetValue(cat, out var list))
                    groups[cat] = list = new List<IColorMap>();
                list.Add(p);
            }
            return groups;
        }

        public static Dictionary<string, List<IColorMap>> GetPalettesByType(ColorPaletteType type)
        {
            var groups = new Dictionary<string, List<IColorMap>>(StringComparer.Ordinal);
            foreach (var p in Palettes)
            {
                if (p.Type != type) continue;
                string cat = GetStaticName(p);
                if (!groups.TryGetValue(cat, out var list))
                    groups[cat] = list = new List<IColorMap>();
                list.Add(p);
            }
            return groups;
        }
        /// <summary>
        /// Returns the tooltip description for a palette name, or empty string.
        /// </summary>
        public static string GetDescription(string name)
        {
            foreach (var p in Palettes)
                if (GetStaticName(p) == name) return GetStaticDescription(p);
            return string.Empty;
        }
        // ── Reflection helpers ────────────────────────────────────────────────
        // Static interface members are not accessible via the interface reference;
        // use reflection to read the implementation type's static properties.

        public static string GetStaticName(IColorMap map)
            => map.GetType().GetProperty("Name")?.GetValue(null)?.ToString() ?? "Unnamed";

        public static string GetStaticCategory(IColorMap map)
            => map.GetType().GetProperty("Category")?.GetValue(null)?.ToString() ?? "General";

        public static string GetStaticDescription(IColorMap map)
            => map.GetType().GetProperty("Description")?.GetValue(null)?.ToString() ?? string.Empty;

        public static ColorMapFeatures GetStaticFeatures(IColorMap map)
        {
            var raw = map.GetType().GetProperty("Features")?.GetValue(null);
            return raw is ColorMapFeatures f ? f : ColorMapFeatures.UsesSmooth;
        }
    }
}
