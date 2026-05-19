// Models/ColorSchemes/ColorPalettes.cs  — v6 (user-defined JSON themes)
//
// Add new built-in themes to the BuiltIns list; they appear automatically in
// the UI.  User-defined themes loaded from %APPDATA%\FracturingFog\colorthemes.json
// are appended via UserPalettes — call LoadUserThemes() once at startup.
//
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
        // ── Built-in palette list ─────────────────────────────────────────────
        // Order here controls the order in the UI combo box.

        public static readonly List<IColorMap> BuiltIns = new()
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
            new CesiumSpectrumPhong3D(),
            new WoodGrainPhong3D(),
            new CesiumSpectrumPbr3D(),
            new CesiumSpectrumPbr3D_Realistic(),
            new CesiumSpectrumPbr3D_UltraGlow(),
            new RadioInterferencePhong3D(),
            new RadioInterferencePbr3D(),
            new GoldenRatioPbr3D(),
            new GoldenRatioPhi3DPbr(),

            // ── Algorithmic 3D — Phong ────────────────────────────────────────
            new BernsteinPhong3D(),
            new CopperSheenPhong3D(),
            new DigitalMatrixPhong3D(),
            new DistanceGlowPhong3D(),
            new FirePhong3D(),
            new GoldenRatioPhong3D(),
            new GrayscalePhong3D(),
            new HsvPhong3D(),
            new MonoBandPhong3D(),
            new NebulaDustPhong3D(),
            new PaintedPhong3D(),
            new PaintedReversedPhong3D(),
            new PastellyPhong3D(),
            new PsychedelicPhong3D(),
            new RadioInterferenceOriginalPhong3D(),
            new RadioInterferenceOriginalBluePhong3D(),
            new RainbowPhong3D(),
            new RedAndBlackPhong3D(),
            new SolarWindPhong3D(),
            new SolarWindModPhong3D(),
            new TwilightCyclicPhong3D(),
            new VintageSepiaPhong3D(),
            new WarpedHsvPhong3D(),
            new GoldenRatioPhi3DPhong(),

            // ── Algorithmic 3D — PBR ──────────────────────────────────────────
            new BernsteinPbr3D(),
            new CopperSheenPbr3D(),
            new DigitalMatrixPbr3D(),
            new DistanceGlowPbr3D(),
            new FirePbr3D(),
            new GoldenRatioPbr3D(),
            new GrayscalePbr3D(),
            new HsvPbr3D(),
            new MonoBandPbr3D(),
            new NebulaDustPbr3D(),
            new PaintedPbr3D(),
            new PaintedReversedPbr3D(),
            new PastellyPbr3D(),
            new PsychedelicPbr3D(),
            new RadioInterferenceOriginalPbr3D(),
            new RainbowPbr3D(),
            new SolarWindPbr3D(),
            new SolarWindModPbr3D(),
            new TwilightCyclicPbr3D(),
            new VintageSepiaPbr3D(),
            new WarpedHsvPbr3D(),

            // ── Classic / algorithmic ─────────────────────────────────────────
            new HsvPalette(),
            new Painted(),
            new PaintedReversed(),
            new Pastelly(),
            new WarpedHsvMap(),
            new RainbowColorMap(),
            new GoldenRatioMap(),
            new MonoBandMap(),
            new BernsteinMap(),
            new CosmicMandalaMap(),
            new RedAndBlack(),

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
            new CesiumSpectrumGradient(),
            new WoodGrainGradient(),
            new RadioInterferenceGradient(),
            new GoldenRatioGradient(),

            // ── Gradient — cycling ────────────────────────────────────────────
            new FirePalette(),
            new CosmicLatteMap(),
            new TropicalMap(),
            new LavaLampMap(),
            new TriColorMap(),
            new CesiumSpectrumCycling(),
            new WoodGrainCycling(),
            new RadioInterferenceCycling(),
            new GoldenRatioCycling(),

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

            // ── Orbit Trap ────────────────────────────────────────────────────
            new OrbitTrapPointMap(),
            new OrbitTrapCrossMap(),
            new OrbitTrapCircleMap(),
            new OrbitTrapLineMap(),
            new OrbitTrapStarMap(),
            new OrbitTrapPickoverStalksMap(),
            new OrbitTrapBiomorphMap(),
            new OrbitTrapImageRainbowMap(),

            // ── Stripe Average + TIA (Ultra Fractal look) ─────────────────────
            new StripeAverageClassicMap(),
            new TriangleInequalityMap(),
            new StripeTiaBlendMap(),

            // ── Statistical averaging ────────────────────────────────────────
            new CurvatureAverageMap(),
            new LyapunovExponentMap(),
            new GaussianIntegerMap(),
            new ExponentialSmoothingMap(),

            // ── Distance Estimation ───────────────────────────────────────────
            new DistanceFieldChromaticMap(),
            new DistanceFieldGlowMap(),
            new DistanceFieldSilverMap(),

            // ── Lambert / slope standalone shading ───────────────────────────
            new LambertShadingMap(),
            new SlopeShadingMap(),

            // ── Post-process effects (Phase 3) ───────────────────────────────
            new EmbossBumpMap(),
            new AmbientOcclusionMap(),
            new SoftShadowMap(),

            // ── Interior colouring (Phase 4) ─────────────────────────────────
            new CyclePeriodMap(),
            new MultiplierMap(),
            new AtomDomainsMap(),
            new InteriorArgumentMap(),
            new FakeDistanceEstimateMap(),

            // ── Distance Estimation ───────────────────────────────────────────
            new DistanceFieldChromaticMap(),
            new DistanceFieldGlowMap(),
            new DistanceFieldSilverMap(),

            // ── Binary Compression  ───────────────────────────────────────────\
            new BinaryDecompClassicMap(),
            new BinaryDecompContourMap(),
            new BinaryDecompGoldMap(),

            // ── Derivative Magnitude Map ──────────────────────────────────────\
            new DerivativeAngleMap(),
            new DerivativeFlowMap(),
            new DerivativeMagnitudeMap(),

            // ── Domain Color Map ──────────────────────────────────────\
            new DomainColorClassicMap(),
            new DomainColorPhasePortraitMap(),
            new DomainColorRiemannMap(),

            // ── Escape Time ────────────────────────────────────────────\
            new RawIterationBandsMap(),
            new LevelSetStaircaseMap(),
            new BinaryDwellRingsMap(),

            // ── Field Lines ────────────────────────────────────────────\
            new FieldLinesBinaryComboMap(),
            new FieldLinesContinuousMap(),
            new FieldLinesDiscreteMap(),

            // ── Histogram Themes ────────────────────────────────────────────\
            new HistogramSpectralMap(),
            new HistogramTwilightMap(),
            new HistogramViridisMap(),

            // ── Lemniscate Edge Maps ────────────────────────────────────────────\
            new LemniscateContourMap(),
            new LemniscateEdgeMap(),
            new LemniscateFilledMap(),

            // ── Potnetial Themes ────────────────────────────────────────────\
            new PotentialContourMap(),
            new PotentialEquipotentialMap(),
            new PotentialSmoothMap(),

            // Eyetrick Themes
            new AcidCarnivalNeonMap(),
            new BubblegumRiotMap(),
            new ChromostereopsisMap(),
            new Chromostereopsis3DPhongMap(),
            new Chromostereopsis3DPbrMap(),
            new Chromostereopsis3DLambertMap(),
            new Chromostereopsis3DSlopeMap(),
            // ── Chromostereopsis family (variation / contrast / saturation / mood) ──
            new ChromostereopsisInvertedMap(),
            new ChromostereopsisOrangeCyanMap(),
            new ChromostereopsisYellowVioletMap(),
            new ChromostereopsisMagentaTealMap(),
            new ChromostereopsisAnaglyphMap(),
            new ChromostereopsisHalloweenMap(),
            new ChromostereopsisToxicMap(),
            new ChromostereopsisVoltageMap(),
            new ChromostereopsisBloodMoonMap(),
            new ChromostereopsisStainedGlassMap(),
            new ChromostereopsisFuneralMap(),
            new ChromostereopsisCandyShockMap(),
            new ChromostereopsisEmberFrostMap(),
            new ChromostereopsisFestivalMap(),
            new ChromostereopsisInfraredMap(),
            new ChromostereopsisPastelWhisperMap(),
            new ChromostereopsisAlarmStripesMap(),
            new OilslickIridescenceMap(),
            new SaturdayMorningCartoonMap(),
            new VermillionStabMap(),

            // Bird of Paradise themes
            new BirdOfParadiseCyclingMap(),
            new BirdOfParadiseMap(),
            new BirdOfParadisePbr3DMap(),
            new BirdOfParadisePhong3DMap(),

            // Emerold Ghost themes
            new EmeraldGhostCyclingMap(),
            new EmeraldGhostGrad2Map(),
            new EmeraldGhostGradMap(),

            // MS Standard themes
            new MSStandardAmberCyclingMap(),
            new MSStandardAmberLinearMap(),
            new MSStandardAmberPbr3DMap(),
            new MSStandardAmberPhong3DMap(),
            new MSStandardBlueCyclingMap(),
            new MSStandardBlueLinearMap(),
            new MSStandardBluePbr3DMap(),
            new MSStandardBluePhong3DMap(),
            new MSStandardSunriseCyclingMap(),
            new MSStandardSunriseLinearMap(),
            new MSStandardSunrisePbr3DMap(),
            new MSStandardSunrisePhong3DMap(),

            // "New" 3D
            new New3DCycMap(),
            new New3DCycModMap(),
            new New3DGradMap(),
            new New3DModPhongMap(),
            new New3DPbrMap(),
            new New3DPhongMap(),
            
            // Pastel Spiral
            new PastelSpiralBloomCyclingMap(),
            new PastelSpiralBloomLinearMap(),
            new PastelSpiralBloomPbr3DMap(),
            new PastelSpiralBloomPhong3DMap(),

            // Plasma 9 themes
            new Plasma9Tst2Map(),
            new Plasma9Tst4Map(),
            new Plasma9TstMap(),
            
        };

        /// <summary>
        /// User-defined palettes loaded from JSON.  Mutated by
        /// <see cref="LoadUserThemes"/> and by add/remove operations on
        /// <see cref="UserColorThemeLibrary"/>.
        /// </summary>
        public static readonly List<IColorMap> UserPalettes = new();

        /// <summary>
        /// Back-compat alias for the original combined list.  Concatenates
        /// built-in themes with any user-defined themes that have been loaded.
        /// </summary>
        public static IEnumerable<IColorMap> Palettes
        {
            get
            {
                foreach (var p in BuiltIns) yield return p;
                foreach (var p in UserPalettes) yield return p;
            }
        }

        // ── User-theme integration ────────────────────────────────────────────

        /// <summary>
        /// Loads user-defined themes from JSON via <see cref="UserColorThemeLibrary"/>
        /// and populates <see cref="UserPalettes"/>.  Safe to call multiple times.
        /// </summary>
        public static void LoadUserThemes()
        {
            UserColorThemeLibrary.Instance.Load();
            RebuildUserPalettes();
        }

        /// <summary>
        /// Re-syncs <see cref="UserPalettes"/> from the current contents of
        /// <see cref="UserColorThemeLibrary.Instance"/>.  Call after adding,
        /// removing, or editing user themes.
        /// </summary>
        public static void RebuildUserPalettes()
        {
            UserPalettes.Clear();
            foreach (var data in UserColorThemeLibrary.Instance.Themes)
            {
                var map = DataDrivenColorThemes.Create(data);
                if (map != null) UserPalettes.Add(map);
            }
        }

        // ── Lookup helpers ────────────────────────────────────────────────────

        /// <summary>
        /// Returns the <see cref="IColorMap"/> whose name matches
        /// <paramref name="name"/>, or a new <see cref="HsvPalette"/> if not found.

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
        /// Returns palettes grouped by category.
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
            LoadUserThemes();
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

        /// <summary>
        /// GetMaxDescriptionLength() is used to size the tooltip box for palette descriptions.
        /// </summary>
        /// <returns>The maximum length of all palette descriptions.</returns>
        public static int GetMaxDescriptionLength()
        {
            int max = 0;
            foreach (var p in Palettes)
            {
                int len = GetStaticDescription(p)?.Length ?? 0;
                if (len > max) max = len;
            }
            return max;
        }

        // ── Reflection helpers ────────────────────────────────────────────────
        // Built-in themes carry Name/Category/Description as static type-level
        // properties (read via reflection).  Data-driven themes implement
        // INamedColorMap so a single runtime type can host many distinct themes.

        public static string GetStaticName(IColorMap map)
        {
            if (map is INamedColorMap n) return n.DisplayName;
            return map.GetType().GetProperty("Name")?.GetValue(null)?.ToString() ?? "Unnamed";
        }

        public static string GetStaticCategory(IColorMap map)
        {
            if (map is INamedColorMap n) return n.DisplayCategory;
            return map.GetType().GetProperty("Category")?.GetValue(null)?.ToString() ?? "General";
        }

        public static string GetStaticDescription(IColorMap map)
        {
            if (map is INamedColorMap n) return n.DisplayDescription;
            return map.GetType().GetProperty("Description")?.GetValue(null)?.ToString() ?? string.Empty;
        }

        public static ColorMapFeatures GetStaticFeatures(IColorMap map)
        {
            var raw = map.GetType().GetProperty("Features")?.GetValue(null);
            return raw is ColorMapFeatures f ? f : ColorMapFeatures.UsesSmooth;
        }

        /// <summary>
        /// Returns the maximum zoom at which the supplied colour map is
        /// recommended for automated viewing (slideshow, video zoom).
        /// Resolution order:
        ///   1. Per-class static <c>MaxRecommendedZoom</c> override (read via reflection).
        ///   2. Per-instance <see cref="INamedColorMap.DisplayMaxRecommendedZoom"/>
        ///      for data-driven themes that carry the cap in their DTO.
        ///   3. Interface-based defaults — orbit-aware themes have no HP/PT path
        ///      so they are capped at <c>1e10</c>; interior-aware themes rely on
        ///      cycle detection budgets and are capped at <c>1e13</c>.
        ///   4. Feature-flag defaults — themes that read distance estimation,
        ///      escape-time derivative, or final z values are capped because
        ///      those quantities lose precision in the perturbation regime.
        ///      Relief3D themes are exempt from the feature-flag rule because
        ///      they consume distance only for ambient occlusion / shadow
        ///      effects, not as the primary colour signal.
        ///   5. Fallback: <see cref="double.PositiveInfinity"/>.
        /// </summary>
        public static double GetStaticMaxZoom(IColorMap map)
        {
            var raw = map.GetType().GetProperty("MaxRecommendedZoom")?.GetValue(null);
            if (raw is double d && d > 0 && !double.IsNaN(d))
                return d;

            if (map is INamedColorMap n)
            {
                double named = n.DisplayMaxRecommendedZoom;
                if (named > 0 && !double.IsNaN(named) && !double.IsPositiveInfinity(named))
                    return named;
            }

            if (map is IOrbitAwareColorMap) return 1e10;
            if (map is IInteriorAwareColorMap) return 1e13;

            // 3D relief themes use distance for AO/shadow, not as a primary
            // colour signal, so they don't fall under the distance-estimation
            // cap below.
            if (map.Type == ColorPaletteType.Relief3D) return double.PositiveInfinity;

            var features = GetStaticFeatures(map);
            if (features.HasFlag(ColorMapFeatures.UsesDistance)) return 1e15;
            if (features.HasFlag(ColorMapFeatures.UsesDerivative)) return 1e15;
            if (features.HasFlag(ColorMapFeatures.UsesFinalZ)) return 1e20;

            return double.PositiveInfinity;
        }

        /// <summary>
        /// Returns the names of all registered palettes whose
        /// <see cref="GetStaticMaxZoom"/> is at least <paramref name="zoom"/>.
        /// Header entries (names starting with "—") are excluded. If the filter
        /// would produce an empty list (every registered theme is capped below
        /// <paramref name="zoom"/>), the unfiltered full list is returned so
        /// callers never get a zero-length pool.
        /// </summary>
        public static List<string> GetPaletteNamesForZoom(double zoom)
        {
            var filtered = new List<string>();
            var fallback = new List<string>();
            foreach (var p in Palettes)
            {
                string name = GetStaticName(p);
                if (string.IsNullOrEmpty(name) || name.StartsWith("—")) continue;
                fallback.Add(name);
                if (zoom <= GetStaticMaxZoom(p)) filtered.Add(name);
            }
            return filtered.Count > 0 ? filtered : fallback;
        }
    }
}