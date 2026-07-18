// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Models/ThemeRecommender.cs
//
// Scores IColorMap themes against an EquationProfile and returns a ranked
// list of recommendations. Two-stage scoring:
//   1. Hard disqualifiers — themes whose math is incompatible with the
//      equation (e.g. distance-estimate themes when DE is invalid).
//   2. Soft positive/negative weights — feature flags that align with or
//      clash with the equation's behavioral fingerprint.
//
// All inputs are pure data (no rendering, no AST eval). Cheap to call —
// designed for UI use during equation editing.

using System;
using System.Collections.Generic;

using FracturingFog.Interefaces;

namespace FracturingFog.Models
{
    public sealed class ThemeRecommendation
    {
        public IColorMap Map { get; init; } = default!;
        public string Name { get; init; } = string.Empty;
        public int Score { get; init; }
        public IReadOnlyList<string> Reasons { get; init; } = Array.Empty<string>();
    }

    public static class ThemeRecommender
    {
        private const int DisqualifyThreshold = -500;

        /// <summary>
        /// Score every supplied theme against the profile. Returns ranked list
        /// (highest score first). Disqualified themes are dropped unless
        /// <paramref name="includeDisqualified"/> is true.
        /// </summary>
        public static List<ThemeRecommendation> Recommend(
            EquationProfile profile,
            IEnumerable<IColorMap> available,
            int maxCount = 10,
            bool includeDisqualified = false)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            if (available == null) throw new ArgumentNullException(nameof(available));

            var scored = new List<ThemeRecommendation>();
            foreach (var map in available)
            {
                var (score, reasons) = Score(profile, map);
                if (!includeDisqualified && score <= DisqualifyThreshold) continue;
                scored.Add(new ThemeRecommendation
                {
                    Map = map,
                    Name = ColorPalette.GetStaticName(map),
                    Score = score,
                    Reasons = reasons,
                });
            }

            scored.Sort((a, b) =>
            {
                int c = b.Score.CompareTo(a.Score);
                return c != 0 ? c : string.CompareOrdinal(a.Name, b.Name);
            });

            if (maxCount > 0 && scored.Count > maxCount)
                scored.RemoveRange(maxCount, scored.Count - maxCount);
            return scored;
        }

        /// <summary>Convenience: just names of the top recommendations.</summary>
        public static List<string> RecommendNames(
            EquationProfile profile,
            IEnumerable<IColorMap> available,
            int maxCount = 10)
        {
            var ranked = Recommend(profile, available, maxCount);
            var names = new List<string>(ranked.Count);
            foreach (var r in ranked) names.Add(r.Name);
            return names;
        }

        // ── Scoring core ──────────────────────────────────────────────────────

        private static (int score, List<string> reasons) Score(EquationProfile p, IColorMap map)
        {
            var reasons = new List<string>();
            int score = 0;

            ColorMapFeatures f = ColorPalette.GetStaticFeatures(map);

            // ── Hard disqualifiers ────────────────────────────────────────────
            // Distance estimation requires holomorphic dynamics with valid
            // closed-form derivative. Antiholomorphic (conj), abs, transcendental,
            // and iteration-dependent equations all break DE math.
            if (f.HasFlag(ColorMapFeatures.UsesDistance) && !p.DistanceEstimateValid)
            {
                reasons.Add("distance estimation invalid for this equation");
                return (DisqualifyThreshold - 10, reasons);
            }

            // Derivative-bailout themes need a clean dz/dc. Same breakers as DE
            // plus high-order maps where derivative magnitudes overflow fast.
            if (f.HasFlag(ColorMapFeatures.UsesDerivative))
            {
                if (p.Antiholomorphic || p.HasAbs)
                {
                    reasons.Add("derivative coloring invalid (antiholomorphic / abs)");
                    return (DisqualifyThreshold - 10, reasons);
                }
                if (p.Transcendental)
                {
                    reasons.Add("derivative coloring unreliable for transcendental escape");
                    return (DisqualifyThreshold - 10, reasons);
                }
            }

            // Interior themes (cycle detection) only make sense for equations
            // that have attracting cycles — pure z^2+c family. Anything with
            // n-dependence or abs breaks the cycle iteration assumption.
            if (f.HasFlag(ColorMapFeatures.UsesInterior))
            {
                if (p.IterationDependent || p.HasAbs || p.Antiholomorphic)
                {
                    reasons.Add("interior cycle detection not meaningful here");
                    return (DisqualifyThreshold - 10, reasons);
                }
            }

            // Orbit-aware extras need stable orbit iteration — degrade hard with
            // transcendental escape (orbit explodes immediately).
            if (map is IOrbitAwareColorMap && p.Transcendental)
            {
                score -= 40;
                reasons.Add("orbit sampling weak for transcendental escape");
            }

            // ── Positive matches ──────────────────────────────────────────────
            if (f.HasFlag(ColorMapFeatures.Cyclic))
            {
                if (p.Transcendental)        { score += 30; reasons.Add("cyclic gradient suits transcendental escape"); }
                if (p.HasImaginaryConst)     { score += 15; reasons.Add("cyclic hue suits rotational structure"); }
                if (p.MaxPolyDegree >= 4)    { score += 10; reasons.Add("cyclic gradient avoids banding at high degree"); }
            }

            if (f.HasFlag(ColorMapFeatures.HighContrast))
            {
                if (p.HasAbs)                { score += 25; reasons.Add("high contrast highlights abs creases"); }
                if (p.HasBranching)          { score += 15; reasons.Add("high contrast separates branched regions"); }
            }

            if (f.HasFlag(ColorMapFeatures.UsesHistogram))
            {
                if (p.IterationDependent)    { score += 30; reasons.Add("histogram tames iteration-dependent smoothing"); }
                if (p.Transcendental)        { score += 20; reasons.Add("histogram normalises uneven escape distribution"); }
            }

            if (f.HasFlag(ColorMapFeatures.Perceptual))
            {
                if (p.MaxPolyDegree >= 4)    { score += 20; reasons.Add("perceptual gradient reveals high-degree structure"); }
                if (p.SmoothEscapeReliable)  { score += 10; reasons.Add("perceptual gradient on clean smooth iter"); }
            }

            if (f.HasFlag(ColorMapFeatures.UsesFinalZ))
            {
                if (p.Transcendental || p.Antiholomorphic)
                {
                    score += 25;
                    reasons.Add("domain coloring works when smooth iter unreliable");
                }
            }

            if (f.HasFlag(ColorMapFeatures.GradientBased) && p.SmoothEscapeReliable)
            {
                score += 10;
                reasons.Add("gradient suits low-degree polynomial escape");
            }

            if (f.HasFlag(ColorMapFeatures.ThreeDEffect) || map.Type == ColorPaletteType.Relief3D)
            {
                score += 5; // mild boost — 3D relief flatters most equations
            }

            // ── Penalties ─────────────────────────────────────────────────────
            // Smooth-only gradient (no cyclic, no histogram) on transcendental
            // escape produces ugly banded results.
            bool plainSmooth =
                f.HasFlag(ColorMapFeatures.UsesSmooth) &&
                !f.HasFlag(ColorMapFeatures.Cyclic) &&
                !f.HasFlag(ColorMapFeatures.UsesHistogram) &&
                !f.HasFlag(ColorMapFeatures.UsesFinalZ);
            if (plainSmooth && p.Transcendental)
            {
                score -= 20;
                reasons.Add("smooth gradient bands under transcendental escape");
            }

            // Default baseline — themes that match nothing specific still rank
            // by a small tie-breaker so the list isn't all zeros.
            if (score == 0) score = 1;

            return (score, reasons);
        }
    }
}
