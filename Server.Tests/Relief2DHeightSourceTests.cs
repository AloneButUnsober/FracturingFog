using System;
using Xunit;
using FracturingFog;
using FracturingFog.Interefaces;
using FracturingFog.Models;

namespace FracturingFog.Server.Tests;

// #139 — the root-finding (Newton/Halley), Buddhabrot-family (density) and
// Apollonian (synthesised dome) calculators expose an IHeightFieldSource so the
// Relief 3D / Oblique raymarch works for them too. These lock in that each
// produces a non-degenerate height field (a base plane plus raised structure).
public class Relief2DHeightSourceTests
{
    private static (double max, int distinct) Stats(float[] h)
    {
        double max = 0;
        var seen = new System.Collections.Generic.HashSet<int>();
        foreach (float v in h)
        {
            if (v > max) max = v;
            seen.Add((int)MathF.Round(v * 100f));
            if (seen.Count > 8) break;
        }
        // Recompute max fully (loop may have broken early on distinct count).
        max = 0;
        foreach (float v in h) if (v > max) max = v;
        return (max, seen.Count);
    }

    [Fact]
    public void Newton_Exposes_HeightField()
    {
        var calc = new NewtonCalculator(96, 96)
        {
            CenterX = 0, CenterY = 0, Zoom = 1.0, MaxIterations = 64,
            FractalParameters = new FractalParameters { NewtonExponent = 3 },
        };
        calc.Calculate(default);
        Assert.IsAssignableFrom<IHeightFieldSource>(calc);
        var (max, distinct) = Stats(calc.SmoothBuffer);
        Assert.True(max > 0, "Newton height all zero");
        Assert.True(distinct > 1, "Newton height flat");
    }

    [Fact]
    public void Halley_Exposes_HeightField()
    {
        var calc = new HalleyCalculator(96, 96)
        {
            CenterX = 0, CenterY = 0, Zoom = 1.0, MaxIterations = 64,
            FractalParameters = new FractalParameters { NewtonExponent = 3 },
        };
        calc.Calculate(default);
        var (max, distinct) = Stats(calc.SmoothBuffer);
        Assert.True(max > 0, "Halley height all zero");
        Assert.True(distinct > 1, "Halley height flat");
    }

    [Fact]
    public void Buddhabrot_Exposes_DensityHeightField()
    {
        var calc = new BuddhabrotCalculator(96, 96)
        {
            CenterX = 0, CenterY = 0, Zoom = 1.0, MaxIterations = 200,
            FractalParameters = new FractalParameters { BuddhaSamples = 200_000 },
        };
        calc.Calculate(default);
        Assert.IsAssignableFrom<IHeightFieldSource>(calc);
        var (max, _) = Stats(calc.SmoothBuffer);
        Assert.True(max > 0, "Buddhabrot density height all zero");
    }

    [Fact]
    public void Apollonian_Exposes_DomeHeightField()
    {
        var calc = new ApollonianCalculator(96, 96)
        {
            CenterX = 0, CenterY = 0, Zoom = 1.0,
            FractalParameters = new FractalParameters
            {
                ApollonianDepth = 12, ApollonianMinPixelRadius = 0.75,
            },
        };
        calc.Calculate(default);
        Assert.IsAssignableFrom<IHeightFieldSource>(calc);
        var (max, distinct) = Stats(calc.SmoothBuffer);
        Assert.True(max > 0, "Apollonian dome height all zero");
        Assert.True(distinct > 1, "Apollonian height flat");
    }
}
