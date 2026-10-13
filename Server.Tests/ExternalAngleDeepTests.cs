// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Diagnostics;
using System.Text;

using FracturingFog.Abstractions.Explore;

using Xunit;

namespace FracturingFog.Server.Tests;

// Finder S4b (#1237): deep external angles. The perturbed ray trace must land
// long periodic angles, and S1's ball scan (independent of the ray and of
// Newton) must re-confirm the landed component's period.
public sealed class ExternalAngleDeepTests
{
    private readonly ITestOutputHelper _out;
    public ExternalAngleDeepTests(ITestOutputHelper o) => _out = o;

    // A random bit string of exact (minimal) period n.
    private static string RandomPeriodic(Random rng, int n)
    {
        while (true)
        {
            var sb = new StringBuilder(n);
            for (int i = 0; i < n; i++) sb.Append(rng.Next(2) == 0 ? '0' : '1');
            string s = sb.ToString();
            bool minimal = true;
            for (int q = 1; q < n && minimal; q++)
                if (n % q == 0 && s == string.Concat(System.Linq.Enumerable.Repeat(s[..q], n / q))) minimal = false;
            if (minimal && s.Contains('0') && s.Contains('1')) return s;
        }
    }

    [Theory]
    [InlineData(24, 11)]
    [InlineData(32, 7)]
    [InlineData(48, 5)]
    [InlineData(64, 3)]
    public void LongPeriodicAngles_Land_AndS1ConfirmsThePeriod(int bits, int seed)
    {
        var rng = new Random(seed);
        int landed = 0;
        const int Trials = 3;
        for (int t = 0; t < Trials; t++)
        {
            var angle = ExternalAngle.FromBits("", RandomPeriodic(rng, bits));
            var sw = Stopwatch.StartNew();
            var r = ExternalRay.Land(angle);
            sw.Stop();
            if (r.Status != RayLandingStatus.Component)
            {
                _out.WriteLine($"{angle}: {r.Status} {r.Detail} ({sw.ElapsedMilliseconds} ms)");
                continue;
            }
            var n = r.Nucleus;
            int s1 = PeriodDetector.Detect(n.Nucleus, 0.01 * n.Size.Magnitude).Period;
            _out.WriteLine($"{angle}: period {n.Period}, |size| {n.Size.Magnitude:G3}, S1 {s1} ({sw.ElapsedMilliseconds} ms)");
            Assert.Equal(bits, n.Period);
            Assert.Equal(bits, s1);
            landed++;
        }
        Assert.Equal(Trials, landed);
    }
}
