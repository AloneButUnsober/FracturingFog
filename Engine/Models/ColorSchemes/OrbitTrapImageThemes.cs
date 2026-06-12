// Models/ColorSchemes/OrbitTrapImageThemes.cs
//
// Image / texture orbit traps.  Per iteration the calculator records the
// orbit point (z_r, z_i) that achieves the minimum trap-shape distance.  At
// escape, that location is mapped through a UV transform into a bitmap and
// the bitmap sample becomes the pixel colour.
//
// The bitmap is pre-extracted into a flat int[] in the constructor so
// Sample/MapWithOrbit run lock-free on the worker threads.
//
//   • OrbitTrapImageRainbowMap — built-in procedural rainbow texture sample.
//
// Phase X.A / Slice A.6: the abstract base now takes a flat int[] + dims
// directly rather than a System.Drawing.Bitmap. The built-in rainbow
// pinwheel builds the int[] itself, no GDI+ involved. External callers
// loading a texture from disk should decode via SkiaSharp (SKBitmap →
// GetPixels) and pass the resulting int[].

using FracturingFog.Interefaces;
using System;

namespace FracturingFog.Models
{
    /// <summary>
    /// Shared base for image-trap colour maps.  Subclasses supply a packed
    /// 0xFFRRGGBB pixel buffer plus dimensions; the base class handles the
    /// orbit-trap accumulation, UV mapping, and bilinear sampling.
    /// </summary>
    public abstract class OrbitTrapImageBaseMap : OrbitTrapBaseMap
    {
        protected readonly int[] Pixels;     // packed 0xFFRRGGBB, width × height
        protected readonly int   ImgWidth;
        protected readonly int   ImgHeight;

        /// <summary>
        /// Range of <c>z</c> covered by one tile of the bitmap, in complex plane
        /// units.  Smaller values shrink the bitmap and produce a tighter,
        /// busier pattern.
        /// </summary>
        protected virtual double UvScale => 1.0;

        /// <summary>Whether the bitmap tiles or clamps off-edge UVs.</summary>
        protected virtual bool TileWrap => true;

        /// <summary>
        /// Construct from a pre-decoded ARGB32 pixel buffer. The buffer is
        /// assumed to be tightly packed (no stride padding) and is taken by
        /// reference — callers must not mutate it after construction.
        /// </summary>
        protected OrbitTrapImageBaseMap(int[] pixels, int width, int height)
        {
            if (pixels == null) throw new ArgumentNullException(nameof(pixels));
            if (width <= 0 || height <= 0)
                throw new ArgumentOutOfRangeException(nameof(width), "Image dimensions must be positive.");
            if (pixels.Length < width * height)
                throw new ArgumentException("Pixel buffer too small for the given dimensions.", nameof(pixels));

            ImgWidth  = width;
            ImgHeight = height;
            Pixels    = pixels;
        }

        public override void InitOrbit(out OrbitAccumulator acc)
        {
            acc = default;
            acc.TrapMin = float.MaxValue;
        }

        // Subclasses override Sample with the trap shape AND store the orbit
        // location at which TrapMin is updated (TrapZr / TrapZi).

        public override int MapWithOrbit(float smooth, float distance, int iterations,
                                         float nx, float ny, in OrbitAccumulator acc)
        {
            // Off-set pixels with no trap hit → fall back to gradient base.
            if (acc.TrapMin == float.MaxValue)
                return base.MapWithOrbit(smooth, distance, iterations, nx, ny, acc);

            // Map captured (TrapZr, TrapZi) → UV ∈ [0, 1) tile coords.
            double u = acc.TrapZr / UvScale * 0.5 + 0.5;
            double v = acc.TrapZi / UvScale * 0.5 + 0.5;

            if (TileWrap)
            {
                u = u - Math.Floor(u);
                v = v - Math.Floor(v);
            }
            else
            {
                u = System.Math.Clamp(u, 0.0, 1.0);
                v = System.Math.Clamp(v, 0.0, 1.0);
            }

            // Bilinear sample.
            double fx = u * (ImgWidth  - 1);
            double fy = v * (ImgHeight - 1);
            int x0 = (int)Math.Floor(fx);
            int y0 = (int)Math.Floor(fy);
            int x1 = Math.Min(x0 + 1, ImgWidth  - 1);
            int y1 = Math.Min(y0 + 1, ImgHeight - 1);
            double tx = fx - x0;
            double ty = fy - y0;

            int p00 = Pixels[y0 * ImgWidth + x0];
            int p10 = Pixels[y0 * ImgWidth + x1];
            int p01 = Pixels[y1 * ImgWidth + x0];
            int p11 = Pixels[y1 * ImgWidth + x1];

            double r = Lerp2(((p00 >> 16) & 0xFF), ((p10 >> 16) & 0xFF),
                             ((p01 >> 16) & 0xFF), ((p11 >> 16) & 0xFF), tx, ty);
            double g = Lerp2(((p00 >>  8) & 0xFF), ((p10 >>  8) & 0xFF),
                             ((p01 >>  8) & 0xFF), ((p11 >>  8) & 0xFF), tx, ty);
            double b = Lerp2(( p00        & 0xFF), ( p10        & 0xFF),
                             ( p01        & 0xFF), ( p11        & 0xFF), tx, ty);

            return ColorUtils.PackArgb(
                (byte)System.Math.Clamp((int)Math.Round(r), 0, 255),
                (byte)System.Math.Clamp((int)Math.Round(g), 0, 255),
                (byte)System.Math.Clamp((int)Math.Round(b), 0, 255));
        }

        private static double Lerp2(double a, double b, double c, double d,
                                    double tx, double ty)
        {
            double ab = a + (b - a) * tx;
            double cd = c + (d - c) * tx;
            return ab + (cd - ab) * ty;
        }
    }

    /// <summary>
    /// Image-trap colouring sampling a procedurally-generated 256×256 rainbow
    /// gradient pinwheel.  Demonstrates the technique without needing the user
    /// to supply a bitmap.  Trap shape: point (distance to origin).
    /// </summary>
    public sealed class OrbitTrapImageRainbowMap : OrbitTrapImageBaseMap
    {
        public static string Name => "Orbit Trap — Image (Rainbow)";
        public static string Category => "Orbit Trap";
        public static string Description =>
            "Image orbit trap: the orbit point at minimum distance from the origin is " +
            "mapped through a procedural rainbow pinwheel texture (256² bilinear).";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesOrbitTrap |
            ColorMapFeatures.HighContrast;

        protected override float TrapScale => 1.5f;
        protected override double UvScale  => 1.6;

        public OrbitTrapImageRainbowMap() : base(BuildRainbowPinwheel(256), 256, 256) { }

        public override void Sample(ref OrbitAccumulator acc,
                                    double zr, double zi,
                                    double cr, double ci, int iter)
        {
            float d = (float)Math.Sqrt(zr * zr + zi * zi);
            if (d < acc.TrapMin)
            {
                acc.TrapMin = d;
                acc.TrapZr  = zr;
                acc.TrapZi  = zi;
            }
        }

        /// <summary>Generates a polar rainbow pinwheel as a synthetic packed
        /// 0xFFRRGGBB pixel buffer of dimensions <paramref name="size"/>².</summary>
        private static int[] BuildRainbowPinwheel(int size)
        {
            var buf = new int[size * size];
            for (int y = 0; y < size; y++)
            {
                double dy = (y / (double)(size - 1)) * 2.0 - 1.0;
                for (int x = 0; x < size; x++)
                {
                    double dx = (x / (double)(size - 1)) * 2.0 - 1.0;
                    double ang = Math.Atan2(dy, dx) / (2.0 * Math.PI) + 0.5;
                    double rad = Math.Min(1.0, Math.Sqrt(dx * dx + dy * dy));
                    var col = ColorUtils.Hsv((float)ang, 1f, (float)(1.0 - 0.4 * rad));
                    buf[y * size + x] =
                        unchecked((int)0xFF000000 | (col.R << 16) | (col.G << 8) | col.B);
                }
            }
            return buf;
        }
    }
}
