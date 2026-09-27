// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using FracturingFog.Rendering;
using Xunit;

namespace FracturingFog.Server.Tests;

/// <summary>
/// #988 — the slideshow region cross-fade blends a frozen outgoing frame over
/// each live upload with <see cref="FractalRenderHost.BlendOutgoing"/>. Locks
/// the endpoints (weight 0 = live frame untouched, 1 = outgoing), a midpoint,
/// opaque output and a partitioned run over a buffer larger than one chunk.
/// </summary>
public sealed class TransitionOverlayTests
{
    private const uint Live = 0xFF204060u;
    private const uint Old = 0xFFA0C0E0u;

    private static uint[] Fill(int n, uint v)
    {
        var a = new uint[n];
        System.Array.Fill(a, v);
        return a;
    }

    [Fact]
    public void Weight_zero_leaves_live_frame()
    {
        var dst = Fill(16, Live);
        FractalRenderHost.BlendOutgoing(dst, Fill(16, Old), 16, 0f);
        Assert.All(dst, p => Assert.Equal(Live, p));
    }

    [Fact]
    public void Weight_one_shows_outgoing_frame()
    {
        var dst = Fill(16, Live);
        FractalRenderHost.BlendOutgoing(dst, Fill(16, Old), 16, 1f);
        Assert.All(dst, p => Assert.Equal(Old, p));
    }

    [Fact]
    public void Half_weight_is_channel_midpoint_and_opaque()
    {
        var dst = Fill(16, Live & 0x00FFFFFFu);   // alpha 0 in → opaque out
        FractalRenderHost.BlendOutgoing(dst, Fill(16, Old), 16, 0.5f);
        // (0x20+0xA0)/2=0x60, (0x40+0xC0)/2=0x80, (0x60+0xE0)/2=0xA0
        Assert.All(dst, p => Assert.Equal(0xFF6080A0u, p));
    }

    [Fact]
    public void Every_pixel_of_a_large_buffer_is_blended()
    {
        const int n = 1920 * 1080 + 7;   // not divisible by the partition count
        var dst = Fill(n, Live);
        FractalRenderHost.BlendOutgoing(dst, Fill(n, Old), n, 1f);
        for (int i = 0; i < n; i++) Assert.Equal(Old, dst[i]);
    }
}
