// Services/PaletteExtractionService.cs
//
// IPaletteExtractionService impl for the standalone Palette Builder app.
// Bridges the neutral DTOs (FracturingFog.Imaging.PaletteExtractionApi) to
// the linked-in extractors + BitmapSampler + PaletteStopBuilder.
//
// Phase 3 additions:
//   • EXIF orientation honoured on load.
//   • Multi-image batch via TryLoadImages — pixels from every source are
//     concatenated into one combined buffer the extractors see as a single
//     synthetic image. SpatialKMeans falls back to colour-only in this
//     mode (SourceWidth=SourceHeight=0).
//   • Per-pixel filters extended: alpha-transparent, saturation band,
//     lightness band, all routed via PaletteExtractionOptions.
//   • Normalised ROI crop applied per-source before downsample.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SkiaSharp;

using FracturingFog.Imaging;
using FracturingFog.Imaging.PaletteExtraction;

namespace PaletteBuilder.Services
{
    public sealed class PaletteExtractionService : IPaletteExtractionService, IDisposable
    {
        private static readonly IPaletteExtractor[] s_extractors =
        {
            new KMeansExtractor(),
            new MedianCutExtractor(),
            new OctreeExtractor(),
            new HistogramExtractor(),
            new WuExtractor(),
            new MiniBatchKMeansExtractor(),
            new MaterialPaletteExtractor(),
            new MeanShiftExtractor(),
            new DbscanExtractor(),
            new GmmExtractor(),
            new SpatialKMeansExtractor(),
        };

        private readonly object _gate = new();
        private readonly List<SKBitmap> _sources = new();
        private readonly List<string> _sourcePaths = new();

        private byte[]? _cachedPixels;
        private int _cachedCount;
        private int _cachedWidth;
        private int _cachedHeight;
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
            // No-op when the requested path is one of an already-loaded batch
            // — keeps the multi-image set intact when the VM's SetImage round
            // trips a single-path TryLoadImage for preview purposes.
            lock (_gate)
            {
                if (_sources.Count > 1 &&
                    _sourcePaths.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)))
                {
                    errorMessage = null;
                    return true;
                }
            }
            return TryLoadImages(new[] { path }, out errorMessage);
        }

        public bool TryLoadImages(IReadOnlyList<string> paths, out string? errorMessage)
        {
            errorMessage = null;
            if (paths == null || paths.Count == 0)
            {
                errorMessage = "No images supplied.";
                return false;
            }

            var loaded = new List<(SKBitmap, string)>(paths.Count);
            try
            {
                foreach (var path in paths)
                {
                    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    {
                        errorMessage = "File not found: " + path;
                        DisposeAll(loaded);
                        return false;
                    }
                    using var fs = File.OpenRead(path);
                    using var codec = SKCodec.Create(fs);
                    if (codec == null)
                    {
                        errorMessage = "Unsupported image format: " + path;
                        DisposeAll(loaded);
                        return false;
                    }
                    var info = new SKImageInfo(codec.Info.Width, codec.Info.Height,
                                               SKColorType.Bgra8888, SKAlphaType.Premul);
                    var raw = new SKBitmap(info);
                    var decodeResult = codec.GetPixels(info, raw.GetPixels());
                    if (decodeResult != SKCodecResult.Success && decodeResult != SKCodecResult.IncompleteInput)
                    {
                        raw.Dispose();
                        errorMessage = "Failed to decode image: " + decodeResult + " — " + path;
                        DisposeAll(loaded);
                        return false;
                    }
                    var oriented = BitmapSampler.ApplyOrigin(raw, codec.EncodedOrigin);
                    loaded.Add((oriented, path));
                }
            }
            catch (Exception ex)
            {
                errorMessage = "Failed to decode image: " + ex.Message;
                DisposeAll(loaded);
                return false;
            }

            lock (_gate)
            {
                DisposeAllNoLock();
                foreach (var (bmp, path) in loaded)
                {
                    _sources.Add(bmp);
                    _sourcePaths.Add(path);
                }
                InvalidatePixelCacheNoLock();
            }
            return true;
        }

        public PaletteExtractionResult Extract(PaletteExtractionRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            EnsureSourceMatches(request.SourcePath);

            lock (_gate)
            {
                if (_sources.Count == 0)
                    return Empty(MethodNameForIndex(request.MethodIndex));

                int idx = ClampIndex(request.MethodIndex);
                var extractor = s_extractors[idx];

                var opts = ToOptions(request);
                var (pixels, count, w, h) = GetPixelsNoLock(opts);
                if (count == 0)
                    return Empty(extractor.Name);
                opts.SourceWidth = w;
                opts.SourceHeight = h;

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
                if (_sources.Count == 0)
                    return Array.Empty<PaletteExtractionResult>();

                var opts = ToOptions(request);
                var (pixels, count, w, h) = GetPixelsNoLock(opts);
                opts.SourceWidth = w;
                opts.SourceHeight = h;
                var results = new List<PaletteExtractionResult>(s_extractors.Length);
                if (count == 0)
                {
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
                DisposeAllNoLock();
                InvalidatePixelCacheNoLock();
            }
        }

        // ── helpers ────────────────────────────────────────────────────────

        private void EnsureSourceMatches(string requestedPath)
        {
            if (string.IsNullOrEmpty(requestedPath)) return;
            lock (_gate)
            {
                if (_sourcePaths.Count == 1 &&
                    string.Equals(_sourcePaths[0], requestedPath, StringComparison.OrdinalIgnoreCase))
                    return;
                // Batch mode: skip reload if requested path is one of the loaded.
                if (_sourcePaths.Count > 1 &&
                    _sourcePaths.Any(p => string.Equals(p, requestedPath, StringComparison.OrdinalIgnoreCase)))
                    return;
            }
            TryLoadImage(requestedPath, out _);
        }

        private (byte[] pixels, int count, int width, int height) GetPixelsNoLock(PaletteExtractionOptions opts)
        {
            string key = BuildCacheKey(opts);
            if (_cachedPixels != null && _cacheKey == key)
                return (_cachedPixels, _cachedCount, _cachedWidth, _cachedHeight);

            int reportWidth = 0, reportHeight = 0;
            if (_sources.Count == 1)
            {
                using var cropped = opts.HasRoi
                    ? BitmapSampler.CropNormalised(_sources[0], opts.RoiX, opts.RoiY, opts.RoiWidth, opts.RoiHeight)
                    : _sources[0].Copy(SKColorType.Bgra8888);
                using var down = BitmapSampler.Downsample(cropped, opts.DownsampleMaxDim);
                reportWidth = down.Width;
                reportHeight = down.Height;

                float[]? saliency = null;
                if (opts.UseSaliency)
                {
                    var raw = BitmapSampler.ExtractPixels(down, false, false, out _);
                    saliency = SaliencyService.Compute(raw, down.Width, down.Height);
                }

                _cachedPixels = BitmapSampler.ExtractPixels(down,
                    opts.ExcludeNearBlack, opts.ExcludeNearWhite, out _cachedCount,
                    excludeTransparent: opts.ExcludeTransparent,
                    alphaThreshold: opts.AlphaThreshold,
                    minSaturation: opts.MinSaturation,
                    maxSaturation: opts.MaxSaturation,
                    minLightness: opts.MinLightness,
                    maxLightness: opts.MaxLightness,
                    saliencyMap: saliency,
                    saliencyThreshold: opts.UseSaliency ? opts.SaliencyThreshold : 0f);
            }
            else
            {
                // Multi-image batch: concatenate per-source pixel buffers.
                // SourceWidth/Height left at 0 so SpatialKMeans falls back.
                var buffers = new List<byte[]>(_sources.Count);
                int total = 0;
                foreach (var src in _sources)
                {
                    using var cropped = opts.HasRoi
                        ? BitmapSampler.CropNormalised(src, opts.RoiX, opts.RoiY, opts.RoiWidth, opts.RoiHeight)
                        : src.Copy(SKColorType.Bgra8888);
                    using var down = BitmapSampler.Downsample(cropped, opts.DownsampleMaxDim);

                    float[]? saliency = null;
                    if (opts.UseSaliency)
                    {
                        var raw = BitmapSampler.ExtractPixels(down, false, false, out _);
                        saliency = SaliencyService.Compute(raw, down.Width, down.Height);
                    }

                    var buf = BitmapSampler.ExtractPixels(down,
                        opts.ExcludeNearBlack, opts.ExcludeNearWhite, out int count,
                        excludeTransparent: opts.ExcludeTransparent,
                        alphaThreshold: opts.AlphaThreshold,
                        minSaturation: opts.MinSaturation,
                        maxSaturation: opts.MaxSaturation,
                        minLightness: opts.MinLightness,
                        maxLightness: opts.MaxLightness,
                        saliencyMap: saliency,
                        saliencyThreshold: opts.UseSaliency ? opts.SaliencyThreshold : 0f);
                    if (count > 0)
                    {
                        if (count * 3 != buf.Length) Array.Resize(ref buf, count * 3);
                        buffers.Add(buf);
                        total += count;
                    }
                }
                _cachedPixels = new byte[total * 3];
                int offset = 0;
                foreach (var buf in buffers)
                {
                    Buffer.BlockCopy(buf, 0, _cachedPixels, offset, buf.Length);
                    offset += buf.Length;
                }
                _cachedCount = total;
            }

            _cachedWidth = reportWidth;
            _cachedHeight = reportHeight;
            _cacheKey = key;
            return (_cachedPixels, _cachedCount, _cachedWidth, _cachedHeight);
        }

        private string BuildCacheKey(PaletteExtractionOptions opts)
        {
            string srcKey = string.Join(";", _sourcePaths);
            return $"{srcKey}|{opts.DownsampleMaxDim}|{opts.ExcludeNearBlack}|{opts.ExcludeNearWhite}" +
                   $"|{opts.ExcludeTransparent}|{opts.AlphaThreshold}" +
                   $"|{opts.MinSaturation:F3}|{opts.MaxSaturation:F3}" +
                   $"|{opts.MinLightness:F3}|{opts.MaxLightness:F3}" +
                   $"|{opts.RoiX:F3}|{opts.RoiY:F3}|{opts.RoiWidth:F3}|{opts.RoiHeight:F3}" +
                   $"|{opts.UseSaliency}|{opts.SaliencyThreshold:F3}";
        }

        private void DisposeAllNoLock()
        {
            foreach (var b in _sources) b.Dispose();
            _sources.Clear();
            _sourcePaths.Clear();
        }

        private static void DisposeAll(List<(SKBitmap, string)> items)
        {
            foreach (var (b, _) in items) b.Dispose();
        }

        private void InvalidatePixelCacheNoLock()
        {
            _cachedPixels = null;
            _cachedCount = 0;
            _cachedWidth = 0;
            _cachedHeight = 0;
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
                PaletteColorSpaceKind.OkLab => PaletteColorSpace.OkLab,
                _ => PaletteColorSpace.Lab,
            },
            DownsampleMaxDim = Math.Max(32, r.DownsampleMaxDim),
            ExcludeNearBlack = r.ExcludeNearBlack,
            ExcludeNearWhite = r.ExcludeNearWhite,
            GammaCorrect = r.GammaCorrect,
            Bandwidth = r.Bandwidth,
            DbscanEpsilon = r.DbscanEpsilon,
            DbscanMinPts = r.DbscanMinPts,
            SpatialWeight = r.SpatialWeight,
            ExcludeTransparent = r.ExcludeTransparent,
            AlphaThreshold = r.AlphaThreshold,
            MinSaturation = r.MinSaturation,
            MaxSaturation = r.MaxSaturation,
            MinLightness = r.MinLightness,
            MaxLightness = r.MaxLightness,
            RoiX = r.RoiX,
            RoiY = r.RoiY,
            RoiWidth = r.RoiWidth,
            RoiHeight = r.RoiHeight,
            UseSaliency = r.UseSaliency,
            SaliencyThreshold = r.SaliencyThreshold,
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
                DedupMetric = r.DedupMetric == DeltaEMetricKind.DeltaE2000
                    ? DeltaEMetric.DeltaE2000
                    : DeltaEMetric.DeltaE76,
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
