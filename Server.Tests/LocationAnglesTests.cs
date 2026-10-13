// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Diagnostics;
using System.Linq;

using FracturingFog.Abstractions.Explore;

using Xunit;

namespace FracturingFog.Server.Tests;

// Finder S4b (#1237): location → angles. Round trip: angle → land (S4) →
// the landed component / point's angles (outward traces, each verified by
// landing) → the original angle is among them, and a component gets a pair.
public sealed class LocationAnglesTests
{
    private readonly ITestOutputHelper _out;
    public LocationAnglesTests(ITestOutputHelper o) => _out = o;

    private static ExternalAngle A(string s) { Assert.True(ExternalAngle.TryParse(s, out var a, out var e), e); return a!; }

    [Theory]
    [InlineData("1/3", "2/3")]
    [InlineData("1/7", "2/7")]          // rabbit
    [InlineData("3/7", "4/7")]          // airplane
    [InlineData("9/31", "10/31")]       // the 2/5 bulb
    [InlineData("6/15", "9/15")]        // period-4 bulb
    public void Component_RoundTrip(string lo, string hi)
    {
        var land = ExternalRay.Land(A(lo));
        Assert.Equal(RayLandingStatus.Component, land.Status);
        var sw = Stopwatch.StartNew();
        var angles = LocationAngles.ComponentAngles(land.Nucleus);
        _out.WriteLine($"{lo}: {string.Join(" ", angles)} ({sw.ElapsedMilliseconds} ms)");
        Assert.Equal(new[] { A(lo), A(hi) }, angles.ToArray());
    }

    [Theory]
    [InlineData(12, 1)]
    [InlineData(16, 2)]
    [InlineData(20, 3)]
    public void RandomComponents_RoundTrip(int period, int seed)
    {
        var rng = new Random(seed);
        for (int t = 0; t < 2; t++)
        {
            ExternalAngle a;
            do
            {
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < period; i++) sb.Append(rng.Next(2) == 0 ? '0' : '1');
                a = ExternalAngle.FromBits("", sb.ToString());
            } while (a.Period != period);
            var land = ExternalRay.Land(a);
            Assert.Equal(RayLandingStatus.Component, land.Status);
            var sw = Stopwatch.StartNew();
            var angles = LocationAngles.ComponentAngles(land.Nucleus);
            _out.WriteLine($"{a}: {string.Join(" ", angles)} ({sw.ElapsedMilliseconds} ms, |size| {land.Nucleus.Size.Magnitude:G3})");
            Assert.Equal(2, angles.Count);
            Assert.Contains(a, angles);
            // Both angles name the same component: the same internal address.
            Assert.Equal(angles[0].InternalAddress(64), angles[1].InternalAddress(64));
        }
    }

    [Theory]
    [InlineData(".0(01)")]          // c = i
    [InlineData(".1(0)")]           // c = −2
    [InlineData(".001(010)")]
    [InlineData(".0011(01)")]
    public void Misiurewicz_RoundTrip(string angle)
    {
        var a = A(angle);
        var land = ExternalRay.Land(a);
        Assert.Equal(RayLandingStatus.Misiurewicz, land.Status);
        var sw = Stopwatch.StartNew();
        var angles = LocationAngles.MisiurewiczAngles(land.Misiurewicz, land.ContextRadius);
        _out.WriteLine($"{angle} → M({land.Misiurewicz.Preperiod},{land.Misiurewicz.Period}): {string.Join(" ", angles)} ({sw.ElapsedMilliseconds} ms)");
        Assert.Contains(a, angles);
    }

    [Fact]
    public void Root_OfTheRabbit_IsWhereTheMultiplierIsOne()
    {
        var land = ExternalRay.Land(A("1/7"));
        Assert.True(LocationAngles.Root(land.Nucleus.Nucleus, 3, out var off));
        // Independent: the closed form of the cardioid's 1/3 point.
        var c = new System.Numerics.Complex(land.Nucleus.Nucleus.Re.X0 + off.Real, land.Nucleus.Nucleus.Im.X0 + off.Imaginary);
        // The rabbit's root is the 1/3 point of the main cardioid: c = e^{2πi/3}/2 − e^{4πi/3}/4.
        var w = System.Numerics.Complex.FromPolarCoordinates(1, 2 * Math.PI / 3);
        var expect = w / 2 - w * w / 4;
        // The root is a double root (parabolic), solved to ~1e-9 relative.
        Assert.True((c - expect).Magnitude < 1e-9, $"root {c}, expected {expect}");
    }
}
