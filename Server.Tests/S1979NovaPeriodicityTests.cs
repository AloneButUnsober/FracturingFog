// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1979 — Nova's periodicity check (idea: pauldelbrot, fractalforums.org topic 5693; Brent 1980).
//   • #1979 Brent's periodicity check in the double loop: with it on and off every buffer (colour,
//     basin, smooth count, final z) is byte-identical, on views full of attracting cycles (a minibrot,
//     its filaments, a Julia set whose c lies in the minibrot, plain Newton) and at an odd cap, while
//     most non-converged pixels stop early.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using FracturingFog.FFMath;
using FracturingFog.Models;
using Xunit;
using static FracturingFog.Server.Tests.S1909ConjR2Tests;
using static FracturingFog.Server.Tests.S1921DeepFxTests;
using static FracturingFog.Server.Tests.S1959NovaDeepTests;
using static FracturingFog.Server.Tests.S1964NovaSwitchTests;

namespace FracturingFog.Server.Tests;

public sealed class S1979NovaPeriodicityTests(ITestOutputHelper output)
{
    private const int Pw = 96, Ph = 64;

    private static NewtonCalculator Frame(double x, double y, double zoom, int cap, bool periodicity, bool julia = false,
                                          double juliaC = -0.45, FractalType type = FractalType.Nova, double relax = 1.0)
    {
        var c = new NewtonCalculator(Pw, Ph)
        {
            FractalType = type, MaxIterations = cap, ColorMap = new NewtonBasinShadedMap(), Quality = QualityPreset.Ultra,
            FractalParameters = new FractalParameters
            {
                NewtonExponent = 3, NewtonRelaxation = relax, NovaJuliaMode = julia, NovaJuliaC = new(juliaC, 0),
            },
            CenterX = x, CenterY = y, Zoom = zoom, Periodicity = periodicity,
        };
        c.Calculate(CancellationToken.None);
        return c;
    }

    [Theory]
    [InlineData("minibrot", -0.45, 0, 12, false, 1500)]
    [InlineData("minibrot, odd cap", -0.45, 0, 12, false, 1777)]
    [InlineData("filaments", -0.60, 0, 30, false, 6000)]
    [InlineData("Julia c = -0.45", 0.9, 0, 1.5, true, 1500)]
    public void Periodicity_Leaves_Every_Buffer_Byte_Identical(string name, double x, double y, double zoom, bool julia, int cap)
    {
        var off = Frame(x, y, zoom, cap, periodicity: false, julia);
        var on = Frame(x, y, zoom, cap, periodicity: true, julia);
        int nc = off.BasinBuffer.Count(b => b < 0);
        var st = on.LastEarlyOuts;
        output.WriteLine($"{name}: {nc} non-converged, {st.Cycles} stopped by the cycle check, {st.IterationsSaved} iterations saved");
        Assert.Equal(0, off.LastEarlyOuts.Cycles);
        Assert.True(nc >= Pw * Ph / 40, $"precondition: non-converged area ({nc})");
        Assert.True(st.Cycles >= nc / 2, $"most cycle pixels stop early ({st.Cycles}/{nc})");
        Assert.Equal(off.ColorBuffer, on.ColorBuffer);
        Assert.Equal(off.BasinBuffer, on.BasinBuffer);
        Assert.Equal(off.SmoothBuffer, on.SmoothBuffer);
        Assert.Equal(off.FinalZrBuffer, on.FinalZrBuffer);
        Assert.Equal(off.FinalZiBuffer, on.FinalZiBuffer);
    }

    [Fact]
    public void Periodicity_Leaves_Plain_Newton_Byte_Identical()
    {
        // Relaxed Newton (R = 1.9) has attracting cycles between the basins.
        var off = Frame(0, 0, 1, 1500, periodicity: false, type: FractalType.Newton, relax: 1.9);
        var on = Frame(0, 0, 1, 1500, periodicity: true, type: FractalType.Newton, relax: 1.9);
        output.WriteLine($"Newton R 1.9: {off.BasinBuffer.Count(b => b < 0)} non-converged, {on.LastEarlyOuts.Cycles} stopped");
        Assert.Equal(off.ColorBuffer, on.ColorBuffer);
        Assert.Equal(off.SmoothBuffer, on.SmoothBuffer);
        Assert.Equal(off.FinalZrBuffer, on.FinalZrBuffer);
        Assert.Equal(off.FinalZiBuffer, on.FinalZiBuffer);
    }
}
