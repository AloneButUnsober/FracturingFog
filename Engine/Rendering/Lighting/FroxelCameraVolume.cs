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
using FracturingFog.Models;

namespace FracturingFog.Rendering.Lighting;

/// <summary>Frames + drives a <see cref="FroxelVolumePass"/> from a relief camera
/// and the fog knobs, then composites it over a beauty buffer by per-pixel world
/// depth (roadmap S6, #408).</summary>
public static class FroxelCameraVolume
{
    /// <summary>Default froxel resolution — near-dense in Z (Frostbite-style),
    /// coarse in X/Y (the volume is low-frequency). Kept modest so the single
    /// populate/integrate stays cheap relative to the primary trace. Equal to
    /// <see cref="FroxelQuality.Balanced"/>.</summary>
    public const int DimX = 24, DimY = 24, DimZ = 48;

    /// <summary>Froxel grid dims (X, Y, Z) for a quality level (roadmap S6, #408).
    /// <see cref="FroxelQuality.Balanced"/> == the historical const dims so a
    /// Balanced scene is byte-identical. Both the CPU post-pass and the GPU kernel
    /// read the dims off the built grid, so this drives both in lock-step.</summary>
    public static (int X, int Y, int Z) Dims(FroxelQuality q) => q switch
    {
        FroxelQuality.Low  => (16, 16, 32),
        FroxelQuality.High => (32, 32, 96),
        _                  => (DimX, DimY, DimZ),   // Balanced (default)
    };

    /// <summary>Build a froxel grid spanning the relief scene along the view ray.
    /// Near/far bracket the height-field slab the camera points at: near clamps
    /// just in front of the camera, far reaches past the slab's far corner.
    /// Balanced resolution (byte-identical legacy dims).</summary>
    public static FroxelGrid BuildGrid(in HeightfieldRaymarch2D.ReliefCamera cam)
        => BuildGrid(in cam, FroxelQuality.Balanced);

    /// <summary>As <see cref="BuildGrid(in HeightfieldRaymarch2D.ReliefCamera)"/>, at a
    /// chosen <paramref name="quality"/> resolution (roadmap S6, #408). The near/far
    /// bracket is unchanged — only the grid dims scale.</summary>
    public static FroxelGrid BuildGrid(in HeightfieldRaymarch2D.ReliefCamera cam, FroxelQuality quality)
        => BuildGrid(FroxelCamera.FromRelief(in cam), quality);   // #1067 — slab framing in FromRelief

    /// <summary>#1067 — the grid for any froxel camera: its near/far at a
    /// <paramref name="quality"/> resolution.</summary>
    public static FroxelGrid BuildGrid(in FroxelCamera cam, FroxelQuality quality)
    {
        var (dx, dy, dz) = Dims(quality);
        return new FroxelGrid(dx, dy, dz, cam.Near, cam.Far);
    }

    /// <summary>Build a fog medium from the lighting knobs + all three lights
    /// (roadmap S6 multi-light, #408). Each light contributes its own direction,
    /// colour, HG phase and — for point/spot — per-froxel positional falloff, the
    /// same three-light model as the per-surface march (#388). View direction is the
    /// camera forward. Extinction is 1 per unit density so extinction == FogDensity,
    /// matching the per-surface march's density·extinction.</summary>
    public static FroxelMedium BuildMedium(in HeightfieldRaymarch2D.ReliefCamera cam, in LightingFxData fx)
        => BuildMedium(FroxelCamera.FromRelief(in cam), in fx);

    /// <summary>#1067 — the fog medium for any froxel camera (view direction =
    /// its forward, populate box = its extent).</summary>
    public static FroxelMedium BuildMedium(in FroxelCamera cam, in LightingFxData fx)
    {
        double extent = cam.Extent;
        return new FroxelMedium
        {
            BaseDensity = fx.FogDensity,
            Extinction = 1.0,
            ViewDx = cam.Fx, ViewDy = cam.Fy, ViewDz = cam.Fz,
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
        => Apply(beauty, worldDepth, w, h, in cam, in fx, null, false, 0.0, FroxelQuality.Balanced);

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
        => Apply(beauty, worldDepth, w, h, in cam, in fx, history, temporal, feedback, FroxelQuality.Balanced);

    /// <summary>As the temporal <see cref="Apply(uint[],float[],int,int,in HeightfieldRaymarch2D.ReliefCamera,in LightingFxData,FroxelHistory,bool,double)"/>
    /// overload, at a chosen <paramref name="quality"/> froxel resolution (roadmap S6,
    /// #408). The near/far bracket is unchanged — only the grid dims scale. Balanced
    /// → byte-identical.</summary>
    public static uint[] Apply(uint[] beauty, float[] worldDepth, int w, int h,
        in HeightfieldRaymarch2D.ReliefCamera cam, in LightingFxData fx,
        FroxelHistory? history, bool temporal, double feedback, FroxelQuality quality,
        float[]? hdrBeauty = null, bool reproject = false)
        => Apply(beauty, worldDepth, w, h, FroxelCamera.FromRelief(in cam), in fx,
            history, temporal, feedback, quality, hdrBeauty, reproject);

    /// <summary>#1067 — the full froxel pass for any <see cref="FroxelCamera"/>:
    /// build the grid + medium, populate (optionally temporal / reprojected),
    /// integrate, and composite over <paramref name="beauty"/> (and
    /// <paramref name="hdrBeauty"/> when given) by per-pixel world depth.</summary>
    public static uint[] Apply(uint[] beauty, float[] worldDepth, int w, int h,
        in FroxelCamera cam, in LightingFxData fx,
        FroxelHistory? history, bool temporal, double feedback, FroxelQuality quality,
        float[]? hdrBeauty = null, bool reproject = false)
    {
        var grid = BuildGrid(in cam, quality);
        var pass = new FroxelVolumePass(grid);
        if (temporal && history != null)
        {
            // S6 (#408) sub-cell reprojection: pass the current camera basis + lateral
            // extent so the history resamples in world space under continuous camera
            // motion. Reproject off → the same-cell blend (byte-identical).
            var cur = cam.Basis;
            double extent = cam.Extent;
            if (extent <= 0.0) extent = 1.0;
            pass.Populate(BuildMedium(in cam, in fx), history, feedback,
                FroxelHistory.GridKey(grid), reproject, in cur, extent);
        }
        else
            pass.Populate(BuildMedium(in cam, in fx));
        // S12 (#655/#652) — when a float HDR beauty plane is captured, composite the
        // SAME populated volume onto it too (in place) so an FX tone map / bloom / view
        // transform tonemaps fog-ful highlights instead of the fog-free beauty. Built
        // once, composited to both byte + HDR. Null → the byte-only path (unchanged).
        if (hdrBeauty != null)
            pass.CompositeWorldDepthHdr(hdrBeauty, worldDepth, w, h);
        return pass.CompositeWorldDepth(beauty, worldDepth, w, h);
    }
}
