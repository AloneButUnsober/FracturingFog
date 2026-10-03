// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Rendering/Lighting/ScreenSpacePost.Froxel3D.cs
//
// #1068 (froxel F2, parent #1061) — froxel volumetrics for the 3D DE raymarchers.
// The relief path already composites a froxel fog volume over a fog-free beauty by
// per-pixel world depth; this brings the same pass (now camera-agnostic via
// FroxelCamera, #1067) to the 3D calculators:
//
//   1. FogFreeForFroxel3D: when on, the calculator shades with the in-surface fog
//      / volumetric in-scatter / background fog switched off (the froxel volume
//      replaces them, as relief does), and the shading fx keeps Froxel3D = true so
//      WantsGBuffer captures depth and ForcesCpuTrace skips the GPU fast path
//      (the GPU kernels have no depth pass yet — #1070).
//   2. ApplyFroxel3D: after SSAO, before the HDR DoF / tone map, frame a froxel
//      camera on the fractal's orbit camera (near 0.05, far = past the escape
//      distance), populate + integrate, and composite over the byte buffer and the
//      HDR plane by the depth G-buffer (+Infinity misses take the full column, so
//      the space around the object is lit haze).
//
// Default off → no allocation, no change (byte-identical). No occlusion yet: the
// volume is haze with light colour + phase; god-ray shafts are F3 (#1069).

using System;

using FracturingFog.Models;
using FracturingFog.Render;

namespace FracturingFog.Rendering.Lighting;

public static partial class ScreenSpacePost
{
    /// <summary>Froxel near plane for the 3D cameras (world units).</summary>
    public const double Froxel3DNear = 0.05;

    /// <summary>The raymarchers' escape distance; the froxel far plane is at least
    /// this (misses composite as "beyond far" → the full column).</summary>
    public const double Froxel3DMinFar = 12.0;

    /// <summary>True when the 3D froxel pass will run for <paramref name="fx"/> —
    /// the option is on and there is fog to fill the volume with.</summary>
    public static bool Froxel3DActive(in LightingFxData fx) => fx.Froxel3D && fx.FogDensity > 0.0;

    /// <summary>The lighting a 3D calculator should shade with. With the froxel pass
    /// active, the surface fog / volumetric in-scatter / background fog are off (the
    /// volume supplies them) and <see cref="LightingFxData.Froxel3D"/> stays true as
    /// the arm flag; otherwise <paramref name="fx"/> unchanged except Froxel3D is
    /// cleared (so a fog-less frame arms nothing).</summary>
    public static LightingFxData FogFreeForFroxel3D(in LightingFxData fx)
    {
        var s = fx;
        if (!Froxel3DActive(in fx))
        {
            s.Froxel3D = false;
            return s;
        }
        s.FogDensity = 0.0;
        s.VolumeSteps = 0;
        s.FogBackground = false;
        return s;
    }

    /// <summary>True when the frame must take the CPU trace: depth is wanted after
    /// the frame, or the 3D froxel pass needs the depth G-buffer.</summary>
    public static bool ForcesCpuTrace(in LightingFxData fx)
        => WantsDepthOutput(in fx) || fx.Froxel3D;

    /// <summary>The froxel camera for a 3D fractal's orbit camera (the same
    /// <c>d·(sinφ cosθ, cosφ, sinφ sinθ)</c> placement the calculators use, looking
    /// at the origin).</summary>
    public static FroxelCamera Froxel3DCamera(FractalParameters p, FractalType type, double fovScale)
    {
        var s = CameraParamBinding.Supports(type) ? CameraParamBinding.Read(p, type) : new CameraState(3.0, 0.0, 1.2);
        double d = s.Distance > 0 ? s.Distance : 3.0;
        var pos = (d * Math.Sin(s.Phi) * Math.Cos(s.Theta), d * Math.Cos(s.Phi), d * Math.Sin(s.Phi) * Math.Sin(s.Theta));
        double far = Math.Max(Froxel3DMinFar, d * 2.0);
        return FroxelCamera.LookAt(pos, (0, 0, 0), 2.0 * Math.Atan(fovScale), Froxel3DNear, far, extent: Math.Max(1.0, d));
    }

    /// <summary>Composite the 3D froxel volume over <paramref name="color"/> (and
    /// <paramref name="hdr"/> when present) by the depth G-buffer. No-op unless
    /// <paramref name="froxelFx"/> (the UNSTRIPPED lighting) has the pass active and a
    /// depth buffer was captured. Returns true when it ran.</summary>
    public static bool ApplyFroxel3D(uint[] color, float[]? hdr, float[]? depth, int w, int h,
        FractalParameters p, FractalType type, double fovScale, in LightingFxData froxelFx)
        => ApplyFroxel3DCore(color, hdr, depth, w, h, p, type, fovScale, in froxelFx, null);

    /// <summary>#1069 — as <see cref="ApplyFroxel3D(uint[],float[],float[],int,int,FractalParameters,FractalType,double,in LightingFxData)"/>,
    /// with the fractal's distance estimator so each froxel's in-scatter is shadowed
    /// by a soft DE march toward every light (god-ray shafts around the object) when
    /// <see cref="LightingFxData.Froxel3DShadowSteps"/> &gt; 0.</summary>
    public static bool ApplyFroxel3D<TDe>(uint[] color, float[]? hdr, float[]? depth, int w, int h,
        FractalParameters p, FractalType type, double fovScale, in LightingFxData froxelFx, in TDe de)
        where TDe : struct, IDistanceEstimator
    {
        FroxelVisibility? vis = null;
        int steps = froxelFx.Froxel3DShadowSteps;
        if (steps > 0)
        {
            var estimator = de;   // struct copy — closures can't capture an `in` parameter
            vis = (x, y, z, lx, ly, lz, maxD) => FroxelShadow(in estimator, x, y, z, lx, ly, lz, maxD, steps);
        }
        return ApplyFroxel3DCore(color, hdr, depth, w, h, p, type, fovScale, in froxelFx, vis);
    }

    private static bool ApplyFroxel3DCore(uint[] color, float[]? hdr, float[]? depth, int w, int h,
        FractalParameters p, FractalType type, double fovScale, in LightingFxData froxelFx, FroxelVisibility? vis)
    {
        if (!Froxel3DActive(in froxelFx) || depth == null || p == null) return false;
        var cam = Froxel3DCamera(p, type, fovScale);
        // #1069 — true frustum positions for the 3D volume (columns map onto the
        // image exactly as the calculators trace their pixel rays).
        var std = FroxelCameraVolume.BuildMedium(in cam, in froxelFx);
        var medium = new FroxelMedium
        {
            BaseDensity = std.BaseDensity, Extinction = std.Extinction,
            ViewDx = std.ViewDx, ViewDy = std.ViewDy, ViewDz = std.ViewDz,
            Anisotropy = std.Anisotropy,
            NoiseAmount = std.NoiseAmount, NoiseScale = std.NoiseScale, NoiseOctaves = std.NoiseOctaves,
            WorldExtent = std.WorldExtent, Lights = std.Lights,
            WorldFrustum = true, Frustum = cam, Aspect = (double)w / h, Visibility = vis,
        };
        var outBuf = FroxelCameraVolume.Apply(color, depth, w, h, in cam, in froxelFx,
            null, false, 0.0, froxelFx.Froxel3DQuality, hdr, medium: medium);
        Array.Copy(outBuf, color, Math.Min(outBuf.Length, color.Length));
        return true;
    }

    /// <summary>#1069 — soft visibility of a light from (x, y, z) along the unit
    /// direction (lx, ly, lz), marched through the fractal's distance estimator for at
    /// most <paramref name="steps"/> steps or <paramref name="maxDistance"/>. 0 when the
    /// point is inside the solid or the ray hits it; otherwise the usual penumbra
    /// factor min(k·d/t) (k = 8), so shafts have soft edges.</summary>
    public static double FroxelShadow<TDe>(in TDe de, double x, double y, double z,
        double lx, double ly, double lz, double maxDistance, int steps)
        where TDe : struct, IDistanceEstimator
    {
        const double HitEps = 1e-4, K = 8.0;
        double d0 = de.Evaluate(x, y, z);
        if (!(d0 > HitEps)) return 0.0;                     // froxel inside the fractal
        double maxStep = Math.Max(0.25, maxDistance / Math.Max(1, steps));
        double t = Math.Min(0.02, d0), res = 1.0;
        for (int i = 0; i < steps && t < maxDistance; i++)
        {
            double d = de.Evaluate(x + lx * t, y + ly * t, z + lz * t);
            if (d < HitEps) return 0.0;
            res = Math.Min(res, K * d / t);
            t += Math.Clamp(d, 0.01, maxStep);
        }
        return Math.Clamp(res, 0.0, 1.0);
    }
}
