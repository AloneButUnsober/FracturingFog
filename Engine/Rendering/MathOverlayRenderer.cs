// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Engine/Rendering/MathOverlayRenderer.cs
//
// #1554 (Math Plane S7, part 2) — draws the math overlay (MathOverlay: axes and
// the orbit of one point) onto a finished 2D frame. The live view calls it from
// FractalOverlayCompositor beside the grid; poster export and --batch call it on
// the composed buffer before the watermark, so a pinned overlay renders the same
// headless. The geometry and orbit come from Abstractions/ViewState/MathOverlay.cs.

using System;
using System.Numerics;
using System.Runtime.InteropServices;
using FracturingFog.ViewState;
using SkiaSharp;

namespace FracturingFog.Rendering
{
    public static class MathOverlayRenderer
    {
        public const uint AxisColor = 0xB3FFFFFF;
        public const uint EscapeCircleColor = 0x99FFFFFF;
        public const uint MarkerColor = 0xFFFFFFFF;

        /// <summary>Draw the overlay for <paramref name="spec"/> onto <paramref name="bgra"/>
        /// (w x h BGRA, the view at centre / zoom). <paramref name="pinned"/> draws the
        /// point as a filled marker (hover: a ring).</summary>
        public static void Draw(uint[] bgra, int w, int h, double centerX, double centerY, double zoom,
                                FractalType type, Complex juliaC, MathOverlaySpec spec, bool pinned)
        {
            if (bgra == null || w <= 1 || h <= 1 || bgra.Length < w * h || !(zoom > 0)) return;
            if (type == FractalType.MathPlane) return;   // the Math Plane draws its own orbit
            var handle = GCHandle.Alloc(bgra, GCHandleType.Pinned);
            try
            {
                var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul);
                using var bmp = new SKBitmap();
                bmp.InstallPixels(info, handle.AddrOfPinnedObject(), info.RowBytes);
                using var canvas = new SKCanvas(bmp);
                Draw(canvas, w, h, centerX, centerY, zoom, type, juliaC, spec, pinned);
                canvas.Flush();
            }
            finally { handle.Free(); }
        }

        private static void Draw(SKCanvas canvas, int w, int h, double centerX, double centerY, double zoom,
                                 FractalType type, Complex juliaC, MathOverlaySpec spec, bool pinned)
        {
            float ui = (float)Math.Clamp(Math.Min(w, h) / 720.0, 1.0, 4.0);
            double s = MathOverlay.Scale(w, h, zoom);
            SKPoint P(double re, double im)
            {
                var (x, y) = MathOverlay.ToPixel(re, im, w, h, centerX, centerY, zoom);
                return new SKPoint((float)Math.Clamp(x, -1e7, 1e7), (float)Math.Clamp(y, -1e7, 1e7));
            }

            // Axes through 0 (when on screen).
            using var axis = new SKPaint { Style = SKPaintStyle.Stroke, IsAntialias = true, StrokeWidth = 1.2f * ui, Color = new SKColor(AxisColor) };
            var o = P(0, 0);
            if (o.Y >= 0 && o.Y <= h) canvas.DrawLine(0, o.Y, w, o.Y, axis);
            if (o.X >= 0 && o.X <= w) canvas.DrawLine(o.X, 0, o.X, h, axis);

            var orbit = MathOverlay.Orbit(type, spec, juliaC);
            if (orbit != null)
            {
                using var dash = SKPathEffect.CreateDash(new[] { 6f * ui, 5f * ui }, 0);
                using var circle = new SKPaint { Style = SKPaintStyle.Stroke, IsAntialias = true, StrokeWidth = 1.2f * ui, Color = new SKColor(EscapeCircleColor), PathEffect = dash };
                canvas.DrawCircle(o, (float)Math.Min(2.0 / s, 1e7), circle);

                using var seg = new SKPaint { Style = SKPaintStyle.Stroke, IsAntialias = true, StrokeWidth = 1.6f * ui };
                using var dot = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };
                for (int n = 1; n < orbit.Count; n++)
                {
                    seg.Color = new SKColor(MathOverlay.StepColor(n, orbit.Count));
                    canvas.DrawLine(P(orbit[n - 1].Real, orbit[n - 1].Imaginary), P(orbit[n].Real, orbit[n].Imaginary), seg);
                }
                float r = (orbit.Count > 200 ? 1.8f : 3f) * ui;
                for (int n = 0; n < orbit.Count; n++)
                {
                    dot.Color = new SKColor(MathOverlay.StepColor(n, orbit.Count));
                    canvas.DrawCircle(P(orbit[n].Real, orbit[n].Imaginary), r, dot);
                }
            }

            // The point itself.
            var at = P(spec.Re, spec.Im);
            using var mark = new SKPaint { Style = pinned ? SKPaintStyle.Fill : SKPaintStyle.Stroke, IsAntialias = true, StrokeWidth = 2f * ui, Color = new SKColor(MarkerColor) };
            using var outline = new SKPaint { Style = SKPaintStyle.Stroke, IsAntialias = true, StrokeWidth = 1.2f * ui, Color = new SKColor(0xFF000000) };
            canvas.DrawCircle(at, 5f * ui, mark);
            canvas.DrawCircle(at, 6.2f * ui, outline);

            string line = MathOverlay.Readout(type, spec, orbit, pinned);
            using var font = new SKFont(SKTypeface.Default, 13f * ui);
            using var halo = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true, Color = new SKColor(0xC0000000) };
            using var ink = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true, Color = new SKColor(MarkerColor) };
            float ty = h - 8f * ui - font.Spacing;   // above the bottom edge (the grid labels sit on it)
            canvas.DrawText(line, 8f * ui + ui, ty + ui, font, halo);
            canvas.DrawText(line, 8f * ui, ty, font, ink);
        }
    }
}
