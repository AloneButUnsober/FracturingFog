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
//      WantsGBuffer captures depth on the CPU trace. #1070: the GPU fast path
//      still runs (GpuTraceAllowed) — its kernels write per-pixel ray distance and
//      the volume is composited over the GPU frame.
//   2. ApplyFroxel3D: after SSAO, before the HDR DoF / tone map, frame a froxel
//      camera on the calculator's actual ray camera (#1079: zoom/floor, eye offset,
//      lens, pan — Froxel3DViewOf; near 0.05, far = past the escape
//      distance), populate + integrate, and composite over the byte buffer and the
//      HDR plane by the depth G-buffer (+Infinity misses take the full column, so
//      the space around the object is lit haze).
//
// Default off → no allocation, no change (byte-identical). No occlusion yet: the
// volume is haze with light colour + phase; god-ray shafts are F3 (#1069).

using System;

using FracturingFog.Models;

namespace FracturingFog.Rendering.Lighting;

public static partial class ScreenSpacePost
{
    /// <summary>#1079 — the camera a 3D calculator traced its primary rays from:
    /// <see cref="Camera"/> (eye, basis, FOV tangent, near / far) plus the
    /// screen-space pan added to every ray's NDC offsets.</summary>
    public readonly record struct Froxel3DView(FroxelCamera Camera, double PanU, double PanV);

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

    /// <summary>#1070 — true when a Froxel3D frame may still take the GPU trace: the
    /// ILGPU kernels write the per-pixel ray distance the froxel composite needs, so
    /// the GPU traces and the CPU populates + composites the volume. Not with thin-lens
    /// DoF (its averaged taps have no single depth).</summary>
    public static bool GpuFroxel3DHybrid(in LightingFxData fx)
        => fx.Froxel3D && !ThinLensDof.IsActive(in fx);

    /// <summary>#1070 / #323 — true when the GPU trace must also emit its per-pixel
    /// ray distance: for the froxel composite, or as the frame's published depth
    /// (stereo / autostereogram, <see cref="WantsDepthOutput"/>). The kernels' depth
    /// is the CPU G-buffer contract (ray distance, +Inf = miss).</summary>
    public static bool GpuWantsDepth(in LightingFxData fx)
        => ForcesCpuTrace(in fx) && !ThinLensDof.IsActive(in fx);

    /// <summary>The 3D calculators' GPU-trace gate. The kernels supply depth
    /// (<see cref="GpuWantsDepth"/>), so the only CPU-only need left is depth with
    /// thin-lens DoF.</summary>
    public static bool GpuTraceAllowed(in LightingFxData fx)
        => !ForcesCpuTrace(in fx) || GpuWantsDepth(in fx);

    /// <summary>#1079 — the froxel view for a 3D calculator's ACTUAL primary-ray
    /// camera: the eye after the zoom / distance-floor clamp and stereo eye offset,
    /// its basis, the FOV tangent (incl. the zoom lens) and the screen-space pan, so
    /// every froxel column is the calculator's own pixel ray
    /// <c>F + R·(ndcX·tan·aspect + panU) + U·(ndcY·tan + panV)</c>.
    /// <paramref name="camDist"/> is the eye's distance to the orbit target; the far
    /// plane sits past the escape distance and at least twice that.</summary>
    public static Froxel3DView Froxel3DViewOf(double camX, double camY, double camZ,
        double fwdX, double fwdY, double fwdZ, double rightX, double rightY, double rightZ,
        double upX, double upY, double upZ, double fovScale, double panU, double panV, double camDist)
    {
        double d = camDist > 0 ? camDist : 3.0;
        var cam = new FroxelCamera(camX, camY, camZ, fwdX, fwdY, fwdZ, rightX, rightY, rightZ, upX, upY, upZ,
            fovScale, false, 0.0, Froxel3DNear, Math.Max(Froxel3DMinFar, d * 2.0), Math.Max(1.0, d));
        return new Froxel3DView(cam, panU, panV);
    }

    /// <summary>Composite the 3D froxel volume over <paramref name="color"/> (and
    /// <paramref name="hdr"/> when present) by the depth G-buffer. No-op unless
    /// <paramref name="froxelFx"/> (the UNSTRIPPED lighting) has the pass active and a
    /// depth buffer was captured. Returns true when it ran.</summary>
    public static bool ApplyFroxel3D(uint[] color, float[]? hdr, float[]? depth, int w, int h,
        in Froxel3DView view, in LightingFxData froxelFx)
        => ApplyFroxel3DCore(color, hdr, depth, w, h, in view, in froxelFx, null);

    /// <summary>#1069 — as <see cref="ApplyFroxel3D(uint[],float[],float[],int,int,in Froxel3DView,in LightingFxData)"/>,
    /// with the fractal's distance estimator so each froxel's in-scatter is shadowed
    /// by a soft DE march toward every light (god-ray shafts around the object) when
    /// <see cref="LightingFxData.Froxel3DShadowSteps"/> &gt; 0.</summary>
    public static bool ApplyFroxel3D<TDe>(uint[] color, float[]? hdr, float[]? depth, int w, int h,
        in Froxel3DView view, in LightingFxData froxelFx, in TDe de)
        where TDe : struct, IDistanceEstimator
    {
        FroxelVisibility? vis = null;
        int steps = froxelFx.Froxel3DShadowSteps;
        if (steps > 0)
        {
            var estimator = de;   // struct copy — closures can't capture an `in` parameter
            vis = (x, y, z, lx, ly, lz, maxD) => FroxelShadow(in estimator, x, y, z, lx, ly, lz, maxD, steps);
        }
        return ApplyFroxel3DCore(color, hdr, depth, w, h, in view, in froxelFx, vis);
    }

    private static bool ApplyFroxel3DCore(uint[] color, float[]? hdr, float[]? depth, int w, int h,
        in Froxel3DView view, in LightingFxData froxelFx, FroxelVisibility? vis)
    {
        if (!Froxel3DActive(in froxelFx) || depth == null) return false;
        var cam = view.Camera;
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
            PanU = view.PanU, PanV = view.PanV,   // #1079
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
