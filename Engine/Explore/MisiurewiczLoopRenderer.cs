// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Engine/Explore/MisiurewiczLoopRenderer.cs
//
// Interesting-location finder S3b (#1235): one frame of a Misiurewicz loop
// (Abstractions/Explore/MisiurewiczLoop). The renderers have no plane
// rotation, so the camera turn is done in image space: render a square big
// enough to cover the rotated frame (side ≥ the frame's diagonal, at the same
// pixel scale), then rotate-and-crop it bilinearly. Any family and any
// calculator path (relief included, through PosterRenderer) works unchanged.
//
// Colour: each cycle adds p to every escape count, so the colour map sees
// smooth − p·Cycles·t (ShiftedColorMap) and the iteration cap grows by the
// same amount. A cyclic palette then wraps with the picture.

using System;
using System.Threading;

using FracturingFog.Abstractions.Explore;
using FracturingFog.Abstractions.Imaging;
using FracturingFog.Imaging;
using FracturingFog.Interefaces;
using FracturingFog.Models;

namespace FracturingFog.Explore
{
    public static class MisiurewiczLoopRenderer
    {
        /// <summary>Render loop time <paramref name="t"/> (0..1) of
        /// <paramref name="loop"/>. <paramref name="req"/> is the loop's first
        /// frame: centre (on the Misiurewicz point), start zoom, size, theme,
        /// parameters. Returns the composed frame (relief included, no output
        /// post-FX) at req.Width × req.Height.</summary>
        public static uint[] RenderFrame(PosterRequest req, MisiurewiczLoop loop, double t, CancellationToken ct)
        {
            int w = req.Width, h = req.Height;
            int side = SquareSide(w, h);
            double shift = loop.IterationShiftAt(t);
            var sq = req with
            {
                Width = side, Height = side,
                // Same pixel scale as the frame: the plane spans the longest axis.
                Zoom = loop.ZoomAt(req.Zoom, t) * Math.Max(w, h) / side,
                MaxIterations = req.MaxIterations + (int)Math.Ceiling(shift),
                ColorMap = shift != 0 ? new ShiftedColorMap(req.ColorMap, (float)shift) : req.ColorMap,
            };
            var src = PosterRenderer.RenderToPixels(sq, ct, out int sw, out int sh);
            var dst = new uint[w * h];
            RotateCrop(src, sw, sh, dst, w, h, loop.CameraTurnAt(t));
            return dst;
        }

        /// <summary>Side of the square that covers a w × h frame at any turn.</summary>
        public static int SquareSide(int w, int h) => (int)Math.Ceiling(Math.Sqrt((double)w * w + (double)h * h)) + 2;

        /// <summary>Fill <paramref name="dst"/> (dw × dh) with the centre of
        /// <paramref name="src"/> (sw × sh) turned by <paramref name="turn"/>
        /// radians counter-clockwise in the complex plane, bilinear. The renderers
        /// map screen y to +Im (ViewCamera.WorldFromScreen), so plane offsets are
        /// (x, y) in pixels with no flip.</summary>
        public static void RotateCrop(uint[] src, int sw, int sh, uint[] dst, int dw, int dh, double turn)
        {
            double cos = Math.Cos(turn), sin = Math.Sin(turn);
            double scx = sw / 2.0, scy = sh / 2.0, dcx = dw / 2.0, dcy = dh / 2.0;
            System.Threading.Tasks.Parallel.For(0, dh, y =>
            {
                double uy = y + 0.5 - dcy;             // pixels along +Im
                for (int x = 0; x < dw; x++)
                {
                    double ux = x + 0.5 - dcx;
                    double rx = cos * ux - sin * uy, ry = sin * ux + cos * uy;
                    double fx = scx + rx - 0.5, fy = scy + ry - 0.5;
                    int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
                    double wx = fx - x0, wy = fy - y0;
                    int xa = Math.Clamp(x0, 0, sw - 1), xb = Math.Clamp(x0 + 1, 0, sw - 1);
                    int ya = Math.Clamp(y0, 0, sh - 1), yb = Math.Clamp(y0 + 1, 0, sh - 1);
                    dst[y * dw + x] = ImageResampler.BilerpPacked(
                        src[ya * sw + xa], src[ya * sw + xb], src[yb * sw + xa], src[yb * sw + xb], wx, wy);
                }
            });
        }
    }

    /// <summary>A colour map that colours escape count smooth − shift: the
    /// palette turned back by <c>shift</c> iterations (Misiurewicz loops). In-set
    /// colour, iteration cap and type pass through.</summary>
    public sealed class ShiftedColorMap : IColorMap
    {
        private readonly IColorMap _inner;
        private readonly float _shift;

        public ShiftedColorMap(IColorMap inner, float shift) { _inner = inner; _shift = shift; }

        public ColorPaletteType Type => _inner.Type;
        public int MaxIterations { get => _inner.MaxIterations; set => _inner.MaxIterations = value; }
        public uint InSetColor => _inner.InSetColor;
        public uint? OutOfBoundsColor => _inner.OutOfBoundsColor;

        public int Map(float smooth, float distance, int iterations)
            => _inner.Map(smooth - _shift, distance, iterations);
        public int Map(float smooth, float distance, int iterations, float nx, float ny)
            => _inner.Map(smooth - _shift, distance, iterations, nx, ny);
        public int Map(float smooth, float distance, int iterations, float nx, float ny,
                       float finalZr, float finalZi, float dzdcR, float dzdcI)
            => _inner.Map(smooth - _shift, distance, iterations, nx, ny, finalZr, finalZi, dzdcR, dzdcI);
    }
}
