// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Engine/Imaging/Qr/QrCompositor.cs
//
// #1305 / S2 (#1397) + S5 (#1400) - composite a QR symbol into a rendered
// fractal so the result still scans. A decoder binarises the image (camera Y
// / BT.601 luma, local thresholds) and samples roughly the centre of each
// module, so the compositor only ever moves LUMA: hue and chroma are kept and
// the fractal's own colours carry through.
//
//   Dots     - a centre square per module is forced into the module's band;
//              the rest keeps the fractal, only its far-end luma compressed.
//   Steer    - the whole module is remapped into its band (internal contrast
//              compressed, not erased), the centre into a stricter band.
//   Halftone - after Chu et al. 2013: each module split 3x3, the centre cell
//              carries the bit, the outer eight cells are the fractal's own
//              two-tone halftone (thresholded at the code area's median luma).
//              The two tones live inside the module's HALF of the luma range
//              (dark module: deep / mid-dark, light: mid-light / bright), so the
//              halftone stays visible yet a blurred or shrunken module still
//              averages to the right side - the pure two-tone version failed
//              the blur / small passes on busy images.
//
// Function patterns (finders, timing, alignment, format/version) are always
// drawn at full strength; the quiet zone is lifted into the light band. The
// band limits are luma levels a decoder separates reliably; QrVerifier is the
// arbiter, not these numbers.

using System;

namespace FracturingFog.Imaging.Qr
{
    /// <summary>Where the symbol landed in the image (module 0,0 top-left).</summary>
    public readonly record struct QrLayout(int OriginX, int OriginY, int ModulePixels, int Modules)
    {
        public int SymbolPixels => ModulePixels * Modules;
        public int QuietPixels => ModulePixels * QrCode.QuietZone;
    }

    public static class QrCompositor
    {
        // Luma bands (0..255). Core = function modules, module centres, quiet
        // zone; edge = the softer band for the rest of a steered module.
        public const int DarkCoreMax  = 56;
        public const int LightCoreMin = 200;
        public const int DarkEdgeMax  = 104;
        public const int LightEdgeMin = 160;
        // Soft = the Dots style's "untouched" surround: only the far end of the
        // range is compressed (dark modules lose their brightest highlights,
        // light modules their deepest shadows) so a soft-focus camera averaging
        // dot + surround still lands on the right side.
        public const int DarkSoftMax  = 176;
        public const int LightSoftMin = 80;
        // Halftone outer-cell tones: [min, max] luma for (module, own pixel).
        public const int HalftoneDarkOwnLightMin = 72,  HalftoneDarkOwnLightMax = 120;
        public const int HalftoneLightOwnDarkMin = 136, HalftoneLightOwnDarkMax = 184;

        /// <summary>Centre the symbol (plus quiet zone) on a <paramref name="w"/> x
        /// <paramref name="h"/> image. <paramref name="modulePixels"/> 0 = the
        /// largest whole module size that fits the short side.</summary>
        public static QrLayout Layout(int w, int h, int modules, int modulePixels)
        {
            int span = modules + 2 * QrCode.QuietZone;
            int fit = Math.Min(w, h) / span;
            int m = modulePixels > 0 ? modulePixels : fit;
            if (m < 1 || span * m > Math.Min(w, h))
                throw new ArgumentException(
                    $"A {modules}-module QR code with its quiet zone needs at least {span}x{span} px "
                    + (modulePixels > 0 ? $"at {modulePixels} px/module ({span * modulePixels} px)" : "")
                    + $"; the image is {w}x{h}.");
            int sym = modules * m;
            return new QrLayout((w - sym) / 2, (h - sym) / 2, m, modules);
        }

        /// <summary>Composite <paramref name="code"/> into <paramref name="buf"/>
        /// (straight-alpha BGRA uint, in place) and return the layout used.</summary>
        public static QrLayout Composite(uint[] buf, int w, int h, QrCode code, QrCodeOptions opt)
        {
            if (buf == null) throw new ArgumentNullException(nameof(buf));
            if (buf.Length < w * h) throw new ArgumentException("Buffer smaller than w*h.", nameof(buf));
            var L = Layout(w, h, code.Size, opt.ModulePixels);
            int m = L.ModulePixels;
            bool invert = opt.Invert;

            // Quiet zone: every pixel in the border ring goes into the light band.
            int q = L.QuietPixels;
            int x0 = L.OriginX - q, y0 = L.OriginY - q, x1 = L.OriginX + L.SymbolPixels + q, y1 = L.OriginY + L.SymbolPixels + q;
            for (int y = Math.Max(0, y0); y < Math.Min(h, y1); y++)
                for (int x = Math.Max(0, x0); x < Math.Min(w, x1); x++)
                {
                    bool inSymbol = x >= L.OriginX && x < L.OriginX + L.SymbolPixels
                                 && y >= L.OriginY && y < L.OriginY + L.SymbolPixels;
                    if (!inSymbol) buf[y * w + x] = Steer(buf[y * w + x], dark: invert, core: false);
                }

            double frac = Math.Clamp(opt.DotFraction, QrCodeOptions.MinDotFraction, QrCodeOptions.MaxDotFraction);
            int dot = Math.Max(1, (int)Math.Round(m * frac));
            int dotLo = (m - dot) / 2, dotHi = dotLo + dot;   // [dotLo, dotHi) within the module

            int halftoneThreshold = opt.Style == QrStyle.Halftone ? MedianLuma(buf, w, L) : 0;
            // Halftone sub-cell edges (3x3), at least 1 px each.
            int c1 = Math.Max(1, m / 3), c2 = Math.Max(c1 + 1, m - m / 3);
            if (m < 3) { c1 = 0; c2 = m; }

            for (int my = 0; my < code.Size; my++)
                for (int mx = 0; mx < code.Size; mx++)
                {
                    bool bitDark = code.Dark[my, mx] ^ invert;
                    bool func = code.IsFunction[my, mx];
                    int px0 = L.OriginX + mx * m, py0 = L.OriginY + my * m;
                    for (int dy = 0; dy < m; dy++)
                    {
                        int y = py0 + dy;
                        int row = y * w;
                        for (int dx = 0; dx < m; dx++)
                        {
                            int i = row + px0 + dx;
                            if (func) { buf[i] = Steer(buf[i], bitDark, core: true); continue; }
                            switch (opt.Style)
                            {
                                case QrStyle.Dots:
                                    buf[i] = dx >= dotLo && dx < dotHi && dy >= dotLo && dy < dotHi
                                        ? Steer(buf[i], bitDark, core: true)
                                        : Steer(buf[i], bitDark, Band.Soft);
                                    break;
                                case QrStyle.Halftone:
                                    bool centre = dx >= c1 && dx < c2 && dy >= c1 && dy < c2;
                                    if (centre) buf[i] = Steer(buf[i], bitDark, core: true);
                                    else
                                    {
                                        bool own = QrVerifier.LumaOf(buf[i]) < halftoneThreshold;
                                        buf[i] = (bitDark, own) switch
                                        {
                                            (true, true)   => Steer(buf[i], 0, DarkCoreMax),
                                            (true, false)  => Steer(buf[i], HalftoneDarkOwnLightMin, HalftoneDarkOwnLightMax),
                                            (false, true)  => Steer(buf[i], HalftoneLightOwnDarkMin, HalftoneLightOwnDarkMax),
                                            (false, false) => Steer(buf[i], LightCoreMin, 255),
                                        };
                                    }
                                    break;
                                default: // Steer
                                    bool core = dx >= dotLo && dx < dotHi && dy >= dotLo && dy < dotHi;
                                    buf[i] = Steer(buf[i], bitDark, core);
                                    break;
                            }
                        }
                    }
                }
            return L;
        }

        private static int MedianLuma(uint[] buf, int w, QrLayout L)
        {
            var hist = new int[256];
            int n = 0;
            for (int y = L.OriginY; y < L.OriginY + L.SymbolPixels; y++)
                for (int x = L.OriginX; x < L.OriginX + L.SymbolPixels; x++) { hist[QrVerifier.LumaOf(buf[y * w + x])]++; n++; }
            int acc = 0;
            for (int v = 0; v < 256; v++) { acc += hist[v]; if (acc * 2 >= n) return Math.Clamp(v, 1, 255); }
            return 128;
        }

        /// <summary>Remap a pixel's luma into the dark or light band, keeping its
        /// hue / chroma (backed off only when the gamut clamp would push luma out
        /// of the band). Output alpha is opaque so a translucent theme cannot let
        /// the page behind the image decide the module.</summary>
        public static uint Steer(uint p, bool dark, bool core) => Steer(p, dark, core ? Band.Core : Band.Edge);

        public enum Band { Core, Edge, Soft }

        public static uint Steer(uint p, bool dark, Band band)
        {
            int max = !dark ? 255 : band switch { Band.Core => DarkCoreMax, Band.Edge => DarkEdgeMax, _ => DarkSoftMax };
            int min = dark ? 0 : band switch { Band.Core => LightCoreMin, Band.Edge => LightEdgeMin, _ => LightSoftMin };
            return Steer(p, min, max);
        }

        /// <summary>Remap luma linearly into [<paramref name="min"/>, <paramref name="max"/>].</summary>
        public static uint Steer(uint p, int min, int max)
        {
            double r = (p >> 16) & 0xFF, g = (p >> 8) & 0xFF, b = p & 0xFF;
            double a = (p >> 24) / 255.0;
            // Composite over white first (what a viewer / decoder sees).
            r = r * a + 255 * (1 - a); g = g * a + 255 * (1 - a); b = b * a + 255 * (1 - a);

            double y = 0.299 * r + 0.587 * g + 0.114 * b;
            double cb = b - y, cr = r - y;
            double ty = min + y * (max - min) / 255.0;

            for (double k = 1.0; ; k *= 0.5)
            {
                if (k < 1.0 / 64) k = 0;
                double nr = ty + k * cr, nb = ty + k * cb;
                double ng = (ty - 0.299 * nr - 0.114 * nb) / 0.587;
                int ir = Clamp(nr), ig = Clamp(ng), ib = Clamp(nb);
                int ly = (299 * ir + 587 * ig + 114 * ib + 500) / 1000;
                if ((ly >= min && ly <= max) || k == 0)
                    return 0xFF000000u | (uint)ir << 16 | (uint)ig << 8 | (uint)ib;
            }
        }

        private static int Clamp(double v) => v <= 0 ? 0 : v >= 255 ? 255 : (int)Math.Round(v);
    }
}
