// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;

namespace FracturingFog.Rendering.Lighting;

/// <summary>#1044 — tiling for the relief GPU dispatch. One Dispatch for the whole
/// frame ran for seconds with volumetric fog on a slow GPU (GeForce GT 710: ~17–32 s
/// at 1080p) and tripped the ~2 s OS watchdog (TDR → DXGI_ERROR_DEVICE_REMOVED).
/// The backend dispatches tiles (each its own Dispatch + Flush) sized so a tile's
/// predicted time stays near <see cref="BandTargetMs"/>: pixels = target /
/// (ns-per-cost-unit × cost-per-pixel). The cost model comes from the uniforms, so
/// switching fog on shrinks the tiles in the SAME frame; the GPU's speed (ns per
/// unit) is learned by the backend from each frame's measured time.</summary>
public static class ReliefGpuTiling
{
    /// <summary>Predicted time per dispatch the tiles aim for (the OS watchdog is ~2 s).</summary>
    public const double BandTargetMs = 150.0;

    /// <summary>Starting speed guess, pessimistic (a GeForce GT 710 measures ~3).</summary>
    public const double InitialNsPerUnit = 6.0;

    /// <summary>#1044 — rough per-pixel work of one relief frame in DE-evaluation
    /// units: the primary march, the shading marches per light, and the fog walk's
    /// per-step shadow marches. Only the ratios matter (the speed is learned).</summary>
    public static double CostPerPixel(in ReliefUniforms u)
    {
        int shadowSteps = u.ShadowSteps > 0 ? u.ShadowSteps : 0;
        bool l0 = u.I0 > 0, l1 = u.I1 > 0, l2 = u.I2 > 0;
        int shadowed = (l0 && (u.ShadowLightMask & 1) != 0 ? 1 : 0)
                     + (l1 && (u.ShadowLightMask & 2) != 0 ? 1 : 0)
                     + (l2 && (u.ShadowLightMask & 4) != 0 ? 1 : 0);
        double c = 0.5 * Math.Max(1, u.Cam.MaxSteps) + shadowed * shadowSteps + Math.Max(0, u.AoSamples);
        if (u.ReflectionStrength > 0) c += (u.ReflectionSteps > 0 ? u.ReflectionSteps : 24) * Math.Max(1, u.MaxBounces);
        if (u.Transmission > 0) c += 64.0 * (1 + Math.Max(0, u.RefractInternalBounces));
        if (u.FogDensity > 0 && u.VolumeSteps > 0)
        {
            int volLights = (l0 && (u.VolumeLightMask & 1) != 0 ? 1 : 0)
                          + (l1 && (u.VolumeLightMask & 2) != 0 ? 1 : 0)
                          + (l2 && (u.VolumeLightMask & 4) != 0 ? 1 : 0);
            double perStep = 1 + volLights * (1 + (u.VolumeSelfShadow > 0 ? u.VolumeSelfShadowSteps : 0))
                           + shadowed * shadowSteps;
            c += u.VolumeSteps * perStep;
        }
        if (u.DofAperture > 0) c *= Math.Max(1, u.DofSamples);
        return c;
    }

    /// <summary>#1044 — the tile size (rows × columns, multiples of the 8×8 thread
    /// group) whose predicted time stays near <paramref name="targetMs"/>: full-width
    /// row bands, narrowed to column tiles when even an 8-row band is too slow.</summary>
    public static (int Rows, int Cols) TileSize(int w, int h, double costPerPixel, double nsPerUnit,
                                                  double targetMs = BandTargetMs)
    {
        double msPerPixel = costPerPixel * nsPerUnit * 1e-6;
        if (!(msPerPixel > 0)) return (RoundUp8(h), RoundUp8(w));
        double pixels = targetMs / msPerPixel;
        int rows = (int)(pixels / w);
        if (rows >= 8) return (Math.Min(RoundUp8(h), rows / 8 * 8), RoundUp8(w));
        int cols = Math.Max(8, (int)(pixels / 8) / 8 * 8);
        return (8, Math.Min(RoundUp8(w), cols));
    }

    private static int RoundUp8(int v) => (Math.Max(1, v) + 7) / 8 * 8;
}
