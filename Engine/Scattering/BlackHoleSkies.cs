// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// BlackHoleSkies.cs (#1925, black hole shadows epic #1922)
//
// The background an escaped ray sees: its final direction looks up a sky. Environment
// is the Lighting & FX sky (the HDRI by name or file path, else the background gradient),
// exactly as the Wada mirror spheres' photo mode samples it (ShadingPipeline.SkyColorHdri),
// so a user image is any .hdr / .exr the Lighting & FX environment can load and the batch
// carries it through --lighting-preset. The procedural skies are seeded and analytic, so
// they need no file: an equirectangular checker, a latitude / longitude grid, and a star
// field. All use y as up (the HDRI convention); Rotation turns the sky about y.

using System;

using FracturingFog.Rendering.Lighting;

namespace FracturingFog.Scattering;

public static class BlackHoleSkies
{
    /// <summary>Checker colours (light grey / mid blue; distinguishable without red-green).</summary>
    public const uint CheckerLight = 0xFFE8E8E8, CheckerDark = 0xFF3A6EA5;
    /// <summary>Grid colours: background, meridian / parallel lines, equator.</summary>
    public const uint GridBack = 0xFF101820, GridLine = 0xFFB8C4D0, GridEquator = 0xFFFFCC00;
    /// <summary>Star-field background.</summary>
    public const uint StarBack = 0xFF020308;

    /// <summary>Settings of one frame's sky.</summary>
    public readonly struct Settings
    {
        public BlackHoleSky Kind { get; init; }
        public int Divisions { get; init; }
        public int Seed { get; init; }
        public double RotationRad { get; init; }
        public LightingFxData Lighting { get; init; }
    }

    /// <summary>Sky colour along direction (dx, dy, dz) (need not be unit length).</summary>
    public static uint Sample(in Settings s, double dx, double dy, double dz)
    {
        double len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        if (!(len > 0)) return StarBack;
        dx /= len; dy /= len; dz /= len;
        if (s.RotationRad != 0.0)
        {
            double c = Math.Cos(s.RotationRad), sn = Math.Sin(s.RotationRad);
            double rx = c * dx + sn * dz, rz = -sn * dx + c * dz;
            dx = rx; dz = rz;
        }
        switch (s.Kind)
        {
            case BlackHoleSky.Environment:
            {
                var fx = s.Lighting;
                return ShadingPipeline.SkyColorHdri(dx, dy, dz, in fx);
            }
            case BlackHoleSky.Checker:
            {
                var (lon, lat) = LonLat(dx, dy, dz);
                double cell = Math.PI / Math.Max(1, s.Divisions);
                long i = (long)Math.Floor(lon / cell), j = (long)Math.Floor(lat / cell);
                return ((i + j) & 1) == 0 ? CheckerLight : CheckerDark;
            }
            case BlackHoleSky.Grid:
            {
                var (lon, lat) = LonLat(dx, dy, dz);
                double cell = Math.PI / Math.Max(1, s.Divisions);
                const double w = 0.06;   // line half-width as a fraction of a cell
                if (Math.Abs(lat) < w * cell) return GridEquator;
                double fl = lat / cell - Math.Round(lat / cell);
                double fo = (lon / cell - Math.Round(lon / cell)) * Math.Cos(lat);
                return Math.Abs(fl) < w || Math.Abs(fo) < w ? GridLine : GridBack;
            }
            case BlackHoleSky.Stars:
                return Star(dx, dy, dz, s.Divisions, s.Seed);
            default:
                return StarBack;
        }
    }

    /// <summary>Longitude in [-π, π) about y (0 along +x, towards +z), latitude in [-π/2, π/2].</summary>
    public static (double Lon, double Lat) LonLat(double dx, double dy, double dz)
        => (Math.Atan2(dz, dx), Math.Asin(Math.Clamp(dy, -1.0, 1.0)));

    // Star field: the sphere is cut into a lon/lat grid 16x finer than Divisions; each cell
    // holds at most one star, placed and sized by a hash of (seed, cell), so the field is
    // the same for every frame, resolution and thread count.
    private static uint Star(double dx, double dy, double dz, int divisions, int seed)
    {
        var (lon, lat) = LonLat(dx, dy, dz);
        double cell = Math.PI / (Math.Max(1, divisions) * 16.0);
        long i = (long)Math.Floor(lon / cell), j = (long)Math.Floor(lat / cell);
        ulong h = Hash((ulong)(uint)seed, (ulong)i, (ulong)j);
        if ((h & 0xFF) >= 26) return StarBack;   // ~10 % of cells hold a star
        double jx = 0.25 + 0.5 * ((h >> 8) & 0xFFFF) / 65535.0;    // centre kept off the cell edges
        double jy = 0.25 + 0.5 * ((h >> 24) & 0xFFFF) / 65535.0;
        double size = 0.08 + 0.17 * ((h >> 40) & 0xFF) / 255.0;    // radius, cell units
        double fx = (lon / cell - i - jx) * Math.Cos(lat), fy = lat / cell - j - jy;
        double r = Math.Sqrt(fx * fx + fy * fy);
        if (r > size) return StarBack;
        double k = 1.0 - r / size;
        double bright = 0.45 + 0.55 * ((h >> 48) & 0xFF) / 255.0;
        // Warm / cool tint from the hash, never red-green coded.
        bool warm = ((h >> 56) & 1) == 1;
        double v = bright * (0.35 + 0.65 * k);
        byte R = (byte)Math.Clamp(255 * v * (warm ? 1.0 : 0.80), 0, 255);
        byte G = (byte)Math.Clamp(255 * v * (warm ? 0.92 : 0.88), 0, 255);
        byte B = (byte)Math.Clamp(255 * v * (warm ? 0.70 : 1.0), 0, 255);
        return 0xFF000000u | ((uint)R << 16) | ((uint)G << 8) | B;
    }

    // SplitMix64-style mixing of three words (Steele, Lea & Flood 2014's finaliser).
    private static ulong Hash(ulong a, ulong b, ulong c)
    {
        ulong x = a * 0x9E3779B97F4A7C15UL ^ (b + 0x632BE59BD9B4E019UL) * 0xBF58476D1CE4E5B9UL ^ (c + 0x85157AF5UL) * 0x94D049BB133111EBUL;
        x ^= x >> 30; x *= 0xBF58476D1CE4E5B9UL;
        x ^= x >> 27; x *= 0x94D049BB133111EBUL;
        x ^= x >> 31;
        return x;
    }
}
