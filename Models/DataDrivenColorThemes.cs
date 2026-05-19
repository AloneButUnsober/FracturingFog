// Models/DataDrivenColorThemes.cs
//
// Concrete IColorMap implementations driven entirely by a ColorThemeData DTO.
// These classes let a JSON-loaded theme actually render — they slot into the
// existing GradientColorMap / CyclingGradientColorMap / GradientPhong3DBase /
// PbrGradient3DBase hierarchy without needing per-theme C# code.
//
// All four classes implement INamedColorMap so the existing ColorPalette
// reflection helpers pick up the per-instance Name / Category / Description
// instead of the (shared) static type-level metadata.

using FracturingFog.Interefaces;

using System;
using System.Collections.Generic;

namespace FracturingFog.Models
{
    /// <summary>
    /// Factory for instantiating the right runtime class for a given
    /// <see cref="ColorThemeData"/>.
    /// </summary>
    public static class DataDrivenColorThemes
    {
        /// <summary>
        /// Builds a runtime <see cref="IColorMap"/> from a serialised theme,
        /// or null if <paramref name="data"/> is missing required fields.
        /// </summary>
        public static IColorMap? Create(ColorThemeData data)
        {
            if (data == null) return null;
            if (data.Stops == null || data.Stops.Count < 2) return null;

            return data.Kind switch
            {
                ColorThemeKind.Gradient => new DataDrivenGradient(data),
                ColorThemeKind.Cycling => new DataDrivenCyclingGradient(data),
                ColorThemeKind.Phong3D => new DataDrivenPhong3D(data),
                ColorThemeKind.Pbr3D => new DataDrivenPbr3D(data),
                _ => null,
            };
        }

        /// <summary>
        /// Snapshots a live <see cref="IColorMap"/> back into a
        /// <see cref="ColorThemeData"/> suitable for JSON export.  For built-in
        /// PBR themes whose metal/roughness function is overridden in code, the
        /// function is sampled at fixed intervals to recover a piecewise band
        /// approximation (good enough for round-trip on the existing themes,
        /// which all use piecewise-constant material functions).
        /// </summary>
        public static ColorThemeData? Export(IColorMap map)
        {
            if (map == null) return null;

            string name = ColorPalette.GetStaticName(map);
            string category = ColorPalette.GetStaticCategory(map);
            string description = ColorPalette.GetStaticDescription(map);
            double maxZoom = ColorPalette.GetStaticMaxZoom(map);
            double? maxZoomField = double.IsPositiveInfinity(maxZoom) ? null : maxZoom;

            switch (map)
            {
                case PbrGradient3DBase pbr:
                    {
                        var data = new ColorThemeData
                        {
                            Name = name,
                            Category = category,
                            Description = description,
                            MaxRecommendedZoom = maxZoomField,
                            Kind = ColorThemeKind.Pbr3D,
                            Stops = StopsToData(pbr.ExportStops),
                            CycleSpeed = pbr.ExportCycleSpeed,
                            Steepness = pbr.ExportSteepness,
                            Ambient = pbr.ExportAmbient,
                            KeyLight = new LightSourceData(pbr.ExportKeyLight),
                            FillLight = new LightSourceData(pbr.ExportFillLight),
                            PbrLightingMode = pbr.ExportLightingMode,
                            MaterialBands = SampleMaterialBands(pbr),
                        };

                        // Approximate GlowBoost as scale * t^exponent.  For piecewise
                        // pow-of-t implementations (the only kind currently in use),
                        // sampling at t=1 recovers the scale; the exponent isn't
                        // recoverable from a single sample, so we keep the default
                        // and let the user tune it.
                        float glowAtOne = pbr.ExportGlowBoost(1f);
                        data.GlowBoostScale = glowAtOne;
                        data.GlowBoostExponent = 8f;   // matches all current Cesium PBR variants
                        return data;
                    }

                case GradientPhong3DBase phong:
                    return new ColorThemeData
                    {
                        Name = name,
                        Category = category,
                        Description = description,
                        MaxRecommendedZoom = maxZoomField,
                        Kind = ColorThemeKind.Phong3D,
                        Stops = StopsToData(phong.ExportStops),
                        CycleSpeed = phong.ExportCycleSpeed,
                        Steepness = phong.ExportSteepness,
                        Ambient = phong.ExportAmbient,
                        KeyLight = new LightSourceData(phong.ExportKeyLight),
                        FillLight = new LightSourceData(phong.ExportFillLight),
                        KeySpecScale = phong.ExportKeySpecScale,
                        FillSpecScale = phong.ExportFillSpecScale,
                        FillDiffScale = phong.ExportFillDiffScale,
                    };

                case CyclingGradientColorMap cyc:
                    return new ColorThemeData
                    {
                        Name = name,
                        Category = category,
                        Description = description,
                        MaxRecommendedZoom = maxZoomField,
                        Kind = ColorThemeKind.Cycling,
                        Stops = StopsToData(cyc.ExportStops),
                        CycleSpeed = cyc.ExportCycleSpeed,
                    };

                case GradientColorMap grad:
                    return new ColorThemeData
                    {
                        Name = name,
                        Category = category,
                        Description = description,
                        MaxRecommendedZoom = maxZoomField,
                        Kind = ColorThemeKind.Gradient,
                        Stops = StopsToData(grad.ExportStops),
                    };

                default:
                    // Algorithmic themes (HSV, Bernstein, etc.) are pure code
                    // with no exposed parameter surface — nothing meaningful to
                    // export.
                    return null;
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static List<ColorStopData> StopsToData(IReadOnlyList<ColorStop> stops)
        {
            var list = new List<ColorStopData>(stops.Count);
            foreach (var s in stops) list.Add(new ColorStopData(s));
            return list;
        }

        /// <summary>
        /// Samples the PBR material function at fine intervals and groups
        /// consecutive identical (metal, roughness) results into bands.
        /// </summary>
        private static List<PbrMaterialBandData> SampleMaterialBands(PbrGradient3DBase pbr)
        {
            const int samples = 50;          // every 0.02 across [0,1]
            const float epsilon = 1e-4f;

            var bands = new List<PbrMaterialBandData>();
            float prevMetal = float.NaN, prevRough = float.NaN;

            for (int i = 0; i <= samples; i++)
            {
                float t = (float)i / samples;
                var mat = pbr.ExportMaterial(t);

                bool changed = bands.Count == 0
                            || MathF.Abs(mat.Metalness - prevMetal) > epsilon
                            || MathF.Abs(mat.Roughness - prevRough) > epsilon;

                if (changed)
                {
                    bands.Add(new PbrMaterialBandData
                    {
                        UpperT = t,                // tightened below
                        Metal = mat.Metalness,
                        Roughness = mat.Roughness,
                    });
                    prevMetal = mat.Metalness;
                    prevRough = mat.Roughness;
                }
                else if (bands.Count > 0)
                {
                    bands[^1].UpperT = t;            // extend current band
                }
            }

            // Ensure the last band acts as a catch-all.
            if (bands.Count > 0) bands[^1].UpperT = 1.0f;
            return bands;
        }
    }

    // =========================================================================
    // 1. Linear gradient
    // =========================================================================

    public sealed class DataDrivenGradient : GradientColorMap, IColorMap, INamedColorMap
    {
        public string DisplayName { get; }
        public string DisplayCategory { get; }
        public string DisplayDescription { get; }
        public double DisplayMaxRecommendedZoom { get; }

        private readonly uint _inSetColor;
        uint IColorMap.InSetColor => _inSetColor;

        public DataDrivenGradient(ColorThemeData data)
        {
            DisplayName = data.Name;
            DisplayCategory = data.Category;
            DisplayDescription = data.Description;
            DisplayMaxRecommendedZoom = data.MaxRecommendedZoom ?? double.PositiveInfinity;
            _inSetColor = data.InSetColor?.ToPackedArgb() ?? 0xFF000000u;
            foreach (var s in data.Stops)
                Stops.Add(s.ToColorStop());
        }
    }

    // =========================================================================
    // 2. Cycling gradient
    // =========================================================================

    public sealed class DataDrivenCyclingGradient : CyclingGradientColorMap, IColorMap, INamedColorMap
    {
        public string DisplayName { get; }
        public string DisplayCategory { get; }
        public string DisplayDescription { get; }
        public double DisplayMaxRecommendedZoom { get; }

        private readonly float _cycleSpeed;
        protected override float CycleSpeed => _cycleSpeed;

        private readonly uint _inSetColor;
        uint IColorMap.InSetColor => _inSetColor;

        public DataDrivenCyclingGradient(ColorThemeData data)
        {
            DisplayName = data.Name;
            DisplayCategory = data.Category;
            DisplayDescription = data.Description;
            DisplayMaxRecommendedZoom = data.MaxRecommendedZoom ?? double.PositiveInfinity;
            _cycleSpeed = data.CycleSpeed;
            _inSetColor = data.InSetColor?.ToPackedArgb() ?? 0xFF000000u;
            foreach (var s in data.Stops)
                Stops.Add(s.ToColorStop());
        }
    }

    // =========================================================================
    // 3. Phong 3D
    // =========================================================================

    public sealed class DataDrivenPhong3D : GradientPhong3DBase, IColorMap, INamedColorMap
    {
        public string DisplayName { get; }
        public string DisplayCategory { get; }
        public string DisplayDescription { get; }
        public double DisplayMaxRecommendedZoom { get; }

        private readonly float _cycleSpeed, _steepness, _ambient;
        private readonly float _keySpecScale, _fillSpecScale, _fillDiffScale;

        protected override float CycleSpeed => _cycleSpeed;
        protected override float Steepness => _steepness;
        protected override float Ambient => _ambient;
        protected override float KeySpecScale => _keySpecScale;
        protected override float FillSpecScale => _fillSpecScale;
        protected override float FillDiffScale => _fillDiffScale;

        private readonly uint _inSetColor;
        uint IColorMap.InSetColor => _inSetColor;

        public DataDrivenPhong3D(ColorThemeData data)
        {
            DisplayName = data.Name;
            DisplayCategory = data.Category;
            DisplayDescription = data.Description;
            DisplayMaxRecommendedZoom = data.MaxRecommendedZoom ?? double.PositiveInfinity;

            _cycleSpeed = data.CycleSpeed;
            _steepness = data.Steepness;
            _ambient = data.Ambient;
            _keySpecScale = data.KeySpecScale;
            _fillSpecScale = data.FillSpecScale;
            _fillDiffScale = data.FillDiffScale;
            _inSetColor = data.InSetColor?.ToPackedArgb() ?? 0xFF000000u;

            foreach (var s in data.Stops)
                Stops.Add(s.ToColorStop());

            KeyLight = (data.KeyLight ?? DefaultKey()).ToLightSource();
            FillLight = (data.FillLight ?? DefaultFill()).ToLightSource();
        }

        private static LightSourceData DefaultKey() => new()
        {
            Lx = -0.5f,
            Ly = 0.7f,
            Lz = 0.6f,
            DiffR = 1f,
            DiffG = 1f,
            DiffB = 1f,
            SpecR = 1f,
            SpecG = 1f,
            SpecB = 1f,
            Shininess = 64f,
        };

        private static LightSourceData DefaultFill() => new()
        {
            Lx = 0.6f,
            Ly = -0.4f,
            Lz = 0.5f,
            DiffR = 0.4f,
            DiffG = 0.4f,
            DiffB = 0.5f,
            SpecR = 0.3f,
            SpecG = 0.3f,
            SpecB = 0.4f,
            Shininess = 32f,
        };
    }

    // =========================================================================
    // 4. PBR 3D
    // =========================================================================

    public sealed class DataDrivenPbr3D : PbrGradient3DBase, IColorMap, INamedColorMap
    {
        public string DisplayName { get; }
        public string DisplayCategory { get; }
        public string DisplayDescription { get; }
        public double DisplayMaxRecommendedZoom { get; }

        private readonly float _cycleSpeed, _steepness, _ambient;
        private readonly PbrLightingMode _lightingMode;
        private readonly float _glowExp, _glowScale;
        private readonly PbrMaterialBandData[] _bands;

        protected override float CycleSpeed => _cycleSpeed;
        protected override float Steepness => _steepness;
        protected override float Ambient => _ambient;
        protected override PbrLightingMode LightingMode => _lightingMode;

        private readonly uint _inSetColor;
        uint IColorMap.InSetColor => _inSetColor;

        public DataDrivenPbr3D(ColorThemeData data)
        {
            DisplayName = data.Name;
            DisplayCategory = data.Category;
            DisplayDescription = data.Description;
            DisplayMaxRecommendedZoom = data.MaxRecommendedZoom ?? double.PositiveInfinity;

            _cycleSpeed = data.CycleSpeed;
            _steepness = data.Steepness;
            _ambient = data.Ambient;
            _lightingMode = data.PbrLightingMode;
            _glowExp = data.GlowBoostExponent;
            _glowScale = data.GlowBoostScale;
            _inSetColor = data.InSetColor?.ToPackedArgb() ?? 0xFF000000u;
            _bands = (data.MaterialBands?.Count ?? 0) > 0
                                ? data.MaterialBands!.ToArray()
                                : new[]
                                  {
                                      new PbrMaterialBandData
                                      {
                                          UpperT = 1.0f,
                                          Metal = 0.0f,
                                          Roughness = 0.7f,
                                      }
                                  };

            foreach (var s in data.Stops)
                Stops.Add(s.ToColorStop());

            KeyLight = (data.KeyLight ?? DefaultKey()).ToLightSource();
            FillLight = (data.FillLight ?? DefaultFill()).ToLightSource();
        }

        protected override float GlowBoost(float t)
            => _glowScale <= 0f ? 0f : _glowScale * MathF.Pow(t, _glowExp);

        protected override PbrMaterial BuildMaterial(float t, float r, float g, float b)
        {
            // First band whose UpperT exceeds t wins; final band acts as catch-all.
            for (int i = 0; i < _bands.Length - 1; i++)
            {
                if (t < _bands[i].UpperT)
                    return new PbrMaterial(r, g, b, _bands[i].Metal, _bands[i].Roughness);
            }
            var last = _bands[^1];
            return new PbrMaterial(r, g, b, last.Metal, last.Roughness);
        }

        private static LightSourceData DefaultKey() => new()
        {
            Lx = -0.5f,
            Ly = 0.7f,
            Lz = 0.7f,
            DiffR = 1.2f,
            DiffG = 1.2f,
            DiffB = 1.3f,
            SpecR = 0f,
            SpecG = 0f,
            SpecB = 0f,
            Shininess = 1f,
        };

        private static LightSourceData DefaultFill() => new()
        {
            Lx = 0.6f,
            Ly = -0.4f,
            Lz = 0.5f,
            DiffR = 0.3f,
            DiffG = 0.4f,
            DiffB = 0.6f,
            SpecR = 0f,
            SpecG = 0f,
            SpecB = 0f,
            Shininess = 1f,
        };
    }
}