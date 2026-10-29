// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Models/MathPlaneSceneEdit.cs
//
// #1549 (Math Plane S2) — UI-free scene edits for the Control Center section and
// the on-canvas editor: pointer <-> world mapping, handle hit-testing, moving a
// handle, adding / removing / reordering objects, and grid snapping. Every edit
// returns a new MathPlaneScene; the caller writes Serialize() back to
// FractalParameters.MathPlaneScene, so the edit is an ordinary param change
// (region snapshot, --batch and the Command builder follow it).
//
// Handles: a point's position; a vector's tip (index 0) and tail (index 1, the
// origin unless it has a `from`); a segment's two ends; a polyline's vertices.
// Dragging a vector's tail moves the whole vector (its components are kept);
// dragging the tip changes the components.

using System;
using System.Collections.Generic;
using System.Linq;
using FracturingFog.ViewState;

namespace FracturingFog.Models
{
    /// <summary>A draggable point of an object: <see cref="Point"/> indexes the
    /// object's handles (see file header).</summary>
    public readonly record struct MathPlaneHandle(int ObjectIndex, int Point);

    public static class MathPlaneSceneEdit
    {
        /// <summary>Hit radius for handles, in client pixels.</summary>
        public const double HitRadiusPixels = 9.0;

        // ── View mapping (the MathPlaneCalculator mapping, in client pixels) ──

        /// <summary>World units per client pixel: the plane pitch 4 / width / zoom.</summary>
        public static double Pitch(int clientWidth, double zoom)
            => (4.0 / Math.Max(1, clientWidth)) / Math.Max(1e-300, zoom);

        /// <summary>Client pixel → world (y-up: world y = -plane y).</summary>
        public static (double X, double Y) ClientToWorld(double x, double y, int clientWidth, int clientHeight,
                                                         double centerX, double centerY, double zoom)
        {
            double pitch = Pitch(clientWidth, zoom);
            return (centerX + (x - clientWidth * 0.5) * pitch, -(centerY + (y - clientHeight * 0.5) * pitch));
        }

        /// <summary>World → client pixel; the inverse of <see cref="ClientToWorld"/>.</summary>
        public static (double X, double Y) WorldToClient(double wx, double wy, int clientWidth, int clientHeight,
                                                         double centerX, double centerY, double zoom)
        {
            double pitch = Pitch(clientWidth, zoom);
            return ((wx - centerX) / pitch + clientWidth * 0.5, (-wy - centerY) / pitch + clientHeight * 0.5);
        }

        /// <summary>The minor grid step the plane draws for a view <paramref name="worldWidth"/>
        /// wide (the major step is the 1-2-5 step near a width / 8; minor is the next
        /// 1-2-5 step below a fifth of it), the Shift-drag snap.</summary>
        public static double MinorStep(double worldWidth)
        {
            var (s, e) = GridAxis.NiceStep(worldWidth / 8.0);
            double major = s * Math.Pow(10, e);
            var (ms, me) = GridAxis.NiceStep(major / 5 * 0.99);
            return ms * Math.Pow(10, me);
        }

        /// <summary><paramref name="v"/> rounded to the nearest multiple of <paramref name="step"/>.</summary>
        public static double Snap(double v, double step)
            => step > 0 && double.IsFinite(step) ? Math.Round(v / step) * step : v;

        // ── Handles ──

        public static int HandleCount(MathPlaneObject o) => o.Kind switch
        {
            MathPlaneObjectKind.Point => 1,
            MathPlaneObjectKind.Vector => 2,
            MathPlaneObjectKind.Segment => 2,
            MathPlaneObjectKind.Curve => 0,   // a curve is edited by its expression
            _ => o.PointCount,
        };

        /// <summary>World position of a handle.</summary>
        public static (double X, double Y) HandlePosition(MathPlaneObject o, int point)
        {
            if (o.Kind == MathPlaneObjectKind.Vector)
            {
                var (tx, ty) = o.Tail;
                return point == 0 ? (tx + o.Coords[0], ty + o.Coords[1]) : (tx, ty);
            }
            return o.Point(point);
        }

        /// <summary>The visible handle nearest (<paramref name="wx"/>, <paramref name="wy"/>)
        /// within <paramref name="tolerance"/> world units; ties go to the object drawn
        /// last (on top). Null when none is close enough.</summary>
        public static MathPlaneHandle? HitTest(MathPlaneScene scene, double wx, double wy, double tolerance)
        {
            MathPlaneHandle? best = null;
            double bestD = tolerance * tolerance;
            for (int i = 0; i < scene.Objects.Count; i++)
            {
                var o = scene.Objects[i];
                if (!o.Visible) continue;
                for (int k = 0; k < HandleCount(o); k++)
                {
                    var (hx, hy) = HandlePosition(o, k);
                    double d = (hx - wx) * (hx - wx) + (hy - wy) * (hy - wy);
                    if (d <= bestD) { bestD = d; best = new MathPlaneHandle(i, k); }
                }
            }
            return best;
        }

        /// <summary>The scene with handle <paramref name="h"/> moved to (x, y).</summary>
        public static MathPlaneScene MoveHandle(MathPlaneScene scene, MathPlaneHandle h, double x, double y)
        {
            var o = scene.Objects[h.ObjectIndex];
            var c = o.Coords.ToArray();
            MathPlaneObject moved;
            if (o.Kind == MathPlaneObjectKind.Vector)
            {
                if (h.Point == 0)
                {
                    var (tx, ty) = o.Tail;
                    c[0] = x - tx; c[1] = y - ty;
                    moved = o with { Coords = c };
                }
                else
                {
                    // Tail: translate the vector, keep its components.
                    moved = o with { Coords = new[] { c[0], c[1], x, y }, HasFrom = true };
                }
            }
            else
            {
                c[2 * h.Point] = x; c[2 * h.Point + 1] = y;
                moved = o with { Coords = c };
            }
            return Replace(scene, h.ObjectIndex, moved);
        }

        // ── Object list ──

        public static MathPlaneScene Replace(MathPlaneScene scene, int index, MathPlaneObject o)
        {
            var list = scene.Objects.ToList();
            list[index] = o;
            return scene with { Objects = list };
        }

        public static MathPlaneScene Remove(MathPlaneScene scene, int index)
        {
            var list = scene.Objects.ToList();
            list.RemoveAt(index);
            return scene with { Objects = list };
        }

        /// <summary>Move object <paramref name="index"/> by <paramref name="delta"/> places
        /// (draw order: later is on top). Out-of-range moves are clamped.</summary>
        public static MathPlaneScene Reorder(MathPlaneScene scene, int index, int delta)
        {
            var list = scene.Objects.ToList();
            int to = Math.Clamp(index + delta, 0, list.Count - 1);
            if (to == index) return scene;
            var o = list[index];
            list.RemoveAt(index);
            list.Insert(to, o);
            return scene with { Objects = list };
        }

        /// <summary>Add an object of <paramref name="kind"/> at (x, y) with a fresh name
        /// (A, B, ... for points; u, v, w, ... for vectors; s1.. / p1..) and the next
        /// palette colour. Returns the scene and the new object's index.</summary>
        public static (MathPlaneScene Scene, int Index) Add(MathPlaneScene scene, MathPlaneObjectKind kind,
                                                            double x, double y, double size = 1.0)
        {
            string name = FreshName(scene, kind);
            uint color = MathPlaneScene.DefaultPalette[scene.Objects.Count % MathPlaneScene.DefaultPalette.Length];
            double[] coords = kind switch
            {
                MathPlaneObjectKind.Point => new[] { x, y },
                MathPlaneObjectKind.Vector => new[] { size, size * 0.5, x, y },
                MathPlaneObjectKind.Segment => new[] { x, y, x + size, y + size * 0.5 },
                _ => new[] { x, y, x + size, y + size * 0.5, x + 2 * size, y },
            };
            bool hasFrom = kind == MathPlaneObjectKind.Vector && (x != 0 || y != 0);
            if (kind == MathPlaneObjectKind.Vector && !hasFrom) coords = new[] { size, size * 0.5 };
            var obj = new MathPlaneObject(kind, name, coords, color, HasFrom: hasFrom);
            var list = scene.Objects.ToList();
            list.Add(obj);
            return (scene with { Objects = list }, list.Count - 1);
        }

        /// <summary>#1550 — add a curve with a starter expression: explicit sin(x),
        /// parametric unit circle (cos t, sin t) over t 0..2pi, polar cardioid
        /// 1 + cos(theta). Returns the scene and the new object's index.</summary>
        public static (MathPlaneScene Scene, int Index) AddCurve(MathPlaneScene scene, MathPlaneCurveKind kind)
        {
            string name = FreshName(scene, MathPlaneObjectKind.Curve);
            uint color = MathPlaneScene.DefaultPalette[scene.Objects.Count % MathPlaneScene.DefaultPalette.Length];
            var (sources, domain) = kind switch
            {
                MathPlaneCurveKind.Explicit => (new[] { "sin(x)" }, Array.Empty<double>()),
                MathPlaneCurveKind.Parametric => (new[] { "cos(t)", "sin(t)" }, new[] { 0.0, 2 * Math.PI }),
                _ => (new[] { "1 + cos(theta)" }, Array.Empty<double>()),
            };
            var obj = new MathPlaneObject(MathPlaneObjectKind.Curve, name, domain, color,
                                          CurveKind: kind, Sources: sources);
            var list = scene.Objects.ToList();
            list.Add(obj);
            return (scene with { Objects = list }, list.Count - 1);
        }

        /// <summary>A name not used by any object in the scene.</summary>
        public static string FreshName(MathPlaneScene scene, MathPlaneObjectKind kind)
        {
            var used = new HashSet<string>(scene.Objects.Select(o => o.Name), StringComparer.Ordinal);
            IEnumerable<string> candidates = kind switch
            {
                MathPlaneObjectKind.Point => Letters("ABCDEFGHKLMNPQRSTUVWXYZ"),
                MathPlaneObjectKind.Vector => Letters("uvwabcdefgh"),
                MathPlaneObjectKind.Segment => Numbered("s"),
                MathPlaneObjectKind.Curve => Letters("fghkqr"),
                _ => Numbered("p"),
            };
            return candidates.First(n => !used.Contains(n));

            static IEnumerable<string> Letters(string set)
            {
                foreach (char c in set) yield return c.ToString();
                for (int i = 1; ; i++) foreach (char c in set) yield return c + i.ToString();
            }
            static IEnumerable<string> Numbered(string prefix)
            {
                for (int i = 1; ; i++) yield return prefix + i;
            }
        }

        /// <summary>True when <paramref name="name"/> is a valid identifier not used by
        /// another object than <paramref name="exceptIndex"/>.</summary>
        public static bool IsFreeName(MathPlaneScene scene, string name, int exceptIndex)
            => MathPlaneScene.IsName(name)
               && !scene.Objects.Where((o, i) => i != exceptIndex).Any(o => o.Name == name);
    }
}
