// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System.Linq;

using FracturingFog.Abstractions.Explore;

using Xunit;

namespace FracturingFog.Server.Tests;

// Finder S4b (#1237): internal address → angles. Checked against textbook
// pairs, and independently against S4's angle → internal address (the
// Lau–Schleicher ρ function), which knows nothing of wakes or tuning.
public sealed class AddressAnglesTests
{
    private readonly ITestOutputHelper _out;
    public AddressAnglesTests(ITestOutputHelper o) => _out = o;

    private static AddressResult Resolve(string address)
    {
        Assert.True(AddressAngles.TryParse(address, out var steps, out var err), err);
        var r = AddressAngles.ToAngles(steps, out err);
        Assert.True(r != null, err);
        return r!;
    }

    [Theory]
    [InlineData("1 → 2", "1/3", "2/3")]
    [InlineData("1 -> 3", "1/7", "2/7")]           // the rabbit (1/3 by default)
    [InlineData("1_2/3 -> 3", "5/7", "6/7")]       // its conjugate
    [InlineData("1 → 2 → 3", "3/7", "4/7")]        // the airplane
    [InlineData("1 → 2 → 4", "2/5", "3/5")]        // 6/15, 9/15
    [InlineData("1_2/5 → 5", "9/31", "10/31")]
    public void TextbookPairs(string address, string lo, string hi)
    {
        var r = Resolve(address);
        ExternalAngle.TryParse(lo, out var a, out _);
        ExternalAngle.TryParse(hi, out var b, out _);
        Assert.Equal(a, r.Lower);
        Assert.Equal(b, r.Upper);
    }

    [Theory]
    [InlineData("1 → 2 → 4 → 8")]
    [InlineData("1 → 3 → 4")]
    [InlineData("1 → 2 → 5 → 11")]
    [InlineData("1 → 3 → 7 → 15")]
    [InlineData("1 → 2 → 3 → 7 → 15 → 31")]
    public void AngleToAddress_RoundTrips(string address)
    {
        var r = Resolve(address);
        Assert.True(AddressAngles.TryParse(address, out var steps, out _));
        var want = steps.Select(st => st.Period).ToArray();
        foreach (var a in new[] { r.Lower, r.Upper })
        {
            var got = a.InternalAddress(64).ToArray();
            _out.WriteLine($"{address}: {a} → {string.Join("→", got)} (resolved {AddressAngles.Format(r.Resolved)})");
            Assert.Equal(want, got);
        }
        Assert.Equal(want[^1], r.Lower.Period);
    }

    // Addresses of random real angles (S4's ρ function), resolved back to a
    // component whose own angles give the same address.
    [Theory]
    [InlineData(8, 1)]
    [InlineData(14, 2)]
    [InlineData(20, 3)]
    [InlineData(28, 4)]
    public void RandomAngles_AddressRoundTrip(int maxPeriod, int seed)
    {
        var rng = new System.Random(seed);
        for (int t = 0; t < 40; t++)
        {
            int n = rng.Next(2, maxPeriod + 1);
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < n; i++) sb.Append(rng.Next(2) == 0 ? '0' : '1');
            var theta = ExternalAngle.FromBits("", sb.ToString());
            if (theta.Period < 2) continue;
            var addr = theta.InternalAddress(64).ToArray();
            var steps = addr.Select(p => new AddressStep(p)).ToList();
            var r = AddressAngles.ToAngles(steps, out var err);
            Assert.True(r != null, $"{theta} → {string.Join("→", addr)}: {err}");
            Assert.Equal(addr, r!.Lower.InternalAddress(64).ToArray());
            Assert.Equal(addr, r.Upper.InternalAddress(64).ToArray());
            Assert.Equal(addr[^1], r.Lower.Period);
        }
    }

    // Geometric check, independent of the combinatorics: the computed angle
    // lands (S4) on a component whose period S1's ball scan confirms.
    [Theory]
    [InlineData("1 → 2 → 5 → 11")]
    [InlineData("1 → 3 → 7 → 15")]
    [InlineData("1_2/5 → 5 → 11")]
    public void ResolvedAngles_LandOnTheRightPeriod(string address)
    {
        var r = Resolve(address);
        int q = r.Resolved[^1].Period;
        foreach (var a in new[] { r.Lower, r.Upper })
        {
            var land = ExternalRay.Land(a);
            Assert.Equal(RayLandingStatus.Component, land.Status);
            Assert.Equal(q, land.Nucleus.Period);
            Assert.Equal(q, PeriodDetector.Detect(land.Nucleus.Nucleus, 0.01 * land.Nucleus.Size.Magnitude).Period);
        }
        // Both angles land on the same component.
        var n0 = ExternalRay.Land(r.Lower).Nucleus;
        var n1 = ExternalRay.Land(r.Upper).Nucleus;
        Assert.True(ExternalRay.Dist(n0.Nucleus, n1.Nucleus) <= 0.01 * n0.Size.Magnitude);
    }

    // Smoke-test regression (#1237): a period-53 minibrot near
    // 0.4068 − 0.5678i, zoom 7.5e13. Its address runs 16 steps down an
    // antenna, where enumerating every period-q angle per step blew up
    // (~8× per step) and stopped at 25 → 29.
    private const string Smoke53 = "1 → 4 → 7 → 8 → 12 → 13 → 17 → 21 → 25 → 29 → 33 → 37 → 41 → 45 → 46 → 53";
    private const string Smoke53Lo = ".(11101011111011110111011101110111011101110111011110110)";
    private const string Smoke53Hi = ".(11101011111100111011101110111011101110111011101110101)";

    [Fact]
    public void LongAntennaAddress_Resolves()
    {
        var r = Resolve(Smoke53);
        Assert.True(AddressAngles.TryParse(Smoke53, out var steps, out _));
        var want = steps.Select(st => st.Period).ToArray();
        Assert.Equal(want, r.Lower.InternalAddress(64).ToArray());
        Assert.Equal(want, r.Upper.InternalAddress(64).ToArray());
        Assert.True(r.Ambiguous);   // 1 → 4 has two conjugates (1/4, 3/4)
    }

    [Fact]
    public void LongAntennaAddress_Angled_GivesTheFoundPair()
    {
        // The minibrot lies in the cardioid's 3/4 limb.
        var r = Resolve("1_3/4" + Smoke53[1..]);
        Assert.Equal(A(Smoke53Lo), r.Lower);
        Assert.Equal(A(Smoke53Hi), r.Upper);
    }

    [Fact]
    public void AngledAddressOf_PastesBackToTheSameComponent()
    {
        var a = A(Smoke53Lo);
        var angled = AddressAngles.AngledAddressOf(a);
        Assert.NotNull(angled);
        _out.WriteLine(AddressAngles.Format(angled!));
        Assert.True(AddressAngles.TryParse(AddressAngles.Format(angled!), out var steps, out var err), err);
        var r = AddressAngles.ToAngles(steps, out err);
        Assert.True(r != null, err);
        Assert.Equal(A(Smoke53Lo), r!.Lower);
        Assert.Equal(A(Smoke53Hi), r.Upper);
        Assert.False(r.Ambiguous);
    }

    private static ExternalAngle A(string s) { Assert.True(ExternalAngle.TryParse(s, out var a, out var e), e); return a!; }

    [Theory]
    [InlineData("1 → 2 → 2")]
    [InlineData("2 → 3")]
    [InlineData("1_1/2 → 3")]       // 3 is not in the 1/2 limb of the cardioid
    public void Invalid(string address)
    {
        if (!AddressAngles.TryParse(address, out var steps, out _)) return;
        Assert.Null(AddressAngles.ToAngles(steps, out _));
    }
}
