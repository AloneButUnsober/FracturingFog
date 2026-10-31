// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Models/MathPlanePresets.cs
//
// #1555 (Math Plane S8) — the built-in "math behind fractals" demos. Each one is
// a scene plus the Math Plane params and a view, and names the idea it shows.
// They are read-only: loading one copies it into the view's params; editing
// afterwards never changes the built-in. Design: MathPlane-DesignPlan.md §1.
//
// Batch parity: ParamArgs() is the --param list (the region-snapshot keys) and
// ViewWorldX / ViewWorldY / Zoom the view, so every demo renders headless as
//   FracturingFog --batch --fractal MathPlane --x X --y Y --zoom Z --param ... --out a.png
// with --y = -(world y), the Math Plane's y-up convention.
//
// The ideas are standard textbook material (complex multiplication, the
// quadratic family's orbits and cobwebs, 2x2 linear maps, the derivative as the
// local linear map); sources FF builds on are in Docs/Resources-Bibliography.md
// (#sanderson-linear-transformations, #cobweb-plot, #wegert-visual-complex-functions).

using System;
using System.Collections.Generic;
using System.Globalization;

namespace FracturingFog.Models
{
    /// <summary>One built-in demo. <see cref="ViewWorldX"/>, <see cref="ViewWorldY"/>
    /// are the world (y-up) centre of the view.</summary>
    public sealed record MathPlanePreset(
        string Name,
        string Idea,
        string Scene,
        double ViewWorldX = 0,
        double ViewWorldY = 0,
        double Zoom = 0.5,
        MathPlaneMapMode MapMode = MathPlaneMapMode.None,
        double M11 = 1, double M12 = 1, double M21 = 0, double M22 = 1,
        double T = 1,
        MathPlaneInterpolation Interpolation = MathPlaneInterpolation.Linear,
        double CRe = -0.5, double CIm = 0.5,
        MathPlaneBackdrop Backdrop = MathPlaneBackdrop.None)
    {
        /// <summary>Copy the demo into <paramref name="p"/>: every Math Plane param is set,
        /// so nothing from the previous scene carries over.</summary>
        public void Apply(FractalParameters p)
        {
            p.MathPlaneScene = Scene;
            p.MathPlaneMapMode = MapMode;
            p.MathPlaneM11 = M11; p.MathPlaneM12 = M12; p.MathPlaneM21 = M21; p.MathPlaneM22 = M22;
            p.MathPlaneT = T;
            p.MathPlaneInterpolation = Interpolation;
            p.MathPlaneCRe = CRe; p.MathPlaneCIm = CIm;
            p.MathPlaneBackdrop = Backdrop;
        }

        /// <summary>The <c>--param KEY=VALUE</c> values that reproduce the demo.</summary>
        public IReadOnlyList<string> ParamArgs()
        {
            var inv = CultureInfo.InvariantCulture;
            string N(double v) => v.ToString("R", inv);
            return new[]
            {
                "MathPlaneScene=" + Scene,
                "MathPlaneMapMode=" + ((int)MapMode).ToString(inv),
                "MathPlaneM11=" + N(M11), "MathPlaneM12=" + N(M12), "MathPlaneM21=" + N(M21), "MathPlaneM22=" + N(M22),
                "MathPlaneT=" + N(T),
                "MathPlaneInterpolation=" + ((int)Interpolation).ToString(inv),
                "MathPlaneCRe=" + N(CRe), "MathPlaneCIm=" + N(CIm),
                "MathPlaneBackdrop=" + ((int)Backdrop).ToString(inv),
            };
        }
    }

    public static class MathPlanePresets
    {
        private const string Basis = "vector i 1 0 #E69F00; vector j 0 1 #56B4E9";
        private const double RabbitRe = -0.12256116687665361, RabbitIm = 0.74486176661974423;

        /// <summary>The built-in demos, in teaching order.</summary>
        public static IReadOnlyList<MathPlanePreset> All { get; } = new MathPlanePreset[]
        {
            // ── Complex numbers ──
            new("Multiply by 1 + i",
                "Multiplying by w = 1 + i turns every point by 45 degrees and scales it by |w| = sqrt 2. "
                + "As a matrix it is [[1, -1], [1, 1]]: det = |w|^2 = 2, and no real eigenvectors (every direction turns).",
                "det on; eigen on; lengths on; " + Basis + "; vector z 2 1 #009E73",
                MapMode: MathPlaneMapMode.Matrix, M11: 1, M12: -1, M21: 1, M22: 1,
                Interpolation: MathPlaneInterpolation.Polar),
            new("z^2 doubles angles",
                "Squaring a complex number squares its length and doubles its angle: v at 30 degrees, length 1.2, "
                + "lands at 60 degrees, length 1.44. The grid's lines fold into parabolas.",
                "lengths on; map \"z^2\"; vector v 1.0392304845413265 0.6 #E69F00; curve r polar \"1.2\" #CC79A7 nolabel",
                MapMode: MathPlaneMapMode.Complex),
            new("Phase portrait of z^2 + c",
                "Colour each point z by the angle of f(z) = z^2 + c: every colour meets twice at each zero of f "
                + "(the two square roots of -c), and the contours step where |f| doubles.",
                "phase contours; map \"z^2 + c\"; axes on; grid off"),
            new("Jacobian: f'(z) is the local map",
                "Near z the map z^2 is multiplication by f'(z) = 2z: the small square turns by arg z and scales by 2|z|. "
                + "A holomorphic map is a rotate-and-scale at every point, a different one at each.",
                "map \"z^2\"; probe 1 0.5",
                ViewWorldX: 0.5, ViewWorldY: 0.8, Zoom: 0.9, MapMode: MathPlaneMapMode.Complex),

            // ── Orbits: the Mandelbrot set ──
            new("Orbit of c inside M",
                "For c inside the main cardioid the orbit of 0 under z^2 + c spirals into a fixed point: it never "
                + "leaves |z| <= 2, so c is in the Mandelbrot set.",
                "orbit 60",
                ViewWorldX: -0.5, Zoom: 0.6, CRe: -0.2, CIm: 0.3, Backdrop: MathPlaneBackdrop.Mandelbrot),
            new("Orbit of c outside M",
                "For c = 0.5 + 0.5i the orbit leaves the escape circle |z| = 2 after a few steps, so c is not in the "
                + "Mandelbrot set. Drag c to find the boundary.",
                "orbit 60",
                ViewWorldX: -0.5, Zoom: 0.6, CRe: 0.5, CIm: 0.5, Backdrop: MathPlaneBackdrop.Mandelbrot),
            new("Period 2: c = -1",
                "At c = -1 the orbit of 0 goes 0, -1, 0, -1 ...: a 2-cycle. c sits at the centre of the disk left of "
                + "the cardioid, the period-2 bulb.",
                "orbit 40",
                ViewWorldX: -0.5, Zoom: 0.6, CRe: -1, CIm: 0, Backdrop: MathPlaneBackdrop.Mandelbrot),
            new("Period 3: the rabbit",
                "At the centre of the period-3 bulb, c^3 + 2c^2 + c + 1 = 0, the orbit of 0 closes a triangle. The "
                + "backdrop is the Julia set of this c (the Douady rabbit); the triangle visits its three ears' centres.",
                "orbit 40",
                Zoom: 0.5, CRe: RabbitRe, CIm: RabbitIm, Backdrop: MathPlaneBackdrop.Julia),

            // ── The real axis: period doubling ──
            new("Cobweb: fixed point (c = -0.5)",
                "On the real axis, x -> x^2 + c. Each step goes up to the curve and across to y = x. At c = -0.5 the "
                + "staircase closes in on the crossing: an attracting fixed point.",
                "cobweb 0.9 30", Zoom: 0.8, CRe: -0.5, CIm: 0),
            new("Cobweb: period 2 (c = -1)",
                "Past c = -0.75 the fixed point repels and the cobweb settles on a square: a 2-cycle.",
                "cobweb 0.1 40", Zoom: 0.8, CRe: -1, CIm: 0),
            new("Cobweb: period 4 (c = -1.3)",
                "Past c = -1.25 the 2-cycle splits into a 4-cycle: two nested squares. The splits come faster and "
                + "faster (period doubling) along the real axis of the Mandelbrot set.",
                "cobweb 0.1 60", Zoom: 0.8, CRe: -1.3, CIm: 0),
            new("Cobweb: chaos (c = -1.9)",
                "Near c = -2 the orbit never settles: the staircase wanders over the whole interval. This is the "
                + "chaotic end of the cascade.",
                "cobweb 0.1 80", Zoom: 0.8, CRe: -1.9, CIm: 0),

            // ── Linear maps ──
            new("Shear",
                "[[1, 1], [0, 1]]: i stays, j leans over. Area is kept (det = 1) and only the x-axis is an eigenvector.",
                "det on; eigen on; " + Basis,
                MapMode: MathPlaneMapMode.Matrix, M11: 1, M12: 1, M21: 0, M22: 1),
            new("Rotation by 90 degrees",
                "[[0, -1], [1, 0]]: a quarter turn, det = 1, and no real eigenvectors. Polar interpolation turns the "
                + "plane instead of squashing it half-way (try Linear to see the difference).",
                "det on; eigen on; " + Basis,
                MapMode: MathPlaneMapMode.Matrix, M11: 0, M12: -1, M21: 1, M22: 0,
                Interpolation: MathPlaneInterpolation.Polar),
            new("Projection",
                "[[1, 0], [0, 0]]: everything drops onto the x-axis. det = 0 (area collapses); the eigenvalues are "
                + "1 (along x) and 0 (along y).",
                "det on; eigen on; " + Basis,
                MapMode: MathPlaneMapMode.Matrix, M11: 1, M12: 0, M21: 0, M22: 0),
        };
    }
}
