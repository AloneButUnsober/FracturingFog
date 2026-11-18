// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1427 — the deep-zoom render pile-up: the reference orbit could not be cancelled, and
// every anti-aliasing sample rebuilt it at its jittered centre. Checked on the bench-kit
// scenes (fractadyne bench-kit, WindySnowOwl, from rith's FF 0.7.1 report; see the
// bibliography; centres as S1664Scenes):
//   • Cancellation: a p951,094 OD orbit build with a cancelled token stops at its first
//     cancel poll, before the orbit outgrows its initial 65,536 entries (a full build
//     needs more), and the next frame at that centre is byte-identical to a fresh
//     calculator's (no half-built orbit is taken for the cache). #1852: counted in
//     iterations, not wall-clock time, which a cold JIT or a loaded machine broke.
//   • Reuse: a jittered sample recycles the unjittered orbit (one recycle hit, no new
//     build). Independent check: it agrees with a fresh calculator rendered at the
//     jittered centre (its own orbit, the path before #1427) on in / out everywhere and
//     on the iteration count of ≥ 99 % of pixels, and is closer to it than to the
//     unjittered frame (so the jitter really reaches every pixel's dc).
//   • An MSAA pass builds one orbit for all its samples; FF_AA_REUSE_ORBIT off (the
//     static switch) builds one per sample again.
//   • GPU (Vulkan; skipped without a device): the recycled sample stays on the GPU route,
//     and agrees with the fresh GPU frame at the jittered centre as above.

using System;
using System.Linq;
using System.Threading;
using FracturingFog.Models;
using FracturingFog.Rendering;
using FracturingFog.Rendering.Vulkan;
using Xunit;

namespace FracturingFog.Server.Tests;

[Collection(MandelbrotGpuStaticsCollection.Name)]   // writes AaReuseReferenceOrbit / UseGpuPerturbation
public sealed class S1427OrbitPileupTests(ITestOutputHelper output) : IDisposable
{
    private const int W = 96, H = 54;
    private readonly bool _savedReuse = MandelbrotCalculator.AaReuseReferenceOrbit;
    private readonly bool _savedPerturb = MandelbrotCalculator.UseGpuPerturbation;
    private readonly double _savedBudget = MandelbrotKernelSource.PerturbBudgetMs;

    public void Dispose()
    {
        MandelbrotCalculator.AaReuseReferenceOrbit = _savedReuse;
        MandelbrotCalculator.UseGpuPerturbation = _savedPerturb;
        MandelbrotKernelSource.PerturbBudgetMs = _savedBudget;
    }

    private static MandelbrotCalculator New(S1664Scenes.Scene s, IGpuKernel? k = null)
    {
        var c = new MandelbrotCalculator(W, H)
        {
            Quality = QualityPreset.Extreme,
            CenterX = s.X[0], CenterXLo = s.X[1], CenterX2 = s.X[2], CenterX3 = s.X[3], CenterX4 = s.X[4], CenterX5 = s.X[5], CenterX6 = s.X[6], CenterX7 = s.X[7],
            CenterY = s.Y[0], CenterYLo = s.Y[1], CenterY2 = s.Y[2], CenterY3 = s.Y[3], CenterY4 = s.Y[4], CenterY5 = s.Y[5], CenterY6 = s.Y[6], CenterY7 = s.Y[7],
            Zoom = S1664Scenes.FfZoom(s, W, H), MaxIterations = s.Iter, ColorMap = new HsvPalette(), GpuKernel = k,
        };
        return c;
    }

    private static double Scale(MandelbrotCalculator c) => 3.5 / Math.Max(c.Width, c.Height) / c.Zoom;

    private static (double Diff, int InOut) Compare(int[] a, int[] b, int maxIter)
    {
        int d = 0, io = 0;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i]) d++;
            if ((a[i] >= maxIter) != (b[i] >= maxIter)) io++;
        }
        return (d / (double)a.Length, io);
    }

    // ── Cancellation ─────────────────────────────────────────────────────────────

    // The orbit arrays start at this many entries and double as the build passes them
    // (#1828; MandelbrotCalculator.RefOrbitInitialCapacity).
    private const int InitialOrbitCapacity = 1 << 16;

    [Fact]
    public void A_Cancelled_Deep_Orbit_Build_Stops_And_Leaves_No_Stale_Cache()
    {
        var s = S1664Scenes.P951094;
        var fresh = New(s);
        fresh.Calculate(CancellationToken.None);
        output.WriteLine($"p951,094 full orbit: {fresh.ReferenceOrbitCapacity:N0} entries");
        Assert.True(fresh.ReferenceOrbitCapacity > InitialOrbitCapacity,
            "precondition: a full build outgrows the initial capacity");

        // #1852: a token cancelled before the frame, not after a delay. The build polls it
        // every 64 iterations (OD) and must stop there: it began (the arrays exist) and never
        // grew them. A build that ignored the token would run all 951,094 iterations.
        var c = New(s);
        Assert.Equal(0, c.ReferenceOrbitCapacity);
        Assert.ThrowsAny<OperationCanceledException>(() => c.Calculate(new CancellationToken(true)));
        output.WriteLine($"cancelled build: {c.ReferenceOrbitCapacity:N0} entries");
        Assert.Equal(InitialOrbitCapacity, c.ReferenceOrbitCapacity);

        // The same centre again: a full build, the same frame as a calculator that never
        // saw the cancel (a stale cache hit would render from a half-built orbit).
        c.Calculate(CancellationToken.None);
        Assert.True(c.IterationBuffer.AsSpan().SequenceEqual(fresh.IterationBuffer));
        Assert.True(c.ColorBuffer.AsSpan().SequenceEqual(fresh.ColorBuffer));
    }

    // A cancelled build overwrites the orbit arrays part-way. The cache key of the orbit
    // they held before must not survive it: back at that centre, the frame is rebuilt and
    // matches a fresh calculator's.
    [Fact]
    public void A_Cancelled_Build_Does_Not_Leave_The_Previous_Centres_Cache_Pointing_At_Its_Arrays()
    {
        // A = p448 (its own orbit), B = p951,094 (a long build). Not p953 as A: p951,094
        // lies inside p953's minibrot, so its first entries are p953's orbit again and a
        // half-written array would not show.
        var a = S1664Scenes.P448;
        var want = New(a);
        want.Calculate(CancellationToken.None);

        var c = New(a);
        c.Calculate(CancellationToken.None);          // p448's orbit cached
        Assert.True(c.IterationBuffer.AsSpan().SequenceEqual(want.IterationBuffer));
        var b = S1664Scenes.P951094;
        c.CenterX = b.X[0]; c.CenterXLo = b.X[1]; c.CenterX2 = b.X[2]; c.CenterX3 = b.X[3]; c.CenterX4 = b.X[4]; c.CenterX5 = b.X[5]; c.CenterX6 = b.X[6]; c.CenterX7 = b.X[7];
        c.CenterY = b.Y[0]; c.CenterYLo = b.Y[1]; c.CenterY2 = b.Y[2]; c.CenterY3 = b.Y[3]; c.CenterY4 = b.Y[4]; c.CenterY5 = b.Y[5]; c.CenterY6 = b.Y[6]; c.CenterY7 = b.Y[7];
        c.Zoom = S1664Scenes.FfZoom(b, W, H); c.MaxIterations = b.Iter;
        // p951,094's build, cut short at its first cancel poll (#1852: no timer). It has
        // already overwritten the first entries of p448's orbit.
        Assert.ThrowsAny<OperationCanceledException>(() => c.Calculate(new CancellationToken(true)));
        c.CenterX = a.X[0]; c.CenterXLo = a.X[1]; c.CenterX2 = a.X[2]; c.CenterX3 = a.X[3]; c.CenterX4 = a.X[4]; c.CenterX5 = a.X[5]; c.CenterX6 = a.X[6]; c.CenterX7 = a.X[7];
        c.CenterY = a.Y[0]; c.CenterYLo = a.Y[1]; c.CenterY2 = a.Y[2]; c.CenterY3 = a.Y[3]; c.CenterY4 = a.Y[4]; c.CenterY5 = a.Y[5]; c.CenterY6 = a.Y[6]; c.CenterY7 = a.Y[7];
        c.Zoom = S1664Scenes.FfZoom(a, W, H); c.MaxIterations = a.Iter;
        c.Calculate(CancellationToken.None);
        Assert.True(c.IterationBuffer.AsSpan().SequenceEqual(want.IterationBuffer));
        Assert.True(c.ColorBuffer.AsSpan().SequenceEqual(want.ColorBuffer));
    }

    // ── Reuse for the jittered samples ───────────────────────────────────────────

    private (MandelbrotCalculator Reused, MandelbrotCalculator Fresh, int[] Centre) Jittered(S1664Scenes.Scene s, IGpuKernel? k,
        double jx, double jy)
    {
        var c = New(s, k);
        c.Calculate(CancellationToken.None);
        var centre = (int[])c.IterationBuffer.Clone();
        long hits = c.RefRecycleHits;
        var orig = CentreLimbs.Capture(c);
        CentreJitter.Apply(c, in orig, jx, jy, Scale(c));
        CentreJitter.Calculate(c, CancellationToken.None);
        Assert.Equal(hits + 1, c.RefRecycleHits);   // the unjittered orbit served the sample

        var f = New(s, k);
        CentreJitter.Apply(f, in orig, jx, jy, Scale(f));
        f.Calculate(CancellationToken.None);   // its own orbit at the jittered centre
        Assert.Equal(0, f.RefRecycleHits);
        return (c, f, centre);
    }

    // p953 (1.3e53) is on the OD tier, p448 and path on QD. Not FF's "3E47 Test" view: it
    // sits at c ≈ −1.99, near the tip, where the orbit loses about a bit per iteration, so
    // after its 3,745-iteration reference no precision (QD, OD, a 360-bit fixed-point
    // check) pins the counts down. Measured there: the unjittered frame matches 360-bit
    // iteration on 14 / 24 samples, with or without acceleration; the reused sample 13 / 24.
    private static readonly S1664Scenes.Scene P953 = new("p953",
        "-0.028041054305504548|1.3292199503012176e-18|-5.275751227722853e-35|-4.203746728154813e-51|-4.269596053996866e-68|1.9389949392385534e-84|6.25819023760954e-101|-2.6646795788290983e-117"
            .Split('|').Select(double.Parse).ToArray(),
        "-0.6948927538996524|5.189489838305484e-17|5.567887477024055e-34|1.3771597857226995e-50|-5.482812918750436e-67|4.692321383846679e-84|-2.313052339214723e-100|2.1139231852108862e-116"
            .Split('|').Select(double.Parse).ToArray(),
        1.342765e53, 28590);

    [Theory]
    [InlineData("p448")]
    [InlineData("path")]
    [InlineData("p953")]
    public void A_Jittered_Sample_Reuses_The_Orbit_And_Matches_A_Fresh_Orbit(string name)
    {
        MandelbrotCalculator.AaReuseReferenceOrbit = true;
        var s = name switch { "p448" => S1664Scenes.P448, "path" => S1664Scenes.Path, _ => P953 };
        var (reused, fresh, centre) = Jittered(s, null, 0.37, -0.29);
        var (d, io) = Compare(reused.IterationBuffer, fresh.IterationBuffer, s.Iter);
        var (dc, _) = Compare(centre, fresh.IterationBuffer, s.Iter);
        output.WriteLine($"{name}: counts differ from the fresh jittered frame on {d:P2} ({io} in/out); the unjittered frame differs on {dc:P2}");
        Assert.Equal(0, io);
        Assert.True(d <= 0.01, $"{d:P2} of counts differ");
        Assert.True(dc > 0.05, "precondition: the jitter changes the frame");
        Assert.True(d < dc / 5, "the reused sample is not jittered");
    }

    [Fact]
    public void An_Msaa_Pass_Builds_One_Orbit_And_The_Switch_Restores_One_Per_Sample()
    {
        var s = S1664Scenes.P448;
        foreach (bool reuse in new[] { true, false })
        {
            MandelbrotCalculator.AaReuseReferenceOrbit = reuse;
            var c = New(s);
            c.Calculate(CancellationToken.None);
            var orig = CentreLimbs.Capture(c);
            long hits = c.RefRecycleHits;
            var st = Supersampling.AccumulateMandelbrot(c, 16, AdaptiveAa.Off, CancellationToken.None, histogramEq: 0);
            long used = c.RefRecycleHits - hits;
            output.WriteLine($"reuse {reuse}: {used} of {st.Samples - 1} jittered samples recycled the orbit");
            Assert.Equal(orig, CentreLimbs.Capture(c));   // the centre is restored
            Assert.Equal(reuse ? 15 : 0, used);
        }
    }

    // ── GPU ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void On_The_Gpu_A_Recycled_Sample_Stays_On_The_Gpu_And_Matches_A_Fresh_Orbit()
    {
        var k = VulkanComputeKernel.TryCreateWithOwnContext();
        if (k is null) Assert.Skip("no Vulkan device on this host");
        using (k)
        {
            MandelbrotCalculator.AaReuseReferenceOrbit = true;
            MandelbrotCalculator.UseGpuPerturbation = true;
            MandelbrotKernelSource.PerturbBudgetMs = 0;   // as S1630: no too-slow abort under load
            var s = S1664Scenes.P448;
            var (reused, fresh, centre) = Jittered(s, k, 0.37, -0.29);
            Assert.True(fresh.LastFrameUsedGpuPerturbation, "precondition: the fresh frame ran on the GPU");
            Assert.True(reused.LastFrameUsedGpuPerturbation, "the recycled sample left the GPU route");
            var (d, io) = Compare(reused.IterationBuffer, fresh.IterationBuffer, s.Iter);
            var (dc, _) = Compare(centre, fresh.IterationBuffer, s.Iter);
            output.WriteLine($"GPU p448: counts differ from the fresh jittered frame on {d:P2} ({io} in/out); the unjittered frame differs on {dc:P2}");
            Assert.Equal(0, io);
            Assert.True(d <= 0.02, $"{d:P2} of counts differ");
            Assert.True(d < dc / 5, "the recycled GPU sample is not jittered");
        }
    }
}
