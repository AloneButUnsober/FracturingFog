// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Imaging/PaletteExtractionApi.cs
//
// UI-side contract for the palette-from-image pipeline. The Avalonia
// ImagePaletteViewModel speaks only this surface; the actual extractors
// (KMeans / MedianCut / Octree / Histogram) and the System.Drawing-based
// BitmapSampler stay in the main project where they have always been.
//
// Decouples UI.Avalonia from System.Drawing — the host implements
// IPaletteExtractionService and translates between its own platform types
// (System.Drawing.Bitmap, FracturingFog.Imaging.PaletteExtraction.*) and
// these neutral DTOs.

using System;
using System.Collections.Generic;

namespace FracturingFog.Imaging
{
    /// <summary>Mirrors PaletteExtraction.PaletteColorSpace without referencing it.</summary>
    public enum PaletteColorSpaceKind
    {
        Rgb,
        Lab,
        Hsl,
        OkLab,
    }

    /// <summary>Mirrors PaletteExtraction.DeltaEMetric without referencing it.</summary>
    public enum DeltaEMetricKind
    {
        DeltaE76,
        DeltaE2000,
    }

    /// <summary>Mirrors PaletteExtraction.StopSortMode without referencing it.</summary>
    public enum StopSortKind
    {
        NearestNeighborChain,
        Hue,
        Luminance,
        ClusterSize,
    }

    /// <summary>One extracted swatch + weight (pixel count it came from).</summary>
    public readonly record struct PaletteSwatch(byte R, byte G, byte B, int Weight);

    /// <summary>
    /// One stop in the produced gradient. Position is normalised [0,1].
    /// Host translates this back into its own ColorStopData when applying.
    /// </summary>
    public readonly record struct PaletteStop(float Position, byte R, byte G, byte B);

    /// <summary>Full per-run config the VM hands to the host.</summary>
    public sealed class PaletteExtractionRequest
    {
        public string SourcePath { get; init; } = "";

        /// <summary>0..N-1 index into <see cref="IPaletteExtractionService.MethodNames"/>.</summary>
        public int MethodIndex { get; init; }

        public int ColorCount { get; init; } = 8;
        public PaletteColorSpaceKind Space { get; init; } = PaletteColorSpaceKind.Lab;
        public int DownsampleMaxDim { get; init; } = 256;
        public bool ExcludeNearBlack { get; init; }
        public bool ExcludeNearWhite { get; init; }

        public StopSortKind Sort { get; init; } = StopSortKind.NearestNeighborChain;
        public float DedupDeltaE { get; init; } = 2f;
        public bool WeightedPositions { get; init; }

        /// <summary>
        /// ΔE formula used by PaletteStopBuilder dedup. DeltaE76 = legacy
        /// behaviour; DeltaE2000 = perceptually accurate.
        /// </summary>
        public DeltaEMetricKind DedupMetric { get; init; } = DeltaEMetricKind.DeltaE76;

        /// <summary>
        /// When true and <see cref="Space"/> is <see cref="PaletteColorSpaceKind.Rgb"/>,
        /// pixels are linearised before clustering. No effect on Lab / OkLab / HSL.
        /// </summary>
        public bool GammaCorrect { get; init; }

        // ── Algorithm-specific knobs ──────────────────────────────────────

        /// <summary>Mean-Shift bandwidth (Lab units). Default 25.</summary>
        public float Bandwidth { get; init; } = 25f;

        /// <summary>DBSCAN ε (Lab units). Default 8.</summary>
        public float DbscanEpsilon { get; init; } = 8f;

        /// <summary>DBSCAN MinPts. Default 20.</summary>
        public int DbscanMinPts { get; init; } = 20;

        /// <summary>Spatial-K-Means colour/position mix. 0 = colour only, 1 = equal weight.</summary>
        public float SpatialWeight { get; init; } = 0.5f;

        // ── Preprocessing (Phase 3) ───────────────────────────────────────

        public bool ExcludeTransparent { get; init; }
        public int AlphaThreshold { get; init; } = 16;

        public float MinSaturation { get; init; } = 0f;
        public float MaxSaturation { get; init; } = 1f;
        public float MinLightness { get; init; } = 0f;
        public float MaxLightness { get; init; } = 1f;

        public float RoiX { get; init; }
        public float RoiY { get; init; }
        public float RoiWidth { get; init; }
        public float RoiHeight { get; init; }

        public bool UseSaliency { get; init; }
        public float SaliencyThreshold { get; init; } = 0.3f;
    }

    /// <summary>One method's output: name + raw palette + built stops.</summary>
    public sealed class PaletteExtractionResult
    {
        public string MethodName { get; init; } = "";
        public IReadOnlyList<PaletteSwatch> Palette { get; init; } = Array.Empty<PaletteSwatch>();
        public IReadOnlyList<PaletteStop> Stops { get; init; } = Array.Empty<PaletteStop>();
    }

    /// <summary>
    /// Host-provided service. Runs the extractors. Implementation lives in
    /// the main project alongside the existing palette-extraction code.
    /// </summary>
    public interface IPaletteExtractionService
    {
        /// <summary>Display names of every method, indexed by MethodIndex.</summary>
        IReadOnlyList<string> MethodNames { get; }

        /// <summary>
        /// Validate / decode the image at <paramref name="path"/>. Returns
        /// false with a user-facing error message on failure (unsupported
        /// format, IO error, etc.).
        /// </summary>
        bool TryLoadImage(string path, out string? errorMessage);

        /// <summary>Run a single method per <paramref name="request"/>.MethodIndex.</summary>
        PaletteExtractionResult Extract(PaletteExtractionRequest request);

        /// <summary>Run every available method against the same source + options.</summary>
        IReadOnlyList<PaletteExtractionResult> ExtractAll(PaletteExtractionRequest request);
    }
}
