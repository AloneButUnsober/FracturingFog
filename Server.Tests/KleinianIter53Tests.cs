// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using FracturingFog;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

// #885 / #53 — locks the analysis answer: KleinianIterations is inert on the
// Smooth shade (the DE descent converges in < 16 inversions for every visible
// ray, so raising the cap is byte-identical), but is the dominant driver under
// the WordLength colour source. See Kleinian-Generalization-DesignPlan.md §3.7.
public sealed class KleinianIter53Tests
{
    private static uint[] Render(int iter, KleinianColorSource src, double zoom, double eps)
    {
        var p = new FractalParameters
        {
            KleinianIterations = iter,
            KleinianColorSource = src,
            KleinianEpsilon = eps,
        };
        var c = new KleinianCalculator(160, 160) { Zoom = zoom, FractalParameters = p };
        c.Calculate();
        return (uint[])c.ColorBuffer.Clone();
    }

    [Theory]
    [InlineData(1.0, 0.0012)]   // default framing
    [InlineData(40.0, 5e-5)]    // deep zoom + small epsilon
    public void Smooth_IsIterationIndependent(double zoom, double eps)
    {
        // The #53 finding: the inversion cap does NOT change the Smooth render —
        // every visible ray escapes in fewer than 16 inversions.
        Assert.Equal(Render(16, KleinianColorSource.Smooth, zoom, eps),
                     Render(64, KleinianColorSource.Smooth, zoom, eps));
    }

    [Fact]
    public void WordLength_RespondsToIterationCap()
    {
        // The #53 answer: colour-by-descent-depth makes the cap visible.
        Assert.NotEqual(Render(16, KleinianColorSource.WordLength, 1.0, 0.0012),
                        Render(64, KleinianColorSource.WordLength, 1.0, 0.0012));
    }
}
