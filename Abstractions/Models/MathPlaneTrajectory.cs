// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Models/MathPlaneTrajectory.cs
//
// #1993 — the Math Plane 2D `trajectory` statement: the path of ONE launch of a
// gravitational-scattering family, drawn as a polyline. The path is traced in Engine
// (Engine/Scattering/ScatterPath.cs) by the family's own scene code (the same
// LaunchState + ScatteringIntegrator.Trace the fractal renders with), so the drawn
// end is the gate the fractal shows at that launch. This file is the UI-free part:
// the statement's families, launch rules and keys, and the mapping onto
// FractalParameters that the Engine scene is built from.
//
//   trajectory NAME henonheiles A B [tangential|radial|section|ANGLE] [E=..] [tmax=..] [tol=..]   [options]
//   trajectory NAME threebody   X Y [prograde|retrograde|radial|ANGLE] [mu=..] [C=..] [crash=..]
//                                   [escape=..] [tmax=..] [tol=..]                                [options]
//   trajectory NAME magbh       A B [ANGLE|kick] [b=..] [E=..] [l=..] [branch=1|-1] [escape=..]
//                                   [tmax=..] [tol=..]                                            [options]
//
// A B are the family's own launch coordinates, as its fractal view reads them:
//   henonheiles  plane launch (x, y), speed from E, direction tangential (anticlockwise),
//                radial or ANGLE degrees; section: (y, py) on x = 0, px >= 0 (#1942 Section view);
//   threebody    (x, y) in the rotating frame, speed from C (#1943 Fates view);
//   magbh        meridional (R, z), ANGLE degrees from +R towards +z (#1944 Meridional view;
//                R < 0 draws the mirrored half, as the fractal does); kick: (rho0, E), the
//                Bautista & Vega kick launch (#1944 Kick view).
// The path is drawn in the family's physical plane: (x, y) for Henon-Heiles and the
// three-body problem, the meridional (R, z) plane for the magnetised black hole.
// Keys left out take the family's FractalParameters defaults; the Engine scene clamps
// every value exactly as the fractal does.
//
// Stored as a MathPlaneObject of kind Trajectory: Coords = (A, B); Sources = family,
// launch token, then KEY=VALUE pairs in the order written (keys canonical case, values
// as written by MathPlaneScene.Num), so text -> objects -> text is exact.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace FracturingFog.Models
{
    /// <summary>#1993 — families the <c>trajectory</c> statement can draw.</summary>
    public enum MathPlaneTrajectoryFamily { HenonHeiles, ThreeBody, MagnetisedBlackHole }

    /// <summary>#1993 — the trajectory statement's families, launch rules and keys (see file header).</summary>
    public static class MathPlaneTrajectory
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>Statement token of each family.</summary>
        public static string Token(MathPlaneTrajectoryFamily f) => f switch
        {
            MathPlaneTrajectoryFamily.HenonHeiles => "henonheiles",
            MathPlaneTrajectoryFamily.ThreeBody => "threebody",
            _ => "magbh",
        };

        public static bool TryFamily(string token, out MathPlaneTrajectoryFamily f)
        {
            switch (token.ToLowerInvariant())
            {
                case "henonheiles": f = MathPlaneTrajectoryFamily.HenonHeiles; return true;
                case "threebody": f = MathPlaneTrajectoryFamily.ThreeBody; return true;
                case "magbh": f = MathPlaneTrajectoryFamily.MagnetisedBlackHole; return true;
                default: f = default; return false;
            }
        }

        /// <summary>The fractal type whose scene traces the family.</summary>
        public static FractalType TypeOf(MathPlaneTrajectoryFamily f) => f switch
        {
            MathPlaneTrajectoryFamily.HenonHeiles => FractalType.HenonHeilesBasins,
            MathPlaneTrajectoryFamily.ThreeBody => FractalType.ThreeBodyBasins,
            _ => FractalType.MagnetisedBlackHole,
        };

        /// <summary>Named launch rules (a number is a fixed angle in degrees for every family).</summary>
        public static IReadOnlyList<string> Launches(MathPlaneTrajectoryFamily f) => f switch
        {
            MathPlaneTrajectoryFamily.HenonHeiles => new[] { "tangential", "radial", "section" },
            MathPlaneTrajectoryFamily.ThreeBody => new[] { "prograde", "retrograde", "radial" },
            _ => new[] { "kick" },
        };

        /// <summary>The launch token used when none is written: the family's default launch.</summary>
        public static string DefaultLaunch(MathPlaneTrajectoryFamily f) => f switch
        {
            MathPlaneTrajectoryFamily.HenonHeiles => "tangential",
            MathPlaneTrajectoryFamily.ThreeBody => "prograde",
            _ => "0",
        };

        /// <summary>Keys each family takes, in canonical case.</summary>
        public static IReadOnlyList<string> Keys(MathPlaneTrajectoryFamily f) => f switch
        {
            MathPlaneTrajectoryFamily.HenonHeiles => new[] { "E", "tmax", "tol" },
            MathPlaneTrajectoryFamily.ThreeBody => new[] { "mu", "C", "crash", "escape", "tmax", "tol" },
            _ => new[] { "b", "E", "l", "branch", "escape", "tmax", "tol" },
        };

        /// <summary>Parse the statement's body after the name: tokens <paramref name="toks"/> from
        /// <paramref name="start"/> (the family) up to the options. Returns the coords, the sources
        /// and the index of the first unread token (the options).</summary>
        internal static bool TryParseBody(List<(string Text, int Col)> toks, int start,
                                          out double[] coords, out List<string> sources, out int next,
                                          out int badTok, out string msg)
        {
            coords = Array.Empty<double>(); sources = new List<string>(); next = start;
            badTok = start; msg = "";
            if (start >= toks.Count || !TryFamily(toks[start].Text, out var fam))
            {
                badTok = Math.Min(start, toks.Count - 1);
                msg = start < toks.Count
                    ? $"unknown trajectory family '{toks[start].Text}' (expected henonheiles, threebody or magbh)"
                    : "'trajectory' needs a family: henonheiles, threebody or magbh";
                return false;
            }
            sources.Add(Token(fam));
            int i = start + 1;
            if (i + 1 >= toks.Count || !MathPlaneScene.TryNum(toks[i].Text, out double a) || !MathPlaneScene.TryNum(toks[i + 1].Text, out double b))
            {
                badTok = Math.Min(i, toks.Count - 1);
                msg = "a trajectory needs its launch point: two numbers after the family";
                return false;
            }
            coords = new[] { a, b };
            i += 2;

            // Optional launch rule: a named rule or a number (degrees).
            string launch = DefaultLaunch(fam);
            if (i < toks.Count && !toks[i].Text.Contains('=') && !toks[i].Text.StartsWith('#'))
            {
                string t = toks[i].Text;
                if (MathPlaneScene.TryNum(t, out double ang)) { launch = MathPlaneScene.Num(ang); i++; }
                else if (Launches(fam).Contains(t.ToLowerInvariant())) { launch = t.ToLowerInvariant(); i++; }
                else if (!IsOptionWord(t))
                {
                    badTok = i;
                    msg = $"unknown {Token(fam)} launch '{t}' (expected {string.Join(", ", Launches(fam))} or an angle in degrees)";
                    return false;
                }
            }
            sources.Add(launch);

            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (i < toks.Count && toks[i].Text.Contains('='))
            {
                string t = toks[i].Text;
                int eq = t.IndexOf('=');
                string key = t[..eq], val = t[(eq + 1)..];
                string? canon = Keys(fam).FirstOrDefault(k => k.Equals(key, StringComparison.OrdinalIgnoreCase));
                if (canon == null)
                {
                    badTok = i;
                    msg = $"unknown {Token(fam)} key '{key}' (expected {string.Join(", ", Keys(fam))})";
                    return false;
                }
                if (!seen.Add(canon)) { badTok = i; msg = $"'{canon}' is given twice"; return false; }
                if (!MathPlaneScene.TryNum(val, out double v))
                { badTok = i; msg = $"'{canon}' needs a number"; return false; }
                if (canon == "branch" && v is not (1 or -1))
                { badTok = i; msg = "'branch' is 1 (the l+ circular orbit) or -1 (l-)"; return false; }
                if (canon is "tmax" or "tol" or "crash" or "escape" && !(v > 0))
                { badTok = i; msg = $"'{canon}' must be above 0"; return false; }
                sources.Add(canon + "=" + MathPlaneScene.Num(v));
                i++;
            }
            next = i;
            return true;
        }

        private static bool IsOptionWord(string t)
            => t.Equals("hidden", StringComparison.OrdinalIgnoreCase) || t.Equals("nolabel", StringComparison.OrdinalIgnoreCase);

        /// <summary>The object's family (Sources[0]).</summary>
        public static MathPlaneTrajectoryFamily FamilyOf(MathPlaneObject o)
            => o.Expressions.Count > 0 && TryFamily(o.Expressions[0], out var f) ? f : MathPlaneTrajectoryFamily.HenonHeiles;

        /// <summary>The object's launch token (Sources[1]).</summary>
        public static string LaunchOf(MathPlaneObject o)
            => o.Expressions.Count > 1 ? o.Expressions[1] : DefaultLaunch(FamilyOf(o));

        /// <summary>The object's KEY=VALUE pairs.</summary>
        public static IEnumerable<(string Key, double Value)> KeysOf(MathPlaneObject o)
        {
            for (int k = 2; k < o.Expressions.Count; k++)
            {
                string s = o.Expressions[k];
                int eq = s.IndexOf('=');
                if (eq > 0 && double.TryParse(s[(eq + 1)..], NumberStyles.Float, Inv, out double v))
                    yield return (s[..eq], v);
            }
        }

        /// <summary>True when the launch coordinates are a position in the drawn plane (so the
        /// path starts there and the start can be dragged). False for the Hénon–Heiles section
        /// launch (y, py) and the magnetised-hole kick launch (rho0, E).</summary>
        public static bool LaunchIsPosition(MathPlaneObject o)
            => LaunchOf(o) is not ("section" or "kick");

        /// <summary>Text after the name: family, launch point, launch, keys.</summary>
        public static string BodyText(MathPlaneObject o)
        {
            var parts = new List<string> { o.Expressions.Count > 0 ? o.Expressions[0] : "henonheiles" };
            foreach (var v in o.Coords) parts.Add(MathPlaneScene.Num(v));
            for (int k = 1; k < o.Expressions.Count; k++) parts.Add(o.Expressions[k]);
            return string.Join(" ", parts);
        }

        /// <summary>Set up <paramref name="p"/> (a fresh default <see cref="FractalParameters"/>, or a
        /// copy of the family's own) for the family's scene: view, launch rule and every key. The
        /// launch point itself is <see cref="MathPlaneObject.Coords"/>.</summary>
        public static void Apply(MathPlaneObject o, FractalParameters p)
        {
            var fam = FamilyOf(o);
            string launch = LaunchOf(o);
            bool angle = double.TryParse(launch, NumberStyles.Float, Inv, out double deg);
            switch (fam)
            {
                case MathPlaneTrajectoryFamily.HenonHeiles:
                    p.HenonHeilesView = launch == "section" ? HenonHeilesView.Section : HenonHeilesView.Plane;
                    if (angle) { p.HenonHeilesLaunch = HenonHeilesLaunch.FixedAngle; p.HenonHeilesLaunchAngle = deg; }
                    else if (launch == "radial") p.HenonHeilesLaunch = HenonHeilesLaunch.Radial;
                    else if (launch == "tangential") p.HenonHeilesLaunch = HenonHeilesLaunch.Tangential;
                    foreach (var (k, v) in KeysOf(o))
                        switch (k)
                        {
                            case "E": p.HenonHeilesEnergy = v; break;
                            case "tmax": p.HenonHeilesMaxTime = v; break;
                            case "tol": p.HenonHeilesTolerance = v; break;
                        }
                    break;
                case MathPlaneTrajectoryFamily.ThreeBody:
                    p.ThreeBodyView = ThreeBodyView.Fates;
                    if (angle) { p.ThreeBodyLaunch = ThreeBodyLaunch.FixedAngle; p.ThreeBodyLaunchAngle = deg; }
                    else p.ThreeBodyLaunch = launch switch
                    {
                        "retrograde" => ThreeBodyLaunch.Retrograde,
                        "radial" => ThreeBodyLaunch.Radial,
                        _ => ThreeBodyLaunch.Prograde,
                    };
                    foreach (var (k, v) in KeysOf(o))
                        switch (k)
                        {
                            case "mu": p.ThreeBodyMassRatio = v; break;
                            case "C": p.ThreeBodyJacobi = v; break;
                            case "crash": p.ThreeBodyCrashRadius = v; break;
                            case "escape": p.ThreeBodyEscapeRadius = v; break;
                            case "tmax": p.ThreeBodyMaxTime = v; break;
                            case "tol": p.ThreeBodyTolerance = v; break;
                        }
                    break;
                default:
                    if (launch == "kick") p.MagBhView = MagnetisedBhView.Kick;
                    else { p.MagBhView = MagnetisedBhView.Meridional; p.MagBhLaunchAngle = angle ? deg : 0.0; }
                    foreach (var (k, v) in KeysOf(o))
                        switch (k)
                        {
                            case "b": p.MagBhField = v; break;
                            case "E": p.MagBhEnergy = v; break;
                            case "l": p.MagBhAngularMomentum = v; break;
                            case "branch": p.MagBhBranch = v < 0 ? MagnetisedBhBranch.Minus : MagnetisedBhBranch.Plus; break;
                            case "escape": p.MagBhEscapeHeight = v; break;
                            case "tmax": p.MagBhMaxTime = v; break;
                            case "tol": p.MagBhTolerance = v; break;
                        }
                    break;
            }
        }

        /// <summary>#1993 — the trajectory of view point (<paramref name="a"/>, <paramref name="b"/>)
        /// of a scattering family with params <paramref name="p"/>, every key written out so the path
        /// is the launch the fractal traced there. Null when <paramref name="type"/> has no
        /// trajectory (not one of the three families, or the three-body Lagrange-basin view, which
        /// is Newton's method rather than motion).</summary>
        public static MathPlaneObject? FromFamily(FractalType type, FractalParameters p, double a, double b,
                                                  string name = "path", uint color = 0xFFE69F00)
        {
            string N(double v) => MathPlaneScene.Num(v);
            List<string> src;
            switch (type)
            {
                case FractalType.HenonHeilesBasins:
                {
                    string launch = p.HenonHeilesView == HenonHeilesView.Section ? "section"
                        : p.HenonHeilesLaunch switch
                        {
                            HenonHeilesLaunch.Radial => "radial",
                            HenonHeilesLaunch.FixedAngle => N(p.HenonHeilesLaunchAngle),
                            _ => "tangential",
                        };
                    src = new() { "henonheiles", launch, "E=" + N(p.HenonHeilesEnergy),
                                  "tmax=" + N(p.HenonHeilesMaxTime), "tol=" + N(p.HenonHeilesTolerance) };
                    break;
                }
                case FractalType.ThreeBodyBasins when p.ThreeBodyView == ThreeBodyView.Fates:
                {
                    string launch = p.ThreeBodyLaunch switch
                    {
                        ThreeBodyLaunch.Retrograde => "retrograde",
                        ThreeBodyLaunch.Radial => "radial",
                        ThreeBodyLaunch.FixedAngle => N(p.ThreeBodyLaunchAngle),
                        _ => "prograde",
                    };
                    src = new() { "threebody", launch, "mu=" + N(p.ThreeBodyMassRatio), "C=" + N(p.ThreeBodyJacobi),
                                  "crash=" + N(p.ThreeBodyCrashRadius), "escape=" + N(p.ThreeBodyEscapeRadius),
                                  "tmax=" + N(p.ThreeBodyMaxTime), "tol=" + N(p.ThreeBodyTolerance) };
                    break;
                }
                case FractalType.MagnetisedBlackHole:
                {
                    string launch = p.MagBhView == MagnetisedBhView.Kick ? "kick" : N(p.MagBhLaunchAngle);
                    src = new() { "magbh", launch, "b=" + N(p.MagBhField), "E=" + N(p.MagBhEnergy),
                                  "l=" + N(p.MagBhAngularMomentum),
                                  "branch=" + (p.MagBhBranch == MagnetisedBhBranch.Minus ? "-1" : "1"),
                                  "escape=" + N(p.MagBhEscapeHeight), "tmax=" + N(p.MagBhMaxTime),
                                  "tol=" + N(p.MagBhTolerance) };
                    break;
                }
                default:
                    return null;
            }
            return new MathPlaneObject(MathPlaneObjectKind.Trajectory, name, new[] { a, b }, color, Sources: src);
        }
    }
}
