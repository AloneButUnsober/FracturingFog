// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using FracturingFog.Abstractions.Imaging;
using Xunit;

namespace FracturingFog.Server.Tests;

// #806 — Ken-Burns image-space resampler for non-spatial static-hold legs.
public sealed class ImageResamplerTests
{
    // A full-frame resample (sx=sy=0, viewW=w, viewH=h) reproduces the source
    // pixel-for-pixel — so Ken-Burns frame 0 matches the held frame (no jump).
    [Fact]
    public void FullFrameResample_IsIdentity()
    {
        const int w = 4, h = 3;
        var src = new uint[w * h];
        for (int i = 0; i < src.Length; i++) src[i] = (uint)(0xFF000000 | (i * 1234567));
        var dst = new uint[w * h];

        ImageResampler.ResampleRectBilinear(src, w, h, 0, 0, w, h, dst);

        Assert.Equal(src, dst);
    }

    // Bilerp at the four corners returns the corner pixels exactly.
    [Theory]
    [InlineData(0.0, 0.0, 0x10203040u)]
    [InlineData(1.0, 0.0, 0x11213141u)]
    [InlineData(0.0, 1.0, 0x12223242u)]
    [InlineData(1.0, 1.0, 0x13233343u)]
    public void Bilerp_AtCorners_ReturnsCorner(double wx, double wy, uint expected)
        => Assert.Equal(expected,
            ImageResampler.BilerpPacked(0x10203040u, 0x11213141u, 0x12223242u, 0x13233343u, wx, wy));

    // Bilerp at the center averages all four lanes.
    [Fact]
    public void Bilerp_Center_AveragesLanes()
    {
        // Lane values 0 and 100 → center = 50 in every byte lane.
        uint lo = 0x00000000u, hi = 0x64646464u; // 100 per lane
        uint mid = ImageResampler.BilerpPacked(lo, hi, hi, lo, 0.5, 0.5);
        for (int shift = 0; shift < 32; shift += 8)
            Assert.Equal(50u, (mid >> shift) & 0xFF);
    }

    // A zoomed-in sub-rect samples interior detail: a 2×1 source [0, 255] sampled
    // in its right half is brighter than sampling the left half.
    [Fact]
    public void ZoomedSubRect_SamplesInteriorRegion()
    {
        const int w = 2, h = 1;
        var src = new uint[] { 0x00000000u, 0x00FF0000u }; // dark, bright (one lane)
        var left = new uint[w * h];
        var right = new uint[w * h];

        // Left half [0 .. 1.0 wide starting at 0], right half starting at 1.0.
        ImageResampler.ResampleRectBilinear(src, w, h, 0.0, 0.0, 1.0, 1.0, left);
        ImageResampler.ResampleRectBilinear(src, w, h, 1.0, 0.0, 1.0, 1.0, right);

        uint LaneR(uint px) => (px >> 16) & 0xFF;
        Assert.True(LaneR(right[0]) > LaneR(left[0]),
            $"right {LaneR(right[0])} not brighter than left {LaneR(left[0])}");
    }

    // Out-of-range sample coords clamp to the edge rather than throwing / wrapping.
    [Fact]
    public void OutOfRange_ClampsToEdge()
    {
        const int w = 2, h = 2;
        var src = new uint[] { 0x01u, 0x02u, 0x03u, 0x04u };
        var dst = new uint[w * h];
        // Way off the right/bottom edge — every sample clamps to the last pixel.
        ImageResampler.ResampleRectBilinear(src, w, h, 100.0, 100.0, 0.001, 0.001, dst);
        foreach (var px in dst) Assert.Equal(0x04u, px);
    }
}
