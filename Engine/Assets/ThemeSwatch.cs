// Engine/Assets/ThemeSwatch.cs
//
// Asset Manager thumbnails (AssetManager-DevPlan "Thumbnails" open question) —
// the cheap slice. Colour themes are the one asset type whose preview needs no
// fractal render: their gradient stops fully describe a swatch. This renders a
// small horizontal gradient PNG straight from ColorThemeData.Stops (linear RGB
// interpolation), self-contained enough to run eagerly during Enumerate().
//
// Regions / animations / bulbs still need a per-asset render through the host
// pipeline for a meaningful preview and stay deferred (see the dev plan) — this
// covers themes only.

using System.Collections.Generic;
using System.Linq;
using SkiaSharp;
using FracturingFog.Models;

namespace FracturingFog.Assets
{
    /// <summary>Renders a small horizontal gradient swatch PNG from a colour
    /// theme's stops for the Asset Manager thumbnail column. Returns null when
    /// the theme carries no usable stops.</summary>
    internal static class ThemeSwatch
    {
        private const int Width = 120;
        private const int Height = 20;

        public static byte[]? RenderPng(ColorThemeData? theme)
        {
            var stops = theme?.Stops;
            if (stops == null || stops.Count == 0) return null;

            // Sort by position; clamp positions into [0,1] so a hand-edited
            // theme with out-of-range stops still rasterizes.
            var sorted = stops
                .Select(s => (t: Clamp01(s.Position), s.R, s.G, s.B))
                .OrderBy(x => x.t)
                .ToList();

            try
            {
                using var bmp = new SKBitmap(Width, Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
                for (int x = 0; x < Width; x++)
                {
                    float t = Width == 1 ? 0f : (float)x / (Width - 1);
                    var (r, g, b) = Sample(sorted, t);
                    var col = new SKColor(r, g, b, 255);
                    for (int y = 0; y < Height; y++)
                        bmp.SetPixel(x, y, col);
                }
                using var img = SKImage.FromBitmap(bmp);
                using var data = img.Encode(SKEncodedImageFormat.Png, 100);
                return data?.ToArray();
            }
            catch
            {
                return null;
            }
        }

        private static (byte r, byte g, byte b) Sample(
            List<(float t, byte R, byte G, byte B)> stops, float t)
        {
            if (t <= stops[0].t) return (stops[0].R, stops[0].G, stops[0].B);
            var last = stops[^1];
            if (t >= last.t) return (last.R, last.G, last.B);
            for (int i = 1; i < stops.Count; i++)
            {
                var a = stops[i - 1];
                var b = stops[i];
                if (t <= b.t)
                {
                    float span = b.t - a.t;
                    float f = span <= 1e-6f ? 0f : (t - a.t) / span;
                    return (Lerp(a.R, b.R, f), Lerp(a.G, b.G, f), Lerp(a.B, b.B, f));
                }
            }
            return (last.R, last.G, last.B);
        }

        private static byte Lerp(byte a, byte b, float f) => (byte)(a + (b - a) * f + 0.5f);

        private static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
    }
}
