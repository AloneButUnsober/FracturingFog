// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Models/MathPlane3DSceneEdit.cs
//
// #1716 (Math Plane 3D T4, #1556) — the pure edits behind the Math Plane 3D
// Control Center section and its on-canvas editor. Design:
// Docs/Technical/MathPlane3D-DesignPlan.md §3.6, §4.
//
//  - Scene edits: add (one kind at a time, with a fresh name), replace, remove, reorder.
//  - Handles: a point, a vector's tip (and tail when it has one), a segment's ends and
//    a polyline's vertices. They are hit-tested on screen where they are drawn (through
//    the frame's map).
//  - Dragging a handle moves it in the plane facing the camera through its drawn place:
//    the pointer's ray meets that plane, so the handle's depth along the view is kept.
//    The world point goes back through the map's inverse (no move when the map is
//    singular). Shift snaps each coordinate to the grid step for the depth; Alt keeps
//    only the largest change from the drag's start (the nearest axis).
//  - Orbiting: the input controller's right-drag rule (π per client width / height,
//    drag down looks up, φ kept inside (0.01, π − 0.01)).

using System;
using System.Collections.Generic;
using System.Linq;

namespace FracturingFog.Models
{
    /// <summary>#1716 — a draggable handle: object index and point (see the file header).</summary>
    public readonly record struct MathPlane3DHandle(int ObjectIndex, int Point);

    public static class MathPlane3DSceneEdit
    {
        /// <summary>Hit radius for handles, in client pixels (as 2D).</summary>
        public const double HitRadiusPixels = MathPlaneSceneEdit.HitRadiusPixels;

        // ── Handles ──

        public static int HandleCount(MathPlaneObject o) => o.Kind switch
        {
            MathPlaneObjectKind.Point => 1,
            MathPlaneObjectKind.Vector => o.HasFrom ? 2 : 1,
            MathPlaneObjectKind.Segment => 2,
            MathPlaneObjectKind.Polyline => o.Coords.Count / 3,
            _ => 0,
        };

        /// <summary>Handle <paramref name="point"/> of <paramref name="o"/> in scene coordinates.
        /// A vector: 0 = tip, 1 = tail.</summary>
        public static (double X, double Y, double Z) HandlePosition(MathPlaneObject o, int point)
            => o.Kind == MathPlaneObjectKind.Vector
                ? (point == 0 ? MathPlane3DScene.Tip(o) : MathPlane3DScene.Tail(o))
                : MathPlane3DScene.Point(o, point);

        /// <summary>The visible handle drawn nearest the pointer, within
        /// <paramref name="tolerancePixels"/>; on a tie, the one nearer the camera.</summary>
        public static MathPlane3DHandle? HitTest(MathPlane3DScene scene, MathPlane3DCamera cam, Mat3 map,
                                                 double px, double py, double tolerancePixels = HitRadiusPixels)
        {
            MathPlane3DHandle? best = null;
            double bestD = tolerancePixels * tolerancePixels, bestZ = double.MaxValue;
            for (int i = 0; i < scene.Objects.Count; i++)
            {
                var o = scene.Objects[i];
                if (!o.Visible) continue;
                for (int k = 0; k < HandleCount(o); k++)
                {
                    var w = map.Apply(HandlePosition(o, k));
                    if (cam.Project(w.X, w.Y, w.Z) is not { } p) continue;
                    double d = (p.X - px) * (p.X - px) + (p.Y - py) * (p.Y - py);
                    double z = 1 / p.InvZ;
                    if (d < bestD || (d == bestD && z < bestZ)) { best = new MathPlane3DHandle(i, k); bestD = d; bestZ = z; }
                }
            }
            return best;
        }

        /// <summary>Where the ray through screen point (<paramref name="px"/>, <paramref name="py"/>)
        /// meets the plane facing the camera through <paramref name="through"/> (world). Null when
        /// that plane is behind the camera.</summary>
        public static (double X, double Y, double Z)? OnViewPlane(MathPlane3DCamera cam, (double X, double Y, double Z) through,
                                                                   double px, double py)
        {
            var f = cam.Forward;
            double depth = (through.X - cam.Eye.X) * f.X + (through.Y - cam.Eye.Y) * f.Y + (through.Z - cam.Eye.Z) * f.Z;
            if (!(depth > MathPlane3DCamera.Near)) return null;
            var d = cam.RayDirection(px, py);
            double along = d.X * f.X + d.Y * f.Y + d.Z * f.Z;   // 1 by construction
            double t = depth / along;
            return (cam.Eye.X + d.X * t, cam.Eye.Y + d.Y * t, cam.Eye.Z + d.Z * t);
        }

        /// <summary>The grid step to snap to at view depth <paramref name="depth"/>: the 2D
        /// editor's minor step for the visible width there.</summary>
        /// <summary>The axis (0 = x, 1 = y, 2 = z) along which <paramref name="to"/> differs most
        /// from <paramref name="from"/>; -1 when they are equal.</summary>
        public static int DominantAxis((double X, double Y, double Z) from, (double X, double Y, double Z) to)
        {
            double ax = Math.Abs(to.X - from.X), ay = Math.Abs(to.Y - from.Y), az = Math.Abs(to.Z - from.Z);
            if (ax == 0 && ay == 0 && az == 0) return -1;
            return ax >= ay && ax >= az ? 0 : ay >= az ? 1 : 2;
        }

        public static double SnapStep(MathPlane3DCamera cam, double depth)
            => MathPlaneSceneEdit.MinorStep(depth * cam.Width / cam.Focal);

        /// <summary>The scene position a drag of handle <paramref name="start"/> (scene coordinates,
        /// where the drag began) reaches at screen point (<paramref name="px"/>, <paramref name="py"/>):
        /// on the camera-facing plane through its drawn place, back through the map. Shift
        /// (<paramref name="snap"/>) rounds each coordinate to the grid; Alt
        /// (<paramref name="axisLock"/>) keeps only one coordinate's change: axis
        /// <paramref name="lockAxis"/> (0 = x, 1 = y, 2 = z) when given, else the largest change.
        /// Null when the map is singular or the plane is behind the camera.</summary>
        public static (double X, double Y, double Z)? DragTo(MathPlane3DCamera cam, Mat3 map, (double X, double Y, double Z) start,
                                                             double px, double py, bool snap = false, bool axisLock = false,
                                                             int lockAxis = -1)
        {
            var drawn = map.Apply(start);
            if (OnViewPlane(cam, drawn, px, py) is not { } w) return null;
            (double X, double Y, double Z) s;
            if (map.IsIdentity) s = w;
            else if (map.Inverse(1e-12) is { } inv) s = inv.Apply(w);
            else return null;
            if (axisLock)
            {
                int a = lockAxis is >= 0 and <= 2 ? lockAxis : DominantAxis(start, s);
                s = a == 0 ? (s.X, start.Y, start.Z)
                  : a == 1 ? (start.X, s.Y, start.Z)
                  : (start.X, start.Y, s.Z);
            }
            if (snap)
            {
                var v = cam.View(drawn.X, drawn.Y, drawn.Z);
                double step = SnapStep(cam, v.Z);
                s = axisLock
                    ? (s.X == start.X ? s.X : MathPlaneSceneEdit.Snap(s.X, step),
                       s.Y == start.Y ? s.Y : MathPlaneSceneEdit.Snap(s.Y, step),
                       s.Z == start.Z ? s.Z : MathPlaneSceneEdit.Snap(s.Z, step))
                    : (MathPlaneSceneEdit.Snap(s.X, step), MathPlaneSceneEdit.Snap(s.Y, step), MathPlaneSceneEdit.Snap(s.Z, step));
            }
            return s;
        }

        /// <summary>The scene with handle <paramref name="h"/> at (x, y, z). Moving a vector's tip
        /// changes its components; moving its tail keeps the tip in place.</summary>
        public static MathPlane3DScene MoveHandle(MathPlane3DScene scene, MathPlane3DHandle h, double x, double y, double z)
        {
            var o = scene.Objects[h.ObjectIndex];
            var c = o.Coords.ToArray();
            if (o.Kind == MathPlaneObjectKind.Vector)
            {
                var tail = MathPlane3DScene.Tail(o);
                if (h.Point == 0) { c[0] = x - tail.X; c[1] = y - tail.Y; c[2] = z - tail.Z; }
                else
                {
                    var tip = MathPlane3DScene.Tip(o);
                    c[0] = tip.X - x; c[1] = tip.Y - y; c[2] = tip.Z - z;
                    c[3] = x; c[4] = y; c[5] = z;
                }
            }
            else { c[3 * h.Point] = x; c[3 * h.Point + 1] = y; c[3 * h.Point + 2] = z; }
            return Replace(scene, h.ObjectIndex, o with { Coords = c });
        }

        // ── Orbit ──

        /// <summary>The camera angles after a drag of (<paramref name="dx"/>, <paramref name="dy"/>)
        /// client pixels from (<paramref name="theta0"/>, <paramref name="phi0"/>): the input
        /// controller's right-drag orbit.</summary>
        public static (double Theta, double Phi) Orbit(double theta0, double phi0, double dx, double dy, int clientWidth, int clientHeight)
        {
            double dTheta = dx / Math.Max(1, clientWidth) * Math.PI;
            double dPhi = -dy / Math.Max(1, clientHeight) * Math.PI;
            double theta = Math.IEEERemainder(theta0 + dTheta, 2 * Math.PI);
            double phi = Math.Clamp(phi0 + dPhi, 0.01, Math.PI - 0.01);
            return (theta, phi);
        }

        // ── Scene edits ──

        public static MathPlane3DScene Replace(MathPlane3DScene scene, int index, MathPlaneObject o)
        {
            var list = scene.Objects.ToList();
            list[index] = o;
            return scene with { Objects = list };
        }

        public static MathPlane3DScene Remove(MathPlane3DScene scene, int index)
        {
            var list = scene.Objects.ToList();
            list.RemoveAt(index);
            return scene with { Objects = list };
        }

        /// <summary>Move object <paramref name="index"/> by <paramref name="delta"/> places (clamped).</summary>
        public static MathPlane3DScene Reorder(MathPlane3DScene scene, int index, int delta)
        {
            var list = scene.Objects.ToList();
            int to = Math.Clamp(index + delta, 0, list.Count - 1);
            var o = list[index];
            list.RemoveAt(index);
            list.Insert(to, o);
            return scene with { Objects = list };
        }

        /// <summary>The kinds the Add buttons make (a curve is a space curve; a surface is z = f(x, y)).</summary>
        public enum AddKind { Point, Vector, Segment, Polyline, Curve, Surface }

        /// <summary>A new object of <paramref name="kind"/> near <paramref name="at"/>, sized
        /// <paramref name="size"/>, with a fresh name and the next palette colour.</summary>
        public static (MathPlane3DScene Scene, int Index) Add(MathPlane3DScene scene, AddKind kind,
                                                              (double X, double Y, double Z) at, double size = 1)
        {
            uint color = MathPlaneScene.DefaultPalette[scene.Objects.Count % MathPlaneScene.DefaultPalette.Length];
            var (x, y, z) = at;
            double s = size;
            MathPlaneObject o = kind switch
            {
                AddKind.Point => new(MathPlaneObjectKind.Point, "", new[] { x, y, z }, color),
                AddKind.Vector => at == (0, 0, 0)
                    ? new(MathPlaneObjectKind.Vector, "", new[] { s, s, s }, color)
                    : new(MathPlaneObjectKind.Vector, "", new[] { s, s, s, x, y, z }, color, HasFrom: true),
                AddKind.Segment => new(MathPlaneObjectKind.Segment, "", new[] { x, y, z, x + s, y + s, z }, color),
                AddKind.Polyline => new(MathPlaneObjectKind.Polyline, "", new[] { x, y, z, x + s, y, z, x + s, y + s, z + s }, color),
                AddKind.Curve => new(MathPlaneObjectKind.Curve, "", new[] { 0.0, 4 * Math.PI }, color,
                                     CurveKind: MathPlaneCurveKind.Parametric, Sources: new[] { "cos(t)", "sin(t)", "t/4" }),
                _ => new(MathPlaneObjectKind.Surface, "", Array.Empty<double>(), color,
                         CurveKind: MathPlaneCurveKind.Explicit, Sources: new[] { "0.5*sin(x)*cos(y)" }),
            };
            o = o with { Name = FreshName(scene, o.Kind) };
            var list = scene.Objects.ToList();
            list.Add(o);
            return (scene with { Objects = list }, list.Count - 1);
        }

        /// <summary>A name not used by any object in the scene.</summary>
        public static string FreshName(MathPlane3DScene scene, MathPlaneObjectKind kind)
        {
            var used = new HashSet<string>(scene.Objects.Select(o => o.Name), StringComparer.Ordinal);
            IEnumerable<string> candidates = kind switch
            {
                MathPlaneObjectKind.Point => Letters("ABCDEFGHKLMNPQRTUVWXYZ"),
                MathPlaneObjectKind.Vector => Letters("uvwabcdefgh"),
                MathPlaneObjectKind.Segment => Numbered("s"),
                MathPlaneObjectKind.Curve => Letters("cfgr"),
                MathPlaneObjectKind.Surface => Numbered("S"),
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

        /// <summary>True when <paramref name="name"/> is a valid identifier not used by another
        /// object than <paramref name="exceptIndex"/>.</summary>
        public static bool IsFreeName(MathPlane3DScene scene, string name, int exceptIndex)
            => MathPlaneScene.IsName(name) && !scene.Objects.Where((o, i) => i != exceptIndex).Any(o => o.Name == name);

        // ── Matrix presets ──

        /// <summary>A named 3×3 matrix for the preset buttons.</summary>
        public sealed record MapPreset(string Name, Mat3 M, string Hint);

        public static IReadOnlyList<MapPreset> Presets { get; } = new[]
        {
            new MapPreset("Rotate x", new Mat3(1, 0, 0, 0, 0, -1, 0, 1, 0), "a quarter turn about x: det 1, x is the only eigenvector"),
            new MapPreset("Rotate y", new Mat3(0, 0, 1, 0, 1, 0, -1, 0, 0), "a quarter turn about y"),
            new MapPreset("Rotate z", new Mat3(0, -1, 0, 1, 0, 0, 0, 0, 1), "a quarter turn about z"),
            new MapPreset("Shear", new Mat3(1, 1, 0, 0, 1, 0, 0, 0, 1), "j leans towards i: det 1, the x-z plane is fixed"),
            new MapPreset("Scale 2", new Mat3(2, 0, 0, 0, 2, 0, 0, 0, 2), "every vector doubles: det 8"),
            new MapPreset("Stretch", new Mat3(2, 0, 0, 0, 1, 0, 0, 0, 0.5), "x doubles, z halves: det 1, eigenvectors on the axes"),
            new MapPreset("Reflect", new Mat3(1, 0, 0, 0, 1, 0, 0, 0, -1), "mirror in the x-y plane: det -1"),
            new MapPreset("Project xy", new Mat3(1, 0, 0, 0, 1, 0, 0, 0, 0), "squash onto the x-y plane: det 0"),
        };
    }
}
