// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// BlackHoleRingCalculator.cs (#1947, gravitational-scattering set §2.6)
//
// Free test particles around a Schwarzschild black hole with a Bach-Weyl ring (Scattering/BlackHoleRing.cs,
// after Semerák & Suková, MNRAS 404, 545, 2010). Two views of the equatorial section (r, u^r):
//  • Section (default): their Poincaré section. A row of orbits starts on the section at u^r = 0 across
//    the visible r range (moving up, u^z ≥ 0 from the normalisation), each is followed for a proper time
//    and every equatorial crossing (both directions, as they record) is plotted. Without the ring the
//    motion is integrable and every orbit draws a closed curve; the ring breaks that into islands and a
//    chaotic sea.
//  • Fates: each pixel is a launch from the section point; colour by captured by the black hole, hit the
//    ring, escaped, or still bound at the time limit.
// The vertical axis is u^r times BhRingVelocityScale (r and u^r differ by two orders of magnitude, and
// the 2D camera has one pitch). Grey: outside the accessible region (no u^z ≥ 0 at that (r, u^r)).

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Scattering;

namespace FracturingFog;

public sealed class BlackHoleRingCalculator : IFractalCalculator, IHeightFieldSource,
    FracturingFog.Rendering.ILowResPreviewCalculator
{
    public int Width { get; private set; }
    public int Height { get; private set; }
    public uint[] ColorBuffer { get; private set; } = Array.Empty<uint>();
    public float[] SmoothBuffer { get; private set; } = Array.Empty<float>();
    /// <summary>Section: crossings per pixel. Fates: gate per pixel (see <see cref="BhRingSystem"/>), −1
    /// bound, <see cref="OutsideGate"/> outside the accessible region.</summary>
    public int[] CountBuffer { get; private set; } = Array.Empty<int>();
    public sbyte[] GateBuffer { get; private set; } = Array.Empty<sbyte>();
    public const sbyte OutsideGate = -2;

    public double CenterX { get; set; } = DefaultCenterX;
    public double CenterY { get; set; } = 0.0;
    public double Zoom { get; set; } = DefaultZoom;
    public int MaxIterations { get; set; } = 256;
    public QualityPreset Quality { get; set; } = QualityPreset.Standard;
    public IColorMap ColorMap { get; set; } = new HsvPalette();
    public bool SupportsZoomPan => true;
    public FractalParameters FractalParameters { get; set; } = new();

    public bool LowResPreview { get; set; }
    public double? LowResPreviewScaleOverride { get; set; }
    private BlackHoleRingCalculator? _preview;

    /// <summary>Default framing: r from about 1.5 to 30 M across, u^r about ±0.14 (scale 60).</summary>
    public const double DefaultCenterX = 16.0, DefaultZoom = 0.12;

    public const uint OutsideColor = 0xFF0A0A0A;
    public const uint InsideColor = 0xFF262626;
    /// <summary>The black hole itself (0 &lt; r below the capture radius) and, darker still, r ≤ 0 (no radius).</summary>
    public const uint HoleColor = 0xFF161616, NoRadiusColor = 0xFF000000;
    public const sbyte HoleGate = -3;

    // Background for a pixel with no launch: the hole, no radius at all, or outside the accessible region.
    private static (uint Colour, sbyte Gate) NoLaunch(double r)
        => r <= 0 ? (NoRadiusColor, OutsideGate) : r < BhRingSystem.CaptureR ? (HoleColor, HoleGate) : (OutsideColor, OutsideGate);

    /// <summary>The section points of the last Section frame (r, u^r), in seed order.</summary>
    public IReadOnlyList<(int Orbit, double R, double Ur)> LastCrossings { get; private set; } = Array.Empty<(int, double, double)>();

    public BlackHoleRingCalculator(int width, int height) => Resize(width, height);

    public void Resize(int width, int height)
    {
        Width = width;
        Height = height;
        ColorBuffer = new uint[width * height];
        SmoothBuffer = new float[width * height];
        CountBuffer = new int[width * height];
        GateBuffer = new sbyte[width * height];
    }

    /// <summary>(r, u^r) of pixel position (x, y) (pixel centres at +0.5).</summary>
    public static (double R, double Ur) PlaneAt(double x, double y, double width, double height,
                                                double centerX, double centerY, double zoom, double velocityScale)
    {
        double pitch = FracturingFog.ViewState.ViewCamera.PixelPitch(width, height, zoom);
        return (centerX + (x - width * 0.5) * pitch, -(centerY + (y - height * 0.5) * pitch) / velocityScale);
    }

    public void Calculate(CancellationToken ct = default)
    {
        if (LowResPreview && RenderPreview(ct)) return;
        var p = FractalParameters;
        var scene = BhRingScene.Build(p);
        int w = Width, h = Height;
        double cx = CenterX, cy = CenterY, zoom = Zoom, vs = scene.VelocityScale;
        double pitch = FracturingFog.ViewState.ViewCamera.PixelPitch(w, h, zoom);

        // λ on the equator per column (launches are on z = 0; λ depends on r only).
        var lamCol = new double[w];
        Parallel.For(0, w, new ParallelOptions { CancellationToken = ct }, x =>
        {
            double r = cx + (x + 0.5 - w * 0.5) * pitch;
            lamCol[x] = r > BhRingSystem.CaptureR ? scene.EquatorLambda(r) : double.NaN;
        });
        if (ct.IsCancellationRequested) return;

        if (p.BhRingView == BlackHoleRingView.Fates) { Fates(scene, lamCol, ct); return; }
        Section(scene, lamCol, p.BhRingColour, ct);
    }

    private void Fates(BhRingScene scene, double[] lamCol, CancellationToken ct)
    {
        int w = Width, h = Height;
        double cx = CenterX, cy = CenterY, zoom = Zoom, vs = scene.VelocityScale;
        var billiard = ColorMap as IBilliardColorMap;
        ColorMap.MaxIterations = MaxIterations;
        uint bound = ColorMap.InSetColor | 0xFF000000u;
        double shadeRef = Math.Log(1.0 + scene.FateTime);
        Parallel.For(0, h, new ParallelOptions { CancellationToken = ct }, y =>
        {
            if (ct.IsCancellationRequested) return;
            Span<double> s = stackalloc double[5];
            for (int x = 0; x < w; x++)
            {
                int idx = y * w + x;
                var (r, ur) = PlaneAt(x + 0.5, y + 0.5, w, h, cx, cy, zoom, vs);
                if (!double.IsFinite(lamCol[x]) || !scene.LaunchState(r, ur, s, lamCol[x]))
                {
                    var (col, g) = NoLaunch(r);
                    GateBuffer[idx] = g; SmoothBuffer[idx] = 0f; ColorBuffer[idx] = col;
                    continue;
                }
                var sys = scene.System(scene.FateTime);
                var settings = scene.Settings;
                ScatteringIntegrator.Trace(ref sys, s, in settings);
                int gate = sys.Gate;
                GateBuffer[idx] = (sbyte)gate;
                double v = Math.Log(1.0 + sys.Time);
                SmoothBuffer[idx] = (float)v;
                double t = Math.Clamp(v / shadeRef, 0.0, 1.0);
                ColorBuffer[idx] = billiard != null
                    ? unchecked((uint)billiard.MapBilliard(gate, BhRingSystem.GateCount, (int)(t * 64), 64, (float)t)) | 0xFF000000u
                    : gate < 0 ? bound
                    : unchecked((uint)BilliardColorHelper.Shade(BilliardColorHelper.GateColor(gate), (float)(1.0 - 0.6 * t))) | 0xFF000000u;
            }
        });
    }

    private void Section(BhRingScene scene, double[] lamCol, BlackHoleRingColour mode, CancellationToken ct)
    {
        int w = Width, h = Height;
        double cx = CenterX, cy = CenterY, zoom = Zoom, vs = scene.VelocityScale;
        double pitch = FracturingFog.ViewState.ViewCamera.PixelPitch(w, h, zoom);

        // Background: the accessible region (some u^z ≥ 0 exists) vs outside.
        Parallel.For(0, h, new ParallelOptions { CancellationToken = ct }, y =>
        {
            Span<double> s = stackalloc double[5];
            for (int x = 0; x < w; x++)
            {
                int idx = y * w + x;
                var (r, ur) = PlaneAt(x + 0.5, y + 0.5, w, h, cx, cy, zoom, vs);
                bool inside = double.IsFinite(lamCol[x]) && scene.LaunchState(r, ur, s, lamCol[x]);
                var (col, g) = inside ? (InsideColor, (sbyte)-1) : NoLaunch(r);
                ColorBuffer[idx] = col;
                GateBuffer[idx] = g;
                CountBuffer[idx] = 0;
                SmoothBuffer[idx] = 0f;
            }
        });
        if (ct.IsCancellationRequested) return;

        // Seeds: u^r = 0 on the section, evenly across the visible r range (the accessible ones).
        int n = scene.Orbits;
        double rLeft = cx - 0.5 * w * pitch, rRight = cx + 0.5 * w * pitch;
        var orbitPoints = new List<(double R, double Ur)>?[n];
        Parallel.For(0, n, new ParallelOptions { CancellationToken = ct }, i =>
        {
            double r = rLeft + (i + 0.5) * (rRight - rLeft) / n;
            if (!(r > BhRingSystem.CaptureR)) return;
            Span<double> s = stackalloc double[5];
            if (!scene.LaunchState(r, 0.0, s)) return;
            var pts = new List<(double R, double Ur)>();
            var sys = scene.System(scene.OrbitTime, pts);
            var settings = scene.Settings;
            ScatteringIntegrator.Trace(ref sys, s, in settings);
            orbitPoints[i] = pts;
        });
        if (ct.IsCancellationRequested) return;

        var all = new List<(int, double, double)>();
        int maxCount = 0;
        for (int i = 0; i < n; i++)
        {
            if (orbitPoints[i] is not { } pts) continue;
            foreach (var (r, ur) in pts)
            {
                all.Add((i, r, ur));
                int x = (int)Math.Floor((r - cx) / pitch + w * 0.5);
                int y = (int)Math.Floor(-ur * vs / pitch - cy / pitch + h * 0.5);
                if (x < 0 || y < 0 || x >= w || y >= h) continue;
                int idx = y * w + x;
                int c = ++CountBuffer[idx];
                if (c > maxCount) maxCount = c;
                if (mode == BlackHoleRingColour.ByOrbit)
                    ColorBuffer[idx] = unchecked((uint)BilliardColorHelper.GateColor(i % 7)) | 0xFF000000u;
            }
        }
        LastCrossings = all;
        if (mode == BlackHoleRingColour.Density && maxCount > 0)
        {
            ColorMap.MaxIterations = MaxIterations;
            double norm = Math.Log(1.0 + maxCount);
            for (int idx = 0; idx < w * h; idx++)
            {
                int c = CountBuffer[idx];
                if (c == 0) continue;
                double v = Math.Log(1.0 + c) / norm;
                SmoothBuffer[idx] = (float)v;
                ColorBuffer[idx] = BlackHoleShadowCalculator.PaletteColour(ColorMap, 0.15 * MaxIterations + v * 0.8 * MaxIterations);
            }
        }
        else
        {
            for (int idx = 0; idx < w * h; idx++) SmoothBuffer[idx] = CountBuffer[idx] > 0 ? 1f : 0f;
        }
    }

    // #1373 drag preview: a smaller sibling, upscaled.
    private bool RenderPreview(CancellationToken ct)
    {
        double sc = FracturingFog.Rendering.LowResPreview.EffectiveScale(true, LowResPreviewScaleOverride,
                                                                          FractalParameters.LowResPreviewScale);
        if (sc >= 1.0) return false;
        var dims = FracturingFog.Rendering.LowResPreview.ComputeDims(Width, Height, sc);
        if (_preview == null || _preview.Width != dims.Width || _preview.Height != dims.Height)
            _preview = new BlackHoleRingCalculator(dims.Width, dims.Height);
        _preview.FractalParameters = FractalParameters;
        _preview.CenterX = CenterX; _preview.CenterY = CenterY; _preview.Zoom = Zoom;
        _preview.MaxIterations = MaxIterations; _preview.ColorMap = ColorMap; _preview.Quality = Quality;
        _preview.Calculate(ct);
        FracturingFog.Rendering.LowResPreview.UpscaleNearest(_preview.ColorBuffer, dims.Width, dims.Height, ColorBuffer, Width, Height);
        return true;
    }
}
