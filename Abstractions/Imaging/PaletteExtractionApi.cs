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
