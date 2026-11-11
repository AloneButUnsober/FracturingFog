// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Engine/Calculators/MathPlane3DCalculator.cs
//
// #1713 (Math Plane 3D T1, #1556) — the Math Plane 3D render mode: axes, grid
// planes, points, vectors, segments and polylines in space under an orbit camera.
// Design: Docs/Technical/MathPlane3D-DesignPlan.md §3.3.
//
// A CPU renderer with a depth buffer (FF has no triangle pipeline: its GPU paths are
// compute kernels). The z-buffer is the hidden-surface method credited to Catmull's
// 1974 thesis (and Straßer's, independently) — see
// Docs/Resources-Bibliography.md#catmull-zbuffer. Passes, in order:
//   1. opaque triangles (here: vector arrowheads, flat-shaded cones) rasterized into
//      the colour buffer and a 1/z depth buffer, pixel centres sampled, top-left fill
//      rule so a closed mesh covers each pixel once;
//   2. lines (grid planes, axes, shafts, segments, polylines) clipped to the near
//      plane, projected, cut into ~3 px pieces; a piece is drawn (Skia, anti-aliased)
//      only where its mid-depth passes the z-buffer test, with a small relative bias
//      so lines on a surface stay visible;
//   3. points, labels and tick numbers in screen space, each shown only when its
//      anchor passes the depth test.
// Single-threaded and deterministic: the same params give the same pixels live and
// in batch.
//
// Camera (z up): eye = target + (distance / Zoom) · (sinφ cosθ, sinφ sinθ, cosφ),
// looking at the target; perspective with a 45° vertical field of view.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using FracturingFog.ViewState;
using SkiaSharp;

namespace FracturingFog;

/// <summary>#1713 — a point projected to the screen: pixel position and 1/z (view depth).</summary>
public readonly record struct Projected3D(double X, double Y, double InvZ);

/// <summary>#1713 — the orbit camera of a Math Plane 3D view.</summary>
public readonly record struct MathPlane3DCamera(
    (double X, double Y, double Z) Eye,
    (double X, double Y, double Z) Right,
    (double X, double Y, double Z) Up,
    (double X, double Y, double Z) Forward,
    double Focal, int Width, int Height)
{
    /// <summary>Near-plane distance in view space; geometry closer is clipped.</summary>
    public const double Near = 1e-3;

    public const double VerticalFovDegrees = 45.0;

    /// <summary>View-space (right, up, forward) coordinates of a world point.</summary>
    public (double X, double Y, double Z) View(double x, double y, double z)
    {
        double dx = x - Eye.X, dy = y - Eye.Y, dz = z - Eye.Z;
        return (dx * Right.X + dy * Right.Y + dz * Right.Z,
                dx * Up.X + dy * Up.Y + dz * Up.Z,
                dx * Forward.X + dy * Forward.Y + dz * Forward.Z);
    }

    /// <summary>A view-space point to the screen; null behind the near plane.</summary>
    public Projected3D? FromView(double vx, double vy, double vz)
        => vz <= Near ? null
         : new Projected3D(Width * 0.5 + Focal * vx / vz, Height * 0.5 - Focal * vy / vz, 1.0 / vz);

    /// <summary>A world point to the screen; null behind the near plane.</summary>
    public Projected3D? Project(double x, double y, double z)
    {
        var (vx, vy, vz) = View(x, y, z);
        return FromView(vx, vy, vz);
    }
}

/// <summary>#1713 — one screen-space triangle for the z-buffer pass.</summary>
public readonly record struct ScreenTriangle(Projected3D A, Projected3D B, Projected3D C, uint Color);

public sealed class MathPlane3DCalculator : IFractalCalculator, IStereoEyeCamera, IDepthAovSource
{
    public int Width { get; private set; }
    public int Height { get; private set; }
    public uint[] ColorBuffer { get; private set; } = Array.Empty<uint>();

    public double CenterX { get; set; }
    public double CenterY { get; set; }
    public double Zoom { get; set; } = 1.0;
    public int MaxIterations { get; set; } = 256;
    public QualityPreset Quality { get; set; } = QualityPreset.Standard;
    /// <summary>Unused: the scene carries its own colours.</summary>
    public IColorMap ColorMap { get; set; } = new HsvPalette();
    public bool SupportsZoomPan => true;
    public FractalParameters FractalParameters { get; set; } = new();

    /// <summary>The scene parse error of the last render, or null.</summary>
    public MathPlaneSceneError? LastSceneError { get; private set; }
    public string? LastPrecisionLabel => LastSceneError is { } e ? "Scene error: " + e : null;

    /// <summary>The z-buffer (1/z in view space; 0 = nothing drawn) of the last render.</summary>
    public float[] ZBuffer { get; private set; } = Array.Empty<float>();

    /// <summary>True stereo: the eye moves this far along the camera's right axis (world
    /// units); the view direction is unchanged (parallel eyes). 0 = mono.</summary>
    public double StereoEyeOffset { get; set; }

    /// <summary>Depth AOV for the depth-parallax stereo warp: per-pixel ray distance to the
    /// nearest triangle (<see cref="ScreenSpacePost.DepthMiss"/> where none), published only
    /// when <see cref="ScreenSpacePost.WantsDepthOutput"/> asks; null otherwise.</summary>
    public float[]? DepthBuffer { get; private set; }

    public const uint BackgroundColor = MathPlaneCalculator.BackgroundColor;
    public const uint GridColor = 0x33FFFFFF;
    public const uint AxisColor = 0xB3FFFFFF;
    public const uint TickLabelColor = MathPlaneCalculator.TickLabelColor;
    /// <summary>Relative depth bias for lines and points against the z-buffer.</summary>
    public const double DepthBias = 2e-3;

    private string? _parsedText;
    private MathPlane3DScene _scene = new();

    public MathPlane3DCalculator(int width, int height) => Resize(width, height);

    public void Resize(int width, int height)
    {
        Width = width;
        Height = height;
        ColorBuffer = new uint[Math.Max(0, width * height)];
        ZBuffer = new float[Math.Max(0, width * height)];
    }

    /// <summary>The camera for <paramref name="p"/> at view <paramref name="zoom"/>.</summary>
    public static MathPlane3DCamera CameraFor(FractalParameters p, double zoom, int w, int h)
    {
        double dist = Math.Max(1e-6, p.MathPlane3DCameraDistance) / Math.Max(1e-9, zoom);
        double th = p.MathPlane3DCameraTheta, ph = Math.Clamp(p.MathPlane3DCameraPhi, 1e-4, Math.PI - 1e-4);
        var target = (X: p.MathPlane3DTargetX, Y: p.MathPlane3DTargetY, Z: p.MathPlane3DTargetZ);
        var eye = (X: target.X + dist * Math.Sin(ph) * Math.Cos(th),
                   Y: target.Y + dist * Math.Sin(ph) * Math.Sin(th),
                   Z: target.Z + dist * Math.Cos(ph));
        var fwd = Normalize((target.X - eye.X, target.Y - eye.Y, target.Z - eye.Z));
        var right = Normalize(Cross(fwd, (0, 0, 1)));
        var up = Cross(right, fwd);
        double focal = h * 0.5 / Math.Tan(MathPlane3DCamera.VerticalFovDegrees * Math.PI / 360.0);
        return new MathPlane3DCamera(eye, right, up, fwd, focal, w, h);
    }

    public void Calculate(CancellationToken ct = default)
    {
        int w = Width, h = Height;
        if (w <= 0 || h <= 0) return;
        Array.Fill(ColorBuffer, BackgroundColor);
        Array.Clear(ZBuffer);
        DepthBuffer = null;

        string text = FractalParameters.MathPlane3DScene ?? "";
        if (text != _parsedText)
        {
            LastSceneError = MathPlane3DScene.TryParse(text, out _scene, out var err) ? null : err;
            _parsedText = text;
        }
        var scene = _scene;
        var cam = CameraFor(FractalParameters, Zoom, w, h);
        if (StereoEyeOffset != 0)
        {
            double o = StereoEyeOffset;
            cam = cam with { Eye = (cam.Eye.X + cam.Right.X * o, cam.Eye.Y + cam.Right.Y * o, cam.Eye.Z + cam.Right.Z * o) };
        }
        float ui = (float)Math.Clamp(Math.Min(w, h) / 720.0, 1.0, 4.0);
        double extent = Extent(scene, FractalParameters, Zoom);
        ct.ThrowIfCancellationRequested();

        // Pass 1: opaque triangles (arrowheads) into colour + depth.
        var tris = new List<ScreenTriangle>();
        foreach (var o in scene.Objects)
            if (o.Visible && o.Kind == MathPlaneObjectKind.Vector)
                AddArrowhead(tris, cam, o, extent);
        RasterizeAll(tris, ColorBuffer, ZBuffer, w, h);
        ct.ThrowIfCancellationRequested();
        var fx = FractalParameters.Lighting;
        if (ScreenSpacePost.WantsDepthOutput(in fx)) DepthBuffer = RayDepth(ZBuffer, cam, w, h);

        // Passes 2-3 on a Skia canvas over the buffer.
        var handle = GCHandle.Alloc(ColorBuffer, GCHandleType.Pinned);
        try
        {
            var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var bmp = new SKBitmap();
            bmp.InstallPixels(info, handle.AddrOfPinnedObject(), info.RowBytes);
            using var canvas = new SKCanvas(bmp);
            DrawLinesAndLabels(canvas, cam, scene, extent, ui);
            canvas.Flush();
        }
        finally { handle.Free(); }
    }

    /// <summary>Ray distance per pixel from a 1/z buffer: z / cos(angle off the view axis).</summary>
    public static float[] RayDepth(float[] zBuffer, MathPlane3DCamera cam, int w, int h)
    {
        var d = new float[w * h];
        for (int y = 0; y < h; y++)
        {
            double dy = (y + 0.5 - h * 0.5) / cam.Focal;
            for (int x = 0; x < w; x++)
            {
                float iz = zBuffer[y * w + x];
                if (iz <= 0) { d[y * w + x] = ScreenSpacePost.DepthMiss; continue; }
                double dx = (x + 0.5 - w * 0.5) / cam.Focal;
                d[y * w + x] = (float)(Math.Sqrt(1 + dx * dx + dy * dy) / iz);
            }
        }
        return d;
    }

    /// <summary>Half-size of the grid and axes: covers the objects and the view.</summary>
    public static double Extent(MathPlane3DScene scene, FractalParameters p, double zoom)
    {
        double m = 2;
        foreach (var o in scene.Objects)
            for (int i = 0; i < o.Coords.Count; i++) m = Math.Max(m, Math.Abs(o.Coords[i]) * 1.2);
        if (scene.Objects.Count == 0) m = Math.Max(m, 0.45 * p.MathPlane3DCameraDistance / Math.Max(1e-9, zoom));
        var (s, e) = GridAxis.NiceStep(m / 4);
        double step = s * Math.Pow(10, e);
        return Math.Ceiling(m / step) * step;
    }

    // ── Pass 1: triangles ────────────────────────────────────────────────────

    /// <summary>Rasterize <paramref name="tris"/> into colour + 1/z depth (nearer = larger 1/z).</summary>
    public static void RasterizeAll(IReadOnlyList<ScreenTriangle> tris, uint[] color, float[] depth, int w, int h)
    {
        foreach (var t in tris) Rasterize(t, color, depth, w, h);
    }

    /// <summary>Fill one triangle: pixel centres inside it (top-left rule on shared edges)
    /// whose interpolated 1/z beats the depth buffer.</summary>
    public static void Rasterize(in ScreenTriangle t, uint[] color, float[] depth, int w, int h)
    {
        var a = t.A; var b = t.B; var c = t.C;
        double area = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        if (area == 0 || !double.IsFinite(area)) return;
        if (area < 0) { (b, c) = (c, b); area = -area; }
        int x0 = Math.Max(0, (int)Math.Floor(Math.Min(a.X, Math.Min(b.X, c.X))));
        int x1 = Math.Min(w - 1, (int)Math.Ceiling(Math.Max(a.X, Math.Max(b.X, c.X))));
        int y0 = Math.Max(0, (int)Math.Floor(Math.Min(a.Y, Math.Min(b.Y, c.Y))));
        int y1 = Math.Min(h - 1, (int)Math.Ceiling(Math.Max(a.Y, Math.Max(b.Y, c.Y))));
        if (x0 > x1 || y0 > y1) return;

        // Edge (p -> q) is "top-left" in screen space (y down) with positive area winding.
        static bool TopLeft(Projected3D p, Projected3D q) => (q.Y == p.Y && q.X < p.X) || q.Y > p.Y;
        bool tlA = TopLeft(b, c), tlB = TopLeft(c, a), tlC = TopLeft(a, b);
        for (int y = y0; y <= y1; y++)
        {
            double py = y + 0.5;
            for (int x = x0; x <= x1; x++)
            {
                double px = x + 0.5;
                double wa = (c.X - b.X) * (py - b.Y) - (c.Y - b.Y) * (px - b.X);
                double wb = (a.X - c.X) * (py - c.Y) - (a.Y - c.Y) * (px - c.X);
                double wc = (b.X - a.X) * (py - a.Y) - (b.Y - a.Y) * (px - a.X);
                if (wa < 0 || wb < 0 || wc < 0) continue;
                if ((wa == 0 && !tlA) || (wb == 0 && !tlB) || (wc == 0 && !tlC)) continue;
                double iz = (wa * a.InvZ + wb * b.InvZ + wc * c.InvZ) / area;
                int i = y * w + x;
                if (iz <= depth[i]) continue;
                depth[i] = (float)iz;
                color[i] = t.Color;
            }
        }
    }

    /// <summary>A screen point with 1/z <paramref name="invZ"/> is in front of (or on) what the
    /// depth buffer holds at its pixel, within <paramref name="bias"/> (relative).</summary>
    public static bool PassesDepth(float[] depth, int w, int h, double x, double y, double invZ, double bias = DepthBias)
    {
        int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y);
        if (ix < 0 || iy < 0 || ix >= w || iy >= h) return true;
        return invZ * (1 + bias) >= depth[iy * w + ix];
    }

    private static void AddArrowhead(List<ScreenTriangle> tris, MathPlane3DCamera cam, MathPlaneObject o, double extent)
    {
        var tip = MathPlane3DScene.Tip(o);
        var d = (X: o.Coords[0], Y: o.Coords[1], Z: o.Coords[2]);
        double len = Math.Sqrt(d.X * d.X + d.Y * d.Y + d.Z * d.Z);
        if (len < 1e-12) return;
        var dir = (d.X / len, d.Y / len, d.Z / len);
        double hl = ArrowheadLength(len, extent), r = hl * 0.38;
        var baseC = (X: tip.X - dir.Item1 * hl, Y: tip.Y - dir.Item2 * hl, Z: tip.Z - dir.Item3 * hl);
        // An orthonormal frame round the shaft.
        var any = Math.Abs(dir.Item3) < 0.9 ? (0.0, 0.0, 1.0) : (1.0, 0.0, 0.0);
        var u = Normalize(Cross(dir, any));
        var v = Cross(dir, u);
        const int n = 10;
        var ring = new (double X, double Y, double Z)[n];
        for (int k = 0; k < n; k++)
        {
            double a = 2 * Math.PI * k / n, ca = Math.Cos(a), sa = Math.Sin(a);
            ring[k] = (baseC.X + r * (ca * u.X + sa * v.X), baseC.Y + r * (ca * u.Y + sa * v.Y), baseC.Z + r * (ca * u.Z + sa * v.Z));
        }
        for (int k = 0; k < n; k++)
        {
            var p0 = ring[k]; var p1 = ring[(k + 1) % n];
            AddShaded(tris, cam, tip, p0, p1, o.Color);       // side
            AddShaded(tris, cam, baseC, p1, p0, o.Color);     // base disc
        }
    }

    /// <summary>The arrowhead's length: a fifth of the vector, capped by the scene size.</summary>
    public static double ArrowheadLength(double vectorLength, double extent)
        => Math.Min(0.22 * vectorLength, 0.06 * extent);

    // Flat Lambert shading from a light over the camera's shoulder; triangles crossing the
    // near plane are skipped (T1 geometry stays in front of the camera).
    private static void AddShaded(List<ScreenTriangle> tris, MathPlane3DCamera cam,
                                  (double X, double Y, double Z) a, (double X, double Y, double Z) b, (double X, double Y, double Z) c, uint color)
    {
        if (cam.Project(a.X, a.Y, a.Z) is not { } pa || cam.Project(b.X, b.Y, b.Z) is not { } pb
            || cam.Project(c.X, c.Y, c.Z) is not { } pc) return;
        var nrm = Normalize(Cross((b.X - a.X, b.Y - a.Y, b.Z - a.Z), (c.X - a.X, c.Y - a.Y, c.Z - a.Z)));
        var light = Normalize((-cam.Forward.X + 0.4 * cam.Up.X + 0.3 * cam.Right.X,
                               -cam.Forward.Y + 0.4 * cam.Up.Y + 0.3 * cam.Right.Y,
                               -cam.Forward.Z + 0.4 * cam.Up.Z + 0.3 * cam.Right.Z));
        double lam = Math.Abs(nrm.X * light.X + nrm.Y * light.Y + nrm.Z * light.Z);
        tris.Add(new ScreenTriangle(pa, pb, pc, Shade(color, 0.35 + 0.65 * lam)));
    }

    private static uint Shade(uint c, double k)
    {
        uint r = (uint)Math.Min(255, Math.Round(((c >> 16) & 0xFF) * k));
        uint g = (uint)Math.Min(255, Math.Round(((c >> 8) & 0xFF) * k));
        uint b = (uint)Math.Min(255, Math.Round((c & 0xFF) * k));
        return 0xFF000000u | (r << 16) | (g << 8) | b;
    }

    // ── Passes 2-3: lines, points, labels ────────────────────────────────────

    private void DrawLinesAndLabels(SKCanvas canvas, MathPlane3DCamera cam, MathPlane3DScene scene, double extent, float ui)
    {
        int w = Width, h = Height;
        using var line = new SKPaint { Style = SKPaintStyle.Stroke, IsAntialias = true, StrokeCap = SKStrokeCap.Round };
        using var font = new SKFont(SKTypeface.Default, 13f * ui);
        using var halo = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true, Color = new SKColor(0xC0000000) };
        using var ink = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };

        var (sS, sE) = GridAxis.NiceStep(extent / 4);
        double step = sS * Math.Pow(10, sE);
        var axis = GridAxis.Build(stackalloc double[] { 0.0 }, extent, sS, sE);

        // Grid planes.
        line.StrokeWidth = 1f * ui;
        line.Color = new SKColor(GridColor);
        int nLines = (int)Math.Round(extent / step);
        for (int k = -nLines; k <= nLines; k++)
        {
            double c = k * step;
            if ((scene.Grid & MathPlane3DGrid.XY) != 0)
            {
                DrawLine3D(canvas, cam, (c, -extent, 0), (c, extent, 0), line);
                DrawLine3D(canvas, cam, (-extent, c, 0), (extent, c, 0), line);
            }
            if ((scene.Grid & MathPlane3DGrid.XZ) != 0)
            {
                DrawLine3D(canvas, cam, (c, 0, -extent), (c, 0, extent), line);
                DrawLine3D(canvas, cam, (-extent, 0, c), (extent, 0, c), line);
            }
            if ((scene.Grid & MathPlane3DGrid.YZ) != 0)
            {
                DrawLine3D(canvas, cam, (0, c, -extent), (0, c, extent), line);
                DrawLine3D(canvas, cam, (0, -extent, c), (0, extent, c), line);
            }
        }

        // Axes, their names and tick numbers.
        if (scene.ShowAxes)
        {
            line.StrokeWidth = 1.6f * ui;
            line.Color = new SKColor(AxisColor);
            DrawLine3D(canvas, cam, (-extent, 0, 0), (extent, 0, 0), line);
            DrawLine3D(canvas, cam, (0, -extent, 0), (0, extent, 0), line);
            DrawLine3D(canvas, cam, (0, 0, -extent), (0, 0, extent), line);
            ink.Color = new SKColor(AxisColor);
            Text(canvas, cam, (extent * 1.06, 0, 0), "x", font, halo, ink, ui);
            Text(canvas, cam, (0, extent * 1.06, 0), "y", font, halo, ink, ui);
            Text(canvas, cam, (0, 0, extent * 1.06), "z", font, halo, ink, ui);
            if (scene.ShowTicks)
            {
                var labels = GridLabelFormatter.Format(axis, scene.Labels).Labels;
                ink.Color = new SKColor(TickLabelColor);
                using var small = new SKFont(SKTypeface.Default, 11f * ui);
                for (int i = 0; i < axis.Ticks.Count && i < labels.Count; i++)
                {
                    if (axis.Ticks[i].IsZero) continue;
                    double v = axis.Ticks[i].OffsetFromCentre;
                    Text(canvas, cam, (v, 0, 0), labels[i], small, halo, ink, ui);
                    Text(canvas, cam, (0, v, 0), labels[i], small, halo, ink, ui);
                    Text(canvas, cam, (0, 0, v), labels[i], small, halo, ink, ui);
                }
            }
        }

        // Objects.
        using var dot = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };
        using var outline = new SKPaint { Style = SKPaintStyle.Stroke, IsAntialias = true, StrokeWidth = 1.5f * ui, Color = new SKColor(0xFF000000) };
        foreach (var o in scene.Objects)
        {
            if (!o.Visible) continue;
            var col = new SKColor(o.Color);
            line.Color = col;
            line.StrokeWidth = 2.5f * ui;
            ink.Color = col;
            switch (o.Kind)
            {
                case MathPlaneObjectKind.Point:
                {
                    var p = MathPlane3DScene.Point(o, 0);
                    if (cam.Project(p.X, p.Y, p.Z) is { } pp && PassesDepth(ZBuffer, w, h, pp.X, pp.Y, pp.InvZ))
                    {
                        dot.Color = col;
                        canvas.DrawCircle((float)pp.X, (float)pp.Y, 5f * ui, dot);
                        canvas.DrawCircle((float)pp.X, (float)pp.Y, 5f * ui, outline);
                    }
                    if (o.ShowLabel) Text(canvas, cam, p, o.Name, font, halo, ink, ui);
                    break;
                }
                case MathPlaneObjectKind.Vector:
                {
                    var tail = MathPlane3DScene.Tail(o);
                    var tip = MathPlane3DScene.Tip(o);
                    double len = Math.Sqrt(o.Coords[0] * o.Coords[0] + o.Coords[1] * o.Coords[1] + o.Coords[2] * o.Coords[2]);
                    if (len > 1e-12)
                    {
                        double k = 1 - ArrowheadLength(len, extent) / len;
                        var shaftEnd = (tail.X + o.Coords[0] * k, tail.Y + o.Coords[1] * k, tail.Z + o.Coords[2] * k);
                        DrawLine3D(canvas, cam, tail, shaftEnd, line);
                    }
                    if (o.ShowLabel) Text(canvas, cam, tip, o.Name, font, halo, ink, ui);
                    break;
                }
                case MathPlaneObjectKind.Segment:
                case MathPlaneObjectKind.Polyline:
                {
                    int n = o.Coords.Count / 3;
                    for (int i = 1; i < n; i++) DrawLine3D(canvas, cam, MathPlane3DScene.Point(o, i - 1), MathPlane3DScene.Point(o, i), line);
                    if (o.ShowLabel) Text(canvas, cam, MathPlane3DScene.Point(o, n - 1), o.Name, font, halo, ink, ui);
                    break;
                }
            }
        }

        if (LastSceneError is { } err)
        {
            ink.Color = new SKColor(MathPlaneCalculator.ErrorColor);
            canvas.DrawText("Scene error: " + err, 6f * ui, 6f * ui - font.Metrics.Ascent, font, ink);
        }
    }

    /// <summary>Draw a world segment: clipped to the near plane, cut into ~3 px pieces, each
    /// drawn only where its mid-depth passes the z-buffer test.</summary>
    private void DrawLine3D(SKCanvas canvas, MathPlane3DCamera cam,
                            (double X, double Y, double Z) a, (double X, double Y, double Z) b, SKPaint paint)
    {
        var va = cam.View(a.X, a.Y, a.Z);
        var vb = cam.View(b.X, b.Y, b.Z);
        const double near = MathPlane3DCamera.Near * 10;
        if (va.Z <= near && vb.Z <= near) return;
        if (va.Z <= near || vb.Z <= near)
        {
            double t = (near - va.Z) / (vb.Z - va.Z);
            var cut = (va.X + (vb.X - va.X) * t, va.Y + (vb.Y - va.Y) * t, near);
            if (va.Z <= near) va = cut; else vb = cut;
        }
        if (cam.FromView(va.X, va.Y, va.Z) is not { } pa || cam.FromView(vb.X, vb.Y, vb.Z) is not { } pb) return;
        double lenPx = Math.Sqrt((pb.X - pa.X) * (pb.X - pa.X) + (pb.Y - pa.Y) * (pb.Y - pa.Y));
        if (!double.IsFinite(lenPx)) return;
        int pieces = Math.Clamp((int)Math.Ceiling(lenPx / 3.0), 1, 4096);
        using var path = new SKPath();
        bool open = false;
        for (int k = 0; k < pieces; k++)
        {
            double t0 = (double)k / pieces, t1 = (double)(k + 1) / pieces, tm = (t0 + t1) * 0.5;
            // Screen position and 1/z are both linear in screen space.
            double mx = pa.X + (pb.X - pa.X) * tm, my = pa.Y + (pb.Y - pa.Y) * tm;
            double mz = pa.InvZ + (pb.InvZ - pa.InvZ) * tm;
            if (PassesDepth(ZBuffer, Width, Height, mx, my, mz))
            {
                if (!open) { path.MoveTo((float)(pa.X + (pb.X - pa.X) * t0), (float)(pa.Y + (pb.Y - pa.Y) * t0)); open = true; }
                path.LineTo((float)(pa.X + (pb.X - pa.X) * t1), (float)(pa.Y + (pb.Y - pa.Y) * t1));
            }
            else open = false;
        }
        canvas.DrawPath(path, paint);
    }

    private void Text(SKCanvas canvas, MathPlane3DCamera cam, (double X, double Y, double Z) at, string s,
                      SKFont font, SKPaint halo, SKPaint ink, float ui)
    {
        if (cam.Project(at.X, at.Y, at.Z) is not { } p || !PassesDepth(ZBuffer, Width, Height, p.X, p.Y, p.InvZ)) return;
        float x = (float)p.X + 6f * ui, y = (float)p.Y - 6f * ui;
        canvas.DrawText(s, x + ui, y + ui, font, halo);
        canvas.DrawText(s, x, y, font, ink);
    }

    // ── Vector helpers ───────────────────────────────────────────────────────

    private static (double X, double Y, double Z) Cross((double X, double Y, double Z) a, (double X, double Y, double Z) b)
        => (a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    private static (double X, double Y, double Z) Normalize((double X, double Y, double Z) v)
    {
        double l = Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
        return l > 0 ? (v.X / l, v.Y / l, v.Z / l) : (0, 0, 0);
    }
}
