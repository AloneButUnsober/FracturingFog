// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// MicrolensingCalculator.LightCurve.cs (#2007, light-curve inset; follow-up of #1948)
//
// What a telescope sees as the source moves behind the lens field: a straight source track
// (a point and an angle in source-plane coordinates, so panning keeps it in place) is drawn over
// the magnification map, and a panel along the bottom of the frame plots log10 of the
// magnification met along it against the distance travelled (Einstein radii). The values come
// from this frame's own ray counts: one sample per pixel of track, each the mean of the three
// pixels across the track (a thin strip, to average the shot noise). For a single lens with no
// smooth matter or shear, Paczyński's A(u) for the same track is drawn dashed alongside
// (ApJ 304, 1, 1986), as a visible check.
//
// Drawn into ColorBuffer with Skia (as Math Plane draws), so live, --batch, poster export and
// video carry it. Colours are Okabe-Ito (colour-blind safe): the measured curve and the track in
// orange, the analytic curve in sky blue.

using System;
using System.Runtime.InteropServices;

using FracturingFog.Lensing;
using FracturingFog.Models;

using SkiaSharp;

namespace FracturingFog;

/// <summary>#2007 — one sampled light curve: distance along the track, magnification, and (single
/// lens) the analytic magnification, plus the track's ends in the source plane.</summary>
public sealed record MicrolensLightCurve(double[] S, double[] Magnification, double[]? Analytic,
                                         (double X, double Y) Start, (double X, double Y) End);

public sealed partial class MicrolensingCalculator
{
    public const uint CurveColor = 0xFFE69F00;      // Okabe-Ito orange
    public const uint AnalyticColor = 0xFF56B4E9;   // Okabe-Ito sky blue
    /// <summary>The panel's share of the frame height.</summary>
    public const double PanelFraction = 0.28;

    /// <summary>#2007 — the light curve of the last frame (null when the inset is off or the track misses the view).</summary>
    public MicrolensLightCurve? LastLightCurve { get; private set; }

    /// <summary>#2007 — sample <paramref name="mag"/> (a <paramref name="w"/> x <paramref name="h"/> frame at
    /// the given view) along the track through (<paramref name="tx"/>, <paramref name="ty"/>) at
    /// <paramref name="angleDeg"/>: one sample per pixel of track inside the view, each the mean over three
    /// pixels across it. <paramref name="analytic"/> adds Paczyński's A(u) about the origin.</summary>
    public static MicrolensLightCurve? SampleLightCurve(double[] mag, int w, int h, double cx, double cy, double zoom,
                                                        double tx, double ty, double angleDeg, bool analytic)
    {
        double pitch = FracturingFog.ViewState.ViewCamera.PixelPitch(w, h, zoom);
        double a0 = cx - 0.5 * w * pitch, a1 = cx + 0.5 * w * pitch;
        double b0 = -(cy + 0.5 * h * pitch), b1 = -(cy - 0.5 * h * pitch);
        double th = (double.IsFinite(angleDeg) ? angleDeg : 0) * Math.PI / 180;
        double dx = Math.Cos(th), dy = Math.Sin(th);
        // Clip the line P(s) = T + s D to the view box (Liang–Barsky).
        double s0 = double.NegativeInfinity, s1 = double.PositiveInfinity;
        if (!Clip(dx, a0 - tx, a1 - tx, ref s0, ref s1) || !Clip(dy, b0 - ty, b1 - ty, ref s0, ref s1) || !(s1 > s0)) return null;
        int n = Math.Max(2, (int)Math.Ceiling((s1 - s0) / pitch));
        var S = new double[n];
        var M = new double[n];
        var A = analytic ? new double[n] : null;
        for (int k = 0; k < n; k++)
        {
            double s = s0 + (s1 - s0) * (k + 0.5) / n;
            double x = tx + s * dx, y = ty + s * dy;
            double sum = 0; int cnt = 0;
            for (int o = -1; o <= 1; o++)
            {
                // Across the track: the perpendicular (−dy, dx), one pixel per step.
                double px = (x - o * dy * pitch - cx) / pitch + 0.5 * w;
                double py = (-(y + o * dx * pitch) - cy) / pitch + 0.5 * h;
                int ix = (int)Math.Floor(px), iy = (int)Math.Floor(py);
                if (ix < 0 || iy < 0 || ix >= w || iy >= h) continue;
                sum += mag[iy * w + ix]; cnt++;
            }
            S[k] = s - s0;
            M[k] = cnt > 0 ? sum / cnt : double.NaN;
            if (A != null)
            {
                double u = Math.Sqrt(x * x + y * y);
                A[k] = u > 0 ? (u * u + 2) / (u * Math.Sqrt(u * u + 4)) : double.PositiveInfinity;
            }
        }
        return new MicrolensLightCurve(S, M, A, (tx + s0 * dx, ty + s0 * dy), (tx + s1 * dx, ty + s1 * dy));
    }

    private static bool Clip(double d, double lo, double hi, ref double s0, ref double s1)
    {
        if (Math.Abs(d) < 1e-15) return lo <= 0 && hi >= 0;
        double t0 = lo / d, t1 = hi / d;
        if (t0 > t1) (t0, t1) = (t1, t0);
        s0 = Math.Max(s0, t0); s1 = Math.Min(s1, t1);
        return s1 > s0;
    }

    private void DrawLightCurve(FractalParameters p, MicrolensField field)
    {
        LastLightCurve = null;
        if (!p.MicroLightCurve) return;
        bool analytic = p.MicroLayout == MicrolensLayout.Single && field.KappaSmooth == 0 && field.Shear == 0;
        var lc = SampleLightCurve(Magnification, Width, Height, CenterX, CenterY, Zoom, p.MicroTrackX, p.MicroTrackY, p.MicroTrackAngle, analytic);
        LastLightCurve = lc;
        if (lc == null) return;
        int w = Width, h = Height;
        var handle = GCHandle.Alloc(ColorBuffer, GCHandleType.Pinned);
        try
        {
            var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var bmp = new SKBitmap();
            bmp.InstallPixels(info, handle.AddrOfPinnedObject(), info.RowBytes);
            using var canvas = new SKCanvas(bmp);
            float ui = Math.Max(0.6f, Math.Min(w, h) / 540f);
            double pitch = FracturingFog.ViewState.ViewCamera.PixelPitch(w, h, Zoom);
            SKPoint Pix(double x, double y) => new((float)((x - CenterX) / pitch + 0.5 * w), (float)((-y - CenterY) / pitch + 0.5 * h));
            using var track = new SKPaint { Style = SKPaintStyle.Stroke, IsAntialias = true, StrokeWidth = 2f * ui, Color = new SKColor(CurveColor) };
            canvas.DrawLine(Pix(lc.Start.X, lc.Start.Y), Pix(lc.End.X, lc.End.Y), track);

            // The panel.
            float ph = (float)(h * PanelFraction), m = 10f * ui;
            var panel = new SKRect(m, h - ph - m, w - m, h - m);
            using (var bg = new SKPaint { Style = SKPaintStyle.Fill, Color = new SKColor(0, 0, 0, 0xB8) })
                canvas.DrawRect(panel, bg);
            var plot = new SKRect(panel.Left + 46f * ui, panel.Top + 10f * ui, panel.Right - 10f * ui, panel.Bottom - 22f * ui);
            if (plot.Width < 8 || plot.Height < 8) { canvas.Flush(); return; }   // a thumbnail: the track only
            double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
            void Range(double[] v) { foreach (double x in v) if (x > 0 && double.IsFinite(x)) { double l = Math.Log10(x); lo = Math.Min(lo, l); hi = Math.Max(hi, l); } }
            Range(lc.Magnification);
            if (lc.Analytic != null) Range(lc.Analytic);
            if (!double.IsFinite(lo)) return;
            if (hi - lo < 0.1) { lo -= 0.05; hi += 0.05; }
            double sMax = lc.S[^1] + (lc.S.Length > 1 ? lc.S[1] - lc.S[0] : 1) * 0.5;
            float X(double s) => plot.Left + (float)(s / sMax) * plot.Width;
            float Y(double mu) => plot.Bottom - (float)((Math.Log10(mu) - lo) / (hi - lo)) * plot.Height;
            using var axis = new SKPaint { Style = SKPaintStyle.Stroke, IsAntialias = true, StrokeWidth = 1f * ui, Color = new SKColor(0xA0FFFFFF) };
            canvas.DrawLine(plot.Left, plot.Bottom, plot.Right, plot.Bottom, axis);
            canvas.DrawLine(plot.Left, plot.Top, plot.Left, plot.Bottom, axis);
            using var font = new SKFont(SKTypeface.Default, 11f * ui);
            using var ink = new SKPaint { IsAntialias = true, Color = new SKColor(0xDDFFFFFF) };
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            canvas.DrawText(Math.Pow(10, hi).ToString("0.##", inv), panel.Left + 4f * ui, plot.Top + 9f * ui, font, ink);
            canvas.DrawText(Math.Pow(10, lo).ToString("0.##", inv), panel.Left + 4f * ui, plot.Bottom, font, ink);
            canvas.DrawText("magnification along the track (Einstein radii: 0 to " + sMax.ToString("0.##", inv) + ")",
                            plot.Left, panel.Bottom - 6f * ui, font, ink);

            void Curve(double[] v, uint colour, bool dashed)
            {
                using var path = new SKPath();
                bool open = false;
                for (int k = 0; k < v.Length; k++)
                {
                    if (!(v[k] > 0) || !double.IsFinite(v[k])) { open = false; continue; }
                    float y = Math.Clamp(Y(v[k]), plot.Top, plot.Bottom);
                    if (!open) { path.MoveTo(X(lc.S[k]), y); open = true; } else path.LineTo(X(lc.S[k]), y);
                }
                using var pen = new SKPaint { Style = SKPaintStyle.Stroke, IsAntialias = true, StrokeWidth = (dashed ? 2.6f : 1.4f) * ui, Color = new SKColor(colour) };
                if (dashed) pen.PathEffect = SKPathEffect.CreateDash(new[] { 10f * ui, 6f * ui }, 0);
                canvas.DrawPath(path, pen);
            }
            Curve(lc.Magnification, CurveColor, dashed: false);
            if (lc.Analytic != null) Curve(lc.Analytic, AnalyticColor, dashed: true);   // on top: the check stays visible
            canvas.Flush();
        }
        finally { handle.Free(); }
    }
}
