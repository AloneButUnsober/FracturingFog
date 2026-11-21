// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1964 — where Nova's double loop stops matching direct BigFloat iteration, and the engine's cost there.

using System;
using System.Threading;
using FracturingFog.FFMath;
using FracturingFog.Perturbation;
using Xunit;

using static FracturingFog.Server.Tests.S1909ConjR2Tests;
using static FracturingFog.Server.Tests.S1921DeepFxTests;
using static FracturingFog.Server.Tests.S1959NovaDeepTests;

namespace FracturingFog.Server.Tests;

public sealed class S1964NovaSwitchTests(ITestOutputHelper output)
{
    // Julia c = −0.5: a point of its repelling 2-cycle (S1959's Julia view).
    internal static ExactCentre JuliaCycle(int zl)
    {
        int p = BigFloat.PrecisionForZoom(zl) + 64;
        var (zr, zi) = Newton((a, b) =>
        {
            var (gr, gi) = NovaStep(a, b, BigFloat.FromDouble(-0.5, p), BigFloat.Zero(p), p);
            var (fr, fi) = NovaStep(gr, gi, BigFloat.FromDouble(-0.5, p), BigFloat.Zero(p), p);
            return (BigFloat.Sub(fr, a, p), BigFloat.Sub(fi, b, p));
        }, -0.4730955739635228, 0.12126155044701115, p, (long)(zl * 3.33) + 40);
        return new ExactCentre(zr, zi);
    }

    internal static View ViewFor(int which, int zl)
        => ViewAt(which < 2 ? Misiurewicz(ParamViews[which].Re, ParamViews[which].Im, zl) : JuliaCycle(zl), zl);

    internal static NewtonCalculator Render(View v, bool julia, bool engine, int w = W, int h = H)
    {
        var c = Nova(w, h, julia);
        c.AllowPerturbation = engine;
        c.DeepZoomFrom = 0;
        c.SetDeepCentre(v.Centre.ToDeep(), v.Centre);
        c.SetZoomLog10(v.Zl);
        c.Calculate(CancellationToken.None);
        return c;
    }

    // Measured (48×32, every third pixel against direct BigFloat iteration; the three views): the double loop
    // matched 176/176 at 1e3..1e6, ≥ 175/176 at 1e7, and fell to 152 / 170 / 150 at 1e11; the engine matched
    // 176/176 at every zoom, at ~5–7× the double loop's time.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void At_The_Switch_The_Engine_Matches_And_The_Double_Loop_Below_It(int which)
    {
        Assert.Equal(1e7, NewtonCalculator.DeepZoomThreshold);
        bool julia = which == 2;
        var at = ViewFor(which, 7);
        var e = Render(at, julia, engine: true);
        Assert.Equal("PT", e.LastPrecisionLabel);
        var a = Agreement(e, at, julia);
        var below = ViewFor(which, 6);
        var b = Agreement(Render(below, julia, engine: false), below, julia);
        var deep = ViewFor(which, 11);
        var c = Agreement(Render(deep, julia, engine: false), deep, julia);
        output.WriteLine($"view {which}: engine 1e7 {a.Eq}/{a.Total}; double loop 1e6 {b.Eq}/{b.Total}, 1e11 {c.Eq}/{c.Total}");
        Assert.True(a.Seen >= 5 && a.Stable >= a.Total * 0.95, "precondition: structure, above the chaotic floor");
        Assert.Equal(a.Total, a.Eq);
        Assert.Equal(b.Total, b.Eq);
        Assert.True(c.Eq < c.Total, "precondition: the double loop drifts at the old switch (1e11)");
    }

    [Fact]
    public void The_Calculator_Switches_At_1e7()
    {
        var v = ViewFor(0, 7);
        NewtonCalculator Run(double zoom)
        {
            var c = Nova(W, H);
            c.SetDeepCentre(v.Centre.ToDeep(), v.Centre);
            c.Zoom = zoom;
            c.Calculate(CancellationToken.None);
            return c;
        }
        Assert.Equal("PT", Run(1e7).LastPrecisionLabel);
        Assert.Equal("SP", Run(0.99e7).LastPrecisionLabel);
    }
}
