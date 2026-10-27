// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// MirrorSpheresCalculator.cs (#1440, epic #1439)
//
// Four mirrored balls stacked as a tetrahedron, looked into through one of the
// gaps between them: the experiment D. Sweet, E. Ott and J. A. Yorke
// photographed to show Wada exit basins in chaotic scattering (Nature 399, 1999;
// see Docs/Resources-Bibliography.md). Each pair of balls touches, and the
// curved triangular gap between every three balls is an opening, so there are
// four openings (one per face of the tetrahedron). A camera ray entering an
// opening reflects specularly between the balls and finally leaves through one
// of the four openings. The pixel shows which one. The boundaries between the
// four exit regions have the Wada property ("Lakes of Wada", Yoneyama 1917):
// every boundary point borders all of them, at every zoom.
//
// Ray-traced renders of this scatterer are prior art (Paul Bourke, "Wada basins",
// 1998, same tangent arrangement and coloured lights at three gaps; see the
// bibliography). FF adds it as an explorable family with the billiard colouring.
//
// This is not a distance-estimated march. Ray-sphere intersection has a closed
// form (a quadratic), so each bounce is exact: nearest hit, reflect about the
// normal, repeat. Geometry matches the Kleinian tetrahedral preset (centres at
// the even-parity (±1, ±1, ±1) cube corners, tangent radius √2), here with
// reflection instead of inversion. MirrorSpheresRadiusScale < 1 shrinks the
// balls apart and opens the gaps.
//
// Outcome per pixel:
//   • exit opening (0..3): the face of the tetrahedron the ray crosses on its way
//     OUT of the inner chamber (opening k lies in the face opposite ball k). That
//     is the paper's basin: a ray that has left cannot come back in, so the trace
//     stops there. -1 = still bouncing at the cap (trapped). A ray that never
//     enters the chamber (a miss, or a bounce off a ball's outer side) is not in any
//     basin; it is coloured by the opening its final direction points through and
//     muted (MirrorSpheresMuteMisses)
//   • bounce count: each reflection keeps MirrorSpheresReflectance of the light,
//     so deep-scattered rays near the basin boundaries darken, as in the photo
// Colour goes through IBilliardColorMap when the theme implements it (the
// billiard's categorical themes and ColorGen inputs: gate = opening, gateCount
// = 4), else the built-in Okabe-Ito opening colours.
//
// Photo mode (#1441) follows the photograph's setup: a coloured board over each
// opening, or the opening left open to the Lighting & FX sky / HDRI. The camera's
// own opening is black (dark room). Each reflection keeps the Schlick (1994) Fresnel
// share. Rays that never enter the chamber show the environment reflected in the
// balls' outer sides.
//
// The DE shading / Lighting & FX post stack does not apply (there is no surface to
// shade, only mirrors); Lighting & FX supplies only the sky / HDRI in photo mode.
// GPU (#1443): with GPU render on, MirrorSpheresGpuCalculator traces every ray (ILGPU,
// as the other 3D families) and the colour pass reads its outcomes, so themes and the
// photo palette are shared code. Low-res previews and a GPU failure trace on the CPU.
// Both paths give the same frame, so Auto (default) measures and keeps the faster one
// per device (the Buddhabrot #1220 pattern); FF_GPU_MIRROR_SPHERES forces either.

using System;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.Calculators.Gpu;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Rendering;
using FracturingFog.Rendering.Lighting;

namespace FracturingFog;

public sealed class MirrorSpheresCalculator : IFractalCalculator, IStereoEyeCamera, IDepthAovSource, ILowResPreviewCalculator, FracturingFog.Render.IGpuRouteSource
{
    public int Width { get; private set; }
    public int Height { get; private set; }
    public uint[] ColorBuffer { get; private set; } = Array.Empty<uint>();

    public double CenterX { get; set; } = 0.0;
    public double CenterY { get; set; } = 0.0;
    public double Zoom { get; set; } = 1.0;
    public int MaxIterations { get; set; } = 64;

    public QualityPreset Quality { get; set; } = QualityPreset.Standard;
    public IColorMap ColorMap { get; set; } = new HsvPalette();

    public bool SupportsZoomPan => true;

    public FractalParameters FractalParameters { get; set; } = new();

    /// <inheritdoc/>
    public double StereoEyeOffset { get; set; }

    /// <inheritdoc/>
    public float[]? DepthBuffer { get; private set; }

    /// <summary>Low-res interactive preview (camera drags). See Mandelbulb for the contract.</summary>
    public bool LowResPreview { get; set; }
    /// <inheritdoc/>
    public double? LowResPreviewScaleOverride { get; set; }

    /// <summary>#1443 — how the last frame was traced (GPU, or CPU and why).</summary>
    public FracturingFog.Render.GpuRoute LastGpuRoute { get; private set; }

    /// <summary>#1443 — where the trace runs when GPU render is on.</summary>
    public enum GpuMirrorSpheresMode
    {
        /// <summary>Measure on the first GPU frame (per device and mode), keep the faster,
        /// re-measure when the pixel count moves by more than <see cref="RemeasureFactor"/>.</summary>
        Auto,
        /// <summary>Always the GPU (<c>FF_GPU_MIRROR_SPHERES=1</c>).</summary>
        Gpu,
        /// <summary>Always the CPU (<c>FF_GPU_MIRROR_SPHERES=0</c>).</summary>
        Cpu,
    }

    /// <summary>#1443 — the GPU and the CPU trace identical outcomes (pixel-identical
    /// frames), so this is purely a speed choice, and the answer depends on the hardware:
    /// on a GT 710 (fp64 at 1/24 rate) against a 12-thread CPU the CPU won (1080p basins
    /// 91 vs 207 ms, photo 155 vs 253 ms), while a card with usable fp64 should win.
    /// <see cref="GpuMirrorSpheresMode.Auto"/> (default) measures; FF_GPU_MIRROR_SPHERES
    /// forces either path (1 / gpu, 0 / cpu). Setting it overrides for the calling thread
    /// only (tests).</summary>
    public static GpuMirrorSpheresMode GpuSelection
    {
        get => t_gpuSelection ?? s_gpuSelectionEnv;
        set => t_gpuSelection = value;
    }

    private static readonly GpuMirrorSpheresMode s_gpuSelectionEnv =
        BuddhaFamilyCalculator.ParseGpuBuddhaMode(Environment.GetEnvironmentVariable("FF_GPU_MIRROR_SPHERES")) switch
        {
            BuddhaFamilyCalculator.GpuBuddhaMode.Gpu => GpuMirrorSpheresMode.Gpu,
            BuddhaFamilyCalculator.GpuBuddhaMode.Cpu => GpuMirrorSpheresMode.Cpu,
            _ => GpuMirrorSpheresMode.Auto,
        };
    [ThreadStatic] private static GpuMirrorSpheresMode? t_gpuSelection;

    /// <summary>#1443 — Auto re-measures when the frame's pixel count differs from the
    /// measured frame by more than this factor.</summary>
    public const double RemeasureFactor = 4.0;

    /// <summary>#1443 — Auto times the CPU on every this-many-th row of the first GPU frame.</summary>
    public const int MeasureRowStride = 8;

    /// <summary>#1443 — one measured comparison: trace cost per pixel on each path.</summary>
    public sealed record GpuMeasurement(double CpuNsPerPixel, double GpuNsPerPixel, int Pixels)
    {
        public bool GpuFaster => GpuNsPerPixel < CpuNsPerPixel;
        public bool Covers(int pixels) => pixels <= Pixels * RemeasureFactor && Pixels <= pixels * RemeasureFactor;
        public string Summary => $"measured CPU {CpuNsPerPixel:0.#} vs GPU {GpuNsPerPixel:0.#} ns/pixel";
    }

    // Auto decisions per accelerator (freed with it), keyed by photo mode.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, System.Collections.Generic.Dictionary<bool, GpuMeasurement>> s_gpuDecisions = new();

    /// <summary>#1443 — the cached Auto measurement for this device and mode, or null.</summary>
    public static GpuMeasurement? CachedGpuMeasurement(object device, bool photo)
    {
        if (!s_gpuDecisions.TryGetValue(device, out var d)) return null;
        lock (d) return d.TryGetValue(photo, out var m) ? m : null;
    }

    private static void StoreGpuMeasurement(object device, bool photo, GpuMeasurement m)
    {
        var d = s_gpuDecisions.GetOrCreateValue(device);
        lock (d) d[photo] = m;
    }

    /// <summary>#1443 — the measurement behind the last Calculate's path (Auto), or null.</summary>
    public GpuMeasurement? LastGpuMeasurement { get; private set; }

    // #1443 — lazily-constructed GPU tracer (ILGPU, the 3D families' path).
    private MirrorSpheresGpuCalculator? _gpu;

    public MirrorSpheresCalculator(int width, int height) => Resize(width, height);

    /// <summary>Built-in colour of opening <paramref name="k"/> when the theme is not a
    /// billiard theme: the Okabe-Ito categorical palette (colour-blind safe).</summary>
    public static uint OpeningColor(int k) => unchecked((uint)BilliardColorHelper.GateColor(k));

    public void Resize(int width, int height)
    {
        Width = width;
        Height = height;
        ColorBuffer = new uint[width * height];
    }

    public void Calculate(CancellationToken ct = default)
    {
        DepthBuffer = null;   // never describe an older frame
        var p = FractalParameters;
        var scene = MirrorSpheresScene.Build(p);
        int maxBounces = scene.MaxBounces;
        ColorMap.MaxIterations = maxBounces;

        int fullW = Width, fullH = Height;
        bool lowRes = LowResPreview;
        double lrScale = FracturingFog.Rendering.LowResPreview.EffectiveScale(lowRes, LowResPreviewScaleOverride, p.LowResPreviewScale);
        var dims = FracturingFog.Rendering.LowResPreview.ComputeDims(fullW, fullH, lrScale);
        int width = dims.Width, height = dims.Height;
        uint[] renderBuffer = lowRes ? new uint[width * height] : ColorBuffer;

        var cam = MirrorSpheresCamera.From(p, Zoom, CenterX, CenterY, StereoEyeOffset, scene.BoundingRadius);

        var fx = p.Lighting;
        float[]? depthBuf = ScreenSpacePost.WantsDepthOutput(in fx) ? new float[width * height] : null;
        bool photo = p.MirrorSpheresMode == MirrorSpheresMode.Photo;

        // #1443 — the GPU traces every ray (the same chamber-exit trace, plus the free
        // continuation photo mode needs) and the colour pass below reads its outcomes.
        // The gate is the shared 3D one: GPU render on, not a low-res preview.
        MirrorSpheresGpuCalculator.Outcomes? gpuOut = null;
        LastGpuMeasurement = null;
        var gpuGate = Gpu3DRoute.Gate(in fx, lowRes);
        LastGpuRoute = gpuGate ?? default;
        bool measureCpu = false;
        double gpuMs = 0;
        object? device = null;
        if (gpuGate is null)
        {
            // #1443 — GPU and CPU trace the same image, so the path is a speed choice
            // (the #1220 pattern): Auto keeps a measured decision per device and mode.
            var mode = GpuSelection;
            if (GpuAcceleratorHost.TryAcquire(out var acc)) device = acc;
            var known = mode == GpuMirrorSpheresMode.Auto && device != null ? CachedGpuMeasurement(device, photo) : null;
            if (known != null && !known.Covers(width * height)) known = null;
            if (mode == GpuMirrorSpheresMode.Cpu)
            {
                LastGpuRoute = FracturingFog.Render.GpuRoute.Cpu("CPU forced", "FF_GPU_MIRROR_SPHERES selects the CPU trace");
            }
            else if (known is { GpuFaster: false })
            {
                LastGpuMeasurement = known;
                LastGpuRoute = FracturingFog.Render.GpuRoute.Cpu("CPU faster", known.Summary);
            }
            else
            {
                _gpu ??= new MirrorSpheresGpuCalculator();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                gpuOut = _gpu.Render(cam.ToGpuParams(width, height), MirrorSpheresGpuCalculator.ParamsFor(scene, photo), ct);
                gpuMs = sw.Elapsed.TotalMilliseconds;
                LastGpuRoute = Gpu3DRoute.AfterRender(gpuOut != null, _gpu.LastError);
                if (ct.IsCancellationRequested) return;
                measureCpu = gpuOut != null && mode == GpuMirrorSpheresMode.Auto && known == null && device != null;
                if (known != null) LastGpuMeasurement = known;
            }
        }

        var billiardMap = ColorMap as IBilliardColorMap;
        uint trapped = ColorMap.InSetColor;
        bool mute = p.MirrorSpheresMuteMisses;
        double reflectance = Math.Clamp(p.MirrorSpheresReflectance, 0.0, 1.0);
        double pathRef = cam.Distance * 4.0;
        var paint = MirrorSpheresPalette.Parse(p.MirrorSpheresOpeningColors);
        int viewer = photo && p.MirrorSpheresViewerBlack
            ? MirrorSpheresScene.Opening(cam.X, cam.Y, cam.Z)   // the face the camera sits in front of
            : -1;

        Parallel.For(0, height, new ParallelOptions { CancellationToken = ct }, y =>
        {
            if (ct.IsCancellationRequested) return;
            int rowBase = y * width;
            for (int x = 0; x < width; x++)
            {
                int idx = rowBase + x;
                double dx = 0, dy = 0, dz = 0;
                MirrorSpheresOutcome o;
                if (gpuOut != null) o = gpuOut.Exit(idx);
                else
                {
                    cam.Ray(x, y, width, height, out dx, out dy, out dz);
                    o = scene.Trace(cam.X, cam.Y, cam.Z, dx, dy, dz);
                }
                if (depthBuf != null) depthBuf[idx] = o.Bounces > 0 ? (float)o.FirstHit : ScreenSpacePost.DepthMiss;

                uint col;
                if (photo)
                {
                    MirrorSpheresOutcome free = default;
                    if (PhotoNeedsFreeTrace(in o, paint, viewer))
                        free = gpuOut != null ? gpuOut.Free(idx) : scene.Trace(cam.X, cam.Y, cam.Z, dx, dy, dz, stopAtChamberExit: false);
                    col = PhotoColor(in o, in free, paint, viewer, in fx);
                }
                else if (o.Gate < 0)
                {
                    col = trapped;
                }
                else
                {
                    col = billiardMap != null
                        ? unchecked((uint)billiardMap.MapBilliard(o.Gate, MirrorSpheresScene.OpeningCount, o.Bounces, maxBounces,
                            (float)Math.Clamp(o.PathLength / pathRef, 0.0, 1.0)))
                        : OpeningColor(o.Gate);
                    if (!o.EnteredChamber)
                    {
                        if (mute) col = ChaoticBilliardCalculator.Mute(col);
                    }
                    else if (reflectance < 1.0 && o.Bounces > 0)
                    {
                        col = unchecked((uint)BilliardColorHelper.Shade(unchecked((int)col), (float)Math.Pow(reflectance, o.Bounces)));
                    }
                }
                renderBuffer[idx] = col;
            }
        });

        if (measureCpu)
        {
            // Auto's first GPU frame: time the CPU trace of every 8th row of the same
            // frame, per pixel, against the GPU frame (trace + readback).
            int rows = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Parallel.For(0, (height + MeasureRowStride - 1) / MeasureRowStride, new ParallelOptions { CancellationToken = ct }, r =>
            {
                int y = r * MeasureRowStride;
                for (int x = 0; x < width; x++)
                {
                    cam.Ray(x, y, width, height, out double dx, out double dy, out double dz);
                    var o = scene.Trace(cam.X, cam.Y, cam.Z, dx, dy, dz);
                    if (photo && PhotoNeedsFreeTrace(in o, paint, viewer))
                        scene.Trace(cam.X, cam.Y, cam.Z, dx, dy, dz, stopAtChamberExit: false);
                }
                Interlocked.Increment(ref rows);
            });
            double cpuNs = sw.Elapsed.TotalMilliseconds * 1e6 / Math.Max(1, rows * width);
            var m = new GpuMeasurement(cpuNs, gpuMs * 1e6 / Math.Max(1, width * height), width * height);
            StoreGpuMeasurement(device!, photo, m);
            LastGpuMeasurement = m;
        }

        DepthBuffer = ScreenSpacePost.PublishDepth(depthBuf, width, height, fullW, fullH, in fx);
        if (lowRes)
            FracturingFog.Rendering.LowResPreview.UpscaleNearest(renderBuffer, width, height, ColorBuffer, fullW, fullH);
        ScreenSpacePost.FinishFrame3D(ColorBuffer, fullW, fullH, in fx, preview: lowRes);
    }

    /// <summary>#1441 — photo mode needs the free continuation (the ray traced on past
    /// the chamber until nothing is ahead) when the ray leaves through an <c>env</c>
    /// opening or never entered the chamber at all.</summary>
    public static bool PhotoNeedsFreeTrace(in MirrorSpheresOutcome o, MirrorSpheresPalette.Paint[] paint, int viewerOpening)
    {
        if (o.Gate < 0) return false;
        if (!o.EnteredChamber) return true;
        return o.Gate != viewerOpening && paint[o.Gate].Environment;
    }

    /// <summary>#1441 — the photograph's physics. A ray leaving the chamber through a
    /// covered opening takes the board's colour; through an <c>env</c> opening (or a ray
    /// that never entered: the room reflected in the balls' outer sides) it takes the
    /// environment along the final direction of <paramref name="free"/>. Either way the
    /// colour is scaled by the light the reflections kept. The camera's own opening is
    /// black when <c>MirrorSpheresViewerBlack</c> is on (<paramref name="viewerOpening"/> ≥ 0).</summary>
    public static uint PhotoColor(in MirrorSpheresOutcome o, in MirrorSpheresOutcome free, MirrorSpheresPalette.Paint[] paint,
                                  int viewerOpening, in LightingFxData fx)
    {
        const uint black = 0xFF000000u;
        if (o.Gate < 0) return black;   // trapped: no light gets out
        if (o.EnteredChamber && o.Gate == viewerOpening) return black;
        if (o.EnteredChamber && !paint[o.Gate].Environment)
            return Scale(paint[o.Gate].Color, o.Throughput);

        if (free.Gate < 0) return black;
        uint sky = ShadingPipeline.ApplyBackgroundFog(
            ShadingPipeline.SkyColorHdri(free.EndDx, free.EndDy, free.EndDz, in fx), free.EndDy, in fx);
        return Scale(sky, free.Throughput);
    }

    private static uint Scale(uint argb, double k)
        => k >= 1.0 ? argb | 0xFF000000u : unchecked((uint)BilliardColorHelper.Shade(unchecked((int)argb), (float)k));
}


/// <summary>One traced camera ray through the mirror spheres (#1440).</summary>
public readonly struct MirrorSpheresOutcome
{
    /// <summary>Exit opening 0..3 (the gap opposite ball k), -1 when trapped at the bounce cap.
    /// For a ray that never entered the chamber: the opening its final direction points through.</summary>
    public int Gate { get; init; }
    /// <summary>True when the ray passed through the inner chamber (its Gate is a basin).</summary>
    public bool EnteredChamber { get; init; }
    /// <summary>Reflections before escape (the cap when trapped).</summary>
    public int Bounces { get; init; }
    /// <summary>Ray distance to the first reflection (+Infinity when no ball is hit).</summary>
    public double FirstHit { get; init; }
    /// <summary>World-space path length from the origin to the last reflection.</summary>
    public double PathLength { get; init; }
    /// <summary>#1441 — share of the light the reflections kept (the product of the
    /// per-bounce reflectance: F0, or Schlick's Fresnel term in photo mode).</summary>
    public double Throughput { get; init; }
    /// <summary>Where the trace ended: the chamber exit point (on the face plane), else
    /// the last reflection point (the origin when no ball is hit).</summary>
    public double EndX { get; init; }
    public double EndY { get; init; }
    public double EndZ { get; init; }
    /// <summary>Final unit direction.</summary>
    public double EndDx { get; init; }
    public double EndDy { get; init; }
    public double EndDz { get; init; }
}

/// <summary>
/// The four mirror balls of one frame, built once from <see cref="FractalParameters"/>
/// and traced per pixel. Public so tests can check the physics (the in-plane reduction
/// to the three-disk billiard, tetrahedral symmetry) on the exact render path.
/// </summary>
public sealed class MirrorSpheresScene
{
    /// <summary>Openings = faces of the tetrahedron.</summary>
    public const int OpeningCount = 4;

    // Even-parity cube corners: every pair is 2√2 apart, so radius √2 is tangent.
    private static readonly double[] s_cx = { 1, 1, -1, -1 };
    private static readonly double[] s_cy = { 1, -1, 1, -1 };
    private static readonly double[] s_cz = { 1, -1, -1, 1 };
    /// <summary>Radius at which neighbouring balls touch.</summary>
    public static readonly double TangentRadius = Math.Sqrt(2.0);

    public double Radius { get; }
    public int MaxBounces { get; }
    /// <summary>#1441 — normal-incidence reflectance F0 (MirrorSpheresReflectance).</summary>
    public double Reflectance { get; init; } = 1.0;
    /// <summary>#1441 — weight each bounce by Schlick's Fresnel term instead of F0.</summary>
    public bool Fresnel { get; init; }
    /// <summary>Radius of a sphere about the origin that encloses every ball.</summary>
    public double BoundingRadius => Math.Sqrt(3.0) + Radius;

    public static double CentreX(int k) => s_cx[k];
    public static double CentreY(int k) => s_cy[k];
    public static double CentreZ(int k) => s_cz[k];

    public MirrorSpheresScene(double radius, int maxBounces)
    {
        Radius = radius;
        MaxBounces = maxBounces;
    }

    public static MirrorSpheresScene Build(FractalParameters p)
        => new(TangentRadius * Math.Clamp(p.MirrorSpheresRadiusScale, 0.05, 1.0),
               Math.Clamp(p.MirrorSpheresMaxBounces, 1, 4096))
        {
            Reflectance = Math.Clamp(p.MirrorSpheresReflectance, 0.0, 1.0),
            Fresnel = p.MirrorSpheresFresnel && p.MirrorSpheresMode == MirrorSpheresMode.Photo,
        };

    /// <summary>#1441 — Schlick's (1994) Fresnel approximation for a mirror with
    /// normal-incidence reflectance <paramref name="f0"/> at incidence cosine
    /// <paramref name="cosTheta"/>: F0 + (1 − F0)(1 − cos θ)⁵.</summary>
    public static double Schlick(double f0, double cosTheta)
    {
        double m = 1.0 - Math.Clamp(cosTheta, 0.0, 1.0);
        double m2 = m * m;
        return f0 + (1.0 - f0) * m2 * m2 * m;
    }

    // Face k (opposite ball k) has outward normal -c_k/|c_k| = -c_k/√3 and sits at
    // distance FaceOffset from the origin (c_k · c_j = -1 for any two corners).
    private static readonly double FaceOffset = 1.0 / Math.Sqrt(3.0);
    private static readonly double InvSqrt3 = 1.0 / Math.Sqrt(3.0);

    /// <summary>Trace a ray from (<paramref name="ox"/>, <paramref name="oy"/>, <paramref name="oz"/>)
    /// along the unit direction (<paramref name="dx"/>, <paramref name="dy"/>, <paramref name="dz"/>),
    /// starting outside every ball. With <paramref name="stopAtChamberExit"/> (the render) the
    /// trace ends where the ray leaves the inner chamber and the gate is that face's opening.
    /// Without it the ray bounces until nothing is ahead and the gate is the opening its
    /// final direction points through (the plain-reflection oracle the tests compare
    /// against the 2D billiard).</summary>
    public MirrorSpheresOutcome Trace(double ox, double oy, double oz, double dx, double dy, double dz,
                                      bool stopAtChamberExit = true)
    {
        double r2 = Radius * Radius;
        const double eps = 1e-9;
        double px = ox, py = oy, pz = oz;
        double first = double.PositiveInfinity, path = 0.0, light = 1.0;
        int bounces = 0, last = -1;

        while (bounces < MaxBounces)
        {
            // Nearest ball ahead. A convex mirror cannot be hit twice in a row, so
            // the ball just left is skipped (no self-hit from rounding).
            double tHit = double.PositiveInfinity;
            int hit = -1;
            for (int k = 0; k < 4; k++)
            {
                if (k == last) continue;
                double qx = px - s_cx[k], qy = py - s_cy[k], qz = pz - s_cz[k];
                double b = qx * dx + qy * dy + qz * dz;
                double c = qx * qx + qy * qy + qz * qz - r2;
                double disc = b * b - c;
                if (disc < 0.0) continue;
                double t = -b - Math.Sqrt(disc);   // near root
                if (t > eps && t < tHit) { tHit = t; hit = k; }
            }

            if (stopAtChamberExit)
            {
                // The part of this segment inside the tetrahedron (the chamber): the
                // ray is outside every ball, so where it crosses a face it is in that
                // face's opening. Leaving before the next hit ends the trace.
                ChamberInterval(px, py, pz, dx, dy, dz, out double tIn, out double tOut, out int exitFace);
                if (tIn < tOut && tOut > 0.0 && tIn < tHit && tOut <= tHit)
                {
                    return new MirrorSpheresOutcome
                    {
                        Gate = exitFace, EnteredChamber = true, Bounces = bounces, FirstHit = first,
                        PathLength = path + tOut, Throughput = light,
                        EndX = px + tOut * dx, EndY = py + tOut * dy, EndZ = pz + tOut * dz,
                        EndDx = dx, EndDy = dy, EndDz = dz,
                    };
                }
            }

            if (hit < 0)
            {
                return new MirrorSpheresOutcome
                {
                    Gate = Opening(dx, dy, dz), EnteredChamber = !stopAtChamberExit && bounces > 0,
                    Bounces = bounces, FirstHit = first, PathLength = path, Throughput = light,
                    EndX = px, EndY = py, EndZ = pz, EndDx = dx, EndDy = dy, EndDz = dz,
                };
            }

            px += tHit * dx; py += tHit * dy; pz += tHit * dz;
            path += tHit;
            if (bounces == 0) first = tHit;
            double nx = (px - s_cx[hit]) / Radius, ny = (py - s_cy[hit]) / Radius, nz = (pz - s_cz[hit]) / Radius;
            double dot = dx * nx + dy * ny + dz * nz;
            light *= Fresnel ? Schlick(Reflectance, -dot) : Reflectance;   // cos θ = -d·n (incoming)
            dx -= 2.0 * dot * nx; dy -= 2.0 * dot * ny; dz -= 2.0 * dot * nz;
            last = hit;
            bounces++;
        }

        return new MirrorSpheresOutcome   // trapped
        {
            Gate = -1, EnteredChamber = true, Bounces = bounces, FirstHit = first, PathLength = path,
            Throughput = light,
            EndX = px, EndY = py, EndZ = pz, EndDx = dx, EndDy = dy, EndDz = dz,
        };
    }

    /// <summary>The ray-parameter interval [<paramref name="tIn"/>, <paramref name="tOut"/>]
    /// inside the tetrahedron (empty when tIn &gt;= tOut), and the face it leaves through.</summary>
    private static void ChamberInterval(double px, double py, double pz, double dx, double dy, double dz,
                                        out double tIn, out double tOut, out int exitFace)
    {
        tIn = double.NegativeInfinity;
        tOut = double.PositiveInfinity;
        exitFace = -1;
        for (int k = 0; k < 4; k++)
        {
            double nx = -s_cx[k] * InvSqrt3, ny = -s_cy[k] * InvSqrt3, nz = -s_cz[k] * InvSqrt3;
            double num = FaceOffset - (nx * px + ny * py + nz * pz);   // >= 0 inside this half-space
            double den = nx * dx + ny * dy + nz * dz;
            if (den > 0.0)
            {
                double t = num / den;
                if (t < tOut) { tOut = t; exitFace = k; }
            }
            else if (den < 0.0)
            {
                double t = num / den;
                if (t > tIn) tIn = t;
            }
            else if (num < 0.0)
            {
                tIn = double.PositiveInfinity;   // parallel and outside: never inside
            }
        }
    }

    /// <summary>The opening an escaping direction belongs to: the face of the tetrahedron
    /// whose outward normal (-c_k / |c_k|, the face opposite ball k) it is closest to.</summary>
    public static int Opening(double dx, double dy, double dz)
    {
        int best = 0;
        double bestDot = double.NegativeInfinity;
        for (int k = 0; k < 4; k++)
        {
            double d = -(dx * s_cx[k] + dy * s_cy[k] + dz * s_cz[k]);   // |c_k| is the same for all four
            if (d > bestDot) { bestDot = d; best = k; }
        }
        return best;
    }
}

/// <summary>The orbit camera of the mirror-spheres view (the Kleinian camera block:
/// spherical position about the origin, looking at it, world up +y).</summary>
public readonly struct MirrorSpheresCamera
{
    public double X { get; init; }
    public double Y { get; init; }
    public double Z { get; init; }
    public double Distance { get; init; }
    private readonly double _fx, _fy, _fz, _rx, _ry, _rz, _ux, _uy, _uz, _fov, _panU, _panV;

    private MirrorSpheresCamera(double x, double y, double z, double dist,
        double fx, double fy, double fz, double rx, double ry, double rz, double ux, double uy, double uz,
        double fov, double panU, double panV)
    {
        X = x; Y = y; Z = z; Distance = dist;
        _fx = fx; _fy = fy; _fz = fz; _rx = rx; _ry = ry; _rz = rz; _ux = ux; _uy = uy; _uz = uz;
        _fov = fov; _panU = panU; _panV = panV;
    }

    public static MirrorSpheresCamera From(FractalParameters p, double zoom, double centerX, double centerY,
                                           double eyeOffset, double boundingRadius)
    {
        double floor = boundingRadius + 0.5;
        double raw = p.MirrorSpheresCameraDistance / Math.Max(0.05, zoom);
        double dist = Math.Max(floor, raw);
        double th = p.MirrorSpheresCameraTheta, ph = p.MirrorSpheresCameraPhi;
        double x = dist * Math.Sin(ph) * Math.Cos(th);
        double y = dist * Math.Cos(ph);
        double z = dist * Math.Sin(ph) * Math.Sin(th);

        Norm(-x, -y, -z, out double fx, out double fy, out double fz);
        Norm(fy * 0 - fz * 1, fz * 0 - fx * 0, fx * 1 - fy * 0, out double rx, out double ry, out double rz);   // fwd × up(0,1,0)
        double ux = ry * fz - rz * fy, uy = rz * fx - rx * fz, uz = rx * fy - ry * fx;
        x += rx * eyeOffset; y += ry * eyeOffset; z += rz * eyeOffset;

        // Inside the floor, zoom narrows the lens instead of moving the camera.
        double lens = raw >= floor ? 1.0 : Math.Max(0.05, raw / floor);
        double fov = Math.Tan(0.5 * Math.PI / 3.0) * lens;
        return new MirrorSpheresCamera(x, y, z, dist, fx, fy, fz, rx, ry, rz, ux, uy, uz, fov, centerX, -centerY);
    }

    /// <summary>#1443 — this camera as the GPU kernel's parameters for a
    /// <paramref name="width"/> × <paramref name="height"/> frame (the kernel rebuilds
    /// each ray exactly as <see cref="Ray"/> does).</summary>
    public GpuRaymarchParams ToGpuParams(int width, int height) => new()
    {
        Width = width, Height = height,
        CamX = X, CamY = Y, CamZ = Z,
        FwdX = _fx, FwdY = _fy, FwdZ = _fz,
        RightX = _rx, RightY = _ry, RightZ = _rz,
        UpX = _ux, UpY = _uy, UpZ = _uz,
        FovScale = _fov, Aspect = (double)width / height,
        PanU = _panU, PanV = _panV,
    };

    /// <summary>Unit ray direction through pixel (<paramref name="px"/>, <paramref name="py"/>).</summary>
    public void Ray(int px, int py, int width, int height, out double dx, out double dy, out double dz)
    {
        double aspect = (double)width / height;
        double u = (2.0 * (px + 0.5) / width - 1.0) * _fov * aspect + _panU;
        double v = (1.0 - 2.0 * (py + 0.5) / height) * _fov + _panV;
        Norm(_rx * u + _ux * v + _fx, _ry * u + _uy * v + _fy, _rz * u + _uz * v + _fz, out dx, out dy, out dz);
    }

    private static void Norm(double x, double y, double z, out double ox, out double oy, out double oz)
    {
        double len = Math.Sqrt(x * x + y * y + z * z);
        if (len < 1e-12) { ox = oy = oz = 0; return; }
        ox = x / len; oy = y / len; oz = z / len;
    }
}

/// <summary>#1441 — what lies beyond each opening in photo mode, parsed from
/// <c>FractalParameters.MirrorSpheresOpeningColors</c>.</summary>
public static class MirrorSpheresPalette
{
    /// <summary>One opening's cover: a board colour, or open to the environment.</summary>
    public readonly record struct Paint(uint Color, bool Environment);

    /// <summary>Default covers: vermillion, blue, white, black (Okabe-Ito vermillion and
    /// blue stand in for the photo's red and blue boards; white for the cloth below).</summary>
    public const string DefaultText = "#D55E00,#0072B2,#F2F2F2,#000000";

    private static readonly Paint[] s_default =
    {
        new(0xFFD55E00u, false), new(0xFF0072B2u, false), new(0xFFF2F2F2u, false), new(0xFF000000u, false),
    };

    /// <summary>Parse "#RRGGBB" / "RRGGBB" / "env" entries, comma-separated, in opening
    /// order. A missing or malformed entry falls back to that opening's default.</summary>
    public static Paint[] Parse(string? text)
    {
        var result = (Paint[])s_default.Clone();
        if (string.IsNullOrWhiteSpace(text)) return result;
        var parts = text.Split(',');
        for (int k = 0; k < result.Length && k < parts.Length; k++)
        {
            string t = parts[k].Trim();
            if (t.Equals("env", StringComparison.OrdinalIgnoreCase))
            {
                result[k] = new Paint(0, true);
                continue;
            }
            if (t.StartsWith('#')) t = t.Substring(1);
            if (t.Length == 6 && uint.TryParse(t, System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out uint rgb))
                result[k] = new Paint(0xFF000000u | rgb, false);
        }
        return result;
    }
}
