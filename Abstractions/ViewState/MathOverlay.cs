// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/ViewState/MathOverlay.cs
//
// #1554 (Math Plane S7, part 2) — the math overlay on normal 2D fractal views:
// the real and imaginary axes plus the orbit of one point of the view, drawn by
// FractalOverlayCompositor over the frame. Design: MathPlane-DesignPlan.md §3.6.
//
// The point is pinned (a view setting; --math-overlay RE,IM in batch) or, live
// only, the pixel under the pointer. What its orbit is depends on the family:
//   Mandelbrot (and the generated z^2 kernel): the pixel is c; z0 = 0.
//   Julia: the pixel is z0; c is the view's Julia constant.
// Other families iterate something else, so the overlay draws the axes and says
// the orbit is not available there, rather than draw a wrong one.
//
// Geometry: the 2D escape-time grid samples real = CenterX + (x - w/2) s and
// imag = CenterY + (y - h/2) s at pixel x, y (y down), with s = 3.5 / max(w, h) /
// zoom. A plane point is drawn at the centre of the pixel that samples it, so
// half a pixel is added. Doubles only: the overlay is for shallow views (an orbit
// is not meaningful to draw at deep zoom anyway).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using FracturingFog.Models;

namespace FracturingFog.ViewState
{
    /// <summary>The overlay's point and orbit length.</summary>
    public readonly record struct MathOverlaySpec(double Re, double Im, int Steps = MathOverlay.DefaultSteps)
    {
        public Complex Point => new(Re, Im);

        /// <summary>The batch flag's value, <c>RE,IM[,STEPS]</c> (steps only when not the default).</summary>
        public string ToFlagValue()
        {
            var inv = CultureInfo.InvariantCulture;
            string s = Re.ToString("R", inv) + "," + Im.ToString("R", inv);
            return Steps == MathOverlay.DefaultSteps ? s : s + "," + Steps.ToString(inv);
        }

        /// <summary>Parse <c>RE,IM[,STEPS]</c>.</summary>
        public static bool TryParse(string? text, out MathOverlaySpec spec, out string? error)
        {
            spec = default; error = null;
            var parts = (text ?? "").Split(',');
            var inv = CultureInfo.InvariantCulture;
            if (parts.Length is < 2 or > 3
                || !double.TryParse(parts[0].Trim(), NumberStyles.Float, inv, out double re) || !double.IsFinite(re)
                || !double.TryParse(parts[1].Trim(), NumberStyles.Float, inv, out double im) || !double.IsFinite(im))
            { error = $"expected \"RE,IM\" or \"RE,IM,STEPS\", got '{text}'"; return false; }
            int steps = MathOverlay.DefaultSteps;
            if (parts.Length == 3 && (!int.TryParse(parts[2].Trim(), NumberStyles.None, inv, out steps)
                                      || steps < 1 || steps > MathPlaneDynamics.MaxSteps))
            { error = $"STEPS must be 1 to {MathPlaneDynamics.MaxSteps}, got '{parts[2].Trim()}'"; return false; }
            spec = new MathOverlaySpec(re, im, steps);
            return true;
        }
    }

    public static class MathOverlay
    {
        public const int DefaultSteps = 50;

        /// <summary>World units per pixel of the 2D escape-time grid.</summary>
        public static double Scale(int width, int height, double zoom)
            => 3.5 / Math.Max(1, Math.Max(width, height)) / Math.Max(1e-300, zoom);

        /// <summary>Plane point -> pixel coordinates (the centre of the sampling pixel).</summary>
        public static (double X, double Y) ToPixel(double re, double im, int width, int height,
                                                   double centerX, double centerY, double zoom)
        {
            double s = Scale(width, height, zoom);
            return ((re - centerX) / s + width * 0.5 + 0.5, (im - centerY) / s + height * 0.5 + 0.5);
        }

        /// <summary>Client pointer position -> plane point (the point the pixel under it samples).</summary>
        public static (double Re, double Im) ClientToPlane(double x, double y, int clientWidth, int clientHeight,
                                                           double centerX, double centerY, double zoom)
        {
            double s = Scale(clientWidth, clientHeight, zoom);
            return (centerX + (Math.Floor(x) - clientWidth * 0.5) * s, centerY + (Math.Floor(y) - clientHeight * 0.5) * s);
        }

        /// <summary>The orbit's start and constant for <paramref name="point"/> on a
        /// <paramref name="type"/> view, or null when the family's orbit is not z^2 + c.</summary>
        public static (Complex Z0, Complex C)? Seed(FractalType type, Complex point, Complex juliaC) => type switch
        {
            FractalType.Mandelbrot or FractalType.GeneratedMandelbrotZ2 => (Complex.Zero, point),
            FractalType.Julia => (point, juliaC),
            _ => null,
        };

        /// <summary>The orbit for the overlay, or null when the family has none here.</summary>
        public static List<Complex>? Orbit(FractalType type, MathOverlaySpec spec, Complex juliaC)
        {
            if (Seed(type, spec.Point, juliaC) is not { } s) return null;
            return MathPlaneDynamics.Orbit(MathPlaneDynamics.Quadratic(s.C), spec.Steps, s.Z0);
        }

        /// <summary>The overlay's read-out line.</summary>
        public static string Readout(FractalType type, MathOverlaySpec spec, IReadOnlyList<Complex>? orbit, bool pinned)
        {
            string at = Cx(spec.Point);
            if (orbit == null) return $"{(pinned ? "pinned " : "")}{at}: orbits are drawn on Mandelbrot and Julia views";
            string what = type == FractalType.Julia ? $"orbit of z0 = {at}" : $"orbit of 0, c = {at}";
            if (MathPlaneDynamics.EscapeStep(orbit) is int e) return $"{what}: leaves |z| <= 2 at step {e}";
            int steps = orbit.Count - 1;
            return MathPlaneDynamics.Period(orbit) is int p
                ? $"{what}: bounded for {steps} steps, settles to period {p}"
                : $"{what}: bounded for {steps} steps";
        }

        private static string Cx(Complex z)
        {
            var inv = CultureInfo.InvariantCulture;
            return $"{z.Real.ToString("0.#####", inv)} {(z.Imaginary < 0 ? "-" : "+")} {Math.Abs(z.Imaginary).ToString("0.#####", inv)}i";
        }

        /// <summary>The orbit's colour at step n of count: Okabe-Ito blue, sky blue,
        /// yellow, orange (the Math Plane orbit's ramp).</summary>
        public static uint StepColor(int n, int count)
        {
            uint[] stops = { 0xFF0072B2, 0xFF56B4E9, 0xFFF0E442, 0xFFE69F00 };
            double u = count <= 1 ? 0 : Math.Clamp(n / (double)(count - 1), 0, 1) * (stops.Length - 1);
            int i = Math.Min((int)u, stops.Length - 2);
            double f = u - i;
            uint a = stops[i], b = stops[i + 1];
            uint Ch(int sh) => (uint)Math.Round(((a >> sh) & 0xFF) * (1 - f) + ((b >> sh) & 0xFF) * f);
            return 0xFF000000u | (Ch(16) << 16) | (Ch(8) << 8) | Ch(0);
        }
    }
}
