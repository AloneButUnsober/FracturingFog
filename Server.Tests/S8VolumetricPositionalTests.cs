// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Roadmap S8 (#404) — positional lights in the legacy per-pixel volumetric
// in-scatter march (the god-ray path). Point/spot lights now attenuate the fog
// per sample (inverse-square × soft range × spot cone) and light it from each
// sample's own direction-to-light, matching the froxel volumetrics twin. These
// drive ShadingPipeline.VolumetricInScatterSegment directly (fog only, no
// surface shade) and lock:
//   • a Directional light is unaffected by its (unused) world position — the
//     path stays byte-identical to before this slice;
//   • a Point light lights the fog more when near than when far (inverse-square);
//   • a Spot light lights the fog more when its cone points at the samples than
//     when aimed away.

using System;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S8VolumetricPositionalTests
{
    // Sphere DE (radius 1). Only the shadow march consults it, and shadows are
    // off here — its presence just satisfies the has-DE gate.
    private static readonly DistanceEstimator SphereDe =
        (x, y, z) => Math.Sqrt(x * x + y * y + z * z) - 1.0;

    private static LightingFxData FogFx()
    {
        var fx = LightingFxData.CreateDefault();
        fx.FogDensity = 0.6;
        fx.VolumeSteps = 32;
        fx.ShadowSteps = 0;   // isolate in-scatter from shadows
        var l = fx.Light1;
        l.Intensity = 3.0;
        l.Color = 0xFFFFFFFFu;
        fx.Light1 = l;
        return fx;
    }

    // March a fog segment from the camera at origin along +Y, returning the
    // accumulated in-scatter brightness (R+G+B) over a black background.
    private static double InScatter(in LightingFxData fx)
    {
        double br = 0, bg = 0, bb = 0;
        var de = new DelegateDeAdapter(SphereDe);
        ShadingPipeline.VolumetricInScatterSegment(
            in fx, in de,
            camX: 0, camY: 0, camZ: 0,
            rdx: 0, rdy: 1, rdz: 0, eps: 1e-4,
            tStart: 0.0, tEnd: 4.0,
            ref br, ref bg, ref bb);
        return br + bg + bb;
    }

    // A Directional light ignores PosX/PosY/PosZ entirely: two runs differing
    // only in the (unused) world position are byte-identical. Guards that the
    // legacy directional fog path is untouched by this slice.
    [Fact]
    public void Directional_Ignores_Position()
    {
        var a = FogFx();   // Light1 default Type == Directional
        var fb = FogFx();
        var l = fb.Light1;
        l.PosX = 123.0; l.PosY = -45.0; l.PosZ = 67.0;   // nonsense for a directional
        fb.Light1 = l;

        Assert.Equal(InScatter(a), InScatter(fb));
    }

    // A Point light lights the fog more when placed near the marched column than
    // when placed far away (inverse-square falloff reaches the fog samples).
    [Fact]
    public void Point_Near_Brighter_Than_Far()
    {
        var near = FogFx();
        var ln = near.Light1;
        ln.Type = LightType.Point;
        ln.PosX = 0; ln.PosY = 2; ln.PosZ = 1;    // beside the samples (y in 0..4)
        ln.Range = 0;                              // pure 1/d²
        near.Light1 = ln;

        var far = FogFx();
        var lf = far.Light1;
        lf.Type = LightType.Point;
        lf.PosX = 0; lf.PosY = 2; lf.PosZ = 80;   // same aim, far away
        lf.Range = 0;
        far.Light1 = lf;

        double bn = InScatter(near), bf = InScatter(far);
        Assert.True(bn > bf, $"near ({bn}) should out-scatter far ({bf}).");
        Assert.True(bf >= 0.0);
    }

    // A Spot light lights the fog more when its cone axis points toward the
    // samples than when aimed the opposite way. The aim that maximises
    // (cone axis · +Y) is picked via the same LightDir the shade path uses, so
    // the test is independent of the Theta/Phi convention.
    [Fact]
    public void Spot_OnAxis_Brighter_Than_OffAxis()
    {
        // Two opposite aims; choose the one whose direction has the larger +Y
        // component as "on axis" (the samples sit above the camera along +Y).
        (double th, double ph) aimA = (0.0, 0.0);
        (double th, double ph) aimB = (Math.PI, Math.PI);   // opposite hemisphere
        var da = ShadingPipeline.LightDir(aimA.th, aimA.ph);
        var db = ShadingPipeline.LightDir(aimB.th, aimB.ph);
        var on  = da.Item2 >= db.Item2 ? aimA : aimB;
        var off = da.Item2 >= db.Item2 ? aimB : aimA;

        LightingFxData Spot((double th, double ph) aim)
        {
            var fx = FogFx();
            var l = fx.Light1;
            l.Type = LightType.Spot;
            l.PosX = 0; l.PosY = 2; l.PosZ = 0;
            l.Range = 0;
            l.SpotInnerDeg = 20;
            l.SpotOuterDeg = 35;
            l.Theta = aim.th; l.Phi = aim.ph;
            fx.Light1 = l;
            return fx;
        }

        double onB = InScatter(Spot(on));
        double offB = InScatter(Spot(off));
        Assert.True(onB > offB, $"on-axis ({onB}) should out-scatter off-axis ({offB}).");
    }
}
