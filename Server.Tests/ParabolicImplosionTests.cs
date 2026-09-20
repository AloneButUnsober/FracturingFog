// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Collections.Generic;
using System.Numerics;
using FracturingFog.Abstractions.Animation;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

// Parabolic implosion Tier A / S1 (#911). The naïve implosion drives the Julia
// parameter c around a parabolic c₀ via the centred Complex polar (Lissajous)
// sweep. These test the new centre offset on that sweep.
public sealed class ParabolicImplosionTests
{
    private static AnimationData ImplosionAnim(double cx, double cy, double eps)
        => new()
        {
            Name = "t",
            Tracks = new List<AnimationTrack>
            {
                new() { ParamName = "JuliaC", Mode = AnimationMode.Lissajous,
                        Min = eps, Max = eps, CenterX = cx, CenterY = cy,
                        FrequencyHz = 0.05, Enabled = true },
            },
        };

    [Fact]
    public void CentredSweep_CirclesTheParabolicPoint()
    {
        var p = new FractalParameters();
        var anims = new List<IParameterAnimator>(ImplosionAnim(0.25, 0.0, 0.03).ToAnimators(p));
        Assert.NotEmpty(anims);

        double minRe = double.MaxValue, maxRe = double.MinValue, minIm = double.MaxValue, maxIm = double.MinValue;
        Complex c0 = new(0.25, 0.0);
        for (int i = 0; i < 400; i++)
        {
            foreach (var a in anims) a.Tick(0.1);
            Complex c = p.JuliaC;
            // Always on the circle of radius ε about c₀.
            Assert.True(Math.Abs((c - c0).Magnitude - 0.03) < 1e-6, $"|c-c₀|={(c - c0).Magnitude}");
            minRe = Math.Min(minRe, c.Real); maxRe = Math.Max(maxRe, c.Real);
            minIm = Math.Min(minIm, c.Imaginary); maxIm = Math.Max(maxIm, c.Imaginary);
        }
        // Over a full loop it spans the whole circle around c₀ = 0.25.
        Assert.True(maxRe > 0.27 && minRe < 0.23, $"Re span [{minRe},{maxRe}]");
        Assert.True(maxIm > 0.02 && minIm < -0.02, $"Im span [{minIm},{maxIm}]");
    }

    [Fact]
    public void DefaultCentre_IsOriginCentred_ByteIdentical()
    {
        // Centre (0,0) → the pre-#911 origin-centred polar sweep: |c| == ε.
        var p = new FractalParameters();
        var anims = new List<IParameterAnimator>(ImplosionAnim(0.0, 0.0, 0.05).ToAnimators(p));
        for (int i = 0; i < 20; i++) foreach (var a in anims) a.Tick(0.1);
        Assert.True(Math.Abs(p.JuliaC.Magnitude - 0.05) < 1e-6, $"|c|={p.JuliaC.Magnitude}");
    }
}
