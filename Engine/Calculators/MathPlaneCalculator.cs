// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// MathPlaneCalculator.cs (#1548, slice S1 of the Math Plane epic #1547)
//
// An interactive Cartesian plane as a render mode: axes, grid, tick labels and
// the scene's objects (points, vectors, segments, polylines), drawn with
// SkiaSharp straight into ColorBuffer. Being an ordinary IFractalCalculator
// raster means --batch, poster export and video render it unchanged, and it
// shows under the native GPU surface (an Avalonia overlay would not).
// Design: Docs/Technical/MathPlane-DesignPlan.md §3.1.
//
// The scene is FractalParameters.MathPlaneScene (text, MathPlaneScene). A scene
// that does not parse renders the empty plane plus the error in yellow; batch
// refuses it up front (BatchRenderer), so a bad --param never writes a file.
//
// View: the standard 2D plane mapping (ChaoticBilliardCalculator.PlaneAt: pitch
// 4 / width / zoom), with world y = -plane y so the picture is y-up while pans
// still follow the pointer (the Billiard Rays convention). One world unit is one
// coordinate unit, so (1, 0) is i-hat.
//
// Ticks and labels come from GridAxis / GridLabelFormatter (#1559), the same
// formatter as the grid overlay, with the scene's label mode.
//
// Determinism: one Skia CPU raster pass in a fixed draw order (grid, axes,
// labels, then objects in scene order), so live, batch and export match.
//
// #1550 — curves (explicit / parametric / polar) are sampled per view by
// MathPlaneCurveSampler (adaptive, breaks at gaps and poles) and stroked.

using System;
using System.Runtime.InteropServices;
using System.Threading;

using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.ViewState;

using SkiaSharp;

namespace FracturingFog;

public sealed class MathPlaneCalculator : IFractalCalculator
{
    public int Width { get; private set; }
    public int Height { get; private set; }
    public uint[] ColorBuffer { get; private set; } = Array.Empty<uint>();

    public double CenterX { get; set; } = 0.0;
    public double CenterY { get; set; } = 0.0;
    public double Zoom { get; set; } = 0.5;
    public int MaxIterations { get; set; } = 256;
    public QualityPreset Quality { get; set; } = QualityPreset.Standard;
    /// <summary>Unused: the plane has its own colours (objects carry theirs).</summary>
    public IColorMap ColorMap { get; set; } = new HsvPalette();
    public bool SupportsZoomPan => true;

    public FractalParameters FractalParameters { get; set; } = new();

    /// <summary>The scene parse error of the last render, or null.</summary>
    public MathPlaneSceneError? LastSceneError { get; private set; }

    /// <summary>Status-bar text: the scene error, if any.</summary>
    public string? LastPrecisionLabel => LastSceneError is { } e ? "Scene error: " + e : null;

    public const uint BackgroundColor = 0xFF14161B;
    public const uint MinorGridColor  = 0x22FFFFFF;
    public const uint MajorGridColor  = 0x4DFFFFFF;
    public const uint AxisColor       = 0xD9FFFFFF;
    public const uint TickLabelColor  = 0xCCDCDCDC;
    /// <summary>Errors are yellow, not red (colour-blind-safe convention).</summary>
    public const uint ErrorColor      = 0xFFFFCC00;

    private string? _parsedText;
    private MathPlaneScene _scene = new();

    public MathPlaneCalculator(int width, int height) => Resize(width, height);

    public void Resize(int width, int height)
    {
        Width = width;
        Height = height;
        ColorBuffer = new uint[Math.Max(0, width * height)];
    }

    /// <summary>Plane units per pixel for a view (ChaoticBilliardCalculator.PlaneAt's pitch).</summary>
    public static double Pitch(int width, double zoom)
        => (4.0 / Math.Max(1.0, width)) / Math.Max(1e-300, zoom);

    /// <summary>World point → pixel (y-up world, pixel centres at +0.5 not applied:
    /// pixel x spans [x, x + 1)).</summary>
    public static (double X, double Y) ToPixel(double wx, double wy, int width, int height,
                                               double centerX, double centerY, double zoom)
    {
        double pitch = Pitch(width, zoom);
        return ((wx - centerX) / pitch + width * 0.5, height * 0.5 - (wy + centerY) / pitch);
    }

    public void Calculate(CancellationToken ct = default)
    {
        int w = Width, h = Height;
        if (w <= 0 || h <= 0) return;
        Array.Fill(ColorBuffer, BackgroundColor);

        string text = FractalParameters.MathPlaneScene ?? "";
        if (text != _parsedText)
        {
            LastSceneError = MathPlaneScene.TryParse(text, out _scene, out var err) ? null : err;
            _parsedText = text;
        }
        ct.ThrowIfCancellationRequested();

        var handle = GCHandle.Alloc(ColorBuffer, GCHandleType.Pinned);
        try
        {
            var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var bmp = new SKBitmap();
            bmp.InstallPixels(info, handle.AddrOfPinnedObject(), info.RowBytes);
            using var canvas = new SKCanvas(bmp);
            Draw(canvas, w, h, _scene, LastSceneError);
            canvas.Flush();
        }
        finally { handle.Free(); }
    }

    // ── Drawing ──────────────────────────────────────────────────────────────

    private void Draw(SKCanvas canvas, int w, int h, MathPlaneScene scene, MathPlaneSceneError? error)
    {
        // Sizes follow the output so a 4K batch still reads like the live view.
        float ui = (float)Math.Clamp(Math.Min(w, h) / 720.0, 1.0, 4.0);
        double pitch = Pitch(w, Zoom);
        double wcx = CenterX, wcy = -CenterY;          // world centre (y-up)
        double halfW = w * 0.5 * pitch, halfH = h * 0.5 * pitch;

        var (stepS, stepE) = GridAxis.NiceStep(2 * halfW / 8.0);
        var xAxis = GridAxis.Build(stackalloc double[] { wcx }, halfW, stepS, stepE);
        var yAxis = GridAxis.Build(stackalloc double[] { wcy }, halfH, stepS, stepE);

        double Px(double off) => off / pitch + w * 0.5;
        double Py(double off) => h * 0.5 - off / pitch;

        using var line = new SKPaint { Style = SKPaintStyle.Stroke, IsAntialias = true };

        if (scene.ShowGrid)
        {
            // Minor lines: the next 1-2-5 step below a fifth of the major step.
            var (mS, mE) = GridAxis.NiceStep(xAxis.Step / 5 * 0.99);
            var xMinor = GridAxis.Build(stackalloc double[] { wcx }, halfW, mS, mE);
            var yMinor = GridAxis.Build(stackalloc double[] { wcy }, halfH, mS, mE);
            line.StrokeWidth = 1f * ui;
            line.Color = new SKColor(MinorGridColor);
            foreach (var t in xMinor.Ticks) { float x = (float)Px(t.OffsetFromCentre); canvas.DrawLine(x, 0, x, h, line); }
            foreach (var t in yMinor.Ticks) { float y = (float)Py(t.OffsetFromCentre); canvas.DrawLine(0, y, w, y, line); }

            line.Color = new SKColor(MajorGridColor);
            foreach (var t in xAxis.Ticks) if (!t.IsZero || !scene.ShowAxes) { float x = (float)Px(t.OffsetFromCentre); canvas.DrawLine(x, 0, x, h, line); }
            foreach (var t in yAxis.Ticks) if (!t.IsZero || !scene.ShowAxes) { float y = (float)Py(t.OffsetFromCentre); canvas.DrawLine(0, y, w, y, line); }
        }

        // The axes' pixel positions (off-screen when the origin is out of view).
        float axisX = (float)Px(-wcx), axisY = (float)Py(-wcy);
        if (scene.ShowAxes)
        {
            line.StrokeWidth = 1.6f * ui;
            line.Color = new SKColor(AxisColor);
            if (axisY >= 0 && axisY <= h) canvas.DrawLine(0, axisY, w, axisY, line);
            if (axisX >= 0 && axisX <= w) canvas.DrawLine(axisX, 0, axisX, h, line);
        }

        using var font = new SKFont(SKTypeface.Default, 12f * ui);
        using var halo = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true, Color = new SKColor(0xB0000000) };
        using var ink = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true, Color = new SKColor(TickLabelColor) };
        float asc = -font.Metrics.Ascent, lineH = font.Metrics.Descent - font.Metrics.Ascent;

        void Text(string s, float x, float top, SKPaint paint)
        {
            canvas.DrawText(s, x + ui, top + asc + ui, font, halo);
            canvas.DrawText(s, x, top + asc, font, paint);
        }

        if (scene.ShowTicks)
        {
            var xl = GridLabelFormatter.Format(xAxis, scene.Labels);
            var yl = GridLabelFormatter.Format(yAxis, scene.Labels);
            float pad = 4f * ui;
            // x labels under the x-axis, kept inside the frame when it is off-screen.
            float xTop = Math.Clamp(axisY + pad, pad, h - lineH - pad);
            for (int i = 0; i < xAxis.Ticks.Count; i++)
            {
                var t = xAxis.Ticks[i];
                if (t.IsZero && scene.ShowAxes) continue;     // the origin gets one "0"
                string s = xl.Anchor != null && xl.Labels[i] == "0" ? "x0" : xl.Labels[i];
                float tw = font.MeasureText(s);
                float x = Math.Clamp((float)Px(t.OffsetFromCentre) - tw * 0.5f, pad, Math.Max(pad, w - tw - pad));
                Text(s, x, xTop, ink);
            }
            // y labels left of the y-axis, kept inside the frame.
            for (int i = 0; i < yAxis.Ticks.Count; i++)
            {
                var t = yAxis.Ticks[i];
                if (t.IsZero && scene.ShowAxes) continue;
                string s = yl.Anchor != null && yl.Labels[i] == "0" ? "y0" : yl.Labels[i];
                float tw = font.MeasureText(s);
                float x = Math.Clamp(axisX - tw - pad, pad, Math.Max(pad, w - tw - pad));
                Text(s, x, (float)Py(t.OffsetFromCentre) - lineH * 0.5f, ink);
            }
            if (scene.ShowAxes && axisX >= 0 && axisX <= w && axisY >= 0 && axisY <= h)
                Text("0", axisX - font.MeasureText("0") - pad, axisY + pad, ink);

            // Offset form: the anchors, bottom right.
            float aTop = h - lineH - pad;
            if (yl.Anchor != null) { string a = "y0 = " + yl.Anchor; Text(a, w - font.MeasureText(a) - pad, aTop, ink); aTop -= lineH; }
            if (xl.Anchor != null) { string a = "x0 = " + xl.Anchor; Text(a, w - font.MeasureText(a) - pad, aTop, ink); }
        }

        DrawObjects(canvas, w, h, scene, ui);

        if (error != null)
        {
            using var errInk = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true, Color = new SKColor(ErrorColor) };
            Text("Scene error: " + error, 6f * ui, 6f * ui, errInk);
        }
    }

    private void DrawObjects(SKCanvas canvas, int w, int h, MathPlaneScene scene, float ui)
    {
        using var stroke = new SKPaint { Style = SKPaintStyle.Stroke, IsAntialias = true, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
        using var fill = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };
        using var outline = new SKPaint { Style = SKPaintStyle.Stroke, IsAntialias = true, StrokeWidth = 1.5f * ui, Color = new SKColor(0xFF000000) };
        using var font = new SKFont(SKTypeface.Default, 15f * ui);
        using var halo = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true, Color = new SKColor(0xC0000000) };

        SKPoint P(double x, double y)
        {
            var (px, py) = ToPixel(x, y, w, h, CenterX, CenterY, Zoom);
            // Far off-screen points are clamped so Skia's float maths stays sane;
            // anything past this is invisible anyway.
            const double lim = 1e7;
            return new SKPoint((float)Math.Clamp(px, -lim, lim), (float)Math.Clamp(py, -lim, lim));
        }

        void Label(MathPlaneObject o, SKPoint at)
        {
            if (!o.ShowLabel) return;
            float x = at.X + 7f * ui, baseline = at.Y - 7f * ui;
            canvas.DrawText(o.Name, x + ui, baseline + ui, font, halo);
            fill.Color = new SKColor(o.Color);
            canvas.DrawText(o.Name, x, baseline, font, fill);
        }

        foreach (var o in scene.Objects)
        {
            if (!o.Visible) continue;
            var c = new SKColor(o.Color);
            stroke.Color = c;
            fill.Color = c;
            switch (o.Kind)
            {
                case MathPlaneObjectKind.Point:
                {
                    var p = P(o.Coords[0], o.Coords[1]);
                    canvas.DrawCircle(p, 5f * ui, fill);
                    canvas.DrawCircle(p, 5f * ui, outline);
                    Label(o, p);
                    break;
                }
                case MathPlaneObjectKind.Vector:
                {
                    var (tx, ty) = o.Tail;
                    var tail = P(tx, ty);
                    var tip = P(tx + o.Coords[0], ty + o.Coords[1]);
                    DrawArrow(canvas, tail, tip, stroke, fill, ui);
                    Label(o, tip);
                    break;
                }
                case MathPlaneObjectKind.Segment:
                {
                    stroke.StrokeWidth = 2.5f * ui;
                    var a = P(o.Coords[0], o.Coords[1]);
                    var b = P(o.Coords[2], o.Coords[3]);
                    canvas.DrawLine(a, b, stroke);
                    Label(o, new SKPoint((a.X + b.X) * 0.5f, (a.Y + b.Y) * 0.5f));
                    break;
                }
                case MathPlaneObjectKind.Curve:
                {
                    stroke.StrokeWidth = 2.5f * ui;
                    var lines = MathPlaneCurveSampler.Sample(o, ViewBox(w, h));
                    SKPoint? labelAt = null;
                    foreach (var poly in lines)
                    {
                        using var path = new SKPath();
                        path.MoveTo(P(poly[0].X, poly[0].Y));
                        for (int i = 1; i < poly.Count; i++) path.LineTo(P(poly[i].X, poly[i].Y));
                        canvas.DrawPath(path, stroke);
                        // Label at the last on-screen vertex (the right / far end).
                        for (int i = poly.Count - 1; i >= 0; i--)
                        {
                            var q = P(poly[i].X, poly[i].Y);
                            if (q.X >= 0 && q.X < w && q.Y >= 0 && q.Y < h) { labelAt ??= q; break; }
                        }
                    }
                    if (labelAt is { } la)
                    {
                        float lx = Math.Min(la.X, w - 30f * ui), ly = Math.Max(la.Y, 24f * ui);
                        Label(o, new SKPoint(lx, ly));
                    }
                    break;
                }
                case MathPlaneObjectKind.Polyline:
                {
                    stroke.StrokeWidth = 2.5f * ui;
                    using var path = new SKPath();
                    path.MoveTo(P(o.Coords[0], o.Coords[1]));
                    for (int i = 1; i < o.PointCount; i++) path.LineTo(P(o.Coords[2 * i], o.Coords[2 * i + 1]));
                    canvas.DrawPath(path, stroke);
                    Label(o, P(o.Coords[^2], o.Coords[^1]));
                    break;
                }
            }
        }
    }

    /// <summary>#1550 — the visible world rectangle of the current view (y-up).</summary>
    public MathPlaneViewBox ViewBox(int w, int h)
    {
        double pitch = Pitch(w, Zoom);
        double wcy = -CenterY;
        return new MathPlaneViewBox(CenterX - w * 0.5 * pitch, CenterX + w * 0.5 * pitch,
                                    wcy - h * 0.5 * pitch, wcy + h * 0.5 * pitch, pitch);
    }

    /// <summary>Shaft plus a filled head whose point is exactly at <paramref name="tip"/>.
    /// A vector shorter on screen than its head draws the head alone, scaled down.</summary>
    private static void DrawArrow(SKCanvas canvas, SKPoint tail, SKPoint tip, SKPaint stroke, SKPaint fill, float ui)
    {
        float dx = tip.X - tail.X, dy = tip.Y - tail.Y;
        float len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 1e-3f) return;
        float ux = dx / len, uy = dy / len;
        float headLen = Math.Min(14f * ui, len), headHalf = headLen * 0.45f;
        var baseCentre = new SKPoint(tip.X - ux * headLen, tip.Y - uy * headLen);

        stroke.StrokeWidth = 3f * ui;
        stroke.StrokeCap = SKStrokeCap.Butt;
        if (len > headLen) canvas.DrawLine(tail, baseCentre, stroke);
        stroke.StrokeCap = SKStrokeCap.Round;

        using var head = new SKPath();
        head.MoveTo(tip);
        head.LineTo(baseCentre.X - uy * headHalf, baseCentre.Y + ux * headHalf);
        head.LineTo(baseCentre.X + uy * headHalf, baseCentre.Y - ux * headHalf);
        head.Close();
        canvas.DrawPath(head, fill);
    }
}
