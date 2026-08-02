// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using Xunit;
using FracturingFog;
using FracturingFog.Interefaces;
using FracturingFog.Models;

namespace FracturingFog.Server.Tests;

// #194 — the Buddhabrot family is a Monte Carlo density plot whose sample pass
// dominates cost. Its accumulated hit histograms persist after Calculate, so a
// colour-theme change can recomposite (Recolor) instead of re-sampling. These
// lock in that:
//   • the family implements ISupportsCheapRecolor
//   • Recolor after a colour-map swap == a full Calculate with that map
//     (deterministic seed) — proving the recolor is faithful AND that it does
//     not re-sample (a re-sample would still match, but the equality is what
//     the host relies on to skip Calculate)
//   • recolour actually reflects the new map (different map => different image)
public class BuddhaCheapRecolorTests
{
    private static BuddhabrotCalculator Make(IColorMap map) => new BuddhabrotCalculator(64, 64)
    {
        CenterX = -0.5, CenterY = 0.0, Zoom = 1.0, MaxIterations = 200,
        ColorMap = map,
        FractalParameters = new FractalParameters
        {
            BuddhaSamples = 200_000,
            BuddhaSeed = 4242,
            BuddhaColorMode = BuddhaColorMode.ColorMap,
        },
    };

    [Fact]
    public void Family_Implements_CheapRecolor()
    {
        Assert.IsAssignableFrom<ISupportsCheapRecolor>(new BuddhabrotCalculator(8, 8));
        Assert.IsAssignableFrom<ISupportsCheapRecolor>(new NebulabrotCalculator(8, 8));
        Assert.IsAssignableFrom<ISupportsCheapRecolor>(new AntiBuddhabrotCalculator(8, 8));
        Assert.IsAssignableFrom<ISupportsCheapRecolor>(new AntiNebulabrotCalculator(8, 8));
    }

    [Fact]
    public void Recolor_Matches_FullRecompute_WithNewMap()
    {
        // Sample once with Grayscale, then swap to Fire and Recolor (no resample).
        var calc = Make(new GrayscalePalette());
        calc.Calculate(default);

        calc.ColorMap = new FirePalette();
        calc.Recolor();
        var recolored = (uint[])calc.ColorBuffer.Clone();

        // Full recompute from scratch with Fire + identical seed/params.
        var fresh = Make(new FirePalette());
        fresh.Calculate(default);

        Assert.Equal(fresh.ColorBuffer, recolored);
    }

    [Fact]
    public void Recolor_ChangesImage_WhenMapChanges()
    {
        var calc = Make(new GrayscalePalette());
        calc.Calculate(default);
        var gray = (uint[])calc.ColorBuffer.Clone();

        calc.ColorMap = new FirePalette();
        calc.Recolor();

        Assert.NotEqual(gray, calc.ColorBuffer);
    }
}
