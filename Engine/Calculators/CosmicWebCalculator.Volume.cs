// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// CosmicWebCalculator.Volume.cs (#2024, Cosmic Web epic #2020)
//
// The volume render: the points deposited on a CIC density grid (CosmicWeb/DensityGrid), mapped to
// log density, and raymarched with emission and absorption (CosmicWeb/VolumeRaymarch) on the GPU
// (D3D11 / Vulkan, Calculators/Gpu/CosmicWebVolumeBackends) or the CPU reference. The grid is cached
// per point set and grid settings, and the GPU backends keep it on the device, so camera moves only
// re-march.

using System;
using System.Diagnostics;
using System.Threading;

using FracturingFog.Calculators.Gpu;
using FracturingFog.CosmicWeb;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;

namespace FracturingFog;

public sealed partial class CosmicWebCalculator
{
    private static readonly object s_vLock = new();
    private static (CosmicWebPoints Points, int Grid, double Smoothing)? s_vKey;
    private static CosmicWebDensityGrid? s_vGrid;

    /// <summary>The last volume frame's backend ("CPU", "D3D11", "Vulkan"), null for the splat.</summary>
    public string? LastVolumeBackend { get; private set; }
    /// <summary>The density grid of the last volume frame.</summary>
    public CosmicWebDensityGrid? LastVolumeGrid { get; private set; }

    /// <summary>The mapped density grid of <paramref name="pts"/> for <paramref name="p"/>'s settings (cached).</summary>
    public static CosmicWebDensityGrid VolumeGridFor(CosmicWebPoints pts, FractalParameters p, CancellationToken ct = default)
    {
        int g = DensityGrid.SanitiseGrid(p.CosmicWebVolumeGrid);
        double sm = double.IsFinite(p.CosmicWebVolumeSmoothing) ? Math.Clamp(p.CosmicWebVolumeSmoothing, 0.0, 8.0) : 0.0;
        lock (s_vLock)
            if (s_vKey is { } k && ReferenceEquals(k.Points, pts) && k.Grid == g && k.Smoothing == sm && s_vGrid != null) return s_vGrid;
        var grid = DensityGrid.Build(pts, g, sm, ct);
        lock (s_vLock) { s_vKey = (pts, g, sm); s_vGrid = grid; }
        return grid;
    }

    /// <summary>Kernel parameters for this frame (also used by the tests and the probe).</summary>
    public static CosmicWebVolumeParams VolumeParamsFor(FractalParameters p, MathPlane3DCamera cam, int grid, uint background)
        => VolumeRaymarch.ParamsFor(cam, grid, p.CosmicWebVolumeSteps, p.CosmicWebVolumeOpacity, p.CosmicWebGamma,
                                    p.CosmicWebColour == CosmicWebColour.Depth, background, p.CosmicWebVolumeBrightness);

    private bool RenderVolume(FractalParameters p, CosmicWebPoints pts, MathPlane3DCamera cam, bool wantDepth, CancellationToken ct, out string note)
    {
        note = "";
        LastVolumeBackend = null;
        CosmicWebDensityGrid grid;
        try { grid = VolumeGridFor(pts, p, ct); }
        catch (OperationCanceledException) { return false; }
        LastVolumeGrid = grid;
        var lut = VolumeRaymarch.BakeLut(ColorMap);
        var vp = VolumeParamsFor(p, cam, grid.Grid, ColorMap.InSetColor);
        var sw = Stopwatch.StartNew();
        CosmicWebVolumeFrame? frame = null;
        string backend = "CPU";
        var kernel = CosmicWebVolumeBackends.KernelFor(CosmicWebVolumeBackends.Selection);
        if (kernel != null)
        {
            frame = kernel.Render(vp, grid, lut, ct);
            if (frame != null) backend = kernel.Backend;
        }
        if (ct.IsCancellationRequested) return false;
        if (frame == null)
        {
            try { frame = VolumeRaymarch.Render(vp, grid.Value, lut, ct); }
            catch (OperationCanceledException) { return false; }
        }
        Array.Copy(frame.Colour, ColorBuffer, Math.Min(frame.Colour.Length, ColorBuffer.Length));
        if (wantDepth)
        {
            var d = new float[frame.Depth.Length];
            for (int i = 0; i < d.Length; i++) d[i] = float.IsPositiveInfinity(frame.Depth[i]) ? ScreenSpacePost.DepthMiss : frame.Depth[i];
            DepthBuffer = d;
        }
        LastVolumeBackend = backend;
        note = $"; volume {grid.Grid}^3 CIC, peak {grid.MaxOverMean:0.#} x mean, {vp.Steps} steps, {backend} {sw.Elapsed.TotalMilliseconds:0} ms";
        return true;
    }
}
