// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System.Linq;
using System.Threading;
using Xunit;
using FracturingFog;
using FracturingFog.Models;

namespace FracturingFog.Server.Tests;

// #1292 — IFSCalculator seeded its chaos game from Environment.TickCount, so
// every render (live or --batch) was a different image and the visual-
// regression harness could not hash it. Slices are now seeded by index.
public class S1292IfsDeterminismTests
{
    private static uint[] Render()
    {
        var c = new IFSCalculator(96, 96)
        {
            ColorMap = new GrayscalePalette(),
            Zoom = 0.5,
            FractalParameters = new FractalParameters { IFSIterations = 200_000 },
        };
        c.Calculate(default);
        return (uint[])c.ColorBuffer.Clone();
    }

    [Fact]
    public void Same_Params_Give_Same_Image()
    {
        var a = Render();
        // Environment.TickCount ticks every ~15 ms. Wait past that so a
        // tick-seeded RNG would differ, rather than passing by coincidence.
        Thread.Sleep(50);
        var b = Render();
        Assert.Equal(a, b);
    }

    [Fact]
    public void Image_Is_Not_Flat()
    {
        // Guard against "deterministic because empty".
        Assert.True(Render().Distinct().Count() > 8);
    }
}
