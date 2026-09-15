// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #609 — CPU deep-zoom perturbation-orbit path. Past HPZoomThreshold the direct
// double orbit in ComputePixelOrbit iterates a wrong/mushy orbit, so trap/stripe/
// TIA accumulators sample garbage. CalculateOrbitAwarePerturbation reconstructs
// z = Z[m] + δ from the shared reference orbit (glitch-free Zhuoran rebasing) and
// samples the accumulator on that full value. These tests gate the two properties
// the issue calls out: reference sanity (perturbation ≈ direct where BOTH are
// accurate) and deep engagement (the path actually runs and renders real orbit
// lace where direct double would be mush).

using System;
using System.Collections.Generic;
using FracturingFog;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class DeepZoomOrbitPerturbationTests
{
    private const int W = 64, H = 48;

    // Seahorse-valley filament — rich exterior orbit lace at deep zoom.
    private const double Cx = -0.743643887037151;
    private const double Cy = 0.13182590420533;

    // Perturbation engages above HPZoomThreshold; drop it to 1e6 so the deep
    // orbit path runs at a MODERATE zoom where direct double is still accurate.
    private static readonly QualityPreset PerturbLowThreshold =
        new() { Name = "perturb", AllowHighPrecision = true, HPZoomThreshold = 1e6 };

    // Never promotes to HP → always the direct z²+c orbit loop, at any zoom.
    private static readonly QualityPreset DirectOnly =
        new() { Name = "direct", AllowHighPrecision = false };

    private static ColorThemeData TrapData() => new()
    {
        Name = "Test Trap",
        Category = "Test",
        Kind = ColorThemeKind.OrbitTrap,
        TrapShape = OrbitTrapShape.Ring,
        TrapScale = 2f,
        TrapPower = 0.35f,
        Stops =
        {
            new ColorStopData { Position = 0.0f, R = 0,   G = 0,   B = 0   },
            new ColorStopData { Position = 0.5f, R = 255, G = 128, B = 0   },
            new ColorStopData { Position = 1.0f, R = 255, G = 255, B = 255 },
        },
    };

    private static uint[] Render(double zoom, QualityPreset quality, int maxIter = 3000)
    {
        var calc = new MandelbrotCalculator(W, H)
        {
            CenterX = Cx, CenterY = Cy, Zoom = zoom, MaxIterations = maxIter,
            Quality = quality,
            ColorMap = DataDrivenColorThemes.Create(TrapData())!,
        };
        calc.Calculate(default);
        return (uint[])calc.ColorBuffer.Clone();
    }

    private static double MeanAbsChannelDiff(uint[] a, uint[] b)
    {
        Assert.Equal(a.Length, b.Length);
        long sum = 0;
        for (int i = 0; i < a.Length; i++)
        {
            uint pa = a[i], pb = b[i];
            for (int s = 0; s < 32; s += 8)
                sum += Math.Abs((int)((pa >> s) & 0xFF) - (int)((pb >> s) & 0xFF));
        }
        return sum / (double)(a.Length * 4);
    }

    // Reference sanity: at Zoom 1e8 BOTH the perturbation-orbit path and the
    // direct-double orbit path are accurate. The same theme + same geometry must
    // therefore produce near-identical images — the perturbation loop reconstructs
    // the same orbit the direct loop iterates, and samples the accumulator on it.
    [Fact]
    public void PerturbationOrbit_MatchesDirect_AtModerateZoom()
    {
        uint[] perturb = Render(1e8, PerturbLowThreshold);
        uint[] direct = Render(1e8, DirectOnly);

        // Both must be real orbit renders, not a flat fill.
        Assert.True(new HashSet<uint>(perturb).Count >= 4, "perturbation render is degenerate");
        Assert.True(new HashSet<uint>(direct).Count >= 4, "direct render is degenerate");

        double diff = MeanAbsChannelDiff(perturb, direct);
        Assert.True(diff < 6.0,
            $"perturbation-orbit should match direct-orbit within tolerance, mean|Δ|={diff:F3}");
    }

    // Deep engagement: at Zoom 1e15 the direct double orbit is mush, so this path
    // is the only correct one. It must still run end-to-end and paint real,
    // non-uniform, opaque orbit lace (Sample actually fires on the reconstructed
    // full value) — not a single flat colour or transparent garbage.
    [Fact]
    public void PerturbationOrbit_EngagesAndRenders_Deep()
    {
        uint[] img = Render(1e15, PerturbLowThreshold, maxIter: 5000);

        Assert.True(new HashSet<uint>(img).Count >= 4,
            "deep perturbation orbit should be non-uniform");

        bool anyOpaqueNonBlack = false;
        foreach (uint px in img)
            if ((px & 0xFF000000u) == 0xFF000000u && (px & 0x00FFFFFFu) != 0u)
            { anyOpaqueNonBlack = true; break; }
        Assert.True(anyOpaqueNonBlack, "deep perturbation orbit should paint opaque trap colour");
    }

    // Crossover: nudging the zoom across the direct→perturbation threshold (with
    // geometry essentially fixed — a 5% zoom step) must not flip the image. This
    // guards the continuity of the accumulator convention (pre-update RAW z,
    // iter > 0, same c) across the two code paths.
    [Fact]
    public void PerturbationOrbit_CrossoverIsContinuous()
    {
        // Threshold 1e6: 0.98e6 → direct loop, 1.02e6 → perturbation loop.
        uint[] justBelow = Render(0.98e6, PerturbLowThreshold);
        uint[] justAbove = Render(1.02e6, PerturbLowThreshold);

        double diff = MeanAbsChannelDiff(justBelow, justAbove);
        Assert.True(diff < 8.0,
            $"image must be continuous across the direct→perturbation crossover, mean|Δ|={diff:F3}");
    }
}
