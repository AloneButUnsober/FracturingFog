// #2033 — the BigFloat reference orbit's step on three threads (BigFloatStepWorkers) is
// bit-identical to the serial step: the same three multiplies and two OD splits on the same
// operands. Checked on the bench-kit locations of S2032 (fractadyne bench-kit, WindySnowOwl,
// Apache-2.0; THIRD-PARTY-NOTICES.md section 6): every stored orbit point and every output
// buffer match with ParallelApOrbit on and off, and the workers' products equal BigFloat.Mul
// directly at several precisions.

using System;
using FracturingFog.FFMath;
using Xunit;

namespace FracturingFog.Server.Tests;

[Collection(MandelbrotGpuStaticsCollection.Name)]   // writes MandelbrotCalculator statics
public sealed class S2033ParallelApOrbitTests
{
    private static MandelbrotCalculator Run(string re, string im, string zoom, int cap, bool parallel)
    {
        bool saved = MandelbrotCalculator.ParallelApOrbit;
        MandelbrotCalculator.ParallelApOrbit = parallel;
        try { return S2032ApPeriodTests.Calc(re, im, zoom, cap, periodic: true); }
        finally { MandelbrotCalculator.ParallelApOrbit = saved; }
    }

    // Bit-for-bit (an OD's eight limbs; -0.0 vs 0.0 counts as a difference).
    private static bool Same(OD a, OD b) =>
        BitConverter.DoubleToInt64Bits(a.X0) == BitConverter.DoubleToInt64Bits(b.X0) && BitConverter.DoubleToInt64Bits(a.X1) == BitConverter.DoubleToInt64Bits(b.X1)
        && BitConverter.DoubleToInt64Bits(a.X2) == BitConverter.DoubleToInt64Bits(b.X2) && BitConverter.DoubleToInt64Bits(a.X3) == BitConverter.DoubleToInt64Bits(b.X3)
        && BitConverter.DoubleToInt64Bits(a.X4) == BitConverter.DoubleToInt64Bits(b.X4) && BitConverter.DoubleToInt64Bits(a.X5) == BitConverter.DoubleToInt64Bits(b.X5)
        && BitConverter.DoubleToInt64Bits(a.X6) == BitConverter.DoubleToInt64Bits(b.X6) && BitConverter.DoubleToInt64Bits(a.X7) == BitConverter.DoubleToInt64Bits(b.X7);

    [Theory]
    [InlineData(true, 14416 + 500)]
    [InlineData(false, 20000)]
    public void Parallel_And_Serial_Orbits_Are_Bit_Identical(bool minibrot, int cap)
    {
        var (re, im, zoom) = minibrot
            ? (S2032ApPeriodTests.MiniRe, S2032ApPeriodTests.MiniIm, S2032ApPeriodTests.MiniZoom)
            : (S2032ApPeriodTests.PathRe, S2032ApPeriodTests.PathIm, S2032ApPeriodTests.PathZoom);
        var par = Run(re, im, zoom, cap, parallel: true);
        var ser = Run(re, im, zoom, cap, parallel: false);
        Assert.Equal(ser.ReferenceOrbitLength, par.ReferenceOrbitLength);
        Assert.Equal(ser.ReferencePeriod, par.ReferencePeriod);
        for (int n = 0; n <= ser.ReferenceOrbitLength; n++)
        {
            var (sr, si) = ser.ReferenceOrbitPoint(n); var (pr, pi) = par.ReferenceOrbitPoint(n);
            Assert.True(Same(sr, pr) && Same(si, pi), $"orbit point {n} differs");
        }
        Assert.Equal(ser.IterationBuffer, par.IterationBuffer);
        Assert.Equal(ser.SmoothBuffer, par.SmoothBuffer);
        Assert.Equal(ser.ColorBuffer, par.ColorBuffer);
    }

    [Theory]
    [InlineData(1100)]
    [InlineData(3400)]
    [InlineData(10000)]
    public void Worker_Step_Equals_The_Serial_Operations(int bits)
    {
        var rng = new Random(bits);
        BigFloat Rand()
        {
            var sb = new System.Text.StringBuilder(rng.Next(2) == 0 ? "0." : "-0.");
            sb.Append((char)('1' + rng.Next(9)));
            for (int i = 0; i < bits * 0.30103; i++) sb.Append((char)('0' + rng.Next(10)));
            return BigFloat.Parse(sb.ToString(), bits);
        }
        using var w = new BigFloatStepWorkers();
        for (int k = 0; k < 20; k++)
        {
            BigFloat zr = Rand(), zi = Rand();
            w.Step(zr, zi, bits, out var zr2, out var zi2, out var zri, out var zrOd, out var ziOd);
            Assert.Equal(BigFloat.Mul(zr, zr, bits), zr2);
            Assert.Equal(BigFloat.Mul(zi, zi, bits), zi2);
            Assert.Equal(BigFloat.Mul(zr, zi, bits), zri);
            Assert.True(Same(zr.ToOD(), zrOd) && Same(zi.ToOD(), ziOd), "OD split differs");
        }
    }
}
