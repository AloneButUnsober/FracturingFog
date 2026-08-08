// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using Xunit;
using FracturingFog.Models;

namespace FracturingFog.Server.Tests;

// #250 — the classic Acid Warp intro fires once per process. These lock in that
// the gate is one-shot and that ApplyClassic stamps the Spurrier configuration.
public class AcidWarpIntroTests
{
    [Fact]
    public void Intro_Fires_Once_Per_Process()
    {
        AcidWarpIntro.ResetForTests();
        Assert.True(AcidWarpIntro.TryConsumeIntro());   // first entry → intro
        Assert.False(AcidWarpIntro.TryConsumeIntro());  // second → no intro
        Assert.False(AcidWarpIntro.TryConsumeIntro());
    }

    [Fact]
    public void ApplyClassic_Stamps_The_Spurrier_Look()
    {
        var p = new FractalParameters
        {
            AcidWarpPattern = 7,
            AcidWarpFrequency = 4.2,
            AcidWarpCenterX = 0.9,
            AcidWarpCenterY = -0.3,
        };
        AcidWarpIntro.ApplyClassic(p);
        Assert.Equal(AcidWarpIntro.ClassicPattern, p.AcidWarpPattern);
        Assert.Equal(AcidWarpIntro.ClassicFrequency, p.AcidWarpFrequency);
        Assert.Equal(0.0, p.AcidWarpCenterX);
        Assert.Equal(0.0, p.AcidWarpCenterY);
    }
}
