// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// CosmicWebCalculator.Dimension.cs (#2023, Cosmic Web epic #2020)
//
// The D(r) measurement of the generated points (Engine/CosmicWeb/FractalDimension.cs: the chain of
// Einasto 2026, arXiv:2608.04694, Eqs. 2-4), its status-bar readout and an optional inset that plots
// D(r) against log r, drawn into the frame so batch and poster export carry it. The inset follows the
// microlensing light-curve inset (#2007): a translucent panel along the bottom, Okabe-Ito colours
// (Okabe & Ito, "Color Universal Design (CUD)", J*FLY, https://jfly.uni-koeln.de/color/): the measured curve orange, the homogeneous D = 3 line
// sky blue (dashed), the Soneira-Peebles model D bluish green (dashed). The measurement is cached per
// point set and settings, so camera moves do not repeat it.

using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

using FracturingFog.CosmicWeb;
using FracturingFog.Models;

using SkiaSharp;

namespace FracturingFog;

public sealed partial class CosmicWebCalculator
{
    public const uint DimensionCurveColor = 0xFFE69F00;   // Okabe-Ito orange
    public const uint HomogeneousColor = 0xFF56B4E9;      // Okabe-Ito sky blue
    public const uint ModelColor = 0xFF009E73;            // Okabe-Ito bluish green
    public const double DimensionPanelFraction = 0.30;
    /// <summary>The scales (fractions of the box side) the status bar reports D at: 0.3, 1, 3, 10 Mpc/h in the default 100 Mpc/h box.</summary>
    public static readonly double[] StatusScales = { 0.003, 0.01, 0.03, 0.1 };

    /// <summary>The last D(r) measurement (null when the measurement is off).</summary>
    public CosmicWebDimension? LastDimension { get; private set; }

    private static readonly object s_dLock = new();
    // A few recent measurements (#2025: one entry was evicted by a second view or render in parallel),
    // most recent first; weak on the point set so a dropped set is not pinned.
    private const int DimensionCacheSize = 4;
    private static readonly System.Collections.Generic.List<(WeakReference<CosmicWebPoints> Points, CosmicWebDimensionSettings Settings, CosmicWebDimension Result)> s_dCache = new();

    public static CosmicWebDimensionSettings DimensionSettingsOf(FractalParameters p)
        => CosmicWebDimensionSettings.Default(Math.Clamp(p.CosmicWebMeasureSample, FractalDimension.MinSample, FractalDimension.MaxSample));

    /// <summary>D(r) of <paramref name="pts"/> (cached on the point set instance and the settings).</summary>
    public static CosmicWebDimension DimensionFor(CosmicWebPoints pts, FractalParameters p, CancellationToken ct = default)
    {
        var s = DimensionSettingsOf(p);
        lock (s_dLock)
            for (int i = 0; i < s_dCache.Count; i++)
                if (s_dCache[i].Points.TryGetTarget(out var held) && ReferenceEquals(held, pts) && s_dCache[i].Settings == s)
                {
                    var hit = s_dCache[i];
                    s_dCache.RemoveAt(i);
                    s_dCache.Insert(0, hit);
                    return hit.Result;
                }
        var d = FractalDimension.Measure(pts, s, ct);
        lock (s_dLock)
        {
            s_dCache.Insert(0, (new WeakReference<CosmicWebPoints>(pts), s, d));
            if (s_dCache.Count > DimensionCacheSize) s_dCache.RemoveAt(s_dCache.Count - 1);
        }
        return d;
    }

    /// <summary>"D(r) [M of N points]: 0.3 Mpc/h 1.23, 1 Mpc/h ..." for the status bar.</summary>
    public static string DimensionStatus(CosmicWebDimension d)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append("; D(r) from ").Append(d.SampleSize.ToString("N0", inv)).Append(" of ").Append(d.TotalPoints.ToString("N0", inv)).Append(" points:");
        foreach (double f in StatusScales)
        {
            double r = f * d.BoxSize, v = d.DAt(r);
            sb.Append(' ').Append(r.ToString("0.###", inv)).Append(" Mpc/h ").Append(double.IsFinite(v) ? v.ToString("0.00", inv) : "n/a").Append(',');
        }
        sb.Length--;
        return sb.ToString();
    }

    private void MeasureDimension(FractalParameters p, CosmicWebPoints pts, double? modelD, CancellationToken ct)
    {
        LastDimension = null;
        if (!p.CosmicWebMeasure) return;
        CosmicWebDimension d;
        try { d = DimensionFor(pts, p, ct); }
        catch (OperationCanceledException) { return; }
        LastDimension = d;
        LastPrecisionLabel += DimensionStatus(d);
        if (p.CosmicWebDimensionOverlay) DrawDimensionInset(d, modelD);
    }

    private void DrawDimensionInset(CosmicWebDimension d, double? modelD)
    {
        int w = Width, h = Height;
        if (ColorBuffer.Length < w * h || w <= 0 || h <= 0) return;
        var handle = GCHandle.Alloc(ColorBuffer, GCHandleType.Pinned);
        try
        {
            var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var bmp = new SKBitmap();
            bmp.InstallPixels(info, handle.AddrOfPinnedObject(), info.RowBytes);
            using var canvas = new SKCanvas(bmp);
            float ui = Math.Max(0.6f, Math.Min(w, h) / 540f);
            float ph = (float)(h * DimensionPanelFraction), m = 10f * ui;
            var panel = new SKRect(m, h - ph - m, w - m, h - m);
            using (var bg = new SKPaint { Style = SKPaintStyle.Fill, Color = new SKColor(0, 0, 0, 0xB8) })
                canvas.DrawRect(panel, bg);
            var plot = new SKRect(panel.Left + 34f * ui, panel.Top + 10f * ui, panel.Right - 10f * ui, panel.Bottom - 34f * ui);
            if (plot.Width < 8 || plot.Height < 8) { canvas.Flush(); return; }   // a thumbnail: no room for the plot

            double lx0 = Math.Log10(d.RMin), lx1 = Math.Log10(d.RMax);
            double yHi = 3.5;
            foreach (double v in d.D) if (double.IsFinite(v)) yHi = Math.Max(yHi, Math.Min(v + 0.25, 6.0));
            float X(double r) => plot.Left + (float)((Math.Log10(r) - lx0) / (lx1 - lx0)) * plot.Width;
            float Y(double v) => plot.Bottom - (float)(Math.Clamp(v, 0.0, yHi) / yHi) * plot.Height;

            var inv = CultureInfo.InvariantCulture;
            using var axis = new SKPaint { Style = SKPaintStyle.Stroke, IsAntialias = true, StrokeWidth = 1f * ui, Color = new SKColor(0xA0FFFFFF) };
            using var font = new SKFont(SKTypeface.Default, 11f * ui);
            using var ink = new SKPaint { IsAntialias = true, Color = new SKColor(0xDDFFFFFF) };
            canvas.DrawLine(plot.Left, plot.Bottom, plot.Right, plot.Bottom, axis);
            canvas.DrawLine(plot.Left, plot.Top, plot.Left, plot.Bottom, axis);
            for (int t = 0; t <= (int)yHi; t++)
            {
                canvas.DrawLine(plot.Left - 3f * ui, Y(t), plot.Left, Y(t), axis);
                canvas.DrawText(t.ToString(inv), panel.Left + 6f * ui, Y(t) + 4f * ui, font, ink);
            }
            for (int e = (int)Math.Ceiling(lx0); e <= (int)Math.Floor(lx1); e++)
            {
                double r = Math.Pow(10, e);
                canvas.DrawLine(X(r), plot.Bottom, X(r), plot.Bottom + 3f * ui, axis);
                canvas.DrawText(r.ToString("0.###", inv), X(r) - 6f * ui, plot.Bottom + 14f * ui, font, ink);
            }
            canvas.DrawText("D(r) = 3 + d log(1 + xi) / d log r against r (Mpc/h); " + d.SampleSize.ToString("N0", inv)
                            + " of " + d.TotalPoints.ToString("N0", inv) + " points", plot.Left, panel.Bottom - 5f * ui, font, ink);

            void Level(double v, uint colour)
            {
                using var pen = new SKPaint { Style = SKPaintStyle.Stroke, IsAntialias = true, StrokeWidth = 2.2f * ui, Color = new SKColor(colour),
                                              PathEffect = SKPathEffect.CreateDash(new[] { 10f * ui, 6f * ui }, 0) };
                canvas.DrawLine(plot.Left, Y(v), plot.Right, Y(v), pen);
            }
            Level(3.0, HomogeneousColor);
            if (modelD is { } md && double.IsFinite(md)) Level(md, ModelColor);

            using var path = new SKPath();
            bool open = false;
            for (int k = 0; k < d.R.Length; k++)
            {
                if (!double.IsFinite(d.D[k])) { open = false; continue; }
                var pt = new SKPoint(X(d.R[k]), Y(d.D[k]));
                if (!open) { path.MoveTo(pt); open = true; } else path.LineTo(pt);
            }
            using var curve = new SKPaint { Style = SKPaintStyle.Stroke, IsAntialias = true, StrokeWidth = 1.8f * ui, Color = new SKColor(DimensionCurveColor) };
            canvas.DrawPath(path, curve);
            using var dot = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true, Color = new SKColor(DimensionCurveColor) };
            for (int k = 0; k < d.R.Length; k++)
                if (double.IsFinite(d.D[k])) canvas.DrawCircle(X(d.R[k]), Y(d.D[k]), 2.2f * ui, dot);
            canvas.Flush();
        }
        finally { handle.Free(); }
    }
}
