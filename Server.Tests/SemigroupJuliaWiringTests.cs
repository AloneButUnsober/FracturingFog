// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using FracturingFog;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

/// <summary>#918 SG3 — semigroup-Julia (faithful implosion) parameter plumbing: clone + region
/// round-trip. The render correctness is covered by the SG1 Lavaurs-engine tests, the S7
/// word-tree tests, and the end-to-end spike; the calculator's static Fatou-table build is too
/// heavy for a per-run unit test, so this guards only the wiring.</summary>
public sealed class SemigroupJuliaWiringTests
{
    [Fact]
    public void Clone_PreservesSemigroupParams()
    {
        var p = new FractalParameters
        {
            SemigroupAlpha = 0.37,
            SemigroupWordDepth = 55,
            SemigroupBeam = 72,
            SemigroupEscapeRadius = 8.5,
        };
        var c = p.Clone();
        Assert.Equal(0.37, c.SemigroupAlpha);
        Assert.Equal(55, c.SemigroupWordDepth);
        Assert.Equal(72, c.SemigroupBeam);
        Assert.Equal(8.5, c.SemigroupEscapeRadius);
    }

    [Fact]
    public void Region_RoundTripsSemigroupParams_AndOmitsDefaults()
    {
        var p = new FractalParameters
        {
            SemigroupAlpha = 0.25,
            SemigroupWordDepth = 60,
            SemigroupBeam = 40,          // != default 48
            SemigroupEscapeRadius = 6.0, // == default → omitted
        };
        var snap = RegionFractalParams.Snapshot(FractalType.SemigroupJulia, p);
        Assert.NotNull(snap);
        Assert.Equal(0.25, snap!.SemigroupAlpha);
        Assert.Equal(60, snap.SemigroupWordDepth);
        Assert.Equal(40, snap.SemigroupBeam);
        Assert.Null(snap.SemigroupEscapeRadius);   // default omitted

        var restored = new FractalParameters();
        snap.ApplyTo(restored);
        Assert.Equal(0.25, restored.SemigroupAlpha);
        Assert.Equal(60, restored.SemigroupWordDepth);
        Assert.Equal(40, restored.SemigroupBeam);
        Assert.Equal(6.0, restored.SemigroupEscapeRadius);  // untouched → stays default

        // default α is omitted too.
        var def = RegionFractalParams.Snapshot(FractalType.SemigroupJulia, new FractalParameters());
        Assert.Null(def!.SemigroupAlpha);

        // a non-semigroup region carries no semigroup fields.
        var plain = RegionFractalParams.Snapshot(FractalType.Julia, p);
        Assert.Null(plain?.SemigroupAlpha);
    }

    [Fact]
    public void SemigroupJulia_IsAnimatable_OnAlpha()
    {
        var list = FracturingFog.Abstractions.Animation.FractalAnimatableParamsMap.For(FractalType.SemigroupJulia);
        Assert.Contains(list, d => d.ParamName == "SemigroupAlpha");
    }
}
