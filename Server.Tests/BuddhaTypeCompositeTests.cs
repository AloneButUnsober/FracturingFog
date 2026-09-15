// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #836 (#65): the Buddha-family FractalType must drive the composite —
// Buddhabrot / AntiBuddhabrot force single-channel ColorMap, Nebulabrot /
// AntiNebulabrot force RGB NebulabrotBands — independent of the shared
// FractalParameters.BuddhaColorMode toggle. Before the fix nothing tied the
// type to a composite, so all four used the default NebulabrotBands and
// Buddhabrot rendered identically to Nebulabrot (likewise the Anti pair).
// Deterministic via a fixed BuddhaSeed + uniform (non-Metropolis) sampling,
// so the two instances accumulate identical hit histograms and any ColorBuffer
// difference is purely the composite the TYPE chose.

using FracturingFog;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class BuddhaTypeCompositeTests
{
    private const int W = 80, H = 60;

    private static FractalParameters Params(BuddhaColorMode mode) => new()
    {
        BuddhaColorMode = mode,
        BuddhaSamples = 30_000,
        BuddhaIterLow = 50,
        BuddhaIterMid = 500,
        BuddhaIterHigh = 5_000,
        BuddhaSeed = 12345,
        BuddhaQualityMode = BuddhaQualityMode.Standard,
        BuddhaMetropolis = false,
        BuddhaProgressive = false,
    };

    private static uint[] Render(BuddhaFamilyCalculator c, FractalParameters fp)
    {
        c.CenterX = -0.5; c.CenterY = 0.0; c.Zoom = 1.0;
        c.MaxIterations = 5_000;
        c.Quality = QualityPreset.Standard;
        c.ColorMap = new HsvPalette();
        c.FractalParameters = fp;
        c.Calculate(default);
        return (uint[])c.ColorBuffer.Clone();
    }

    [Fact]
    public void Buddhabrot_And_Nebulabrot_Render_Differently_With_Identical_Params()
    {
        // Same param mode for both — the type override must still diverge them.
        var buddha = Render(new BuddhabrotCalculator(W, H), Params(BuddhaColorMode.NebulabrotBands));
        var nebula = Render(new NebulabrotCalculator(W, H), Params(BuddhaColorMode.NebulabrotBands));

        Assert.Equal(buddha.Length, nebula.Length);
        Assert.NotEqual(buddha, nebula);   // distinct composites → distinct frames
    }

    [Fact]
    public void AntiBuddhabrot_And_AntiNebulabrot_Render_Differently()
    {
        var anti  = Render(new AntiBuddhabrotCalculator(W, H), Params(BuddhaColorMode.NebulabrotBands));
        var antiN = Render(new AntiNebulabrotCalculator(W, H), Params(BuddhaColorMode.NebulabrotBands));

        Assert.Equal(anti.Length, antiN.Length);
        Assert.NotEqual(anti, antiN);
    }

    [Fact]
    public void Type_ForcedMode_Overrides_The_Param_Toggle()
    {
        // Buddhabrot forces ColorMap, so flipping the param has no effect.
        var withColorMap = Render(new BuddhabrotCalculator(W, H), Params(BuddhaColorMode.ColorMap));
        var withBands    = Render(new BuddhabrotCalculator(W, H), Params(BuddhaColorMode.NebulabrotBands));
        Assert.Equal(withColorMap, withBands);

        // Nebulabrot forces NebulabrotBands, likewise ignoring the param.
        var nebColorMap = Render(new NebulabrotCalculator(W, H), Params(BuddhaColorMode.ColorMap));
        var nebBands    = Render(new NebulabrotCalculator(W, H), Params(BuddhaColorMode.NebulabrotBands));
        Assert.Equal(nebColorMap, nebBands);
    }
}
