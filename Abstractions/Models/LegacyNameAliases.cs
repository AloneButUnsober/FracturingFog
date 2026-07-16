// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Engine/Models/LegacyNameAliases.cs
//
// Back-compat alias table for built-in region + color-theme names that
// were de-Unicode-d (em-dash and other non-ASCII stripped from
// user-facing names). Saved data on disk - slideshow-configs.json
// (IncludedRegions / IncludedColorThemes), scenes.json shot themes,
// region CuratedThemes - may still reference the old Unicode names.
// ColorPalette.GetPaletteByName and FractalRegionLibrary.FindByName
// consult this map on a lookup MISS so those references keep resolving.
// Key = old (Unicode) name, Value = new (ASCII) name.

using System.Collections.Generic;

namespace FracturingFog.Models
{
    /// <summary>Old-Unicode-name to new-ASCII-name back-compat map for
    /// built-in regions and color themes. Consulted on lookup miss.</summary>
    public static class LegacyNameAliases
    {
        private static readonly Dictionary<string, string> Map =
            new(System.StringComparer.OrdinalIgnoreCase)
        {
            { "Apollonian — (−1, 2, 2, 3) Gasket", "Apollonian - (-1, 2, 2, 3) Gasket" },
            { "Apollonian — L/R Kissing Cusp", "Apollonian - L/R Kissing Cusp" },
            { "Arg Decomp — Pinwheel (8)", "Arg Decomp - Pinwheel (8)" },
            { "Arg Decomp — Quadrants", "Arg Decomp - Quadrants" },
            { "Arg Decomp — Spectral Pinwheel", "Arg Decomp - Spectral Pinwheel" },
            { "Bicomplex Mandelbrot — Slice k = 0", "Bicomplex Mandelbrot - Slice k = 0" },
            { "Bicomplex Mandelbrot — Slice k = 0.4", "Bicomplex Mandelbrot - Slice k = 0.4" },
            { "Binary Decomp — Classic", "Binary Decomp - Classic" },
            { "Binary Decomp — Contour Grid", "Binary Decomp - Contour Grid" },
            { "Binary Decomp — Gold / Navy", "Binary Decomp - Gold / Navy" },
            { "Bulb → Box", "Bulb -> Box" },
            { "DLA — Default Brownian Tree", "DLA - Default Brownian Tree" },
            { "Derivative — Flow Field", "Derivative - Flow Field" },
            { "Derivative — arg(dz/dc)", "Derivative - arg(dz/dc)" },
            { "Derivative — log|dz/dc|", "Derivative - log|dz/dc|" },
            { "Distance — Chromatic", "Distance - Chromatic" },
            { "Distance — Glow", "Distance - Glow" },
            { "Distance — Silver Etching", "Distance - Silver Etching" },
            { "Domain Color — Classic", "Domain Color - Classic" },
            { "Domain Color — Phase Portrait", "Domain Color - Phase Portrait" },
            { "Domain Color — Riemann Sphere", "Domain Color - Riemann Sphere" },
            { "Entropy — Contrast", "Entropy - Contrast" },
            { "Entropy — Filament Glow", "Entropy - Filament Glow" },
            { "Entropy — Heatmap", "Entropy - Heatmap" },
            { "Escape Time — 16-Step Staircase", "Escape Time - 16-Step Staircase" },
            { "Escape Time — Binary Dwell Rings", "Escape Time - Binary Dwell Rings" },
            { "Escape Time — Rainbow Bands", "Escape Time - Rainbow Bands" },
            { "Field Lines — 16 External Rays", "Field Lines - 16 External Rays" },
            { "Field Lines — Böttcher Grid", "Field Lines - Bottcher Grid" },
            { "Field Lines — Continuous Flow", "Field Lines - Continuous Flow" },
            { "Flame — Default Chaos", "Flame - Default Chaos" },
            { "Glynn — Canonical", "Glynn - Canonical" },
            { "Halley — z³ − 1 basins", "Halley - z3 - 1 basins" },
            { "Histogram — Spectral", "Histogram - Spectral" },
            { "Histogram — Twilight", "Histogram - Twilight" },
            { "Histogram — Viridis", "Histogram - Viridis" },
            { "KIFS — Menger sponge", "KIFS - Menger sponge" },
            { "KIFS — Sierpinski tetra", "KIFS - Sierpinski tetra" },
            { "Kleinian — Tetrahedral 4-Sphere", "Kleinian - Tetrahedral 4-Sphere" },
            { "Lemniscate — Bright Edges", "Lemniscate - Bright Edges" },
            { "Lemniscate — Coloured Contours", "Lemniscate - Coloured Contours" },
            { "Lemniscate — Filled Bands", "Lemniscate - Filled Bands" },
            { "Logistic — Full Cascade", "Logistic - Full Cascade" },
            { "Logistic — r ∈ [2.9, 4.0]", "Logistic - r in [2.9, 4.0]" },
            { "Magnet 1 — Main Body", "Magnet 1 - Main Body" },
            { "Magnet 2 — Triple Lobe", "Magnet 2 - Triple Lobe" },
            { "Mandelbox — Canonical (scale 2)", "Mandelbox - Canonical (scale 2)" },
            { "Mandelbox — Inverse (scale −1.5)", "Mandelbox - Inverse (scale -1.5)" },
            { "Mandelbox — Open Pore (scale 3)", "Mandelbox - Open Pore (scale 3)" },
            { "Mandelbulb — Power 8", "Mandelbulb - Power 8" },
            { "Multiplier |λ|", "Multiplier |lambda|" },
            { "Orbit Trap — Biomorph", "Orbit Trap - Biomorph" },
            { "Orbit Trap — Cardioid", "Orbit Trap - Cardioid" },
            { "Orbit Trap — Cardioid 3D", "Orbit Trap - Cardioid 3D" },
            { "Orbit Trap — Circle", "Orbit Trap - Circle" },
            { "Orbit Trap — Circle 3D", "Orbit Trap - Circle 3D" },
            { "Orbit Trap — Concentric", "Orbit Trap - Concentric" },
            { "Orbit Trap — Concentric 3D", "Orbit Trap - Concentric 3D" },
            { "Orbit Trap — Cross", "Orbit Trap - Cross" },
            { "Orbit Trap — Cross 3D", "Orbit Trap - Cross 3D" },
            { "Orbit Trap — Diagonal Cross", "Orbit Trap - Diagonal Cross" },
            { "Orbit Trap — Diagonal Cross 3D", "Orbit Trap - Diagonal Cross 3D" },
            { "Orbit Trap — Grid", "Orbit Trap - Grid" },
            { "Orbit Trap — Grid 3D", "Orbit Trap - Grid 3D" },
            { "Orbit Trap — Heart", "Orbit Trap - Heart" },
            { "Orbit Trap — Heart 3D", "Orbit Trap - Heart 3D" },
            { "Orbit Trap — Hexagon", "Orbit Trap - Hexagon" },
            { "Orbit Trap — Hexagon 3D", "Orbit Trap - Hexagon 3D" },
            { "Orbit Trap — Hyperbola", "Orbit Trap - Hyperbola" },
            { "Orbit Trap — Hyperbola 3D", "Orbit Trap - Hyperbola 3D" },
            { "Orbit Trap — Image (Rainbow)", "Orbit Trap - Image (Rainbow)" },
            { "Orbit Trap — Lemniscate", "Orbit Trap - Lemniscate" },
            { "Orbit Trap — Lemniscate 3D", "Orbit Trap - Lemniscate 3D" },
            { "Orbit Trap — Line", "Orbit Trap - Line" },
            { "Orbit Trap — Line 3D", "Orbit Trap - Line 3D" },
            { "Orbit Trap — Pickover Stalks", "Orbit Trap - Pickover Stalks" },
            { "Orbit Trap — Pinwheel", "Orbit Trap - Pinwheel" },
            { "Orbit Trap — Pinwheel 3D", "Orbit Trap - Pinwheel 3D" },
            { "Orbit Trap — Point", "Orbit Trap - Point" },
            { "Orbit Trap — Point 3D", "Orbit Trap - Point 3D" },
            { "Orbit Trap — Polar Rose", "Orbit Trap - Polar Rose" },
            { "Orbit Trap — Polar Rose 3D", "Orbit Trap - Polar Rose 3D" },
            { "Orbit Trap — Ring", "Orbit Trap - Ring" },
            { "Orbit Trap — Ring 3D", "Orbit Trap - Ring 3D" },
            { "Orbit Trap — Sine Wave", "Orbit Trap - Sine Wave" },
            { "Orbit Trap — Sine Wave 3D", "Orbit Trap - Sine Wave 3D" },
            { "Orbit Trap — Square", "Orbit Trap - Square" },
            { "Orbit Trap — Square 3D", "Orbit Trap - Square 3D" },
            { "Orbit Trap — Star", "Orbit Trap - Star" },
            { "Orbit Trap — Star 3D", "Orbit Trap - Star 3D" },
            { "Orbit Trap — Triangle", "Orbit Trap - Triangle" },
            { "Orbit Trap — Triangle 3D", "Orbit Trap - Triangle 3D" },
            { "Plasma — Default", "Plasma - Default" },
            { "Potential — Equipotential Bands", "Potential - Equipotential Bands" },
            { "Potential — Octave Contours", "Potential - Octave Contours" },
            { "Potential — Smooth", "Potential - Smooth" },
            { "Quat Julia — Classic Norton (−0.2, 0.4, −0.4, −0.4)", "Quat Julia - Classic Norton (-0.2, 0.4, -0.4, -0.4)" },
            { "Quat Julia — Dendrite (0.0, 1.0, 0.0, 0.0)", "Quat Julia - Dendrite (0.0, 1.0, 0.0, 0.0)" },
            { "Quat Julia — Spheroid (−1.0, 0.2, 0.0, 0.0)", "Quat Julia - Spheroid (-1.0, 0.2, 0.0, 0.0)" },
            { "Quat Mandelbrot — Slice W = 0", "Quat Mandelbrot - Slice W = 0" },
            { "Quat Mandelbrot — Slice W = 0.5", "Quat Mandelbrot - Slice W = 0.5" },
            { "Secant — z³ − 1 basins", "Secant - z3 - 1 basins" },
            { "Spider — Canonical", "Spider - Canonical" },
            { "Stripe Average — Classic", "Stripe Average - Classic" },
            { "TearDrop — Default", "TearDrop - Default" },
        };

        /// <summary>Return the current ASCII name for a possibly-legacy
        /// Unicode name, or null when there is no alias.</summary>
        public static string? Resolve(string? name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            return Map.TryGetValue(name, out var v) ? v : null;
        }
    }
}
