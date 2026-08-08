// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// AcidWarpCalculator.cs
//
// Acid Warp procedural pattern field (#247). Inspired by Noah Spurrier's
// 1992 DOS "Acid Warp" palette-cycling demo (modern SDL/Emscripten port by
// Boris Gjenero / dreamlayers). This is a CLEAN-ROOM implementation: the
// pattern *equations* are reimplemented from the mathematics — no acidwarp
// source, lookup tables, or palette data are copied. Fracturing Fog is
// AGPL-3.0-or-later; acidwarp is GPL-licensed.
//
// Shape mirrors PlasmaCalculator: a non-fractal, one-shot procedural fill.
// Each pattern is a closed-form map from a pixel coordinate to a *cyclic*
// scalar v (any real). The colour index is t = frac(v) = v - floor(v), so the
// field tiles seamlessly under palette rotation — which is exactly what the
// palette-cycling motion effect (#249 / IDEA-1) wants. Pan/zoom is a no-op:
// the generated field IS the image.
//
// The DOS original precomputed lut_sin / lut_dist / lut_angle to dodge slow
// VGA-era floating point. Modern FF just evaluates the trig directly.

using System;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.Interefaces;
using FracturingFog.Models;

namespace FracturingFog;

public sealed class AcidWarpCalculator : IFractalCalculator
{
    public int Width { get; private set; }
    public int Height { get; private set; }
    public uint[] ColorBuffer { get; private set; } = Array.Empty<uint>();

    public double CenterX { get; set; } = 0.0;
    public double CenterY { get; set; } = 0.0;
    public double Zoom { get; set; } = 1.0;
    public int MaxIterations { get; set; } = 0;

    public QualityPreset Quality { get; set; } = QualityPreset.Standard;
    public IColorMap ColorMap { get; set; } = new HsvPalette();

    // One-shot procedural fill — pan/zoom would just rescale the same field.
    public bool SupportsZoomPan => false;

    public FractalParameters FractalParameters { get; set; } = new();

    /// <summary>Number of distinct clean-room patterns. The pattern selector
    /// is taken modulo this, so any <c>AcidWarpPattern</c> value is legal.
    /// Aliases <see cref="FractalParameters.AcidWarpPatternCount"/> so Engine
    /// and the Abstractions-only UI agree on the count.</summary>
    public const int PatternCount = FractalParameters.AcidWarpPatternCount;

    public AcidWarpCalculator(int width, int height) => Resize(width, height);

    public void Resize(int width, int height)
    {
        Width = width;
        Height = height;
        ColorBuffer = new uint[Math.Max(1, width) * Math.Max(1, height)];
    }

    public void Calculate(CancellationToken ct = default)
    {
        ColorMap.MaxIterations = 256;

        int w = Width, h = Height;
        if (w <= 0 || h <= 0) return;

        var p = FractalParameters;
        bool titleCard = p.AcidWarpTitleCard;
        int pattern = titleCard ? 0
            : ((p.AcidWarpPattern % PatternCount) + PatternCount) % PatternCount;

        // Continuous pattern morph (#247 follow-up). When enabled, the base +
        // next pattern and the blend weight come from the fractional Flow
        // position instead of the discrete selector, so animating Flow melts
        // one field into the next. Off (or on the title card) → a single
        // pattern with mix 0, i.e. byte-identical to the discrete path.
        bool morph = p.AcidWarpMorph && !titleCard;
        int patternB = pattern;
        double mix = 0.0;
        if (morph)
        {
            double flow = p.AcidWarpFlow;
            double fp = flow - Math.Floor(flow / PatternCount) * PatternCount; // → [0, count)
            int a = (int)Math.Floor(fp);
            if (a >= PatternCount) a = PatternCount - 1;                        // guard fp==count
            mix = fp - a;
            pattern  = a;
            patternB = (a + 1) % PatternCount;
        }

        double freq = p.AcidWarpFrequency <= 0 ? 1.0 : p.AcidWarpFrequency;
        double cx = p.AcidWarpCenterX;
        double cy = p.AcidWarpCenterY;
        int seed = p.AcidWarpSeed;
        double warp = p.AcidWarpWarpStrength;

        // Normalise so the shorter axis spans [-1, 1]; 0 is screen centre.
        double scale = 2.0 / Math.Max(w, h);
        double halfW = w * 0.5, halfH = h * 0.5;

        // Title-card wordmark layout (font-pixel space); computed once.
        var title = titleCard ? TitleLayout.For(w, h) : default;

        // Snapshot the colour map once — Map() is a pure LUT sample.
        IColorMap map = ColorMap;

        var opts = new ParallelOptions
        {
            CancellationToken = ct,
            MaxDegreeOfParallelism = Environment.ProcessorCount
        };

        try
        {
            Parallel.For(0, h, opts, j =>
            {
                double ny = (j - halfH) * scale - cy;
                int outRow = j * w;
                for (int i = 0; i < w; i++)
                {
                    double nx = (i - halfW) * scale - cx;
                    double wx = nx, wy = ny;
                    if (warp != 0.0) DomainWarp(ref wx, ref wy, warp, freq);
                    double v = Evaluate(pattern, wx, wy, freq, seed);
                    // Morph: blend the base and next pattern in field space, then
                    // take frac below — melting one pattern into the next.
                    if (mix > 0.0)
                    {
                        double v2 = Evaluate(patternB, wx, wy, freq, seed);
                        v += (v2 - v) * mix;
                    }

                    if (titleCard)
                    {
                        // Title card wordmark legibility (#250 smoke): the letters
                        // read in the complementary palette colour (phase +0.5)
                        // *and* are darkened for luminance contrast, wrapped in a
                        // dark one-pixel halo so "ACID FOG" stays legible over any
                        // phase of the cycling ring field behind it.
                        if (title.Hit(i, j))
                        {
                            double tg = v + 0.5; tg -= Math.Floor(tg);
                            ColorBuffer[outRow + i] =
                                Darken((uint)map.Map((float)(tg * 256.0), 0f, 256), 0.72);
                            continue;
                        }
                        if (title.Halo(i, j))
                        {
                            double th = v - Math.Floor(v);
                            ColorBuffer[outRow + i] =
                                Darken((uint)map.Map((float)(th * 256.0), 0f, 256), 0.20);
                            continue;
                        }
                    }

                    double t = v - Math.Floor(v);            // frac → [0,1)
                    ColorBuffer[outRow + i] = (uint)map.Map((float)(t * 256.0), 0f, 256);
                }
            });
        }
        catch (OperationCanceledException)
        {
            // Cancelled mid-fill — partial buffer is fine; host will re-run.
        }
    }

    /// <summary>Closed-form pattern evaluation. Returns a cyclic scalar; the
    /// caller takes the fractional part as the colour index. Clean-room from
    /// the Acid Warp pattern families (radial, angular, spiral, multi-centre
    /// interference, wave plaid, bitwise XOR, stochastic value-noise).</summary>
    private static double Evaluate(int pattern, double nx, double ny, double freq, int seed)
    {
        double dist = Math.Sqrt(nx * nx + ny * ny);
        double angle = Math.Atan2(ny, nx);            // [-π, π]
        double ang01 = (angle + Math.PI) / (2.0 * Math.PI); // [0, 1)

        switch (pattern)
        {
            case 0:  // Concentric rings
                return dist * 6.0 * freq;

            case 1:  // Radial spokes / rays
                return ang01 * 12.0 * freq;

            case 2:  // Spiral (rings + angular shear)
                return dist * 4.0 * freq + ang01 * 4.0;

            case 3:  // Five-arm star
                return dist * 5.0 * freq + Math.Sin(5.0 * angle) * 0.5;

            case 4:  // Sine-modulated rings
                return dist * 3.0 * freq + Math.Sin(dist * 12.0 * freq) * 0.5;

            case 5:  // Pure concentric sine bands
                return Math.Sin(dist * 10.0 * freq);

            case 6:  // Horizontal waves
                return nx * 6.0 * freq;

            case 7:  // Plaid — sin(x) + cos(y)
                return Math.Sin(nx * 8.0 * freq) + Math.Cos(ny * 8.0 * freq);

            case 8:  // Two-centre interference
            {
                double d1 = Dist(nx, ny, 0.45, 0.30);
                double d2 = Dist(nx, ny, -0.50, 0.20);
                return Math.Sin(d1 * 10.0 * freq) + Math.Sin(d2 * 10.0 * freq);
            }

            case 9:  // Peacock — three-centre interference
            {
                double d1 = Dist(nx, ny, 0.45, 0.30);
                double d2 = Dist(nx, ny, -0.50, 0.20);
                double d3 = Dist(nx, ny, 0.0, -0.55);
                return Math.Sin(d1 * 9.0 * freq)
                     + Math.Sin(d2 * 9.0 * freq)
                     + Math.Sin(d3 * 9.0 * freq);
            }

            case 10: // Angular sine ripples over rings
                return Math.Sin(angle * 7.0) * 2.0 + dist * 2.0 * freq;

            case 11: // Kaleidoscopic — angular × radial sine
                return Math.Sin(angle * 6.0) * Math.Cos(dist * 8.0 * freq);

            case 12: // Diagonal plaid
                return (nx + ny) * 6.0 * freq;

            case 13: // Cross weave — sin(x)*sin(y)
                return Math.Sin(nx * 10.0 * freq) * Math.Sin(ny * 10.0 * freq) * 2.0;

            case 14: // Concentric ripple with radial falloff
                return Math.Sin(dist * 16.0 * freq) / (1.0 + dist);

            case 15: // XOR of quantised coordinates (plaid / moiré)
            {
                int ix = QuantiseSigned(nx, freq);
                int iy = QuantiseSigned(ny, freq);
                return (ix ^ iy) / 32.0;
            }

            case 16: // XOR of angle and distance (demoscene rosette)
            {
                int ia = (int)(ang01 * 256.0);
                int id = (int)(dist * 64.0 * freq);
                return (ia ^ id) / 48.0;
            }

            case 17: // Rose / rhodonea petals
                return Math.Cos(angle * 5.0) * dist * 4.0 * freq;

            case 18: // Interference lattice — sum of three axis waves
                return Math.Sin(nx * 7.0 * freq)
                     + Math.Sin(ny * 7.0 * freq)
                     + Math.Sin((nx + ny) * 7.0 * freq);

            case 19: // Stochastic smooth value-noise field
                return ValueNoise(nx * 3.0 * freq, ny * 3.0 * freq, seed) * 4.0;

            default:
                return dist * 6.0 * freq;
        }
    }

    // #253 / IDEA-3: domain warp. Displace the sampling coordinate by a smooth
    // interference field (sin of the orthogonal axis) so straight rings/waves
    // fold into organic swirls. A standard IQ-style two-tap warp; strength 0 is
    // an exact no-op (guarded by the caller).
    private static void DomainWarp(ref double x, ref double y, double strength, double freq)
    {
        double k = 3.0 * freq;
        double dx = Math.Sin(y * k + x * 1.3);
        double dy = Math.Sin(x * k - y * 1.3);
        x += strength * dx;
        y += strength * dy;
    }

    private static double Dist(double x, double y, double ox, double oy)
    {
        double dx = x - ox, dy = y - oy;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    // Map a signed normalised coordinate to a non-negative integer lattice
    // index for bitwise patterns. freq scales the lattice density.
    private static int QuantiseSigned(double v, double freq)
    {
        return (int)Math.Floor((v + 4.0) * 32.0 * freq) & 0x7FFFFFFF;
    }

    // ---- Deterministic smooth value noise (seeded) -----------------------
    // Cheap 2D value noise: hash lattice points, smootherstep-interpolate.
    // Deterministic in (x, y, seed) — no per-pixel RNG state.

    private static double ValueNoise(double x, double y, int seed)
    {
        int x0 = (int)Math.Floor(x);
        int y0 = (int)Math.Floor(y);
        double fx = x - x0;
        double fy = y - y0;
        double u = Smoother(fx);
        double v = Smoother(fy);

        double n00 = Hash01(x0,     y0,     seed);
        double n10 = Hash01(x0 + 1, y0,     seed);
        double n01 = Hash01(x0,     y0 + 1, seed);
        double n11 = Hash01(x0 + 1, y0 + 1, seed);

        double top = n00 + (n10 - n00) * u;
        double bot = n01 + (n11 - n01) * u;
        return top + (bot - top) * v;   // [0, 1]
    }

    private static double Smoother(double t) => t * t * t * (t * (t * 6.0 - 15.0) + 10.0);

    /// <summary>Scale the RGB channels of a packed colour by <paramref name="f"/>
    /// (0..1), preserving the top (alpha) byte. Channel order is irrelevant — the
    /// three low bytes are scaled uniformly — so this works for ARGB or ABGR.</summary>
    private static uint Darken(uint c, double f)
    {
        uint a =  c & 0xFF000000u;
        uint r = (uint)(((c >> 16) & 0xFF) * f) & 0xFF;
        uint g = (uint)(((c >> 8)  & 0xFF) * f) & 0xFF;
        uint b = (uint)(( c        & 0xFF) * f) & 0xFF;
        return a | (r << 16) | (g << 8) | b;
    }

    private static double Hash01(int x, int y, int seed)
    {
        // Integer hash → [0, 1). Splittable-style avalanche.
        uint hh = (uint)(x * 374761393 + y * 668265263 + seed * 362437);
        hh = (hh ^ (hh >> 13)) * 1274126177u;
        hh ^= hh >> 16;
        return hh / 4294967296.0;
    }

    // ---- #250 title card: "ACID FOG" wordmark in a 5x7 pixel font ---------
    // A clean-room homage to the Acid Warp intro — an original name/styling,
    // rendered as a mask over the ring field so it colour-cycles like the rest.

    private const string TitleText = "ACID FOG";
    private const int GlyphW = 5, GlyphH = 7, GlyphAdvance = 6;

    private readonly struct TitleLayout
    {
        private readonly int _originX, _originY, _scale, _spanW, _spanH;

        private TitleLayout(int ox, int oy, int s, int sw, int sh)
        { _originX = ox; _originY = oy; _scale = s; _spanW = sw; _spanH = sh; }

        public static TitleLayout For(int w, int h)
        {
            int fontW = TitleText.Length * GlyphAdvance;     // font-pixels wide
            int sX = (int)(w * 0.82) / fontW;
            int sY = (int)(h * 0.42) / GlyphH;
            int s = Math.Max(1, Math.Min(sX, sY));
            int spanW = fontW * s, spanH = GlyphH * s;
            return new TitleLayout((w - spanW) / 2, (h - spanH) / 2, s, spanW, spanH);
        }

        /// <summary>True if pixel (i,j) falls inside a lit glyph cell.</summary>
        public bool Hit(int i, int j)
        {
            int fx = i - _originX, fy = j - _originY;
            if (fx < 0 || fy < 0 || fx >= _spanW || fy >= _spanH) return false;
            int col = fx / _scale, row = fy / _scale;
            int charIdx = col / GlyphAdvance;
            int colInChar = col - charIdx * GlyphAdvance;
            if (colInChar >= GlyphW) return false;           // inter-glyph gap
            if (charIdx >= TitleText.Length) return false;
            int bits = GlyphRow(TitleText[charIdx], row);
            return (bits & (1 << (GlyphW - 1 - colInChar))) != 0;
        }

        /// <summary>True for a pixel just outside a glyph (a one-font-pixel ring):
        /// not itself lit, but with a lit glyph cell in its 8-neighbourhood at
        /// font-pixel scale. Drives the dark outline that keeps the wordmark
        /// legible over the cycling field.</summary>
        public bool Halo(int i, int j)
        {
            if (Hit(i, j)) return false;
            int s = _scale;
            return Hit(i - s, j)     || Hit(i + s, j)
                || Hit(i, j - s)     || Hit(i, j + s)
                || Hit(i - s, j - s) || Hit(i + s, j - s)
                || Hit(i - s, j + s) || Hit(i + s, j + s);
        }
    }

    // 5-bit-per-row glyphs (MSB = leftmost column), rows top→bottom.
    private static int GlyphRow(char c, int row) => c switch
    {
        'A' => Row(0b01110, 0b10001, 0b10001, 0b11111, 0b10001, 0b10001, 0b10001, row),
        'C' => Row(0b01110, 0b10001, 0b10000, 0b10000, 0b10000, 0b10001, 0b01110, row),
        'I' => Row(0b11111, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100, 0b11111, row),
        'D' => Row(0b11110, 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b11110, row),
        'F' => Row(0b11111, 0b10000, 0b10000, 0b11110, 0b10000, 0b10000, 0b10000, row),
        'O' => Row(0b01110, 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b01110, row),
        'G' => Row(0b01110, 0b10001, 0b10000, 0b10111, 0b10001, 0b10001, 0b01111, row),
        _   => 0, // space and anything else → blank
    };

    private static int Row(int r0, int r1, int r2, int r3, int r4, int r5, int r6, int row)
        => row switch { 0 => r0, 1 => r1, 2 => r2, 3 => r3, 4 => r4, 5 => r5, _ => r6 };
}
