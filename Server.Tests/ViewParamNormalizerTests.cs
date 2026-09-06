// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Roadmap slice S10-LW.1 (PaletteBuilder-Design.md §4a, #392 / #690) — the pure half of
// the view-parameter keystone: normalise a render's smooth-iteration field to the palette
// parameter t∈[0,1]. Deterministic → the colour parity twin.

using FracturingFog.Imaging;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class ViewParamNormalizerTests
{
    [Fact]
    public void NormalizeSmooth_Divides_By_MaxIterations_And_Clamps()
    {
        var smooth = new float[] { 0f, 128f, 256f, 512f, 600f };
        var t = ViewParamNormalizer.NormalizeSmooth(smooth, 256, smooth.Length);
        Assert.Equal(5, t.Length);
        Assert.Equal(0f, t[0]);            // in-set → 0
        Assert.Equal(0.5f, t[1], 5);       // 128/256
        Assert.Equal(1f, t[2], 5);         // 256/256
        Assert.Equal(1f, t[3]);            // 512/256 clamps to 1
        Assert.Equal(1f, t[4]);            // 600/256 clamps to 1
    }

    [Fact]
    public void NormalizeSmooth_InSet_Negative_And_NonFinite_Collapse_To_Zero()
    {
        var smooth = new float[] { 0f, -3f, float.NaN, float.PositiveInfinity, 64f };
        var t = ViewParamNormalizer.NormalizeSmooth(smooth, 128, smooth.Length);
        Assert.Equal(0f, t[0]);
        Assert.Equal(0f, t[1]);
        Assert.Equal(0f, t[2]);
        Assert.Equal(0f, t[3]);
        Assert.Equal(0.5f, t[4], 5);
    }

    [Fact]
    public void NormalizeSmooth_NonPositive_MaxIterations_Yields_All_Zero()
    {
        var smooth = new float[] { 10f, 20f, 30f };
        Assert.All(ViewParamNormalizer.NormalizeSmooth(smooth, 0, 3), v => Assert.Equal(0f, v));
        Assert.All(ViewParamNormalizer.NormalizeSmooth(smooth, -5, 3), v => Assert.Equal(0f, v));
    }

    [Fact]
    public void NormalizeSmooth_Count_Clamped_To_Source_Length_And_Handles_Null()
    {
        var smooth = new float[] { 100f, 200f };
        // Asking for more than the source has → clamped to the source length.
        Assert.Equal(2, ViewParamNormalizer.NormalizeSmooth(smooth, 256, 10).Length);
        // Null / negative are safe.
        Assert.Empty(ViewParamNormalizer.NormalizeSmooth(null!, 256, 4));
        Assert.Empty(ViewParamNormalizer.NormalizeSmooth(smooth, 256, -1));
    }
}
