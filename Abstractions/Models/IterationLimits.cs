// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Models/IterationLimits.cs
//
// #1858 (epic #1428, C8b) — iteration limits that are not a quality tier's own formula cap.
// A quality tier caps the iterations its zoom formula gives (QualityPreset.IterMax). An
// explore plan (minibrot jump, angle landing, Julia-morph step) asks for what the target's
// period needs (#1426), which for deep, high-period targets is far more; it is bounded by
// the ceiling here instead of the deepest tier's cap.

namespace FracturingFog.Models
{
    /// <summary>#1858 — iteration ceilings outside the quality tiers.</summary>
    public static class IterationLimits
    {
        /// <summary>The highest iteration count an explore plan asks for (a sanity bound:
        /// past the tier's own cap renders get slower in proportion, see
        /// <c>MinibrotJumpPlan.BeyondTierCap</c>). 50 M covers period × 100 up to period
        /// 500,000; since #1856 the reference orbit's memory follows the orbit (it stops at
        /// the period), not this cap.</summary>
        public static int ExploreCeiling { get; set; } = 50_000_000;
    }
}
