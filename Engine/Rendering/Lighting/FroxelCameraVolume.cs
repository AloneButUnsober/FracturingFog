// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Rendering/Lighting/FroxelCameraVolume.cs
//
// Roadmap slice S6 (3D-Rendering-Roadmap.md, parent #389 / issue #408) — the
// missing link between the froxel PRIMITIVES (FroxelGrid + FroxelVolumePass) and
// the live relief render. It:
//   (1) frames a camera-aligned FroxelGrid over the relief scene (near/far derived
//       from the oblique camera + the height-field slab it points at),
//   (2) builds a FroxelMedium from the LightingFxData fog knobs + the key light
//       (the same model as the per-surface march, so a froxel scene reads like the
//       existing fog), and
//   (3) composites the populated + integrated volume over a beauty buffer by the
//       render's own per-pixel world depth — one depth-indexed read per pixel,
//       replacing the per-pixel background in-scatter march.
//
// Pure + deterministic (no RNG, no device state) → identical live and under
// --batch, and a twin for a future GPU froxel compute pass. Opt-in from
// HeightfieldRaymarch2D (FractalParameters.Relief2DFroxelVolumetrics); default off
// leaves every render byte-identical.

using System;

namespace FracturingFog.Rendering.Lighting;

/// <summary>Frames + drives a <see cref="FroxelVolumePass"/> from a relief camera
/// and the fog knobs, then composites it over a beauty buffer by per-pixel world
/// depth (roadmap S6, #408).</summary>
public static class FroxelCameraVolume
{
    /// <summary>Default froxel resolution — near-dense in Z (Frostbite-style),
    /// coarse in X/Y (the volume is low-frequency). Kept modest so the single
    /// populate/integrate stays cheap relative to the primary trace.</summary>
    public const int DimX = 24, DimY = 24, DimZ = 48;

    /// <summary>Build a froxel grid spanning the relief scene along the view ray.
    /// Near/far bracket the height-field slab the camera points at: near clamps
    /// just in front of the camera, far reaches past the slab's far corner.</summary>
    public static FroxelGrid BuildGrid(in HeightfieldRaymarch2D.ReliefCamera cam)
    {
        double camDist = Math.Sqrt(cam.CamX * cam.CamX + cam.CamY * cam.CamY + cam.CamZ * cam.CamZ);
        // Full slab diagonal (the AABB is [-Bx,Bx]×[0,By]×[-Bz,Bz]).
        double diag = Math.Sqrt((2 * cam.Bx) * (2 * cam.Bx) + cam.By * cam.By + (2 * cam.Bz) * (2 * cam.Bz));
        double near = Math.Max(1e-3, camDist - diag);
        double far = camDist + diag;
        if (far <= near) far = near * 100.0;
        return new FroxelGrid(DimX, DimY, DimZ, near, far);
    }

    /// <summary>Build a fog medium from the lighting knobs + all three lights
    /// (roadmap S6 multi-light, #408). Each light contributes its own direction,
    /// colour, HG phase and — for point/spot — per-froxel positional falloff, the
    /// same three-light model as the per-surface march (#388). View direction is the
    /// camera forward. Extinction is 1 per unit density so extinction == FogDensity,
    /// matching the per-surface march's density·extinction.</summary>
    public static FroxelMedium BuildMedium(in HeightfieldRaymarch2D.ReliefCamera cam, in LightingFxData fx)
    {
        double extent = Math.Max(cam.Bx, Math.Max(cam.By, cam.Bz));
        return new FroxelMedium
        {
            BaseDensity = fx.FogDensity,
            Extinction = 1.0,
            ViewDx = cam.FX, ViewDy = cam.FY, ViewDz = cam.FZ,
            Anisotropy = fx.VolumeAnisotropy,
            NoiseAmount = fx.VolumeNoiseAmount,
            NoiseScale = fx.VolumeNoiseScale,
            NoiseOctaves = fx.VolumeNoiseOctaves,
            WorldExtent = extent > 0 ? extent : 1.0,
            Lights = new[]
            {
                ToFroxelLight(in fx.Light1, (fx.VolumeLightMask & 0x1) != 0),
                ToFroxelLight(in fx.Light2, (fx.VolumeLightMask & 0x2) != 0),
                ToFroxelLight(in fx.Light3, (fx.VolumeLightMask & 0x4) != 0),
            },
        };
    }

    /// <summary>Map a scene light to a <see cref="FroxelLight"/>: resolve the
    /// direction from Theta/Phi (directional aim / spot cone axis) and convert the
    /// spot half-angles to cosines. Point/spot carry their world position + range.
    /// <paramref name="fogsLight"/> is the light's VolumeLightMask bit — false zeroes
    /// its intensity so it lights surfaces but not the fog (roadmap S6, #408).</summary>
    private static FroxelLight ToFroxelLight(in DirectionalLight d, bool fogsLight)
    {
        var (lx, ly, lz) = ShadingPipeline.LightDir(d.Theta, d.Phi);
        return new FroxelLight
        {
            Type = (int)d.Type,
            Color = d.Color,
            Intensity = fogsLight ? d.Intensity : 0.0,
            Lx = lx, Ly = ly, Lz = lz,
            PosX = d.PosX, PosY = d.PosY, PosZ = d.PosZ,
            Range = d.Range,
            InnerCos = Math.Cos(d.SpotInnerDeg * Math.PI / 180.0),
            OuterCos = Math.Cos(d.SpotOuterDeg * Math.PI / 180.0),
        };
    }

    /// <summary>One-shot: build the grid + medium, populate/integrate the volume,
    /// and composite it over <paramref name="beauty"/> by per-pixel world depth
    /// (<paramref name="worldDepth"/> = ray distance from the camera, the relief
    /// render's own depth AOV). Returns a new buffer; alpha preserved.</summary>
    public static uint[] Apply(uint[] beauty, float[] worldDepth, int w, int h,
        in HeightfieldRaymarch2D.ReliefCamera cam, in LightingFxData fx)
        => Apply(beauty, worldDepth, w, h, in cam, in fx, null, false, 0.0);

    /// <summary>As <see cref="Apply(uint[],float[],int,int,in HeightfieldRaymarch2D.ReliefCamera,in LightingFxData)"/>,
    /// with optional temporal reprojection (roadmap S6, #408). When
    /// <paramref name="temporal"/> is on and a <paramref name="history"/> is supplied,
    /// the per-cell scatter + extinction is exponentially blended with the previous
    /// frame's (weight <paramref name="feedback"/>) before integration — animated fog
    /// reads as a stable volume. History is keyed by the grid's dims + near/far, so a
    /// camera move that changes the slab re-seeds cleanly. Temporal off / null history
    /// → byte-identical to the single-frame <see cref="Apply"/>.</summary>
    public static uint[] Apply(uint[] beauty, float[] worldDepth, int w, int h,
        in HeightfieldRaymarch2D.ReliefCamera cam, in LightingFxData fx,
        FroxelHistory? history, bool temporal, double feedback)
    {
        var grid = BuildGrid(in cam);
        var pass = new FroxelVolumePass(grid);
        if (temporal && history != null)
            pass.Populate(BuildMedium(in cam, in fx), history, feedback, FroxelHistory.GridKey(grid));
        else
            pass.Populate(BuildMedium(in cam, in fx));
        return pass.CompositeWorldDepth(beauty, worldDepth, w, h);
    }
}
