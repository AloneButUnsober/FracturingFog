// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #633 (part of #626) — ColorGen billiard inputs. A ColorGen DSL program that
// references a chaotic-billiard outcome input (gateId / gateCount / bounceCount
// / maxBounces / pathLength) parses to an InterpretedBilliardColorMap (an
// IBilliardColorMap) which ChaoticBilliardCalculator routes per-pixel colour
// through. Mirrors the F15 orbit two-type split, but the billiard inputs come
// from the calculator's per-pixel outcome (no per-iteration sampling). These
// tests lock: the type split, colouring by each input, that non-billiard
// programs are unaffected, and that C# export is rejected (interpreter-only).

using FracturingFog.ColorGen;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class BilliardColorGenTests
{
    private static IBilliardColorMap Billiard(string src)
    {
        var map = InterpretedColorMap.TryCreate(src, null, out var err);
        Assert.Null(err);
        var bil = Assert.IsType<InterpretedBilliardColorMap>(map);
        return bil;
    }

    [Fact]
    public void Program_With_Billiard_Input_Becomes_Billiard_Map()
    {
        var map = InterpretedColorMap.TryCreate(
            "return hsv(gateId / gateCount, 1.0, 1.0);", null, out var err);
        Assert.Null(err);
        Assert.IsType<InterpretedBilliardColorMap>(map);
        Assert.IsAssignableFrom<IBilliardColorMap>(map);
    }

    [Fact]
    public void NonBilliard_Program_Stays_Plain_Interpreted()
    {
        var map = InterpretedColorMap.TryCreate(
            "return hsv(smooth * 0.01, 1.0, 1.0);", null, out var err);
        Assert.Null(err);
        Assert.NotNull(map);
        Assert.IsNotType<InterpretedBilliardColorMap>(map);
        Assert.False(map is IBilliardColorMap);
    }

    [Fact]
    public void Colours_By_GateId()
    {
        var bil = Billiard("return hsv(gateId / gateCount, 1.0, 1.0);");
        int g0 = bil.MapBilliard(0, 3, 5, 100, 0.2f);
        int g1 = bil.MapBilliard(1, 3, 5, 100, 0.2f);
        int g2 = bil.MapBilliard(2, 3, 5, 100, 0.2f);
        Assert.NotEqual(g0, g1);
        Assert.NotEqual(g1, g2);
    }

    [Fact]
    public void Trapped_Gate_Can_Be_Branched()
    {
        var bil = Billiard("return gateId < 0 ? rgb(0, 0, 0) : rgb(1, 0, 0);");
        Assert.Equal(unchecked((int)0xFF000000), bil.MapBilliard(-1, 3, 100, 100, 0f)); // trapped -> black
        Assert.Equal(unchecked((int)0xFFFF0000), bil.MapBilliard(0, 3, 5, 100, 0f));    // escaped -> red
    }

    [Fact]
    public void Colours_By_PathLength()
    {
        var bil = Billiard("return rgb(pathLength, 0, 0);");
        Assert.NotEqual(bil.MapBilliard(0, 3, 5, 100, 0.0f), bil.MapBilliard(0, 3, 5, 100, 1.0f));
    }

    [Fact]
    public void Colours_By_BounceCount()
    {
        var bil = Billiard("return hsv(bounceCount / maxBounces, 1.0, 1.0);");
        Assert.NotEqual(bil.MapBilliard(0, 3, 1, 100, 0f), bil.MapBilliard(0, 3, 90, 100, 0f));
    }

    [Fact]
    public void Reparse_RoundTrips_To_Billiard_Map()
    {
        const string src = "let h = gateId / gateCount;\nreturn hsv(h, 1.0, 1.0);";
        var a = Billiard(src);
        var b = Billiard(src);   // theme persists as source; re-create is the round-trip
        Assert.Equal(a.MapBilliard(1, 3, 5, 100, 0.5f), b.MapBilliard(1, 3, 5, 100, 0.5f));
    }

    [Fact]
    public void CSharp_Export_Rejects_Billiard_Themes()
    {
        var res = ColorGenApi.Generate("return hsv(gateId / gateCount, 1.0, 1.0);", "MyBilliard");
        Assert.False(res.Ok);
        Assert.Contains("billiard", res.Error!, System.StringComparison.OrdinalIgnoreCase);
    }
}
