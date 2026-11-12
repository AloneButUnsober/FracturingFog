// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Engine/Rendering/MathOverlay3DRenderer.cs
//
// #1734 (Math Plane 3D fractal bridge B4) — the math overlay on a 3D fractal view: the axes,
// the bailout sphere's outline and the orbit of one point under the fractal's own step (B2,
// MathPlane3DOrbit), drawn with the raymarcher's exact camera (B3, Raymarch3DCamera) onto the
// finished frame. Design: Docs/Technical/MathPlane3D-FractalBridge-DesignPlan.md §3.4.
//
// Depth test: every line is cut into pieces about two pixels long; a piece whose ray distance
// is beyond the frame's published depth at its pixel (plus a small bias) is behind the fractal
// and drawn dimmed and dashed, not dropped, so an orbit inside the set stays readable. With no
// depth (thin-lens DoF publishes none) everything is drawn plainly and the read-out says so.
//
// Extent: the axes reach the orbit's display ball, min(escape radius, 1.5 x the fractal's mesh
// range) (B2); the bailout sphere is outlined only when it is that ball (its true radius is in
// view). The outline is the sphere's silhouette from the eye: the circle where the tangent cone
// touches it, centre E r^2/|E|^2, radius r sqrt(1 - r^2/|E|^2).
// Colours: Okabe-Ito (no red / green pair); the warning is yellow #FFCC00.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using FracturingFog.Export;
using FracturingFog.Models;
using FracturingFog.ViewState;
using SkiaSharp;

namespace FracturingFog.Rendering
{
    public static class MathOverlay3DRenderer
    {
        public const uint AxisXColor = 0xFFE69F00, AxisYColor = 0xFF56B4E9, AxisZColor = 0xFF009E73;
        public const uint SphereColor = 0xB3FFFFFF, MarkerColor = 0xFFFFFFFF, WarningColor = 0xFFFFCC00;
        /// <summary>Alpha of a hidden (behind the fractal) piece.</summary>
        public const byte HiddenAlpha = 0xA8;

        /// <summary>A piece's depth-test verdict, exposed for tests.</summary>
        public static bool IsHidden(Raymarch3DCamera cam, float[]? depth, (double X, double Y, double Z) p)
        {
            if (depth == null || cam.Project(p) is not { } q) return false;
            int ix = (int)Math.Floor(q.X), iy = (int)Math.Floor(q.Y);
            if (ix < 0 || iy < 0 || ix >= cam.Width || iy >= cam.Height || depth.Length < cam.Width * cam.Height) return false;
            float d = depth[iy * cam.Width + ix];
            if (!float.IsFinite(d) || d <= 0) return false;   // sky
            return q.Distance > d + Bias(d);
        }

        private static double Bias(double d) => 1e-3 + 0.01 * d;

        /// <summary>The surface point under continuous pixel (<paramref name="px"/>, <paramref name="py"/>) of
        /// a frame: the frame's own depth along its camera's ray through that pixel's centre; null over
        /// the sky, off the frame, or without depth.</summary>
        public static (double X, double Y, double Z)? Pick(Raymarch3DCamera cam, float[]? depth, double px, double py)
        {
            if (depth == null || depth.Length < cam.Width * cam.Height) return null;
            int ix = (int)Math.Floor(px), iy = (int)Math.Floor(py);
            if (ix < 0 || iy < 0 || ix >= cam.Width || iy >= cam.Height) return null;
            float d = depth[iy * cam.Width + ix];
            if (!float.IsFinite(d) || d <= 0) return null;
            return cam.PointAt(ix + 0.5, iy + 0.5, d);
        }

        /// <summary>The overlay's extent: min(escape radius, 1.5 x mesh range).</summary>
        public static double Extent(FractalType type, FractalParameters p)
        {
            double range = MeshExportCapabilities.IsMeshExportable(type) ? RaymarchMeshSampler.SuggestedRange(type, p) : 2.0;
            double esc = ((IList<FractalType>)MathPlane3DScene.OrbitTypes).Contains(type) ? MathPlane3DOrbit.EscapeRadius(type, p) : double.PositiveInfinity;
            return Math.Min(esc, 1.5 * range);
        }

        /// <summary>The read-out line for the overlay.</summary>
        public static string Readout(FractalType type, MathOverlay3DSpec? spec, MathPlane3DOrbitResult? orbit, bool pinned)
        {
            var inv = CultureInfo.InvariantCulture;
            if (spec is not { } s) return "Point at the fractal to see the orbit of that point";
            string at = $"({s.X.ToString("0.####", inv)}, {s.Y.ToString("0.####", inv)}, {s.Z.ToString("0.####", inv)})";
            string who = (pinned ? "pinned " : "") + at;
            if (orbit == null) return $"{who}: no orbit for {type} (axes only)";
            int steps = orbit.Points.Count - 1;
            string w = type is FractalType.QuaternionJulia or FractalType.QuaternionMandelbrot ? " (w dropped)" : "";
            return orbit.Escaped ? $"orbit of {who} under {type}{w}: escapes at step {steps}"
                                 : $"orbit of {who} under {type}{w}: bounded for {steps} steps";
        }

        /// <summary>Draw the overlay onto <paramref name="bgra"/> (the frame <paramref name="cam"/>
        /// rendered, at its size).</summary>
        public static void Draw(uint[] bgra, Raymarch3DCamera cam, float[]? depth, FractalType type, FractalParameters p,
                                MathOverlay3DSpec? spec, bool pinned)
        {
            int w = cam.Width, h = cam.Height;
            if (bgra == null || w <= 1 || h <= 1 || bgra.Length < w * h) return;
            if (depth != null && depth.Length < w * h) depth = null;
            var handle = GCHandle.Alloc(bgra, GCHandleType.Pinned);
            try
            {
                var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul);
                using var bmp = new SKBitmap();
                bmp.InstallPixels(info, handle.AddrOfPinnedObject(), info.RowBytes);
                using var canvas = new SKCanvas(bmp);
                Draw(canvas, cam, depth, type, p, spec, pinned);
                canvas.Flush();
            }
            finally { handle.Free(); }
        }

        private static void Draw(SKCanvas canvas, Raymarch3DCamera cam, float[]? depth, FractalType type, FractalParameters p,
                                 MathOverlay3DSpec? spec, bool pinned)
        {
            int w = cam.Width, h = cam.Height;
            float ui = (float)Math.Clamp(Math.Min(w, h) / 720.0, 1.0, 4.0);
            double ext = Extent(type, p);
            using var dash = SKPathEffect.CreateDash(new[] { 5f * ui, 4f * ui }, 0);
            using var solid = new SKPaint { Style = SKPaintStyle.Stroke, IsAntialias = true, StrokeWidth = 1.6f * ui };
            using var hidden = new SKPaint { Style = SKPaintStyle.Stroke, IsAntialias = true, StrokeWidth = 1.4f * ui, PathEffect = dash };
            // A thin dark halo under every line keeps it readable over the fractal's texture.
            using var haloLine = new SKPaint { Style = SKPaintStyle.Stroke, IsAntialias = true, StrokeWidth = 3.2f * ui, Color = new SKColor(0x80000000) };
            using var haloDash = new SKPaint { Style = SKPaintStyle.Stroke, IsAntialias = true, StrokeWidth = 3.0f * ui, Color = new SKColor(0x60000000), PathEffect = dash };

            // A world polyline, depth-tested piece by piece.
            void Line(IReadOnlyList<(double X, double Y, double Z)> pts, uint color)
            {
                solid.Color = new SKColor(color);
                hidden.Color = new SKColor(color).WithAlpha(HiddenAlpha);
                using var shown = new SKPath();
                using var behind = new SKPath();
                // Consecutive pieces of the same kind form one contour: Skia restarts the dash
                // pattern at every MoveTo, so two-pixel contours would all be "on" (solid).
                SKPath? open = null;
                for (int k = 1; k < pts.Count; k++)
                {
                    var a = pts[k - 1]; var b = pts[k];
                    if (cam.Project(a) is not { } pa || cam.Project(b) is not { } pb) { open = null; continue; }   // behind the eye
                    double len = Math.Sqrt((pb.X - pa.X) * (pb.X - pa.X) + (pb.Y - pa.Y) * (pb.Y - pa.Y));
                    int n = Math.Clamp((int)(len / 2.0) + 1, 1, 4000);
                    for (int i = 0; i < n; i++)
                    {
                        double t0 = (double)i / n, t1 = (double)(i + 1) / n, tm = 0.5 * (t0 + t1);
                        var q0 = Lerp(a, b, t0); var q1 = Lerp(a, b, t1);
                        if (cam.Project(q0) is not { } s0 || cam.Project(q1) is not { } s1) { open = null; continue; }
                        var path = IsHidden(cam, depth, Lerp(a, b, tm)) ? behind : shown;
                        if (!ReferenceEquals(path, open)) { path.MoveTo((float)s0.X, (float)s0.Y); open = path; }
                        path.LineTo((float)s1.X, (float)s1.Y);
                    }
                }
                canvas.DrawPath(behind, haloDash);
                canvas.DrawPath(behind, hidden);
                canvas.DrawPath(shown, haloLine);
                canvas.DrawPath(shown, solid);
            }

            // Axes, out to the display ball; labelled at the positive ends.
            using var font = new SKFont(SKTypeface.Default, 13f * ui);
            using var halo = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true, Color = new SKColor(0xC0000000) };
            using var ink = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };
            void Label((double X, double Y, double Z) at, string text, uint color)
            {
                if (cam.Project(at) is not { } s) return;
                ink.Color = new SKColor(color);
                canvas.DrawText(text, (float)s.X + 4 * ui + ui, (float)s.Y - 4 * ui + ui, font, halo);
                canvas.DrawText(text, (float)s.X + 4 * ui, (float)s.Y - 4 * ui, font, ink);
            }
            Line(new[] { (-ext, 0.0, 0.0), (ext, 0.0, 0.0) }, AxisXColor); Label((ext, 0, 0), "x", AxisXColor);
            Line(new[] { (0.0, -ext, 0.0), (0.0, ext, 0.0) }, AxisYColor); Label((0, ext, 0), "y", AxisYColor);
            Line(new[] { (0.0, 0.0, -ext), (0.0, 0.0, ext) }, AxisZColor); Label((0, 0, ext), "z", AxisZColor);

            // The bailout sphere's silhouette, when its radius is the ball shown.
            bool orbitType = ((IList<FractalType>)MathPlane3DScene.OrbitTypes).Contains(type);
            if (orbitType && MathPlane3DOrbit.EscapeRadius(type, p) is double esc && esc <= ext && Silhouette(cam.Eye, esc) is { } ring)
                Line(ring, SphereColor);

            // The orbit.
            MathPlane3DOrbitResult? orbit = null;
            if (spec is { } s0 && orbitType)
            {
                orbit = MathPlane3DOrbit.Run(type, p, s0.Point, Math.Max(1, s0.Steps));
                var pts = new List<(double X, double Y, double Z)>(orbit.Points.Count);
                foreach (var q in orbit.Points) pts.Add((q.X, q.Y, q.Z));
                using var dot = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };
                foreach (var piece in MathPlane3DOrbit.Clip(pts, ext))
                    for (int k = 1; k < piece.Count; k++)
                        Line(new[] { piece[k - 1], piece[k] }, MathOverlay.StepColor(k, pts.Count));
                float r = (pts.Count > 200 ? 1.8f : 3f) * ui;
                for (int n = 0; n < pts.Count; n++)
                {
                    var q = pts[n];
                    if (q.X * q.X + q.Y * q.Y + q.Z * q.Z > ext * ext || cam.Project(q) is not { } sq) continue;
                    var c = new SKColor(MathOverlay.StepColor(n, pts.Count));
                    dot.Color = IsHidden(cam, depth, q) ? c.WithAlpha(HiddenAlpha) : c;
                    canvas.DrawCircle((float)sq.X, (float)sq.Y, r, dot);
                }
            }

            // The point itself.
            if (spec is { } sp && cam.Project(sp.Point) is { } at)
            {
                using var mark = new SKPaint { Style = pinned ? SKPaintStyle.Fill : SKPaintStyle.Stroke, IsAntialias = true, StrokeWidth = 2f * ui, Color = new SKColor(MarkerColor) };
                using var outline = new SKPaint { Style = SKPaintStyle.Stroke, IsAntialias = true, StrokeWidth = 1.2f * ui, Color = new SKColor(0xFF000000) };
                canvas.DrawCircle((float)at.X, (float)at.Y, 5f * ui, mark);
                canvas.DrawCircle((float)at.X, (float)at.Y, 6.2f * ui, outline);
            }

            // Read-out, and the depth warning.
            float ty = h - 8f * ui - font.Spacing;
            void Text(string line, float y, uint color)
            {
                ink.Color = new SKColor(color);
                canvas.DrawText(line, 8f * ui + ui, y + ui, font, halo);
                canvas.DrawText(line, 8f * ui, y, font, ink);
            }
            Text(Readout(type, spec, orbit, pinned), ty, MarkerColor);
            if (depth == null) Text("No depth this frame (thin-lens DoF): hidden parts are not dimmed", ty - font.Spacing, WarningColor);
        }

        private static (double X, double Y, double Z) Lerp((double X, double Y, double Z) a, (double X, double Y, double Z) b, double t)
            => (a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t);

        /// <summary>The silhouette of the sphere |p| = r seen from <paramref name="eye"/>: a closed
        /// polyline on the sphere; null when the eye is inside it.</summary>
        public static List<(double X, double Y, double Z)>? Silhouette((double X, double Y, double Z) eye, double r, int n = 128)
        {
            double d2 = eye.X * eye.X + eye.Y * eye.Y + eye.Z * eye.Z;
            if (!(d2 > r * r)) return null;
            double d = Math.Sqrt(d2);
            var e = (eye.X / d, eye.Y / d, eye.Z / d);
            var c = (eye.X * r * r / d2, eye.Y * r * r / d2, eye.Z * r * r / d2);
            double rho = r * Math.Sqrt(1 - r * r / d2);
            // An orthonormal pair perpendicular to e.
            var helper = Math.Abs(e.Item2) < 0.9 ? (0.0, 1.0, 0.0) : (1.0, 0.0, 0.0);
            var a = Normalize(Cross(e, helper));
            var b = Cross(e, a);
            var pts = new List<(double X, double Y, double Z)>(n + 1);
            for (int k = 0; k <= n; k++)
            {
                double t = 2 * Math.PI * k / n, ct = Math.Cos(t), st = Math.Sin(t);
                pts.Add((c.Item1 + rho * (ct * a.Item1 + st * b.Item1), c.Item2 + rho * (ct * a.Item2 + st * b.Item2), c.Item3 + rho * (ct * a.Item3 + st * b.Item3)));
            }
            return pts;
        }

        private static (double, double, double) Cross((double X, double Y, double Z) u, (double X, double Y, double Z) v)
            => (u.Y * v.Z - u.Z * v.Y, u.Z * v.X - u.X * v.Z, u.X * v.Y - u.Y * v.X);

        private static (double X, double Y, double Z) Normalize((double X, double Y, double Z) v)
        {
            double l = Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
            return (v.X / l, v.Y / l, v.Z / l);
        }
    }
}
