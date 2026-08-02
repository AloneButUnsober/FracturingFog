// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Models/UserBulbChainPrimitives.cs
//
// Catalog of named "fold + power" snippets users can drop into a User Bulb
// chain. Each primitive is one UserBulbChainStep (output name + body) that
// returns Vec3 — the body uses the helpers already exposed by Vec3.cs
// (BoxFold, SphereFold, Pow) so no new runtime code is needed.
//
// Used by:
//   • Worked-example chains seeded into UserBulbStore.
//   • The "+ Primitive" menu in UserBulbView, which clones an entry and
//     appends it to the active chain.
//
// Primitives are intentionally self-contained — each step reads its prior
// step's output via the chain's `z` slot (the chain runner threads each
// step's return value into the next step's z), so a Mandelbox fold followed
// by a Mandelbulb power composes by concatenation.

using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace FracturingFog.Models
{
    /// <summary>One named fold/power that drops into a User Bulb chain.</summary>
    public sealed class UserBulbChainPrimitive
    {
        public string DisplayName { get; init; } = string.Empty;
        public string DefaultOutputName { get; init; } = "out";
        public string Source { get; init; } = "z";
        public string Description { get; init; } = string.Empty;

        /// <summary>Per-iteration linear scale of this fold, for the scalar-KIFS
        /// distance estimator (<c>|z| / scale^iter</c>). KIFS folds are
        /// discontinuous — the numerical Jacobian DE can't estimate distance
        /// across them (blank / blobby / zero-triangle export), so a fold
        /// primitive declares its scale here and the editor sets
        /// <c>UserBulbKifsScale</c> to it. 0 = not a pure-scale fold (power maps
        /// etc. use the numerical DE). See #113.</summary>
        public double KifsScale { get; init; } = 0.0;

        public UserBulbChainStep ToStep() => new()
        {
            OutputName = DefaultOutputName,
            Source = Source,
        };
    }

    public static class UserBulbChainPrimitives
    {
        /// <summary>Identifier used by the "Mandelbox fold" primitive.</summary>
        public const string IdMandelbox = "mbox";
        /// <summary>Identifier used by the "KIFS Menger fold" primitive.</summary>
        public const string IdMenger = "menger";
        /// <summary>Identifier used by the "KIFS Sierpinski tetra fold" primitive.</summary>
        public const string IdSierpinski = "sierp";
        /// <summary>Identifier used by the "Mandelbulb power" primitive.</summary>
        public const string IdBulbPow = "bulb";
        /// <summary>Identifier used by the Kaleidoscopic-IFS fold primitive.</summary>
        public const string IdKifsFold = "kfold";
        /// <summary>Identifier used by the Kaleidoscopic-IFS rotation primitive.</summary>
        public const string IdKifsRot = "kifsrot";
        /// <summary>Identifier used by the Kaleidoscopic-IFS scale primitive.</summary>
        public const string IdKifsScale = "kifsscale";

        private static readonly UserBulbChainPrimitive[] _all =
        {
            new()
            {
                DisplayName = "Mandelbox fold (boxFold + sphereFold + scale)",
                DefaultOutputName = IdMandelbox,
                KifsScale = 2.0,
                Source =
                    "// Classic Mandelbox fold step. scale ~ 2, fixedRadius 1, minRadius 0.5.\n" +
                    "spherefold(boxfold(z, 1.0), 0.5, 1.0) * 2.0 + c",
                Description = "z = scale·sphereFold(boxFold(z)) + c. Try scale -1.5, 2, or 3.",
            },
            new()
            {
                DisplayName = "KIFS Menger fold",
                DefaultOutputName = IdMenger,
                KifsScale = 3.0,
                Source =
                    "// Menger-sponge fold: |x|,|y|,|z| then scale-3 from (1,1,1).\n" +
                    "// if (cond) v = A;  becomes  v = (cond ? A : v) via nested lets.\n" +
                    "let v0 = abs(z) in\n" +
                    "let v1 = (v0.x - v0.y < 0 ? vec(v0.y, v0.x, v0.z) : v0) in\n" +
                    "let v2 = (v1.x - v1.z < 0 ? vec(v1.z, v1.y, v1.x) : v1) in\n" +
                    "let v3 = (v2.y - v2.z < 0 ? vec(v2.x, v2.z, v2.y) : v2) in\n" +
                    "vec(v3.x * 3.0 - 2.0, v3.y * 3.0 - 2.0, v3.z * 3.0)",
                Description = "Sort |components|, scale 3 from (1,1,1). Tile-3 fold.",
            },
            new()
            {
                DisplayName = "KIFS Sierpinski tetra fold",
                DefaultOutputName = IdSierpinski,
                KifsScale = 2.0,
                Source =
                    "// Sierpinski tetrahedron fold: 3 vertex reflections, scale-2 from (1,1,1).\n" +
                    "let v0 = z in\n" +
                    "let v1 = (v0.x + v0.y < 0 ? vec(-v0.y, -v0.x,  v0.z) : v0) in\n" +
                    "let v2 = (v1.x + v1.z < 0 ? vec(-v1.z,  v1.y, -v1.x) : v1) in\n" +
                    "let v3 = (v2.y + v2.z < 0 ? vec( v2.x, -v2.z, -v2.y) : v2) in\n" +
                    "vec(v3.x * 2.0 - 1.0, v3.y * 2.0 - 1.0, v3.z * 2.0 - 1.0)",
                Description = "3 vertex reflections, scale 2 from (1,1,1).",
            },
            new()
            {
                DisplayName = "Mandelbulb power (Vec3.Pow + c)",
                DefaultOutputName = IdBulbPow,
                Source =
                    "// Triplex spherical-power Mandelbulb step. Power 8 is canonical.\n" +
                    "z^8.0 + c",
                Description = "Real Mandelbulb iteration z^n + c. Change 8.0 to try p=2, 4, 16.",
            },
        };

        /// <summary>All primitives in display order.</summary>
        public static IReadOnlyList<UserBulbChainPrimitive> All => _all;

        /// <summary>Lookup by default-output-name. Null when unknown.</summary>
        public static UserBulbChainPrimitive? GetById(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (var p in _all)
                if (p.DefaultOutputName == id) return p;
            return null;
        }

        /// <summary>
        /// Rewrites a primitive's source so every standalone `z` identifier
        /// becomes a reference to <paramref name="priorName"/>. Used when a
        /// primitive is appended after an existing chain step — the chain
        /// runner threads each step's input as the original pixel z, so to
        /// compose folds we must explicitly name the prior output.
        /// </summary>
        public static string RebindZ(string source, string priorName)
        {
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(priorName)) return source;
            return Regex.Replace(source, @"\bz\b", priorName);
        }

        /// <summary>
        /// Builds a worked-example chain: Mandelbox fold + Mandelbulb power.
        /// Second step's `z` token is rebound to the first step's output so
        /// the bulb-power consumes the folded value, not the original pixel z.
        /// </summary>
        public static List<UserBulbChainStep> MandelboxBulbHybrid()
        {
            return new List<UserBulbChainStep>
            {
                new() { OutputName = IdMandelbox, Source = GetById(IdMandelbox)!.Source },
                new() { OutputName = IdBulbPow,   Source = RebindZ(GetById(IdBulbPow)!.Source, IdMandelbox) },
            };
        }

        /// <summary>
        /// Worked-example chain: Menger fold + Mandelbulb power. The KIFS
        /// fold has scale 3 — left unbounded it pushes |z| past the bulb
        /// bailout in one iteration and the raymarcher returns a solid
        /// background. The bulb step contracts the fold output by 0.3 to
        /// keep the orbit in the bulb's working range.
        /// </summary>
        public static List<UserBulbChainStep> MengerBulbHybrid()
        {
            return new List<UserBulbChainStep>
            {
                new() { OutputName = IdMenger, Source = GetById(IdMenger)!.Source },
                new()
                {
                    OutputName = IdBulbPow,
                    Source = "// Contract Menger output so bulb-pow stays under bailout.\n" +
                             "(" + IdMenger + " * 0.3)^8.0 + c",
                },
            };
        }

        /// <summary>
        /// Worked-example chain: Sierpinski fold → axis rotation → scale + offset.
        /// Matches the Kaleidoscopic-IFS step popularised by Knighty / Syntopia
        /// (fold three reflections, rotate, scale-2 with translation offset).
        /// </summary>
        public static List<UserBulbChainStep> KaleidoscopicIfsChain()
        {
            return new List<UserBulbChainStep>
            {
                new()
                {
                    OutputName = IdKifsFold,
                    Source = "// Menger-style fold: |x|,|y|,|z| then sort descending (no scale).\n" +
                             "// A pure isometric fold — the scale lives in the last step so the\n" +
                             "// Scalar KIFS DE (declared scale 3) tracks it exactly.\n" +
                             "let v0 = abs(z) in\n" +
                             "let v1 = (v0.x - v0.y < 0 ? vec(v0.y, v0.x, v0.z) : v0) in\n" +
                             "let v2 = (v1.x - v1.z < 0 ? vec(v1.z, v1.y, v1.x) : v1) in\n" +
                             "let v3 = (v2.y - v2.z < 0 ? vec(v2.x, v2.z, v2.y) : v2) in\n" +
                             "v3",
                },
                new()
                {
                    OutputName = IdKifsRot,
                    Source = "// Per-iteration rotation — the 'kaleidoscopic' twist. This is why the\n" +
                             "// preset needs DE Mode = Scalar KIFS: rotating across the fold's\n" +
                             "// discontinuity planes defeats the numerical-Jacobian DE. Try 0.1..0.8.\n" +
                             "rot(" + IdKifsFold + ", vec(0, 1, 0), 0.3)",
                },
                new()
                {
                    OutputName = IdKifsScale,
                    Source = "// Scale-3 + translation offset. The scale factor here (3) must match\n" +
                             "// the KIFS Scale setting so the running-derivative DE is exact.\n" +
                             IdKifsRot + " * 3.0 - vec(2, 2, 0)",
                },
            };
        }

        /// <summary>Per-iteration linear scale of <see cref="KaleidoscopicIfsChain"/>,
        /// to be declared as FractalParameters.UserBulbKifsScale so the scalar
        /// KIFS distance estimator is exact.</summary>
        public const double KaleidoscopicIfsScale = 3.0;

        /// <summary>Leading fold scale of <see cref="MandelboxBulbHybrid"/> — the
        /// scalar-KIFS DE the editor engages so the fold export isn't degenerate
        /// (#113). Approximate for the trailing bulb-power, but far better than
        /// the numerical Jacobian's blob on the discontinuous fold.</summary>
        public const double MandelboxBulbHybridScale = 2.0;

        /// <summary>Leading fold scale of <see cref="MengerBulbHybrid"/>. See
        /// <see cref="MandelboxBulbHybridScale"/>.</summary>
        public const double MengerBulbHybridScale = 3.0;

        /// <summary>Suggested <c>UserBulbKifsScale</c> for a chain, or 0 when it
        /// has no leading fold (leave the numerical DE). Returns the first fold
        /// primitive's declared scale — the fold dominates the running-derivative
        /// DE, so this makes a pure-fold chain exact and a fold+power hybrid at
        /// least non-degenerate (#113).</summary>
        public static double SuggestedKifsScaleForChain(
            IReadOnlyList<UserBulbChainStep>? chain)
        {
            if (chain == null) return 0.0;
            foreach (var step in chain)
            {
                var prim = GetById(step.OutputName);
                if (prim != null && prim.KifsScale > 0.0) return prim.KifsScale;
            }
            return 0.0;
        }
    }
}
