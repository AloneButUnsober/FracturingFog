// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1033 — the relief's shading marches (soft shadow, AO, reflections) under Real
// height. The height DE is (y − h)·invLip with ONE Lipschitz factor from the
// field's steepest cell, so on fine fields it is far below the true distance
// almost everywhere, and the marches read "surface right here": at app defaults
// ~93% of the terrain came out shadowed and AO darkened open ground by ~⅓.
// The invariants checked here do not depend on the fix's internals:
//   • over flat ground / a planar slope the probe's distance is the true distance;
//   • a lit point is lit and a point behind a ridge is shadowed, and neither
//     changes when the Lipschitz bound is loosened (any smaller invLip is still a
//     valid bound, so correct shading must not depend on it);
//   • end to end at app defaults, most terrain is lit and open ground has no AO,
//     as a brute-force oracle (true nearest-surface AO, fine-step hard shadows)
//     measured on the same view while fixing it.

using System;
using FracturingFog;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class ReliefShadingProbeTests
{
    private const int N = 200;          // field cells per axis; aspect 1 → world [-0.5, 0.5]²
    private const double RidgeH = 0.2, RidgeW = 0.02;

    // A Gaussian ridge along x at z = 0 on flat ground (sy = 1: field value = world height).
    private static float[] RidgeField(double slope = 0.0)
    {
        var f = new float[N * N];
        for (int j = 0; j < N; j++)
            for (int i = 0; i < N; i++)
            {
                double z = (j + 0.5) / N - 0.5, x = (i + 0.5) / N - 0.5;
                f[j * N + i] = (float)(RidgeH * Math.Exp(-z * z / (2 * RidgeW * RidgeW)) + slope * (x + 0.5));
            }
        return f;
    }

    // The steepest slope of the ridge sets the global Lipschitz factor.
    private static double InvLip(float[] f)
    {
        double m = 0;
        for (int j = 0; j < N; j++)
            for (int i = 0; i + 1 < N; i++)
            {
                m = Math.Max(m, Math.Abs(f[j * N + i + 1] - f[j * N + i]) * N);
                if (j + 1 < N) m = Math.Max(m, Math.Abs(f[(j + 1) * N + i] - f[j * N + i]) * N);
            }
        return 1.0 / Math.Sqrt(1 + m * m);
    }

    private static HeightfieldRaymarch2D.HeightDe De(float[] f, double invLip)
        => new(f, N, N, 1.0, 1.0, invLip, false);

    [Fact]
    public void Probe_MeasuresTheTrueDistance_OverFlatGroundAndAPlanarSlope()
    {
        var flat = RidgeField();
        var de = De(flat, InvLip(flat));
        var sde = new HeightfieldRaymarch2D.HeightShadeDe(in de, null, 0, 0);
        // 0.05 above flat ground, far from the ridge.
        double d = sde.ShadeProbe(0.1, 0.05, 0.35, 0, 1, 0, 0.0, out _, out bool hit);
        Assert.False(hit);
        Assert.Equal(0.05, d, 4);
        // The old estimate: the same point, scaled by the ridge's Lipschitz factor.
        Assert.True(de.Evaluate(0.1, 0.05, 0.35) < 0.02, "precondition: the global bound is loose here");

        // A plane rising at slope 0.5 along x (plus the ridge far away): the
        // perpendicular distance from a point a height g above it is g / √(1 + 0.25).
        var ramp = RidgeField(slope: 0.5);
        var rde = De(ramp, InvLip(ramp));
        var rsde = new HeightfieldRaymarch2D.HeightShadeDe(in rde, null, 0, 0);
        double x = 0.1, z = 0.35, g = 0.04;
        double y = rde.SampleHeight(x, z) + g;
        Assert.Equal(g / Math.Sqrt(1.25), rsde.ShadeProbe(x, y, z, 0, 1, 0, 0.0, out _, out _), 3);
    }

    // Light low from −z: a point on the −z side of the ridge sees it; a point on the
    // +z side looks through the ridge (at z = 0 the ray is ~0.06 up, the ridge 0.2).
    private static readonly (double X, double Y, double Z) ToLight = Norm(0.0, Math.Sin(Math.PI / 6), -Math.Cos(Math.PI / 6));

    private static (double, double, double) Norm(double x, double y, double z)
    {
        double l = Math.Sqrt(x * x + y * y + z * z); return (x / l, y / l, z / l);
    }

    private static double Shadow<TDe>(in TDe de, double z) where TDe : struct, IDistanceEstimator
        => ShadingPipeline.SoftShadow(in de, 0.0, 0.002, z, ToLight.X, ToLight.Y, ToLight.Z, 1e-3, 12.0, 8.0, 24);

    [Fact]
    public void SoftShadow_LitAndShadowedPointsAreRight_WhateverTheBound()
    {
        var f = RidgeField();
        double tight = InvLip(f);
        foreach (double invLip in new[] { tight, tight / 8 })
        {
            var de = De(f, invLip);
            var sde = new HeightfieldRaymarch2D.HeightShadeDe(in de, null, 0, 0);
            Assert.True(Shadow(in sde, -0.25) > 0.9, $"lit point shadowed ({Shadow(in sde, -0.25):0.###}) at invLip {invLip:0.####}");
            Assert.True(Shadow(in sde, 0.1) < 0.1, $"point behind the ridge lit ({Shadow(in sde, 0.1):0.###}) at invLip {invLip:0.####}");
        }
        // Sensitivity: the plain height-DE march shadows the lit point once the
        // bound is loose (the reported over-occlusion).
        var loose = De(f, tight / 8);
        Assert.True(Shadow(in loose, -0.25) < 0.5, $"plain march on the lit point: {Shadow(in loose, -0.25):0.###}");
    }

    // End to end at app defaults (Real height, auto-shade AO + shadows).
    [Fact]
    public void RealHeightRender_MostTerrainIsLit_AndOpenGroundHasNoAo()
    {
        int w = 320, h = 180;
        var fc = new MandelbrotCalculator(960, 540) { CenterX = -0.5, CenterY = 0, Zoom = 1, MaxIterations = 300, ColorMap = new MonoBandMap() };
        fc.Calculate(default);
        var ac = new MandelbrotCalculator(w, h) { CenterX = -0.5, CenterY = 0, Zoom = 1, MaxIterations = 300, ColorMap = new MonoBandMap() };
        ac.Calculate(default);
        var p = new FractalParameters
        {
            Relief2DEnabled = true, Relief2DRaymarch = true, Relief2DGpuRaymarch = false, Relief2DSupersample = 1,
            Relief2DTrueHeight = true, Relief2DGroundPlane = false,
        };
        var aov = new HeightfieldRaymarch2D.ReliefAovBuffers(w, h, captureComponents: true);
        HeightfieldRaymarch2D.Render(ac.ColorBuffer, fc.SmoothBuffer, w, h, 960, 540, p, new uint[w * h], out _, null, aov);

        double aoSum = 0; int n = 0, shadowed = 0;
        for (int i = 0; i < w * h; i++)
        {
            float d = aov.Depth[i];
            if (!(d > 0f && d < 9.9e5f)) continue;
            var c = aov.Components![i];
            aoSum += c.Ao; n++;
            if (c.Shadow < 0.5f) shadowed++;
        }
        Assert.True(n > w * h / 5, $"too few terrain hits ({n})");
        double shadowedFrac = (double)shadowed / n, aoMean = aoSum / n;
        // Brute-force reference on the same view (measured while fixing #1033): about
        // 21% of the terrain is in hard shadow and the true AO averages 0.999.
        Assert.True(shadowedFrac < 0.45, $"{shadowedFrac:P1} of the terrain shadowed (was ~93% before #1033)");
        Assert.True(aoMean > 0.97, $"mean AO {aoMean:0.###} (was ~0.66 before #1033)");
    }
}
