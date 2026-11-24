// #2012 — the GPU deep too-slow guard: decide from bands 0 and 1, not band 0 alone.
// Cases from rith's FF 0.7.2 bench (RTX 3080, 4K, 270 bands, 3000 ms budget).

using FracturingFog.Rendering;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S2012PerturbTooSlowTests
{
    private const double Budget = 3000;

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    [InlineData(3, 2)]
    [InlineData(270, 2)]
    public void Timed_Bands(int bandCount, int timed)
        => Assert.Equal(timed, MandelbrotKernelSource.PerturbTimedBands(bandCount));

    [Fact]
    public void First_Run_Driver_Compile_In_Band0_Does_Not_Abort_When_Band1_Is_Fast()
    {
        // band0 = 73.5 ms (the old rule: 73.5 × 270 = 19,845 ms > 3000 → abort).
        Assert.True(MandelbrotKernelSource.PerturbTooSlow(73.5, 270, Budget));
        Assert.False(MandelbrotKernelSource.PerturbTooSlowAfter(0, 73.5, 0, 73.5, 270, Budget));   // waits for band 1
        // band 1 warm at ~5 ms: 78.5 + 5 × 268 = 1,418.5 ms.
        Assert.False(MandelbrotKernelSource.PerturbTooSlowAfter(1, 73.5, 5.0, 78.5, 270, Budget));
    }

    [Fact]
    public void One_Noisy_Band_Does_Not_Abort_When_The_Other_Is_Fast()
    {
        // band0 = 25.6 ms (old rule: 6,912 ms > 3000) on a frame that took ~1.4 s.
        Assert.True(MandelbrotKernelSource.PerturbTooSlow(25.6, 270, Budget));
        Assert.False(MandelbrotKernelSource.PerturbTooSlowAfter(1, 25.6, 5.2, 30.8, 270, Budget));
        // Either band can be the noisy one: the faster of the two is the rate.
        Assert.False(MandelbrotKernelSource.PerturbTooSlowAfter(1, 5.2, 25.6, 30.8, 270, Budget));
    }

    [Fact]
    public void A_GPU_That_Is_Really_Too_Slow_Still_Aborts_After_Band1()
    {
        // Weak fp64 (GT 710-like): both bands ~60 ms → 120 + 60 × 268 ≫ 3000.
        Assert.False(MandelbrotKernelSource.PerturbTooSlowAfter(0, 60, 0, 60, 270, Budget));
        Assert.True(MandelbrotKernelSource.PerturbTooSlowAfter(1, 60, 61, 121, 270, Budget));
    }

    [Fact]
    public void Time_Already_Spent_Past_The_Budget_Aborts_At_Once()
        => Assert.True(MandelbrotKernelSource.PerturbTooSlowAfter(0, 3500, 0, 3500, 270, Budget));

    [Fact]
    public void Two_Bands_Keep_The_Band0_Rule()
    {
        // No band 1 is timed (it is the last one): band0 × bandCount decides, as before.
        Assert.True(MandelbrotKernelSource.PerturbTooSlowAfter(0, 1600, 0, 1600, 2, Budget));
        Assert.False(MandelbrotKernelSource.PerturbTooSlowAfter(0, 1400, 0, 1400, 2, Budget));
    }

    [Fact]
    public void Budget_Zero_Never_Aborts()
    {
        Assert.False(MandelbrotKernelSource.PerturbTooSlowAfter(0, 1e6, 0, 1e6, 270, 0));
        Assert.False(MandelbrotKernelSource.PerturbTooSlowAfter(1, 1e6, 1e6, 2e6, 270, -1));
    }

    [Fact]
    public void Message_Carries_The_Marker_And_Both_Bands()
    {
        string m = MandelbrotKernelSource.PerturbTooSlowMessage(60, 61, 2, 270, " (test)");
        Assert.StartsWith(MandelbrotKernelSource.PerturbTooSlowMarker, m);
        Assert.Contains("band0=60.0ms band1=61.0ms × 270 bands", m);
        Assert.DoesNotContain("band1", MandelbrotKernelSource.PerturbTooSlowMessage(60, 0, 1, 2, ""));
    }
}
