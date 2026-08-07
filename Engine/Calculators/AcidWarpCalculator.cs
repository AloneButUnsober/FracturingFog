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
        int pattern = ((p.AcidWarpPattern % PatternCount) + PatternCount) % PatternCount;
        double freq = p.AcidWarpFrequency <= 0 ? 1.0 : p.AcidWarpFrequency;
        double cx = p.AcidWarpCenterX;
        double cy = p.AcidWarpCenterY;
        int seed = p.AcidWarpSeed;

        // Normalise so the shorter axis spans [-1, 1]; 0 is screen centre.
        double scale = 2.0 / Math.Max(w, h);
        double halfW = w * 0.5, halfH = h * 0.5;

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
                    double v = Evaluate(pattern, nx, ny, freq, seed);
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

    private static double Hash01(int x, int y, int seed)
    {
        // Integer hash → [0, 1). Splittable-style avalanche.
        uint hh = (uint)(x * 374761393 + y * 668265263 + seed * 362437);
        hh = (hh ^ (hh >> 13)) * 1274126177u;
        hh ^= hh >> 16;
        return hh / 4294967296.0;
    }
}
