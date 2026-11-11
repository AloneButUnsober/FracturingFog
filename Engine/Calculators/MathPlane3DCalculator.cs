// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Engine/Calculators/MathPlane3DCalculator.cs
//
// #1713 (Math Plane 3D T1, #1556) — the Math Plane 3D render mode: axes, grid
// planes, points, vectors, segments and polylines in space under an orbit camera.
// #1714 (T2) adds surfaces (explicit z = f(x, y), parametric (u, v)), space curves,
// flat / Gouraud shading and a translucent pass. Design:
// Docs/Technical/MathPlane3D-DesignPlan.md §3.3, §3.5.
//
// A CPU renderer with a depth buffer (FF has no triangle pipeline: its GPU paths are
// compute kernels). The z-buffer is the hidden-surface method credited to Catmull's
// 1974 thesis (and Straßer's, independently) — see
// Docs/Resources-Bibliography.md#catmull-zbuffer. Passes, in order:
//   1. opaque triangles (vector arrowheads, opaque surfaces) are clipped to the near
//      plane and rasterized into the colour buffer and a 1/z depth buffer: pixel
//      centres sampled, top-left fill rule so a closed mesh covers each pixel once,
//      colours interpolated across the triangle for Gouraud shading (Gouraud 1971,
//      Docs/Resources-Bibliography.md#gouraud-shading); triangles crossing the near plane
//      are clipped against it as in Sutherland & Hodgman's reentrant polygon clipping
//      (#sutherland-hodgman-clipping); shading is Lambert's cosine law, two-sided, plus
//      ambient (#lambert-cosine-law);
//   2. translucent triangles (#RRGGBBAA surfaces, filled grid planes) are sorted back
//      to front by centroid depth — the painter's order of Newell, Newell & Sancha's
//      depth sort (Docs/Resources-Bibliography.md#newell-depth-sort), without their
//      face splitting, so intersecting translucent faces can mis-sort — and blended
//      over the opaque result, depth-tested against it but not writing depth;
//   3. lines (grid planes, axes, shafts, segments, polylines, space curves) clipped to
//      the near plane, projected, cut into ~3 px pieces; a piece is drawn (Skia,
//      anti-aliased) only where its mid-depth passes the z-buffer test, with a small
//      relative bias so lines on a surface stay visible. Pieces behind the nearest
//      translucent layer are drawn before the blend (so they are tinted), the others
//      after it;
//   4. points (split the same way), then labels and tick numbers in screen space,
//      each shown only when its anchor passes the depth test.
// Single-threaded and deterministic: the same params give the same pixels live and
// in batch.
//
// #1715 (T3): the 3×3 map (MathPlane3DMap.Current) moves every object, the grid
// planes (drawn again in blue over the dimmed originals) and every surface vertex;
// surface normals come from the mapped triangles. Read-outs: det (the unit cube's
// image, translucent, with its det), eigen (dashed lines through the origin with λ)
// and lengths (|M v| beside each vector).
//
// Camera (z up): eye = target + (distance / Zoom) · (sinφ cosθ, sinφ sinθ, cosφ),
// looking at the target; perspective with a 45° vertical field of view.

using System;
using System.Collections.Generic;
using System.Linq;
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

/// <summary>#1713 — one screen-space triangle for the z-buffer pass. #1714: the vertex
/// colours <see cref="Color"/>, <see cref="ColorB"/>, <see cref="ColorC"/> (ARGB) are
/// interpolated across it (Gouraud); equal colours fill it flat.</summary>
public readonly record struct ScreenTriangle(Projected3D A, Projected3D B, Projected3D C, uint Color, uint ColorB, uint ColorC)
{
    public ScreenTriangle(Projected3D a, Projected3D b, Projected3D c, uint color) : this(a, b, c, color, color, color) { }

    /// <summary>#1714 — centroid view depth (mean of z), the translucent sort key.</summary>
    public double CentroidDepth => (1.0 / A.InvZ + 1.0 / B.InvZ + 1.0 / C.InvZ) / 3.0;
}

/// <summary>#1714 — a sampled surface: (N+1)² world vertices and which N² cells are drawn.</summary>
public sealed class MathPlane3DSurfaceMesh
{
    public int N { get; init; }
    public (double X, double Y, double Z)[] Vertices { get; init; } = Array.Empty<(double, double, double)>();
    /// <summary>Cell (i, j) at [j·N + i]: false where a vertex is not finite or the cell jumps (a pole).</summary>
    public bool[] CellOk { get; init; } = Array.Empty<bool>();
    /// <summary>Largest |coordinate| of the central 90 % of the samples (per axis), so a
    /// pole's tall walls do not blow up the grid.</summary>
    public double RobustExtent { get; init; }

    public (double X, double Y, double Z) At(int i, int j) => Vertices[j * (N + 1) + i];
}

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

    /// <summary>#1714 — 1/z of the nearest translucent fragment per pixel (0 = none) of the last render.</summary>
    public float[] TranslucentBuffer { get; private set; } = Array.Empty<float>();

    /// <summary>True stereo: the eye moves this far along the camera's right axis (world
    /// units); the view direction is unchanged (parallel eyes). 0 = mono.</summary>
    public double StereoEyeOffset { get; set; }

    /// <summary>Depth AOV for the depth-parallax stereo warp: per-pixel ray distance to the
    /// nearest opaque triangle (<see cref="ScreenSpacePost.DepthMiss"/> where none), published
    /// only when <see cref="ScreenSpacePost.WantsDepthOutput"/> asks; null otherwise.</summary>
    public float[]? DepthBuffer { get; private set; }

    public const uint BackgroundColor = MathPlaneCalculator.BackgroundColor;
    public const uint GridColor = 0x33FFFFFF;
    /// <summary>#1714 — the tint of a filled grid plane (<c>fill xy</c>).</summary>
    public const uint GridFillColor = 0x2E9FB8D8;
    public const uint AxisColor = 0xB3FFFFFF;
    /// <summary>#1715 — the original axes while a map is drawn, and the map's colours (as 2D).</summary>
    public const uint DimAxisColor = 0x66FFFFFF;
    public const uint MapGridColor = MathPlaneCalculator.MapGridColor;
    public const uint DetFillColor = MathPlaneCalculator.DetFillColor;
    public const uint DetEdgeColor = MathPlaneCalculator.DetEdgeColor;
    public const uint EigenColor = MathPlaneCalculator.EigenColor;
    public const uint TickLabelColor = MathPlaneCalculator.TickLabelColor;
    /// <summary>Relative depth bias for lines and points against the z-buffer.</summary>
    public const double DepthBias = 2e-3;
    /// <summary>#1714 — segments of a space curve.</summary>
    public const int CurveSegments = 512;

    /// <summary>#1715 — the map of the frame being drawn (identity when off).</summary>
    public Mat3 LastMap { get; private set; } = Mat3.Identity;
    private Mat3 _map = Mat3.Identity;
    private bool _mapped;

    private string? _parsedText;
    private MathPlane3DScene _scene = new();
    private Dictionary<MathPlaneObject, MathPlane3DSurfaceMesh> _meshes = new(ReferenceEqualityComparer.Instance);
    private Dictionary<MathPlaneObject, List<List<(double X, double Y, double Z)>>> _curves = new(ReferenceEqualityComparer.Instance);

    public MathPlane3DCalculator(int width, int height) => Resize(width, height);

    public void Resize(int width, int height)
    {
        Width = width;
        Height = height;
        ColorBuffer = new uint[Math.Max(0, width * height)];
        ZBuffer = new float[Math.Max(0, width * height)];
        TranslucentBuffer = new float[Math.Max(0, width * height)];
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
        Array.Clear(TranslucentBuffer);
        DepthBuffer = null;

        string text = FractalParameters.MathPlane3DScene ?? "";
        if (text != _parsedText)
        {
            LastSceneError = MathPlane3DScene.TryParse(text, out _scene, out var err) ? null : err;
            _parsedText = text;
            _meshes = new(ReferenceEqualityComparer.Instance);
            _curves = new(ReferenceEqualityComparer.Instance);
            foreach (var o in _scene.Objects)
            {
                if (o.Kind == MathPlaneObjectKind.Surface && BuildSurface(o) is { } m) _meshes[o] = m;
                else if (o.Kind == MathPlaneObjectKind.Curve) _curves[o] = SampleCurve(o);
            }
        }
        var scene = _scene;
        var cam = CameraFor(FractalParameters, Zoom, w, h);
        if (StereoEyeOffset != 0)
        {
            double o = StereoEyeOffset;
            cam = cam with { Eye = (cam.Eye.X + cam.Right.X * o, cam.Eye.Y + cam.Right.Y * o, cam.Eye.Z + cam.Right.Z * o) };
        }
        float ui = (float)Math.Clamp(Math.Min(w, h) / 720.0, 1.0, 4.0);
        // The grid's size comes from the unmapped scene, so it holds still while t animates.
        double extent = Extent(scene, FractalParameters, Zoom, Bounds(scene));
        _map = MathPlane3DMap.Current(FractalParameters);
        if (!IsFinite(_map)) _map = Mat3.Identity;
        _mapped = !_map.IsIdentity;
        LastMap = _map;
        ct.ThrowIfCancellationRequested();

        // Triangles: opaque (arrowheads, opaque surfaces) and translucent (alpha surfaces, fills, det).
        var opaque = new List<ScreenTriangle>();
        var translucent = new List<ScreenTriangle>();
        foreach (var o in scene.Objects)
        {
            if (!o.Visible) continue;
            if (o.Kind == MathPlaneObjectKind.Vector)
                AddArrowhead(opaque, cam, X(MathPlane3DScene.Tail(o)), X(MathPlane3DScene.Tip(o)), o.Color, extent);
            else if (o.Kind == MathPlaneObjectKind.Surface && _meshes.TryGetValue(o, out var mesh))
                AddSurface((o.Color >> 24) == 0xFF ? opaque : translucent, cam, MapMesh(mesh, _map), o.Color, scene.FlatShading);
        }
        AddFills(translucent, cam, scene.Fill, extent, _map);
        if (scene.ShowDet) AddDet(translucent, cam, _map);
        ct.ThrowIfCancellationRequested();

        // Pass 1: opaque into colour + depth.
        RasterizeAll(opaque, ColorBuffer, ZBuffer, w, h);
        ct.ThrowIfCancellationRequested();
        var fx = FractalParameters.Lighting;
        if (ScreenSpacePost.WantsDepthOutput(in fx)) DepthBuffer = RayDepth(ZBuffer, cam, w, h);

        // Pass 2 (prepared): back-to-front order and the nearest translucent depth per pixel.
        var sorted = SortBackToFront(translucent);
        foreach (var t in sorted) RasterizeNearest(t, ZBuffer, TranslucentBuffer, w, h);
        bool anyTranslucent = sorted.Count > 0;

        var handle = GCHandle.Alloc(ColorBuffer, GCHandleType.Pinned);
        try
        {
            var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var bmp = new SKBitmap();
            bmp.InstallPixels(info, handle.AddrOfPinnedObject(), info.RowBytes);
            using var canvas = new SKCanvas(bmp);
            // Lines and points behind the nearest translucent layer, then the blend, then the rest.
            DrawScene(canvas, cam, scene, extent, ui, Phase.Behind, anyTranslucent);
            canvas.Flush();
            if (anyTranslucent)
            {
                foreach (var t in sorted) RasterizeBlend(t, ColorBuffer, ZBuffer, w, h);
                DrawScene(canvas, cam, scene, extent, ui, Phase.Front, anyTranslucent);
            }
            DrawScene(canvas, cam, scene, extent, ui, Phase.Labels, anyTranslucent);
            canvas.Flush();
        }
        finally { handle.Free(); }
    }

    /// <summary>#1715 — a world point through the frame's map.</summary>
    private (double X, double Y, double Z) X((double X, double Y, double Z) p) => _mapped ? _map.Apply(p) : p;

    private static bool IsFinite(Mat3 m) => double.IsFinite(m.M11) && double.IsFinite(m.M12) && double.IsFinite(m.M13)
        && double.IsFinite(m.M21) && double.IsFinite(m.M22) && double.IsFinite(m.M23)
        && double.IsFinite(m.M31) && double.IsFinite(m.M32) && double.IsFinite(m.M33);

    /// <summary>#1715 — a surface mesh with every vertex mapped (the drawn cells unchanged).</summary>
    public static MathPlane3DSurfaceMesh MapMesh(MathPlane3DSurfaceMesh m, Mat3 map)
    {
        if (map.IsIdentity) return m;
        var v = new (double X, double Y, double Z)[m.Vertices.Length];
        for (int k = 0; k < v.Length; k++) v[k] = map.Apply(m.Vertices[k]);
        return new MathPlane3DSurfaceMesh { N = m.N, Vertices = v, CellOk = m.CellOk, RobustExtent = m.RobustExtent };
    }

    /// <summary>#1715 — the unit cube's image: six translucent faces (two triangles each).</summary>
    private static void AddDet(List<ScreenTriangle> dst, MathPlane3DCamera cam, Mat3 map)
    {
        var v = MathPlane3DMap.Parallelepiped(map);
        // Faces by corner index a + 2b + 4c.
        int[][] faces = { new[] { 0, 1, 3, 2 }, new[] { 4, 5, 7, 6 }, new[] { 0, 1, 5, 4 }, new[] { 2, 3, 7, 6 }, new[] { 0, 2, 6, 4 }, new[] { 1, 3, 7, 5 } };
        foreach (var f in faces)
        {
            AddWorld(dst, cam, v[f[0]], v[f[1]], v[f[2]], DetFillColor);
            AddWorld(dst, cam, v[f[0]], v[f[2]], v[f[3]], DetFillColor);
        }
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

    /// <summary>Half-size of the grid and axes: covers the objects and the view.
    /// #1714: curves count by their sampled points and surfaces by the central 90 % of theirs
    /// (<paramref name="extra"/>), not their parameter ranges.</summary>
    public static double Extent(MathPlane3DScene scene, FractalParameters p, double zoom,
                                IEnumerable<(double X, double Y, double Z)>? extra = null)
    {
        double m = 2;
        foreach (var o in scene.Objects)
        {
            if (o.Kind is MathPlaneObjectKind.Curve or MathPlaneObjectKind.Surface) continue;
            for (int i = 0; i < o.Coords.Count; i++) m = Math.Max(m, Math.Abs(o.Coords[i]) * 1.2);
        }
        if (extra != null)
            foreach (var (x, y, z) in extra) m = Math.Max(m, Math.Max(Math.Abs(x), Math.Max(Math.Abs(y), Math.Abs(z))) * 1.2);
        if (scene.Objects.Count == 0) m = Math.Max(m, 0.45 * p.MathPlane3DCameraDistance / Math.Max(1e-9, zoom));
        var (s, e) = GridAxis.NiceStep(m / 4);
        double step = s * Math.Pow(10, e);
        return Math.Ceiling(m / step) * step;
    }

    // The sampled points of the visible curves; a surface counts by its robust extent.
    private IEnumerable<(double X, double Y, double Z)> Bounds(MathPlane3DScene scene)
    {
        foreach (var o in scene.Objects)
        {
            if (!o.Visible) continue;
            if (_meshes.TryGetValue(o, out var mesh))
                yield return (mesh.RobustExtent, 0, 0);
            else if (_curves.TryGetValue(o, out var pieces))
                foreach (var piece in pieces) foreach (var q in piece) yield return q;
        }
    }

    // ── Surfaces and curves (#1714) ──────────────────────────────────────────

    /// <summary>#1714 — sample a surface on its (N+1)² grid. A cell is dropped when a vertex is
    /// not finite, or when one coordinate spans more across the cell than the surface's central
    /// 90 % range of that coordinate's samples (a jump: the cell straddles a pole), so poles cut
    /// the surface instead of spiking it. Null when an expression does not parse.</summary>
    public static MathPlane3DSurfaceMesh? BuildSurface(MathPlaneObject o)
    {
        bool param = o.CurveKind == MathPlaneCurveKind.Parametric;
        var ex = new PlotExpression[o.Expressions.Count];
        for (int k = 0; k < ex.Length; k++)
            if (!PlotExpression.TryParse(o.Expressions[k], param, out var e, out _)) return null; else ex[k] = e!;
        if (ex.Length != (param ? 3 : 1)) return null;

        int n = MathPlane3DScene.MeshOf(o);
        var (a0, a1, b0, b1) = MathPlane3DScene.SurfaceDomain(o);
        var v = new (double X, double Y, double Z)[(n + 1) * (n + 1)];
        for (int j = 0; j <= n; j++)
        {
            double s1 = b0 + (b1 - b0) * j / n;
            for (int i = 0; i <= n; i++)
            {
                double s0 = a0 + (a1 - a0) * i / n;
                v[j * (n + 1) + i] = param
                    ? (ex[0].EvalUV(s0, s1), ex[1].EvalUV(s0, s1), ex[2].EvalUV(s0, s1))
                    : (s0, s1, ex[0].Eval(x: s0, y: s1));
            }
        }

        static bool Finite((double X, double Y, double Z) q) => double.IsFinite(q.X) && double.IsFinite(q.Y) && double.IsFinite(q.Z);
        double[] range = new double[3];
        double robust = 0;
        for (int c = 0; c < 3; c++)
        {
            var vals = v.Where(Finite).Select(q => c == 0 ? q.X : c == 1 ? q.Y : q.Z).OrderBy(x => x).ToArray();
            if (vals.Length == 0) continue;
            double lo = vals[(int)Math.Floor(0.05 * (vals.Length - 1))], hi = vals[(int)Math.Ceiling(0.95 * (vals.Length - 1))];
            range[c] = hi - lo;
            robust = Math.Max(robust, Math.Max(Math.Abs(lo), Math.Abs(hi)));
        }
        double limit = Math.Max(range[0], Math.Max(range[1], range[2]));

        var ok = new bool[n * n];
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                var p0 = v[j * (n + 1) + i]; var p1 = v[j * (n + 1) + i + 1];
                var p2 = v[(j + 1) * (n + 1) + i]; var p3 = v[(j + 1) * (n + 1) + i + 1];
                if (!Finite(p0) || !Finite(p1) || !Finite(p2) || !Finite(p3)) continue;
                static double Spread(double a, double b, double c, double d) => Math.Max(Math.Max(a, b), Math.Max(c, d)) - Math.Min(Math.Min(a, b), Math.Min(c, d));
                if (Spread(p0.X, p1.X, p2.X, p3.X) > limit || Spread(p0.Y, p1.Y, p2.Y, p3.Y) > limit
                    || Spread(p0.Z, p1.Z, p2.Z, p3.Z) > limit) continue;
                ok[j * n + i] = true;
            }
        return new MathPlane3DSurfaceMesh { N = n, Vertices = v, CellOk = ok, RobustExtent = robust };
    }

    /// <summary>#1714 — a space curve as polylines: <see cref="CurveSegments"/> uniform steps in
    /// t; a non-finite sample breaks the curve.</summary>
    public static List<List<(double X, double Y, double Z)>> SampleCurve(MathPlaneObject o)
    {
        var result = new List<List<(double, double, double)>>();
        if (o.Expressions.Count != 3 || o.Domain is not { } d) return result;
        var ex = new PlotExpression[3];
        for (int k = 0; k < 3; k++)
            if (!PlotExpression.TryParse(o.Expressions[k], out var e, out _)) return result; else ex[k] = e!;
        List<(double, double, double)>? cur = null;
        for (int k = 0; k <= CurveSegments; k++)
        {
            double t = d.Min + (d.Max - d.Min) * k / CurveSegments;
            double x = ex[0].Eval(t: t), y = ex[1].Eval(t: t), z = ex[2].Eval(t: t);
            if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z))
            {
                if (cur is { Count: >= 2 }) result.Add(cur);
                cur = null;
                continue;
            }
            (cur ??= new()).Add((x, y, z));
        }
        if (cur is { Count: >= 2 }) result.Add(cur);
        return result;
    }

    private static void AddSurface(List<ScreenTriangle> dst, MathPlane3DCamera cam, MathPlane3DSurfaceMesh m, uint color, bool flat)
    {
        int n = m.N;
        var light = LightDir(cam);
        // Gouraud: per-vertex normals from the (area-weighted) normals of the drawn cells round it.
        (double X, double Y, double Z)[]? vn = null;
        if (!flat)
        {
            vn = new (double, double, double)[(n + 1) * (n + 1)];
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                {
                    if (!m.CellOk[j * n + i]) continue;
                    var c = Cross(Sub(m.At(i + 1, j + 1), m.At(i, j)), Sub(m.At(i, j + 1), m.At(i + 1, j)));
                    foreach (int idx in new[] { j * (n + 1) + i, j * (n + 1) + i + 1, (j + 1) * (n + 1) + i, (j + 1) * (n + 1) + i + 1 })
                        vn[idx] = (vn[idx].X + c.X, vn[idx].Y + c.Y, vn[idx].Z + c.Z);
                }
        }
        uint VertexColor(int i, int j) => Shade(color, Lambert(Normalize(vn![j * (n + 1) + i]), light));
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                if (!m.CellOk[j * n + i]) continue;
                var p00 = m.At(i, j); var p10 = m.At(i + 1, j); var p01 = m.At(i, j + 1); var p11 = m.At(i + 1, j + 1);
                if (flat)
                {
                    AddWorld(dst, cam, p00, p10, p11, Shade(color, Lambert(FaceNormal(p00, p10, p11), light)));
                    AddWorld(dst, cam, p00, p11, p01, Shade(color, Lambert(FaceNormal(p00, p11, p01), light)));
                }
                else
                {
                    uint c00 = VertexColor(i, j), c10 = VertexColor(i + 1, j), c01 = VertexColor(i, j + 1), c11 = VertexColor(i + 1, j + 1);
                    AddWorld(dst, cam, p00, p10, p11, c00, c10, c11);
                    AddWorld(dst, cam, p00, p11, p01, c00, c11, c01);
                }
            }
    }

    // Filled grid planes: one quad per grid cell, so near-plane clipping stays local.
    private static void AddFills(List<ScreenTriangle> dst, MathPlane3DCamera cam, MathPlane3DGrid fill, double extent, Mat3 map)
    {
        if (fill == MathPlane3DGrid.None) return;
        var (sS, sE) = GridAxis.NiceStep(extent / 4);
        double step = sS * Math.Pow(10, sE);
        int nl = (int)Math.Round(extent / step);
        for (int a = -nl; a < nl; a++)
            for (int b = -nl; b < nl; b++)
            {
                double u0 = a * step, u1 = (a + 1) * step, v0 = b * step, v1 = (b + 1) * step;
                if ((fill & MathPlane3DGrid.XY) != 0) Quad(dst, cam, (u0, v0, 0), (u1, v0, 0), (u1, v1, 0), (u0, v1, 0));
                if ((fill & MathPlane3DGrid.XZ) != 0) Quad(dst, cam, (u0, 0, v0), (u1, 0, v0), (u1, 0, v1), (u0, 0, v1));
                if ((fill & MathPlane3DGrid.YZ) != 0) Quad(dst, cam, (0, u0, v0), (0, u1, v0), (0, u1, v1), (0, u0, v1));
            }
        void Quad(List<ScreenTriangle> d, MathPlane3DCamera c, (double X, double Y, double Z) p0, (double X, double Y, double Z) p1,
                  (double X, double Y, double Z) p2, (double X, double Y, double Z) p3)
        {
            var (q0, q1, q2, q3) = (map.Apply(p0), map.Apply(p1), map.Apply(p2), map.Apply(p3));
            AddWorld(d, c, q0, q1, q2, GridFillColor);
            AddWorld(d, c, q0, q2, q3, GridFillColor);
        }
    }

    /// <summary>#1714 — sort translucent triangles back to front by centroid depth (farthest
    /// first; stable, so equal depths keep their input order).</summary>
    public static List<ScreenTriangle> SortBackToFront(IEnumerable<ScreenTriangle> tris)
        => tris.OrderByDescending(t => t.CentroidDepth).ToList();

    // ── Triangles ────────────────────────────────────────────────────────────

    /// <summary>Rasterize <paramref name="tris"/> into colour + 1/z depth (nearer = larger 1/z).</summary>
    public static void RasterizeAll(IReadOnlyList<ScreenTriangle> tris, uint[] color, float[] depth, int w, int h)
    {
        foreach (var t in tris) Rasterize(t, color, depth, w, h);
    }

    /// <summary>Fill one triangle: pixel centres inside it (top-left rule on shared edges)
    /// whose interpolated 1/z beats the depth buffer.</summary>
    public static void Rasterize(in ScreenTriangle t, uint[] color, float[] depth, int w, int h)
        => Scan(t, w, h, (int i, double iz, uint c) =>
        {
            if (iz <= depth[i]) return;
            depth[i] = (float)iz;
            color[i] = 0xFF000000u | (c & 0xFFFFFF);
        });

    /// <summary>#1714 — blend one translucent triangle over <paramref name="color"/> where it is
    /// in front of the opaque depth (no depth write): out = src·a + dst·(1 − a).</summary>
    public static void RasterizeBlend(in ScreenTriangle t, uint[] color, float[] depth, int w, int h)
        => Scan(t, w, h, (int i, double iz, uint c) =>
        {
            if (iz <= depth[i]) return;
            color[i] = Blend(color[i], c);
        });

    // The nearest translucent fragment per pixel that passes the opaque depth test.
    private static void RasterizeNearest(in ScreenTriangle t, float[] depth, float[] nearest, int w, int h)
        => Scan(t, w, h, (int i, double iz, uint c) =>
        {
            if (iz <= depth[i] || iz <= nearest[i]) return;
            nearest[i] = (float)iz;
        });

    /// <summary>#1714 — src (ARGB, straight alpha) over opaque dst.</summary>
    public static uint Blend(uint dst, uint src)
    {
        uint a = src >> 24;
        uint Mix(int sh) => (uint)((((src >> sh) & 0xFF) * a + ((dst >> sh) & 0xFF) * (255 - a) + 127) / 255);
        return 0xFF000000u | (Mix(16) << 16) | (Mix(8) << 8) | Mix(0);
    }

    private delegate void Fragment(int index, double invZ, uint color);

    // Visit the pixel centres inside a triangle (top-left rule), with 1/z and the vertex
    // colours interpolated (screen-linear barycentrics).
    private static void Scan(in ScreenTriangle t, int w, int h, Fragment frag)
    {
        var a = t.A; var b = t.B; var c = t.C;
        uint ca = t.Color, cb = t.ColorB, cc = t.ColorC;
        double area = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        if (area == 0 || !double.IsFinite(area)) return;
        if (area < 0) { (b, c) = (c, b); (cb, cc) = (cc, cb); area = -area; }
        int x0 = Math.Max(0, (int)Math.Floor(Math.Min(a.X, Math.Min(b.X, c.X))));
        int x1 = Math.Min(w - 1, (int)Math.Ceiling(Math.Max(a.X, Math.Max(b.X, c.X))));
        int y0 = Math.Max(0, (int)Math.Floor(Math.Min(a.Y, Math.Min(b.Y, c.Y))));
        int y1 = Math.Min(h - 1, (int)Math.Ceiling(Math.Max(a.Y, Math.Max(b.Y, c.Y))));
        if (x0 > x1 || y0 > y1) return;
        bool flatColor = ca == cb && ca == cc;

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
                frag(y * w + x, iz, flatColor ? ca : Lerp(ca, cb, cc, wa / area, wb / area, wc / area));
            }
        }
    }

    private static uint Lerp(uint a, uint b, uint c, double wa, double wb, double wc)
    {
        uint Ch(int sh) => (uint)Math.Clamp(Math.Round(((a >> sh) & 0xFF) * wa + ((b >> sh) & 0xFF) * wb + ((c >> sh) & 0xFF) * wc), 0, 255);
        return (Ch(24) << 24) | (Ch(16) << 16) | (Ch(8) << 8) | Ch(0);
    }

    /// <summary>A screen point with 1/z <paramref name="invZ"/> is in front of (or on) what the
    /// depth buffer holds at its pixel, within <paramref name="bias"/> (relative).</summary>
    public static bool PassesDepth(float[] depth, int w, int h, double x, double y, double invZ, double bias = DepthBias)
    {
        int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y);
        if (ix < 0 || iy < 0 || ix >= w || iy >= h) return true;
        return invZ * (1 + bias) >= depth[iy * w + ix];
    }

    private static void AddArrowhead(List<ScreenTriangle> tris, MathPlane3DCamera cam,
                                     (double X, double Y, double Z) tail, (double X, double Y, double Z) tip, uint color, double extent)
    {
        var d = (X: tip.X - tail.X, Y: tip.Y - tail.Y, Z: tip.Z - tail.Z);
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
        var light = LightDir(cam);
        uint col = 0xFF000000u | color;   // arrowheads are always opaque
        for (int k = 0; k < n; k++)
        {
            var p0 = ring[k]; var p1 = ring[(k + 1) % n];
            AddWorld(tris, cam, tip, p0, p1, Shade(col, Lambert(FaceNormal(tip, p0, p1), light)));       // side
            AddWorld(tris, cam, baseC, p1, p0, Shade(col, Lambert(FaceNormal(baseC, p1, p0), light)));   // base disc
        }
    }

    /// <summary>The arrowhead's length: a fifth of the vector, capped by the scene size.</summary>
    public static double ArrowheadLength(double vectorLength, double extent)
        => Math.Min(0.22 * vectorLength, 0.06 * extent);

    // A light over the camera's shoulder; Lambert's cosine law (Photometria, 1760; bibliography
    // #lambert-cosine-law), two-sided (|n·l|), plus ambient.
    private static (double X, double Y, double Z) LightDir(MathPlane3DCamera cam)
        => Normalize((-cam.Forward.X + 0.4 * cam.Up.X + 0.3 * cam.Right.X,
                      -cam.Forward.Y + 0.4 * cam.Up.Y + 0.3 * cam.Right.Y,
                      -cam.Forward.Z + 0.4 * cam.Up.Z + 0.3 * cam.Right.Z));

    private static double Lambert((double X, double Y, double Z) n, (double X, double Y, double Z) l)
        => 0.35 + 0.65 * Math.Abs(n.X * l.X + n.Y * l.Y + n.Z * l.Z);

    private static (double X, double Y, double Z) FaceNormal((double X, double Y, double Z) a, (double X, double Y, double Z) b, (double X, double Y, double Z) c)
        => Normalize(Cross(Sub(b, a), Sub(c, a)));

    /// <summary>A world triangle (flat colour) to screen triangles, clipped to the near plane.</summary>
    private static void AddWorld(List<ScreenTriangle> tris, MathPlane3DCamera cam,
                                 (double X, double Y, double Z) a, (double X, double Y, double Z) b, (double X, double Y, double Z) c, uint color)
        => AddWorld(tris, cam, a, b, c, color, color, color);

    /// <summary>#1714 — a world triangle with vertex colours to screen triangles. It is clipped
    /// to the near plane in view space (Sutherland–Hodgman against one plane: 0, 1 or 2
    /// triangles), colours interpolated along the cut edges.</summary>
    private static void AddWorld(List<ScreenTriangle> tris, MathPlane3DCamera cam,
                                 (double X, double Y, double Z) a, (double X, double Y, double Z) b, (double X, double Y, double Z) c,
                                 uint ca, uint cb, uint cc)
    {
        Span<(double X, double Y, double Z, uint C)> inp = stackalloc (double, double, double, uint)[3];
        var va = cam.View(a.X, a.Y, a.Z); var vb = cam.View(b.X, b.Y, b.Z); var vc = cam.View(c.X, c.Y, c.Z);
        inp[0] = (va.X, va.Y, va.Z, ca); inp[1] = (vb.X, vb.Y, vb.Z, cb); inp[2] = (vc.X, vc.Y, vc.Z, cc);
        const double near = MathPlane3DCamera.Near * 10;
        Span<(double X, double Y, double Z, uint C)> outp = stackalloc (double, double, double, uint)[4];
        int n = 0;
        for (int k = 0; k < 3; k++)
        {
            var p = inp[k]; var q = inp[(k + 1) % 3];
            bool pin = p.Z > near, qin = q.Z > near;
            if (pin) outp[n++] = p;
            if (pin != qin)
            {
                double t = (near - p.Z) / (q.Z - p.Z);
                outp[n++] = (p.X + (q.X - p.X) * t, p.Y + (q.Y - p.Y) * t, near, LerpColor(p.C, q.C, t));
            }
        }
        if (n < 3) return;
        Span<Projected3D> pr = stackalloc Projected3D[4];
        for (int k = 0; k < n; k++)
        {
            if (cam.FromView(outp[k].X, outp[k].Y, outp[k].Z) is not { } pp) return;
            pr[k] = pp;
        }
        for (int k = 1; k + 1 < n; k++)
            tris.Add(new ScreenTriangle(pr[0], pr[k], pr[k + 1], outp[0].C, outp[k].C, outp[k + 1].C));
    }

    private static uint LerpColor(uint p, uint q, double t)
    {
        uint Ch(int sh) => (uint)Math.Clamp(Math.Round(((p >> sh) & 0xFF) * (1 - t) + ((q >> sh) & 0xFF) * t), 0, 255);
        return (Ch(24) << 24) | (Ch(16) << 16) | (Ch(8) << 8) | Ch(0);
    }

    // Scale RGB by k, keeping alpha.
    private static uint Shade(uint c, double k)
    {
        uint r = (uint)Math.Min(255, Math.Round(((c >> 16) & 0xFF) * k));
        uint g = (uint)Math.Min(255, Math.Round(((c >> 8) & 0xFF) * k));
        uint b = (uint)Math.Min(255, Math.Round((c & 0xFF) * k));
        return (c & 0xFF000000u) | (r << 16) | (g << 8) | b;
    }

    // ── Lines, points, labels ────────────────────────────────────────────────

    /// <summary>#1714 — which part of the overlay a <see cref="DrawScene"/> call draws: lines and
    /// points behind the nearest translucent layer (before the blend), those in front of it
    /// (after), then the labels.</summary>
    private enum Phase { Behind, Front, Labels }

    private void DrawScene(SKCanvas canvas, MathPlane3DCamera cam, MathPlane3DScene scene, double extent, float ui,
                           Phase phase, bool anyTranslucent)
    {
        int w = Width, h = Height;
        bool labels = phase == Phase.Labels;
        using var line = new SKPaint { Style = SKPaintStyle.Stroke, IsAntialias = true, StrokeCap = SKStrokeCap.Round };
        using var font = new SKFont(SKTypeface.Default, 13f * ui);
        using var halo = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true, Color = new SKColor(0xC0000000) };
        using var ink = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };
        var ctx = new LineContext(cam, phase, anyTranslucent);

        var (sS, sE) = GridAxis.NiceStep(extent / 4);
        double step = sS * Math.Pow(10, sE);
        var axis = GridAxis.Build(stackalloc double[] { 0.0 }, extent, sS, sE);

        // Grid planes: the originals, then (#1715) their image under the map in blue.
        if (!labels)
        {
            int nLines = (int)Math.Round(extent / step);
            foreach (bool image in _mapped ? new[] { false, true } : new[] { false })
            {
                line.StrokeWidth = (image ? 1.2f : 1f) * ui;
                line.Color = new SKColor(image ? MapGridColor : GridColor);
                Func<(double X, double Y, double Z), (double X, double Y, double Z)> f = image ? X : p => p;
                for (int k = -nLines; k <= nLines; k++)
                {
                    double c = k * step;
                    if ((scene.Grid & MathPlane3DGrid.XY) != 0)
                    {
                        DrawLine3D(canvas, ctx, f((c, -extent, 0)), f((c, extent, 0)), line);
                        DrawLine3D(canvas, ctx, f((-extent, c, 0)), f((extent, c, 0)), line);
                    }
                    if ((scene.Grid & MathPlane3DGrid.XZ) != 0)
                    {
                        DrawLine3D(canvas, ctx, f((c, 0, -extent)), f((c, 0, extent)), line);
                        DrawLine3D(canvas, ctx, f((-extent, 0, c)), f((extent, 0, c)), line);
                    }
                    if ((scene.Grid & MathPlane3DGrid.YZ) != 0)
                    {
                        DrawLine3D(canvas, ctx, f((0, c, -extent)), f((0, c, extent)), line);
                        DrawLine3D(canvas, ctx, f((0, -extent, c)), f((0, extent, c)), line);
                    }
                }
            }
        }

        // Axes, their names and tick numbers.
        if (scene.ShowAxes)
        {
            if (!labels)
            {
                line.StrokeWidth = 1.6f * ui;
                line.Color = new SKColor(_mapped ? DimAxisColor : AxisColor);
                DrawLine3D(canvas, ctx, (-extent, 0, 0), (extent, 0, 0), line);
                DrawLine3D(canvas, ctx, (0, -extent, 0), (0, extent, 0), line);
                DrawLine3D(canvas, ctx, (0, 0, -extent), (0, 0, extent), line);
            }
            else
            {
                ink.Color = new SKColor(AxisColor);
                Text(canvas, cam, (extent * 1.06, 0, 0), "x", font, halo, ink, ui);
                Text(canvas, cam, (0, extent * 1.06, 0), "y", font, halo, ink, ui);
                Text(canvas, cam, (0, 0, extent * 1.06), "z", font, halo, ink, ui);
                if (scene.ShowTicks)
                {
                    var tickLabels = GridLabelFormatter.Format(axis, scene.Labels).Labels;
                    ink.Color = new SKColor(TickLabelColor);
                    using var small = new SKFont(SKTypeface.Default, 11f * ui);
                    for (int i = 0; i < axis.Ticks.Count && i < tickLabels.Count; i++)
                    {
                        if (axis.Ticks[i].IsZero) continue;
                        double v = axis.Ticks[i].OffsetFromCentre;
                        Text(canvas, cam, (v, 0, 0), tickLabels[i], small, halo, ink, ui);
                        Text(canvas, cam, (0, v, 0), tickLabels[i], small, halo, ink, ui);
                        Text(canvas, cam, (0, 0, v), tickLabels[i], small, halo, ink, ui);
                    }
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
            ink.Color = col.WithAlpha(0xFF);
            switch (o.Kind)
            {
                case MathPlaneObjectKind.Point:
                {
                    var p = X(MathPlane3DScene.Point(o, 0));
                    if (labels) { if (o.ShowLabel) Text(canvas, cam, p, o.Name, font, halo, ink, ui); break; }
                    if (cam.Project(p.X, p.Y, p.Z) is { } pp && PassesDepth(ZBuffer, w, h, pp.X, pp.Y, pp.InvZ)
                        && InPhase(ctx, pp.X, pp.Y, pp.InvZ))
                    {
                        dot.Color = col;
                        canvas.DrawCircle((float)pp.X, (float)pp.Y, 5f * ui, dot);
                        canvas.DrawCircle((float)pp.X, (float)pp.Y, 5f * ui, outline);
                    }
                    break;
                }
                case MathPlaneObjectKind.Vector:
                {
                    var tail = X(MathPlane3DScene.Tail(o));
                    var tip = X(MathPlane3DScene.Tip(o));
                    var d = (X: tip.X - tail.X, Y: tip.Y - tail.Y, Z: tip.Z - tail.Z);
                    double len = Math.Sqrt(d.X * d.X + d.Y * d.Y + d.Z * d.Z);
                    if (labels)
                    {
                        if (o.ShowLabel) Text(canvas, cam, tip, o.Name, font, halo, ink, ui);
                        if (scene.ShowLengths) Text(canvas, cam, tip, "|" + o.Name + "| = " + Fmt(len), font, halo, ink, ui, 0, 15f);
                        break;
                    }
                    if (len > 1e-12)
                    {
                        double k = 1 - ArrowheadLength(len, extent) / len;
                        var shaftEnd = (tail.X + d.X * k, tail.Y + d.Y * k, tail.Z + d.Z * k);
                        DrawLine3D(canvas, ctx, tail, shaftEnd, line);
                    }
                    break;
                }
                case MathPlaneObjectKind.Segment:
                case MathPlaneObjectKind.Polyline:
                {
                    int n = o.Coords.Count / 3;
                    if (labels) { if (o.ShowLabel) Text(canvas, cam, X(MathPlane3DScene.Point(o, n - 1)), o.Name, font, halo, ink, ui); break; }
                    for (int i = 1; i < n; i++) DrawLine3D(canvas, ctx, X(MathPlane3DScene.Point(o, i - 1)), X(MathPlane3DScene.Point(o, i)), line);
                    break;
                }
                case MathPlaneObjectKind.Curve when _curves.TryGetValue(o, out var pieces):
                {
                    if (labels)
                    {
                        if (o.ShowLabel && pieces.Count > 0) Text(canvas, cam, X(pieces[^1][^1]), o.Name, font, halo, ink, ui);
                        break;
                    }
                    foreach (var piece in pieces)
                        for (int i = 1; i < piece.Count; i++) DrawLine3D(canvas, ctx, X(piece[i - 1]), X(piece[i]), line);
                    break;
                }
                case MathPlaneObjectKind.Surface when labels && o.ShowLabel && _meshes.TryGetValue(o, out var mesh):
                {
                    if (LabelVertex(mesh) is { } at) Text(canvas, cam, X(at), o.Name, font, halo, ink, ui);
                    break;
                }
            }
        }

        if (scene.ShowDet) DrawDetEdges(canvas, ctx, scene, ui, labels, font, halo, ink);
        if (scene.ShowEigen) DrawEigen(canvas, ctx, extent, ui, labels, font, halo, ink);
        if (labels && MathPlane3DMap.PolarFallsBack(FractalParameters))
        {
            ink.Color = new SKColor(TickLabelColor);
            canvas.DrawText("polar needs det > 0: drawn linear", 6f * ui, h - 8f * ui, font, ink);
        }

        if (labels && LastSceneError is { } err)
        {
            ink.Color = new SKColor(MathPlaneCalculator.ErrorColor);
            canvas.DrawText("Scene error: " + err, 6f * ui, 6f * ui - font.Metrics.Ascent, font, ink);
        }
    }

    /// <summary>#1715 — the parallelepiped's 12 edges and its det value.</summary>
    private void DrawDetEdges(SKCanvas canvas, in LineContext ctx, MathPlane3DScene scene, float ui, bool labels,
                              SKFont font, SKPaint halo, SKPaint ink)
    {
        var v = MathPlane3DMap.Parallelepiped(_map);
        if (labels)
        {
            ink.Color = new SKColor(DetEdgeColor);
            Text(canvas, ctx.Cam, v[7], "det = " + Fmt(_map.Det), font, halo, ink, ui);
            return;
        }
        using var edge = new SKPaint { Style = SKPaintStyle.Stroke, IsAntialias = true, StrokeWidth = 1.5f * ui, Color = new SKColor(DetEdgeColor), StrokeCap = SKStrokeCap.Round };
        for (int a = 0; a < 8; a++)
            for (int bit = 1; bit < 8; bit <<= 1)
                if ((a & bit) == 0) DrawLine3D(canvas, ctx, v[a], v[a | bit], edge);
    }

    /// <summary>#1715 — the real eigenvector lines through the origin (dashed) with their λ.</summary>
    private void DrawEigen(SKCanvas canvas, in LineContext ctx, double extent, float ui, bool labels,
                           SKFont font, SKPaint halo, SKPaint ink)
    {
        var eig = MathPlane3DMap.Eigen(_map, out bool all);
        if (labels)
        {
            ink.Color = new SKColor(EigenColor);
            string? note = all ? "every vector is an eigenvector, \u03BB = " + Fmt(_map.M11)
                         : eig.Count == 0 ? "no real eigenvectors" : null;
            if (note != null) { canvas.DrawText(note, 6f * ui, Height - 26f * ui, font, ink); return; }
            foreach (var e in eig)
                Text(canvas, ctx.Cam, (0.6 * extent * e.X, 0.6 * extent * e.Y, 0.6 * extent * e.Z), "\u03BB = " + Fmt(e.Lambda), font, halo, ink, ui);
            return;
        }
        using var dash = SKPathEffect.CreateDash(new[] { 8f * ui, 6f * ui }, 0);
        using var line = new SKPaint { Style = SKPaintStyle.Stroke, IsAntialias = true, StrokeWidth = 2f * ui, Color = new SKColor(EigenColor), PathEffect = dash };
        double r = 1.5 * extent;
        foreach (var e in eig) DrawLine3D(canvas, ctx, (-r * e.X, -r * e.Y, -r * e.Z), (r * e.X, r * e.Y, r * e.Z), line);
    }

    /// <summary>Read-out number: up to 4 decimals, no trailing zeros (as 2D).</summary>
    private static string Fmt(double v)
        => Math.Abs(v) < 5e-5 ? "0" : v.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);

    // A surface's label anchor: the drawn vertex nearest the grid centre.
    private static (double X, double Y, double Z)? LabelVertex(MathPlane3DSurfaceMesh m)
    {
        int n = m.N, best = -1;
        double bestD = double.MaxValue;
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                if (!m.CellOk[j * n + i]) continue;
                double d = (i - n / 2.0) * (i - n / 2.0) + (j - n / 2.0) * (j - n / 2.0);
                if (d < bestD) { bestD = d; best = j * (n + 1) + i; }
            }
        return best >= 0 ? m.Vertices[best] : null;
    }

    private readonly record struct LineContext(MathPlane3DCamera Cam, Phase Phase, bool AnyTranslucent);

    // In the Behind phase: everything when there is no translucent layer, else what is behind
    // the nearest translucent fragment; in the Front phase the rest.
    private bool InPhase(in LineContext ctx, double x, double y, double invZ)
    {
        if (!ctx.AnyTranslucent) return ctx.Phase == Phase.Behind;
        int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y);
        bool front = ix < 0 || iy < 0 || ix >= Width || iy >= Height
                     || invZ * (1 + DepthBias) >= TranslucentBuffer[iy * Width + ix];
        return front == (ctx.Phase == Phase.Front);
    }

    /// <summary>Draw a world segment: clipped to the near plane, cut into ~3 px pieces, each
    /// drawn only where its mid-depth passes the z-buffer test (and belongs to this phase).</summary>
    private void DrawLine3D(SKCanvas canvas, in LineContext ctx,
                            (double X, double Y, double Z) a, (double X, double Y, double Z) b, SKPaint paint)
    {
        var cam = ctx.Cam;
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
            if (PassesDepth(ZBuffer, Width, Height, mx, my, mz) && InPhase(ctx, mx, my, mz))
            {
                if (!open) { path.MoveTo((float)(pa.X + (pb.X - pa.X) * t0), (float)(pa.Y + (pb.Y - pa.Y) * t0)); open = true; }
                path.LineTo((float)(pa.X + (pb.X - pa.X) * t1), (float)(pa.Y + (pb.Y - pa.Y) * t1));
            }
            else open = false;
        }
        canvas.DrawPath(path, paint);
    }

    private void Text(SKCanvas canvas, MathPlane3DCamera cam, (double X, double Y, double Z) at, string s,
                      SKFont font, SKPaint halo, SKPaint ink, float ui, float dx = 0, float dy = 0)
    {
        if (cam.Project(at.X, at.Y, at.Z) is not { } p || !PassesDepth(ZBuffer, Width, Height, p.X, p.Y, p.InvZ)) return;
        float x = (float)p.X + (6f + dx) * ui, y = (float)p.Y + (dy - 6f) * ui;
        canvas.DrawText(s, x + ui, y + ui, font, halo);
        canvas.DrawText(s, x, y, font, ink);
    }

    // ── Vector helpers ───────────────────────────────────────────────────────

    private static (double X, double Y, double Z) Sub((double X, double Y, double Z) a, (double X, double Y, double Z) b)
        => (a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    private static (double X, double Y, double Z) Cross((double X, double Y, double Z) a, (double X, double Y, double Z) b)
        => (a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    private static (double X, double Y, double Z) Normalize((double X, double Y, double Z) v)
    {
        double l = Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
        return l > 0 ? (v.X / l, v.Y / l, v.Z / l) : (0, 0, 0);
    }
}
