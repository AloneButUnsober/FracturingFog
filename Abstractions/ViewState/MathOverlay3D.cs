// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/ViewState/MathOverlay3D.cs
//
// #1734 (Math Plane 3D fractal bridge B4) — the math overlay on 3D fractal views: the axes,
// the bailout sphere and the orbit of one point under the fractal's own step (B2), drawn with
// the raymarcher's exact camera (B3) and hidden parts dimmed by its depth.
// Design: Docs/Technical/MathPlane3D-FractalBridge-DesignPlan.md §3.4.
//
// The point is in the fractal's own object space (the raymarcher's, y up): the point the
// pointer is over on the surface (live), or a pinned one. --math-overlay-3d X,Y,Z[,STEPS]
// (B5, #1735) carries the pin to poster export and the batch.

using System.Globalization;
using FracturingFog.Models;

namespace FracturingFog.ViewState
{
    /// <summary>#1734 — the 3D overlay's point (object space) and orbit length.</summary>
    public readonly record struct MathOverlay3DSpec(double X, double Y, double Z, int Steps = MathOverlay.DefaultSteps)
    {
        public (double X, double Y, double Z) Point => (X, Y, Z);

        /// <summary><c>X,Y,Z[,STEPS]</c> (steps only when not the default).</summary>
        public string ToFlagValue()
        {
            var inv = CultureInfo.InvariantCulture;
            string s = X.ToString("R", inv) + "," + Y.ToString("R", inv) + "," + Z.ToString("R", inv);
            return Steps == MathOverlay.DefaultSteps ? s : s + "," + Steps.ToString(inv);
        }

        /// <summary>Parse <c>X,Y,Z[,STEPS]</c>.</summary>
        public static bool TryParse(string? text, out MathOverlay3DSpec spec, out string? error)
        {
            spec = default; error = null;
            var parts = (text ?? "").Split(',');
            var inv = CultureInfo.InvariantCulture;
            var v = new double[3];
            if (parts.Length is < 3 or > 4)
            { error = $"expected \"X,Y,Z\" or \"X,Y,Z,STEPS\", got '{text}'"; return false; }
            for (int k = 0; k < 3; k++)
                if (!double.TryParse(parts[k].Trim(), NumberStyles.Float, inv, out v[k]) || !double.IsFinite(v[k]))
                { error = $"expected \"X,Y,Z\" or \"X,Y,Z,STEPS\", got '{text}'"; return false; }
            int steps = MathOverlay.DefaultSteps;
            if (parts.Length == 4 && (!int.TryParse(parts[3].Trim(), NumberStyles.None, inv, out steps)
                                      || steps < 1 || steps > MathPlaneDynamics.MaxSteps))
            { error = $"STEPS must be 1 to {MathPlaneDynamics.MaxSteps}, got '{parts[3].Trim()}'"; return false; }
            spec = new MathOverlay3DSpec(v[0], v[1], v[2], steps);
            return true;
        }
    }
}
