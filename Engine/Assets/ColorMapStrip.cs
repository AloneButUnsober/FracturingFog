// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Engine/Assets/ColorMapStrip.cs
//
// Asset Manager thumbnails — the built-in colour-theme slice. Data-driven
// themes carry gradient Stops that ThemeSwatch can rasterise directly; the
// large curated roster (ColorPalette.BuiltIns) are C# IColorMap classes with
// no Stops, so their swatch has to come from the theme's own Map() output.
//
// This samples Map() across the iteration range into a small horizontal strip
// PNG — the same thing the toolbar theme combo does per row (owner-draw sets
// MaxIterations then reads a Map sample), just widened from a single swatch
// colour to a gradient strip. Rendering is deferred: ColorThemeAssetSource
// hands the Asset Manager a factory, not bytes, so ~250 built-ins only
// rasterise as their rows scroll into view — never during Enumerate().

using SkiaSharp;
using FracturingFog.Interefaces;

namespace FracturingFog.Assets
{
    /// <summary>Renders a small horizontal swatch PNG for a built-in colour map
    /// by sampling <see cref="IColorMap.Map(float,float,int,float,float)"/> across
    /// the iteration range. Returns null if the map is null or rasterisation
    /// throws (a swatch is never worth crashing the manager over).</summary>
    internal static class ColorMapStrip
    {
        private const int Width = 120;
        private const int Height = 20;

        // Matches the toolbar theme-combo owner-draw (Views/Controls.cs), which
        // sets MaxIterations = 500 before reading a swatch sample, so a built-in
        // shows the same colours here as it does in the toolbar dropdown.
        private const int SampleIterations = 500;

        public static byte[]? RenderPng(IColorMap? map)
        {
            if (map == null) return null;

            try
            {
                // These built-ins are shared singletons in ColorPalette.BuiltIns;
                // the toolbar owner-draw already mutates MaxIterations on them for
                // its swatch, and the renderer re-sets it every frame, so setting
                // it here for the strip is consistent with existing behaviour.
                map.MaxIterations = SampleIterations;
                if (map is IColorMapWithPixelScale ps) ps.PixelScale = 1e-3;

                using var bmp = new SKBitmap(Width, Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
                for (int x = 0; x < Width; x++)
                {
                    float t = Width == 1 ? 0f : (float)x / (Width - 1);
                    // Smooth iteration count sweeps 0 → maxIter across the strip;
                    // a small distance + gently tilted normal (as IColorMap's own
                    // SwatchSample uses) lets 3D relief themes show shading.
                    float smooth = t * (SampleIterations - 1);
                    int argb = map.Map(smooth, 0.05f, SampleIterations, 0.30f, 0.20f);

                    var col = new SKColor(
                        (byte)((argb >> 16) & 0xFF),
                        (byte)((argb >> 8) & 0xFF),
                        (byte)(argb & 0xFF),
                        255);
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
    }
}
