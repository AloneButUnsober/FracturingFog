// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Models/UiScaleLadder.cs
//
// Pure step-ladder math for the global UI scale feature (#809 / S1 #810). Lives
// in Abstractions (no Avalonia dependency) so it is unit-testable from the
// headless test project and so the value contract is shared with the workspace
// model (S5 #814). The live service that holds the current value, raises the
// change event, and persists it is UI.Avalonia/Services/UiScaleService (S1),
// which delegates every value decision to the helpers here.
//
// The ladder is a fixed set of discrete zoom steps rather than a free slider:
// discrete steps keep the transform on "nice" ratios (crisper text, fewer
// half-pixel seams) and make the +/- buttons and Ctrl+= / Ctrl+- shortcuts land
// on predictable values.

using System;

namespace FracturingFog.Models
{
    /// <summary>Pure math for the global UI-scale step ladder: the allowed
    /// discrete zoom levels plus snap / step-up / step-down operations. No state,
    /// no I/O, no UI types.</summary>
    public static class UiScaleLadder
    {
        /// <summary>The discrete zoom levels, ascending. 1.0 (100%) is the
        /// neutral default and MUST be a member so <see cref="Reset"/> and the
        /// back-compat "unset" path both resolve to an exact step.</summary>
        public static readonly double[] Steps =
        {
            0.70, 0.80, 0.90, 1.00, 1.10, 1.25, 1.50, 1.75, 2.00,
        };

        /// <summary>Smallest allowed scale (first step).</summary>
        public static double Min => Steps[0];

        /// <summary>Largest allowed scale (last step).</summary>
        public static double Max => Steps[Steps.Length - 1];

        /// <summary>The neutral 100% scale.</summary>
        public const double Default = 1.0;

        /// <summary>Snaps an arbitrary value to the nearest ladder step (clamped
        /// to <see cref="Min"/>/<see cref="Max"/>). A non-finite or non-positive
        /// input — including the store's "unset" 0 — resolves to
        /// <see cref="Default"/>, so an older/absent persisted value reads as
        /// 100%.</summary>
        public static double Snap(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
                return Default;
            if (value <= Min) return Min;
            if (value >= Max) return Max;

            double best = Steps[0];
            double bestDist = Math.Abs(value - best);
            for (int i = 1; i < Steps.Length; i++)
            {
                double d = Math.Abs(value - Steps[i]);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = Steps[i];
                }
            }
            return best;
        }

        /// <summary>The next step above <paramref name="value"/> (snaps first).
        /// Saturates at <see cref="Max"/>.</summary>
        public static double Next(double value)
        {
            double cur = Snap(value);
            int i = IndexOf(cur);
            return i < Steps.Length - 1 ? Steps[i + 1] : Max;
        }

        /// <summary>The next step below <paramref name="value"/> (snaps first).
        /// Saturates at <see cref="Min"/>.</summary>
        public static double Prev(double value)
        {
            double cur = Snap(value);
            int i = IndexOf(cur);
            return i > 0 ? Steps[i - 1] : Min;
        }

        // Snap() guarantees the argument is an exact ladder member, so a direct
        // compare is safe (no epsilon needed).
        private static int IndexOf(double snapped)
        {
            for (int i = 0; i < Steps.Length; i++)
                if (Steps[i] == snapped) return i;
            return Array.IndexOf(Steps, Default); // unreachable; defensive
        }
    }
}
