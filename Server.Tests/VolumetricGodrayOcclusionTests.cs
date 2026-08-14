// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Does FF's volumetric lighting produce real god-ray SHAFTS (occlusion-aware
// in-scatter), or only a uniform brighten/darken? The whole effect hinges on the
// in-scatter walk casting the key light's soft shadow at each volume step — and
// the engine only does that when ShadowSteps > 0 (ShadingPipeline.AddVolumeScatter
// gates the per-step SoftShadow on it; the GPU relief kernel gates identically).
// A preset that sets VolumeSteps but forgets ShadowSteps renders the exact
// "only brightens or darkens" symptom the user reported.
//
// This is a direct UNIT test of that mechanism — no render, no framing luck, no
// surface-shading confound. It drives ShadingPipeline.VolumetricInScatterSegment
// on a fixed volume segment lit by a single key light, with a synthetic occluder
// sphere placed in the light's path, and asserts:
//
//   ShadowSteps == 0 : occluder makes NO difference (in-scatter ignores it) —
//                      flat glow, the reported symptom.
//   ShadowSteps  > 0 : occluder measurably DIMS the in-scatter (the shaft) —
//                      the medium is shadowed by geometry -> god rays.

using System;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class VolumetricGodrayOcclusionTests
{
    // Signed-distance sphere: an occluder the SoftShadow march can hit.
    private static DistanceEstimator Sphere(double cx, double cy, double cz, double r)
        => (x, y, z) =>
        {
            double dx = x - cx, dy = y - cy, dz = z - cz;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz) - r;
        };

    private static LightingFxData Fog(int shadowSteps)
    {
        var fx = LightingFxData.CreateDefault();
        fx.FogDensity = 0.5;
        fx.VolumeSteps = 40;
        fx.VolumeStepsFalloff = 0.0;     // fixed step count
        fx.VolumeNoiseAmount = 0.0;      // isolate: no FBM cloud modulation
        fx.VolumeSelfShadow = 0.0;       // isolate: no cloud self-shadow, only terrain
        fx.VolumeAnisotropy = 0.0;       // isotropic — measure raw scatter, not the halo
        fx.FogColor = 0xFFFFFFFFu;
        fx.VolumePaletteStrength = 0.0;
        fx.Light1.Intensity = 1.5;       // key light on
        fx.Light2.Intensity = 0.0;
        fx.Light3.Intensity = 0.0;
        fx.ShadowSteps = shadowSteps;
        fx.ShadowSoftK = 32.0;           // fairly hard shadow -> crisp shaft edge
        fx.ShadowLightMask = 0x1;        // key light casts shadow
        return fx;
    }

    // Total in-scatter (br+bg+bb, starting from black) accumulated along a fixed
    // +Z volume segment lit by an overhead (+Y) key light, with the given DE as
    // the shadow-caster.
    private static double InScatter<TDe>(in LightingFxData fx, in TDe de)
        where TDe : struct, IDistanceEstimator
    {
        // Volume samples run along +Z from z=1..3 at the origin line; the key
        // light points straight up (+Y).
        var up = (X: 0.0, Y: 1.0, Z: 0.0);
        double br = 0, bg = 0, bb = 0;
        ShadingPipeline.VolumetricInScatterSegment(
            in fx, in de,
            camX: 0, camY: 0, camZ: 0,
            rdx: 0, rdy: 0, rdz: 1,
            eps: 1e-3,
            tStart: 1.0, tEnd: 3.0,
            in up, in up, in up,
            ref br, ref bg, ref bb);
        return br + bg + bb;
    }

    [Fact]
    public void PerStep_Shadow_Makes_InScatter_Occlusion_Aware()
    {
        // Occluder sphere sits above the segment, in the +Y light path, so the
        // SoftShadow march from each volume sample toward the light hits it.
        var occluder = new DelegateDeAdapter(Sphere(0.0, 3.0, 2.0, 1.5));
        var empty = new NullDe();   // no geometry -> full light visibility

        // ── ShadowSteps == 0: the per-step shadow is skipped, so the occluder is
        //    invisible to the medium — flat glow. ──
        double flatOpen = InScatter(Fog(0), in empty);
        double flatOccl = InScatter(Fog(0), in occluder);
        Assert.True(flatOpen > 0, "in-scatter should be positive");
        Assert.True(Math.Abs(flatOccl - flatOpen) < flatOpen * 0.01,
            $"with ShadowSteps=0 the occluder must not change in-scatter " +
            $"(open={flatOpen:0.000}, occluded={flatOccl:0.000})");

        // ── ShadowSteps > 0: the occluder shadows the medium, dimming the shaft. ──
        double shaftOpen = InScatter(Fog(24), in empty);
        double shaftOccl = InScatter(Fog(24), in occluder);

        // Unoccluded, shadows-on ~ matches shadows-off (nothing to shadow).
        Assert.True(Math.Abs(shaftOpen - flatOpen) < flatOpen * 0.05,
            $"unoccluded in-scatter should be shadow-independent " +
            $"(shadowsOff={flatOpen:0.000}, shadowsOn={shaftOpen:0.000})");

        // The occluder removes a large fraction of the in-scatter — the shaft.
        Assert.True(shaftOccl < shaftOpen * 0.6,
            $"occluder should measurably dim the shadowed in-scatter " +
            $"(open={shaftOpen:0.000}, occluded={shaftOccl:0.000})");
    }
}
