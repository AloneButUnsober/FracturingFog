// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

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
                ColorThemeKind.OrbitTrap => new DataDrivenOrbitTrap(data),
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

            // Post-FX defaults are per-instance metadata carried only by
            // data-driven user themes. Built-in C# themes don't implement
            // IThemePostFx so all three fields stay null (no opinion).
            int? bright = null, contrast = null, adaptive = null;
            if (map is IThemePostFx pfx)
            {
                bright = pfx.ThemeBrightness;
                contrast = pfx.ThemeContrast;
                adaptive = pfx.ThemeAdaptive;
            }

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
                            InSetColor = InSetFromMap(map),
                            InterpolationSpace = pbr.ExportInterpolationSpace,
                            InterpolationCurve = pbr.ExportInterpolationCurve,
                            TransferFunction = pbr.ExportTransferFunction,
                            TransferStrength = pbr.ExportTransferStrength,
                            PaletteGamma = pbr.ExportPaletteGamma,
                            SparkleStride = pbr.ExportSparkleStride,
                            SparkleBoost = pbr.ExportSparkleBoost,
                            SeamlessCycle = pbr.ExportSeamlessCycle,
                            XorLevels = pbr.ExportXorLevels,
                            XorMask = pbr.ExportXorMask,
                            ColorOffset = pbr.ExportColorOffset,
                            ColorDensity = pbr.ExportColorDensity,
                            WrapMode = pbr.ExportWrapMode,
                            Stops = StopsToData(pbr.ExportStops),
                            CycleSpeed = pbr.ExportCycleSpeed,
                            Steepness = pbr.ExportSteepness,
                            Ambient = pbr.ExportAmbient,
                            KeyLight = new LightSourceData(pbr.ExportKeyLight),
                            FillLight = new LightSourceData(pbr.ExportFillLight),
                            RimLight = pbr.ExportUseRimLight ? new LightSourceData(pbr.ExportRimLight) : null,
                            PbrLightingMode = pbr.ExportLightingMode,
                            MaterialBands = SampleMaterialBands(pbr),
                            Brightness = bright,
                            Contrast = contrast,
                            Adaptive = adaptive,
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
                        InSetColor = InSetFromMap(map),
                        InterpolationSpace = phong.ExportInterpolationSpace,
                        InterpolationCurve = phong.ExportInterpolationCurve,
                        TransferFunction = phong.ExportTransferFunction,
                        TransferStrength = phong.ExportTransferStrength,
                        PaletteGamma = phong.ExportPaletteGamma,
                        SparkleStride = phong.ExportSparkleStride,
                        SparkleBoost = phong.ExportSparkleBoost,
                        SeamlessCycle = phong.ExportSeamlessCycle,
                        XorLevels = phong.ExportXorLevels,
                        XorMask = phong.ExportXorMask,
                        ColorOffset = phong.ExportColorOffset,
                        ColorDensity = phong.ExportColorDensity,
                        WrapMode = phong.ExportWrapMode,
                        Stops = StopsToData(phong.ExportStops),
                        CycleSpeed = phong.ExportCycleSpeed,
                        Steepness = phong.ExportSteepness,
                        Ambient = phong.ExportAmbient,
                        KeyLight = new LightSourceData(phong.ExportKeyLight),
                        FillLight = new LightSourceData(phong.ExportFillLight),
                        RimLight = phong.ExportUseRimLight ? new LightSourceData(phong.ExportRimLight) : null,
                        KeySpecScale = phong.ExportKeySpecScale,
                        FillSpecScale = phong.ExportFillSpecScale,
                        FillDiffScale = phong.ExportFillDiffScale,
                        RimSpecScale = phong.ExportRimSpecScale,
                        RimDiffScale = phong.ExportRimDiffScale,
                        Brightness = bright,
                        Contrast = contrast,
                        Adaptive = adaptive,
                    };

                case CyclingGradientColorMap cyc:
                    return new ColorThemeData
                    {
                        Name = name,
                        Category = category,
                        Description = description,
                        MaxRecommendedZoom = maxZoomField,
                        Kind = ColorThemeKind.Cycling,
                        InSetColor = InSetFromMap(map),
                        InterpolationSpace = cyc.ExportInterpolationSpace,
                        InterpolationCurve = cyc.ExportInterpolationCurve,
                        TransferFunction = cyc.ExportTransferFunction,
                        TransferStrength = cyc.ExportTransferStrength,
                        PaletteGamma = cyc.ExportPaletteGamma,
                        SparkleStride = cyc.ExportSparkleStride,
                        SparkleBoost = cyc.ExportSparkleBoost,
                        SeamlessCycle = cyc.ExportSeamlessCycle,
                        XorLevels = cyc.ExportXorLevels,
                        XorMask = cyc.ExportXorMask,
                        ColorOffset = cyc.ExportColorOffset,
                        ColorDensity = cyc.ExportColorDensity,
                        WrapMode = cyc.ExportWrapMode,
                        Stops = StopsToData(cyc.ExportStops),
                        CycleSpeed = cyc.ExportCycleSpeed,
                        Brightness = bright,
                        Contrast = contrast,
                        Adaptive = adaptive,
                    };

                case DataDrivenOrbitTrap trap:
                    return new ColorThemeData
                    {
                        Name = name,
                        Category = category,
                        Description = description,
                        MaxRecommendedZoom = maxZoomField,
                        Kind = ColorThemeKind.OrbitTrap,
                        InSetColor = InSetFromMap(map),
                        TrapShape = trap.Shape,
                        TrapScale = trap.ExportTrapScale,
                        TrapPower = trap.ExportTrapPower,
                        ColorInterior = trap.ExportColorInterior,
                        InterpolationSpace = trap.ExportInterpolationSpace,
                        InterpolationCurve = trap.ExportInterpolationCurve,
                        TransferFunction = trap.ExportTransferFunction,
                        TransferStrength = trap.ExportTransferStrength,
                        PaletteGamma = trap.ExportPaletteGamma,
                        SparkleStride = trap.ExportSparkleStride,
                        SparkleBoost = trap.ExportSparkleBoost,
                        SeamlessCycle = trap.ExportSeamlessCycle,
                        XorLevels = trap.ExportXorLevels,
                        XorMask = trap.ExportXorMask,
                        Stops = StopsToData(trap.ExportStops),
                        Brightness = bright,
                        Contrast = contrast,
                        Adaptive = adaptive,
                    };

                case GradientColorMap grad:
                    return new ColorThemeData
                    {
                        Name = name,
                        Category = category,
                        Description = description,
                        MaxRecommendedZoom = maxZoomField,
                        Kind = ColorThemeKind.Gradient,
                        InSetColor = InSetFromMap(map),
                        InterpolationSpace = grad.ExportInterpolationSpace,
                        InterpolationCurve = grad.ExportInterpolationCurve,
                        TransferFunction = grad.ExportTransferFunction,
                        TransferStrength = grad.ExportTransferStrength,
                        PaletteGamma = grad.ExportPaletteGamma,
                        SparkleStride = grad.ExportSparkleStride,
                        SparkleBoost = grad.ExportSparkleBoost,
                        SeamlessCycle = grad.ExportSeamlessCycle,
                        XorLevels = grad.ExportXorLevels,
                        XorMask = grad.ExportXorMask,
                        Stops = StopsToData(grad.ExportStops),
                        Brightness = bright,
                        Contrast = contrast,
                        Adaptive = adaptive,
                    };

                default:
                    // Algorithmic themes (HSV, Bernstein, etc.) are pure code
                    // with no exposed parameter surface — nothing meaningful to
                    // export.
                    return null;
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        /// <summary>Snapshots a runtime map's in-set colour back into an
        /// <see cref="InSetColorData"/> so an edited interior (colour + alpha, F10
        /// / #96) round-trips through Export. Returns null for the default opaque
        /// black so themes without an override serialise byte-for-byte as before
        /// (an opaque-black override collapses to the identical default).</summary>
        private static InSetColorData? InSetFromMap(IColorMap map)
        {
            uint c = map.InSetColor;
            if (c == 0xFF000000u) return null;   // default → no override
            return new InSetColorData(
                (byte)((c >> 16) & 0xFF),
                (byte)((c >> 8) & 0xFF),
                (byte)(c & 0xFF))
            {
                A = (byte)((c >> 24) & 0xFF),
            };
        }

        private static List<ColorStopData> StopsToData(IReadOnlyList<ColorStop> stops)
        {
            var list = new List<ColorStopData>(stops.Count);
            // FromColorStop replaces the old `new ColorStopData(stop)` ctor;
            // the DTO moved to FracturingFog.Abstractions and the ColorStop
            // interop helpers now live as extension methods.
            foreach (var s in stops) list.Add(ColorStopDataExtensions.FromColorStop(s));
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

    public sealed class DataDrivenGradient : GradientColorMap, IColorMap, INamedColorMap, IThemePostFx
    {
        public string DisplayName { get; }
        public string DisplayCategory { get; }
        public string DisplayDescription { get; }
        public double DisplayMaxRecommendedZoom { get; }

        public int? ThemeBrightness { get; }
        public int? ThemeContrast { get; }
        public int? ThemeAdaptive { get; }

        private readonly uint _inSetColor;
        uint IColorMap.InSetColor => _inSetColor;

        public DataDrivenGradient(ColorThemeData data)
        {
            DisplayName = data.Name;
            DisplayCategory = data.Category;
            DisplayDescription = data.Description;
            DisplayMaxRecommendedZoom = data.MaxRecommendedZoom ?? double.PositiveInfinity;
            ThemeBrightness = data.Brightness;
            ThemeContrast = data.Contrast;
            ThemeAdaptive = data.Adaptive;
            _inSetColor = data.InSetColor?.ToPackedArgb() ?? 0xFF000000u;
            InterpolationSpace = data.InterpolationSpace;
            InterpCurve = data.InterpolationCurve;
            Transfer = data.TransferFunction;
            TransferStrength = data.TransferStrength;
            PaletteGamma = data.PaletteGamma;
            SparkleStride = data.SparkleStride;
            SparkleBoost = data.SparkleBoost;
            SeamlessCycle = data.SeamlessCycle;
            XorLevels = data.XorLevels;
            XorMask = data.XorMask;
            foreach (var s in data.Stops)
                Stops.Add(s.ToColorStop());
        }
    }

    // =========================================================================
    // 2. Cycling gradient
    // =========================================================================

    public sealed class DataDrivenCyclingGradient : CyclingGradientColorMap, IColorMap, INamedColorMap, IThemePostFx
    {
        public string DisplayName { get; }
        public string DisplayCategory { get; }
        public string DisplayDescription { get; }
        public double DisplayMaxRecommendedZoom { get; }

        public int? ThemeBrightness { get; }
        public int? ThemeContrast { get; }
        public int? ThemeAdaptive { get; }

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
            ThemeBrightness = data.Brightness;
            ThemeContrast = data.Contrast;
            ThemeAdaptive = data.Adaptive;
            _cycleSpeed = data.CycleSpeed;
            _inSetColor = data.InSetColor?.ToPackedArgb() ?? 0xFF000000u;
            InterpolationSpace = data.InterpolationSpace;
            InterpCurve = data.InterpolationCurve;
            Transfer = data.TransferFunction;
            TransferStrength = data.TransferStrength;
            PaletteGamma = data.PaletteGamma;
            SparkleStride = data.SparkleStride;
            SparkleBoost = data.SparkleBoost;
            SeamlessCycle = data.SeamlessCycle;
            XorLevels = data.XorLevels;
            XorMask = data.XorMask;
            ColorOffset = data.ColorOffset;
            ColorDensity = data.ColorDensity;
            CycleWrap = data.WrapMode;
            foreach (var s in data.Stops)
                Stops.Add(s.ToColorStop());
        }
    }

    // =========================================================================
    // 3. Phong 3D
    // =========================================================================

    public sealed class DataDrivenPhong3D : GradientPhong3DBase, IColorMap, INamedColorMap, IThemePostFx
    {
        public string DisplayName { get; }
        public string DisplayCategory { get; }
        public string DisplayDescription { get; }
        public double DisplayMaxRecommendedZoom { get; }

        public int? ThemeBrightness { get; }
        public int? ThemeContrast { get; }
        public int? ThemeAdaptive { get; }

        private readonly float _cycleSpeed, _steepness, _ambient;
        private readonly float _keySpecScale, _fillSpecScale, _fillDiffScale;
        private readonly float _rimSpecScale, _rimDiffScale;

        protected override float CycleSpeed => _cycleSpeed;
        protected override float Steepness => _steepness;
        protected override float Ambient => _ambient;
        protected override float KeySpecScale => _keySpecScale;
        protected override float FillSpecScale => _fillSpecScale;
        protected override float FillDiffScale => _fillDiffScale;
        protected override float RimSpecScale => _rimSpecScale;
        protected override float RimDiffScale => _rimDiffScale;

        private readonly uint _inSetColor;
        uint IColorMap.InSetColor => _inSetColor;

        public DataDrivenPhong3D(ColorThemeData data)
        {
            DisplayName = data.Name;
            DisplayCategory = data.Category;
            DisplayDescription = data.Description;
            DisplayMaxRecommendedZoom = data.MaxRecommendedZoom ?? double.PositiveInfinity;
            ThemeBrightness = data.Brightness;
            ThemeContrast = data.Contrast;
            ThemeAdaptive = data.Adaptive;

            _cycleSpeed = data.CycleSpeed;
            _steepness = data.Steepness;
            _ambient = data.Ambient;
            _keySpecScale = data.KeySpecScale;
            _fillSpecScale = data.FillSpecScale;
            _fillDiffScale = data.FillDiffScale;
            _rimSpecScale = data.RimSpecScale;
            _rimDiffScale = data.RimDiffScale;
            _inSetColor = data.InSetColor?.ToPackedArgb() ?? 0xFF000000u;
            InterpolationSpace = data.InterpolationSpace;
            InterpCurve = data.InterpolationCurve;
            Transfer = data.TransferFunction;
            TransferStrength = data.TransferStrength;
            PaletteGamma = data.PaletteGamma;
            SparkleStride = data.SparkleStride;
            SparkleBoost = data.SparkleBoost;
            SeamlessCycle = data.SeamlessCycle;
            XorLevels = data.XorLevels;
            XorMask = data.XorMask;
            ColorOffset = data.ColorOffset;
            ColorDensity = data.ColorDensity;
            CycleWrap = data.WrapMode;

            foreach (var s in data.Stops)
                Stops.Add(s.ToColorStop());

            KeyLight = (data.KeyLight ?? DefaultKey()).ToLightSource();
            FillLight = (data.FillLight ?? DefaultFill()).ToLightSource();
            if (data.RimLight != null)
            {
                RimLight = data.RimLight.ToLightSource();
                UseRimLight = true;
            }
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

    public sealed class DataDrivenPbr3D : PbrGradient3DBase, IColorMap, INamedColorMap, IThemePostFx
    {
        public string DisplayName { get; }
        public string DisplayCategory { get; }
        public string DisplayDescription { get; }
        public double DisplayMaxRecommendedZoom { get; }

        public int? ThemeBrightness { get; }
        public int? ThemeContrast { get; }
        public int? ThemeAdaptive { get; }

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
            ThemeBrightness = data.Brightness;
            ThemeContrast = data.Contrast;
            ThemeAdaptive = data.Adaptive;

            _cycleSpeed = data.CycleSpeed;
            _steepness = data.Steepness;
            _ambient = data.Ambient;
            _lightingMode = data.PbrLightingMode;
            _glowExp = data.GlowBoostExponent;
            _glowScale = data.GlowBoostScale;
            _inSetColor = data.InSetColor?.ToPackedArgb() ?? 0xFF000000u;
            InterpolationSpace = data.InterpolationSpace;
            InterpCurve = data.InterpolationCurve;
            Transfer = data.TransferFunction;
            TransferStrength = data.TransferStrength;
            PaletteGamma = data.PaletteGamma;
            SparkleStride = data.SparkleStride;
            SparkleBoost = data.SparkleBoost;
            SeamlessCycle = data.SeamlessCycle;
            XorLevels = data.XorLevels;
            XorMask = data.XorMask;
            ColorOffset = data.ColorOffset;
            ColorDensity = data.ColorDensity;
            CycleWrap = data.WrapMode;
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
            if (data.RimLight != null)
            {
                RimLight = data.RimLight.ToLightSource();
                UseRimLight = true;
            }
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

    // =========================================================================
    // 5. Orbit Trap (F13 / #589)
    // =========================================================================

    /// <summary>
    /// Data-driven orbit-trap theme.  Reuses the whole gradient stack
    /// (<see cref="OrbitTrapPowerBaseMap"/> : <see cref="GradientColorMap"/>) and
    /// **delegates the per-iteration distance measurement** to the built-in trap
    /// sampler for the chosen <see cref="OrbitTrapShape"/> — so no shape maths is
    /// duplicated.  The gradient (this theme's own stops + the F1-F9 knobs) maps
    /// the running minimum trap distance through the tunable
    /// <see cref="TrapScale"/> / <see cref="TrapPower"/> response.
    /// </summary>
    // Re-list IOrbitAwareColorMap (already implemented by the base) so this
    // type's public WantsInteriorColor re-implements the interface's default
    // member — otherwise the base's default (false) wins and F14 never engages.
    public sealed class DataDrivenOrbitTrap : OrbitTrapPowerBaseMap, IOrbitAwareColorMap, INamedColorMap, IThemePostFx
    {
        public string DisplayName { get; }
        public string DisplayCategory { get; }
        public string DisplayDescription { get; }
        public double DisplayMaxRecommendedZoom { get; }

        public int? ThemeBrightness { get; }
        public int? ThemeContrast { get; }
        public int? ThemeAdaptive { get; }

        private readonly OrbitTrapBaseMap _shape;
        private readonly float _trapScale, _trapPower;
        private readonly bool _colorInterior;
        private readonly uint _inSetColor;

        /// <summary>Selected trap shape (exported for round-trip).</summary>
        public OrbitTrapShape Shape { get; }
        public float ExportTrapScale => _trapScale;
        public float ExportTrapPower => _trapPower;
        public bool ExportColorInterior => _colorInterior;

        protected override float TrapScale => _trapScale;
        protected override float TrapPower => _trapPower;

        // F14 — theme-driven interior orbit colouring (implements the
        // IOrbitAwareColorMap default member).
        public bool WantsInteriorColor => _colorInterior;

        uint IColorMap.InSetColor => _inSetColor;

        public DataDrivenOrbitTrap(ColorThemeData data)
        {
            DisplayName = data.Name;
            DisplayCategory = data.Category;
            DisplayDescription = data.Description;
            DisplayMaxRecommendedZoom = data.MaxRecommendedZoom ?? double.PositiveInfinity;
            ThemeBrightness = data.Brightness;
            ThemeContrast = data.Contrast;
            ThemeAdaptive = data.Adaptive;

            Shape = data.TrapShape;
            _trapScale = data.TrapScale > 0f ? data.TrapScale : 2f;
            _trapPower = data.TrapPower > 0f ? data.TrapPower : 0.35f;
            _colorInterior = data.ColorInterior;
            _shape = ShapeImpl(data.TrapShape);
            _inSetColor = data.InSetColor?.ToPackedArgb() ?? 0xFF000000u;

            InterpolationSpace = data.InterpolationSpace;
            InterpCurve = data.InterpolationCurve;
            Transfer = data.TransferFunction;
            TransferStrength = data.TransferStrength;
            PaletteGamma = data.PaletteGamma;
            SparkleStride = data.SparkleStride;
            SparkleBoost = data.SparkleBoost;
            SeamlessCycle = data.SeamlessCycle;
            XorLevels = data.XorLevels;
            XorMask = data.XorMask;
            foreach (var s in data.Stops)
                Stops.Add(s.ToColorStop());
        }

        // Delegate the orbit hooks to the shape sampler; MapWithOrbit is
        // inherited from OrbitTrapPowerBaseMap and maps acc.TrapMin through this
        // theme's own gradient + TrapScale/TrapPower.
        public override void InitOrbit(out OrbitAccumulator acc) => _shape.InitOrbit(out acc);

        public override void Sample(ref OrbitAccumulator acc,
                                    double zr, double zi, double cr, double ci, int iter)
            => _shape.Sample(ref acc, zr, zi, cr, ci, iter);

        /// <summary>Maps a trap shape to the built-in sampler that measures it.
        /// Only single-channel (TrapMin) shapes are exposed — the bespoke
        /// two-channel maps (Pickover / Biomorph) carry their own MapWithOrbit
        /// and are not data-driven here.</summary>
        private static OrbitTrapBaseMap ShapeImpl(OrbitTrapShape shape) => shape switch
        {
            OrbitTrapShape.Point         => new OrbitTrapPointMap(),
            OrbitTrapShape.Cross         => new OrbitTrapCrossMap(),
            OrbitTrapShape.Circle        => new OrbitTrapCircleMap(),
            OrbitTrapShape.Line          => new OrbitTrapLineMap(),
            OrbitTrapShape.Star          => new OrbitTrapStarMap(),
            OrbitTrapShape.Square        => new OrbitTrapSquareMap(),
            OrbitTrapShape.Ring          => new OrbitTrapRingMap(),
            OrbitTrapShape.Hyperbola     => new OrbitTrapHyperbolaMap(),
            OrbitTrapShape.Lemniscate    => new OrbitTrapLemniscateMap(),
            OrbitTrapShape.Cardioid      => new OrbitTrapCardioidMap(),
            OrbitTrapShape.DiagonalCross => new OrbitTrapDiagonalCrossMap(),
            OrbitTrapShape.Triangle      => new OrbitTrapTriangleMap(),
            OrbitTrapShape.Hexagon       => new OrbitTrapHexagonMap(),
            OrbitTrapShape.Heart         => new OrbitTrapHeartMap(),
            OrbitTrapShape.SineWave      => new OrbitTrapSineWaveMap(),
            OrbitTrapShape.Concentric    => new OrbitTrapConcentricMap(),
            OrbitTrapShape.Grid          => new OrbitTrapGridMap(),
            OrbitTrapShape.Pinwheel      => new OrbitTrapPinwheelMap(),
            OrbitTrapShape.PolarRose     => new OrbitTrapPolarRoseMap(),
            _                            => new OrbitTrapPointMap(),
        };
    }
}