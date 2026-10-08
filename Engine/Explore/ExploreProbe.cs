// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Engine/Explore/ExploreProbe.cs
//
// Interesting-location finder S6 (#1190): the probe renderer behind
// AutoExplorer. Renders a small square view of the active family through the
// same calculators the poster / batch path builds (PosterRenderer.
// BuildCaptureCalculator, or MandelbrotCalculator with the full octuple-double
// centre) and hands back the field the scorer reads: the smooth-iteration
// buffer when the family has one, else the rendered luminance.
//
// Relief, lighting and post-FX are deliberately not applied: they restyle a
// view, they do not change where its structure is.

using System;
using System.Threading;

using FracturingFog.Abstractions.Explore;
using FracturingFog.Imaging;
using FracturingFog.Interefaces;
using FracturingFog.Models;

namespace FracturingFog.Explore
{
    public static class ExploreProbe
    {
        /// <summary>A probe delegate for <see cref="AutoExplorer.Run"/> that
        /// renders <paramref name="template"/>'s family, parameters and theme at
        /// each view, <paramref name="size"/> pixels square. Iterations follow
        /// the quality tier (promoted for depth, as a live zoom would).</summary>
        public static Func<ExploreView, CancellationToken, ProbeField?> For(PosterRequest template, int size)
            => (view, ct) => Render(template, view, size, ct);

        public static ProbeField? Render(PosterRequest template, ExploreView view, int size, CancellationToken ct)
        {
            var quality = MinibrotJump.QualityFor(template.Quality, view.Zoom);
            var re = view.Center.Re; var im = view.Center.Im;
            var req = template with
            {
                Width = size, Height = size,
                CenterX = re.X0, CenterXLo = re.X1, CenterX2 = re.X2, CenterX3 = re.X3,
                CenterX4 = re.X4, CenterX5 = re.X5, CenterX6 = re.X6, CenterX7 = re.X7,
                CenterY = im.X0, CenterYLo = im.X1, CenterY2 = im.X2, CenterY3 = im.X3,
                CenterY4 = im.X4, CenterY5 = im.X5, CenterY6 = im.X6, CenterY7 = im.X7,
                Zoom = view.Zoom,
                Quality = quality,
                MaxIterations = quality.ComputeIterations(view.Zoom),
            };

            uint[] colors; float[]? smooth; int w, h;
            var alt = PosterRenderer.BuildCaptureCalculator(req);
            if (alt != null)
            {
                if (!alt.SupportsZoomPan) return null;
                alt.Calculate(ct);
                ct.ThrowIfCancellationRequested();
                (colors, w, h) = (alt.ColorBuffer, alt.Width, alt.Height);
                smooth = (alt as IHeightFieldSource)?.SmoothBuffer;
            }
            else
            {
                var calc = new MandelbrotCalculator(size, size)
                {
                    CenterX = req.CenterX, CenterXLo = req.CenterXLo, CenterX2 = req.CenterX2, CenterX3 = req.CenterX3,
                    CenterX4 = req.CenterX4, CenterX5 = req.CenterX5, CenterX6 = req.CenterX6, CenterX7 = req.CenterX7,
                    CenterY = req.CenterY, CenterYLo = req.CenterYLo, CenterY2 = req.CenterY2, CenterY3 = req.CenterY3,
                    CenterY4 = req.CenterY4, CenterY5 = req.CenterY5, CenterY6 = req.CenterY6, CenterY7 = req.CenterY7,
                    Zoom = req.Zoom,
                    MaxIterations = req.MaxIterations,
                    ColorMap = req.ColorMap,
                    Quality = req.Quality,
                };
                calc.Calculate(ct);
                ct.ThrowIfCancellationRequested();
                (colors, w, h) = (calc.ColorBuffer, calc.Width, calc.Height);
                smooth = calc.SmoothBuffer;
            }

            if (w <= 0 || h <= 0) return null;
            int n = w * h;
            // A family that implements the height-field interface but leaves it
            // empty (all 0 while the colours vary) is scored on its rendered
            // colours instead. All 0 over flat colour is a genuinely all-inside view.
            if (smooth != null && smooth.Length >= n
                && (Array.Exists(smooth, v => v != 0f) || !Varies(colors, n)))
                return ProbeField.FromSmooth(w, h, smooth, req.MaxIterations);
            return colors.Length >= n ? ProbeField.FromArgb(w, h, colors) : null;
        }

        private static bool Varies(uint[] colors, int n)
        {
            if (colors.Length < n || n == 0) return false;
            uint first = colors[0] & 0x00FFFFFFu;
            for (int i = 1; i < n; i++) if ((colors[i] & 0x00FFFFFFu) != first) return true;
            return false;
        }
    }
}
