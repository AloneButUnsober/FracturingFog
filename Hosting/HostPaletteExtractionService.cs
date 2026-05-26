// Hosting/HostPaletteExtractionService.cs
//
// Concrete IPaletteExtractionService for the Avalonia shell. Bridges the
// neutral DTOs in FracturingFog.Abstractions/Imaging/PaletteExtractionApi
// to the real System.Drawing-based BitmapSampler + the four extractors
// (KMeans / MedianCut / Octree / Histogram) + PaletteStopBuilder.
//
// State: the most recently loaded source image lives here as a Bitmap so
// repeated Extract / ExtractAll calls against the same path reuse the
// decoded pixels via an internal pixel-buffer cache (mirrors the legacy
// ImagePaletteDialog cache behaviour). TryLoadImage(path, ...) replaces
// the source; subsequent calls with a different SourcePath also trigger a
// reload as a safety net.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;

using FracturingFog.Imaging;
using FracturingFog.Imaging.PaletteExtraction;
using FracturingFog.Models;

namespace FracturingFog.Hosting
{
    /// <inheritdoc/>
    public sealed class HostPaletteExtractionService : IPaletteExtractionService, IDisposable
    {
        private static readonly IPaletteExtractor[] s_extractors =
        {
            new KMeansExtractor(),
            new MedianCutExtractor(),
            new OctreeExtractor(),
            new HistogramExtractor(),
        };

        private readonly object _gate = new();
        private Bitmap? _source;
        private string? _sourcePath;

        // Cached downsampled-and-filtered RGB buffer so back-to-back single
        // / compare-all runs against the same image + filter combo don't
        // redo decode + downsample work.
        private byte[]? _cachedPixels;
        private int _cachedCount;
        private string? _cacheKey;

        public IReadOnlyList<string> MethodNames
        {
            get
            {
                var names = new string[s_extractors.Length];
                for (int i = 0; i < s_extractors.Length; i++)
                    names[i] = s_extractors[i].Name;
                return names;
            }
        }

        public bool TryLoadImage(string path, out string? errorMessage)
        {
            errorMessage = null;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                errorMessage = "File not found: " + path;
                return false;
            }

            try
            {
                // Decode into an independent owned Bitmap so the original
                // file handle is released immediately (Bitmap.FromFile keeps
                // the file locked for the bitmap's lifetime).
                using var decoded = (Bitmap)Image.FromFile(path);
                var copy = new Bitmap(decoded);

                lock (_gate)
                {
                    _source?.Dispose();
                    _source = copy;
                    _sourcePath = path;
                    InvalidatePixelCacheNoLock();
                }
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = "Failed to decode image: " + ex.Message;
                return false;
            }
        }

        public PaletteExtractionResult Extract(PaletteExtractionRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            EnsureSourceMatches(request.SourcePath);

            lock (_gate)
            {
                if (_source == null)
                    return Empty(MethodNameForIndex(request.MethodIndex));

                int idx = ClampIndex(request.MethodIndex);
                var extractor = s_extractors[idx];

                var opts = ToOptions(request);
                var (pixels, count) = GetPixelsNoLock(opts);
                if (count == 0)
                    return Empty(extractor.Name);

                var palette = extractor.Extract(pixels, count, opts);
                var stops = ToStops(BuildStopBuilder(request), palette);

                return new PaletteExtractionResult
                {
                    MethodName = extractor.Name,
                    Palette = ToSwatches(palette),
                    Stops = stops,
                };
            }
        }

        public IReadOnlyList<PaletteExtractionResult> ExtractAll(PaletteExtractionRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            EnsureSourceMatches(request.SourcePath);

            lock (_gate)
            {
                if (_source == null)
                    return Array.Empty<PaletteExtractionResult>();

                var opts = ToOptions(request);
                var (pixels, count) = GetPixelsNoLock(opts);
                var results = new List<PaletteExtractionResult>(s_extractors.Length);
                if (count == 0)
                {
                    // Still surface one row per method with empty palette so
                    // the VM can show "no pixels" without inferring it.
                    foreach (var ex in s_extractors)
                        results.Add(Empty(ex.Name));
                    return results;
                }

                var builder = BuildStopBuilder(request);
                foreach (var ex in s_extractors)
                {
                    var palette = ex.Extract(pixels, count, opts);
                    var stops = ToStops(builder, palette);
                    results.Add(new PaletteExtractionResult
                    {
                        MethodName = ex.Name,
                        Palette = ToSwatches(palette),
                        Stops = stops,
                    });
                }
                return results;
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _source?.Dispose();
                _source = null;
                _sourcePath = null;
                InvalidatePixelCacheNoLock();
            }
        }

        // ── helpers ────────────────────────────────────────────────────────

        private void EnsureSourceMatches(string requestedPath)
        {
            if (string.IsNullOrEmpty(requestedPath)) return;
            lock (_gate)
            {
                if (string.Equals(_sourcePath, requestedPath, StringComparison.OrdinalIgnoreCase))
                    return;
            }
            // Different path than what's cached — silently reload. Caller
            // already validated via TryLoadImage in the normal flow.
            TryLoadImage(requestedPath, out _);
        }

        private (byte[] pixels, int count) GetPixelsNoLock(PaletteExtractionOptions opts)
        {
            string key = $"{_sourcePath}|{opts.DownsampleMaxDim}|{opts.ExcludeNearBlack}|{opts.ExcludeNearWhite}";
            if (_cachedPixels != null && _cacheKey == key)
                return (_cachedPixels, _cachedCount);

            using var down = BitmapSampler.Downsample(_source!, opts.DownsampleMaxDim);
            _cachedPixels = BitmapSampler.ExtractPixels(down,
                opts.ExcludeNearBlack, opts.ExcludeNearWhite,
                out _cachedCount);
            _cacheKey = key;
            return (_cachedPixels, _cachedCount);
        }

        private void InvalidatePixelCacheNoLock()
        {
            _cachedPixels = null;
            _cachedCount = 0;
            _cacheKey = null;
        }

        private static int ClampIndex(int idx)
            => idx < 0 ? 0 : (idx >= s_extractors.Length ? s_extractors.Length - 1 : idx);

        private static string MethodNameForIndex(int idx)
            => s_extractors[ClampIndex(idx)].Name;

        private static PaletteExtractionOptions ToOptions(PaletteExtractionRequest r) => new()
        {
            ColorCount = Math.Max(2, r.ColorCount),
            Space = r.Space switch
            {
                PaletteColorSpaceKind.Rgb => PaletteColorSpace.Rgb,
                PaletteColorSpaceKind.Hsl => PaletteColorSpace.Hsl,
                _ => PaletteColorSpace.Lab,
            },
            DownsampleMaxDim = Math.Max(32, r.DownsampleMaxDim),
            ExcludeNearBlack = r.ExcludeNearBlack,
            ExcludeNearWhite = r.ExcludeNearWhite,
        };

        private static PaletteStopBuilder BuildStopBuilder(PaletteExtractionRequest r)
            => new()
            {
                Sort = r.Sort switch
                {
                    StopSortKind.Hue => StopSortMode.Hue,
                    StopSortKind.Luminance => StopSortMode.Luminance,
                    StopSortKind.ClusterSize => StopSortMode.ClusterSize,
                    _ => StopSortMode.NearestNeighborChain,
                },
                DedupDeltaE = r.DedupDeltaE,
                WeightedPositions = r.WeightedPositions,
            };

        private static IReadOnlyList<PaletteSwatch> ToSwatches(IReadOnlyList<ExtractedColor> palette)
        {
            var arr = new PaletteSwatch[palette.Count];
            for (int i = 0; i < palette.Count; i++)
            {
                var c = palette[i];
                arr[i] = new PaletteSwatch(c.R, c.G, c.B, c.Weight);
            }
            return arr;
        }

        private static IReadOnlyList<PaletteStop> ToStops(PaletteStopBuilder builder, IReadOnlyList<ExtractedColor> palette)
        {
            var built = builder.Build(palette);
            var arr = new PaletteStop[built.Count];
            for (int i = 0; i < built.Count; i++)
            {
                var s = built[i];
                arr[i] = new PaletteStop(s.Position, s.R, s.G, s.B);
            }
            return arr;
        }

        private static PaletteExtractionResult Empty(string methodName) => new()
        {
            MethodName = methodName,
            Palette = Array.Empty<PaletteSwatch>(),
            Stops = Array.Empty<PaletteStop>(),
        };
    }
}
