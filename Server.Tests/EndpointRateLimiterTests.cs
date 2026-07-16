// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System.Net;
using FracturingFog.Server.Guard;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class EndpointRateLimiterTests
{
    [Fact]
    public void Disabled_When_PerMinute_NonPositive()
    {
        var lim = new EndpointRateLimiter(perMinute: 0, burst: 5);
        Assert.False(lim.Enabled);
        // A disabled limiter accepts everything without consuming tokens.
        var ep = new IPEndPoint(IPAddress.Loopback, 47823);
        for (int i = 0; i < 100; i++)
            Assert.True(lim.TryAccept(ep));
    }

    [Fact]
    public void Burst_Exhaustion_Then_Reject()
    {
        var lim = new EndpointRateLimiter(perMinute: 60, burst: 3);
        var ep = new IPEndPoint(IPAddress.Parse("10.1.2.3"), 1234);
        Assert.True(lim.TryAccept(ep));
        Assert.True(lim.TryAccept(ep));
        Assert.True(lim.TryAccept(ep));
        // 4th request inside the same instant — bucket empty.
        Assert.False(lim.TryAccept(ep));
    }

    [Fact]
    public void Buckets_Are_Per_IP()
    {
        var lim = new EndpointRateLimiter(perMinute: 60, burst: 2);
        var ip1 = new IPEndPoint(IPAddress.Parse("10.0.0.1"), 1234);
        var ip2 = new IPEndPoint(IPAddress.Parse("10.0.0.2"), 1234);
        Assert.True(lim.TryAccept(ip1));
        Assert.True(lim.TryAccept(ip1));
        Assert.False(lim.TryAccept(ip1));   // ip1 spent
        Assert.True(lim.TryAccept(ip2));    // ip2 has its own bucket
        Assert.True(lim.TryAccept(ip2));
        Assert.False(lim.TryAccept(ip2));
    }

    [Fact]
    public void NullRemote_TreatedAsSingleBucket()
    {
        var lim = new EndpointRateLimiter(perMinute: 60, burst: 1);
        Assert.True(lim.TryAccept(remote: null));
        Assert.False(lim.TryAccept(remote: null));
    }
}
