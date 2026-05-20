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
    }

    public enum StopSortMode
    {
        Hue,
        Luminance,
        ClusterSize,
        NearestNeighborChain,
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
    }

    public interface IPaletteExtractor
    {
        string Name { get; }
        IReadOnlyList<ExtractedColor> Extract(byte[] rgbPixels, int pixelCount, PaletteExtractionOptions opts);
    }
}
