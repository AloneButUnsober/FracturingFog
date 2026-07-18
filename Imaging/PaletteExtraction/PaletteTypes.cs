// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Imaging/PaletteExtraction/PaletteTypes.cs
//
// Common types for the palette-from-image pipeline: extractor interface,
// the options struct that drives each extractor, and the weighted color
// result type fed into the stop builder.

using System.Collections.Generic;

namespace FracturingFog.Imaging.PaletteExtraction
{
    public enum PaletteMethod
    {
        KMeans,
        MedianCut,
        Octree,
        Histogram,
    }

    public enum PaletteColorSpace
    {
        Rgb,
        Lab,
        Hsl,
        OkLab,
    }

    public enum StopSortMode
    {
        Hue,
        Luminance,
        ClusterSize,
        NearestNeighborChain,
    }

    /// <summary>
    /// Which ΔE formula PaletteStopBuilder uses for the dedup pass.
    /// CIEDE2000 (DeltaE2000) is more accurate; CIE76 stays the default for
    /// backwards compatibility with existing saved themes.
    /// </summary>
    public enum DeltaEMetric
    {
        DeltaE76,
        DeltaE2000,
    }

    /// <summary>
    /// One extracted swatch plus the weight (pixel count) it came from.
    /// Weight is used both for stop-position weighting and for ranking
    /// dominant colors in the Histogram method.
    /// </summary>
    public readonly record struct ExtractedColor(byte R, byte G, byte B, int Weight);

    public sealed class PaletteExtractionOptions
    {
        public int ColorCount { get; set; } = 8;
        public PaletteColorSpace Space { get; set; } = PaletteColorSpace.Lab;
        public int DownsampleMaxDim { get; set; } = 256;
        public bool ExcludeNearBlack { get; set; } = false;
        public bool ExcludeNearWhite { get; set; } = false;
        public int RandomSeed { get; set; } = 1337;

        /// <summary>
        /// When true and <see cref="Space"/> is <see cref="PaletteColorSpace.Rgb"/>,
        /// pixels are converted sRGB → linear before clustering so distances
        /// reflect physical light intensity, not display-encoded values. No
        /// effect on Lab / OkLab / HSL (those already do their own gamma
        /// handling).
        /// </summary>
        public bool GammaCorrect { get; set; } = false;

        // ── Preprocessing filters (Phase 3) ───────────────────────────────

        /// <summary>Drop pixels with alpha &lt; <see cref="AlphaThreshold"/>.</summary>
        public bool ExcludeTransparent { get; set; }

        /// <summary>Alpha cutoff [0,255] for ExcludeTransparent. Default 16.</summary>
        public int AlphaThreshold { get; set; } = 16;

        /// <summary>HSL saturation lower bound [0,1]. Default 0 (no filter).</summary>
        public float MinSaturation { get; set; } = 0f;

        /// <summary>HSL saturation upper bound [0,1]. Default 1 (no filter).</summary>
        public float MaxSaturation { get; set; } = 1f;

        /// <summary>HSL lightness lower bound [0,1]. Default 0 (no filter).</summary>
        public float MinLightness { get; set; } = 0f;

        /// <summary>HSL lightness upper bound [0,1]. Default 1 (no filter).</summary>
        public float MaxLightness { get; set; } = 1f;

        // ── ROI (Phase 3.4) ───────────────────────────────────────────────

        /// <summary>Normalised crop X [0,1]. Inactive when RoiWidth or RoiHeight ≤ 0.</summary>
        public float RoiX { get; set; }
        public float RoiY { get; set; }
        public float RoiWidth { get; set; }
        public float RoiHeight { get; set; }

        public bool HasRoi => RoiWidth > 0f && RoiHeight > 0f;

        // ── Saliency (Phase 3.6) ──────────────────────────────────────────

        /// <summary>Skip pixels whose saliency score is below <see cref="SaliencyThreshold"/>.</summary>
        public bool UseSaliency { get; set; }

        /// <summary>Saliency cutoff in [0,1]. Default 0.3 (subject-leaning).</summary>
        public float SaliencyThreshold { get; set; } = 0.3f;

        // ── Algorithm-specific knobs (ignored by extractors that don't use them) ──

        /// <summary>Mean-Shift bandwidth in Lab units. 0 → default 25.</summary>
        public float Bandwidth { get; set; } = 25f;

        /// <summary>DBSCAN neighbourhood radius in Lab units. 0 → default 8.</summary>
        public float DbscanEpsilon { get; set; } = 8f;

        /// <summary>DBSCAN minimum pixel weight inside ε for core points. 0 → default 20.</summary>
        public int DbscanMinPts { get; set; } = 20;

        /// <summary>
        /// Spatial-aware k-means feature mixing factor (0 = pure colour,
        /// 1 = colour and position weighted equally). Caller must set
        /// SourceWidth + SourceHeight for the spatial extractor to read xy.
        /// </summary>
        public float SpatialWeight { get; set; } = 0.5f;

        /// <summary>Downsampled image width (set by sampler so spatial extractor knows xy).</summary>
        public int SourceWidth { get; set; }

        /// <summary>Downsampled image height (set by sampler so spatial extractor knows xy).</summary>
        public int SourceHeight { get; set; }
    }

    public interface IPaletteExtractor
    {
        string Name { get; }
        IReadOnlyList<ExtractedColor> Extract(byte[] rgbPixels, int pixelCount, PaletteExtractionOptions opts);
    }
}
