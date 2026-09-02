// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #588 — the User-Equation interpreter path must feed finalZ (z at escape) +
// dz/dc to the nine-param IColorMap.Map overload, so finalZ-dependent themes
// (Potential, Binary/Argument Decomposition, Iter+FinalZ, domain/field-line)
// work on the DSL path the way they do on the native Mandelbrot + CalcGen paths.

using System;
using System.Threading;
using FracturingFog;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Security;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class DslFinalZColorPathTests
{
    private const int W = 48, H = 36;

    // Records whether the calculator ever passed a non-zero finalZ / dz through
    // the nine-param overload. Monotonic set via Interlocked so it is race-free
    // under the calculator's Parallel.For.
    private sealed class FinalZProbe : IColorMap
    {
        public ColorPaletteType Type => ColorPaletteType.Algorithmic;
        public int MaxIterations { get; set; }
        public int SawFinalZ;
        public int SawDz;

        public int Map(float smooth, float distance, int iterations)
            => unchecked((int)0xFF000000);

        public int Map(float smooth, float distance, int iterations, float nx, float ny)
            => unchecked((int)0xFF000000);

        public int Map(float smooth, float distance, int iterations, float nx, float ny,
                       float finalZr, float finalZi, float dzdcR, float dzdcI)
        {
            if (finalZr != 0f || finalZi != 0f) Interlocked.Exchange(ref SawFinalZ, 1);
            if (dzdcR != 0f || dzdcI != 0f) Interlocked.Exchange(ref SawDz, 1);
            return unchecked((int)0xFF000000);
        }
    }

    [Fact]
    public void Interpreter_FeedsFinalZ_ToNineParamOverload()
    {
        var probe = new FinalZProbe();
        var calc = new UserEquationCalculator(W, H)
        {
            CenterX = -0.5, CenterY = 0.0, Zoom = 1.0, MaxIterations = 100,
            ColorMap = probe,
            FractalParameters = new FractalParameters
            {
                UserEquationSource = "z*z + c",
                UserCodeOrigin = UserCodeOrigin.Interactive,
            },
        };
        calc.Calculate(default);

        Assert.Equal(1, probe.SawFinalZ);   // escape z reached the colour map
        Assert.Equal(1, probe.SawDz);       // dz/dc reached it too
    }

    // A real finalZ theme (Binary Decomposition colours by the sign of Im(z) at
    // escape) must now produce more than pure-smooth banding on the DSL path.
    [Fact]
    public void Interpreter_BinaryDecomposition_IsNonUniform()
    {
        // Binary Decomposition colours by the sign of Im(z) at escape — pure
        // finalZ, so it is flat unless the nine-param overload is fed.
        var theme = new BinaryDecompClassicMap();

        var calc = new UserEquationCalculator(W, H)
        {
            CenterX = -0.5, CenterY = 0.0, Zoom = 1.0, MaxIterations = 100,
            ColorMap = theme,
            FractalParameters = new FractalParameters
            {
                UserEquationSource = "z*z + c",
                UserCodeOrigin = UserCodeOrigin.Interactive,
            },
        };
        calc.Calculate(default);

        var distinct = new System.Collections.Generic.HashSet<uint>(calc.ColorBuffer);
        Assert.True(distinct.Count >= 3,
            $"finalZ theme should not be flat on the DSL path, saw {distinct.Count} colour(s)");
    }
}
