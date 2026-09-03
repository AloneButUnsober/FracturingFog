// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Roadmap slice S1 (3D-Rendering-Roadmap.md, #389 / #398) — the first consumer of
// the motion-vector AOV: per-pixel vector motion blur. Contract: strength 0 / null
// or zero motion is the identity; a horizontal motion vector smears a vertical edge
// across X; a vertical motion vector leaves that same vertical edge sharp (it blurs
// along Y, perpendicular to the edge); deterministic.

using FracturingFog.Imaging;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class MotionBlurFromVectorsTests
{
    // A vertical black|white edge at x = w/2.
    private static uint[] VerticalEdge(int w, int h)
    {
        var b = new uint[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                b[y * w + x] = x < w / 2 ? 0xFF000000u : 0xFFFFFFFFu;
        return b;
    }

    private static float[] UniformMotion(int w, int h, float du, float dv)
    {
        var m = new float[w * h * 2];
        for (int i = 0; i < w * h; i++) { m[i * 2] = du; m[i * 2 + 1] = dv; }
        return m;
    }

    private static int R(uint c) => (int)((c >> 16) & 0xFF);

    [Fact]
    public void ZeroStrength_Is_Identity()
    {
        int w = 16, h = 8;
        var img = VerticalEdge(w, h);
        var outp = MotionBlurFromVectors.Apply(img, UniformMotion(w, h, 8, 0), w, h, 0.0, 8);
        Assert.Equal(img, outp);
    }

    [Fact]
    public void ZeroMotion_Is_Identity()
    {
        int w = 16, h = 8;
        var img = VerticalEdge(w, h);
        var outp = MotionBlurFromVectors.Apply(img, UniformMotion(w, h, 0, 0), w, h, 1.0, 8);
        Assert.Equal(img, outp);
    }

    [Fact]
    public void NullMotion_Is_Identity()
    {
        int w = 16, h = 8;
        var img = VerticalEdge(w, h);
        var outp = MotionBlurFromVectors.Apply(img, null, w, h, 1.0, 8);
        Assert.Equal(img, outp);
    }

    [Fact]
    public void Horizontal_Motion_Blurs_Vertical_Edge()
    {
        int w = 32, h = 8, mid = w / 2, row = 4;
        var img = VerticalEdge(w, h);
        var outp = MotionBlurFromVectors.Apply(img, UniformMotion(w, h, 8, 0), w, h, 1.0, 8);

        // The white pixel just right of the seam now averages black+white taps → grey.
        int seam = R(outp[row * w + mid]);
        Assert.InRange(seam, 40, 215);

        // Far from the seam (all taps the same colour) the pixel is unchanged.
        Assert.Equal(0, R(outp[row * w + 1]));            // deep left stays black
        Assert.Equal(255, R(outp[row * w + (w - 2)]));    // deep right stays white
    }

    [Fact]
    public void Vertical_Motion_Leaves_Vertical_Edge_Sharp()
    {
        int w = 32, h = 8, mid = w / 2, row = 4;
        var img = VerticalEdge(w, h);
        // Motion along Y only — the gather stays within each column, so the vertical
        // seam is untouched (a black|white edge parallel to the blur direction).
        var outp = MotionBlurFromVectors.Apply(img, UniformMotion(w, h, 0, 8), w, h, 1.0, 8);

        Assert.True(R(outp[row * w + mid - 1]) < 40, "left of seam should stay black");
        Assert.True(R(outp[row * w + mid]) > 215, "right of seam should stay white");
    }

    [Fact]
    public void Is_Deterministic()
    {
        int w = 24, h = 12;
        var img = VerticalEdge(w, h);
        var m = UniformMotion(w, h, 6, 3);
        var a = MotionBlurFromVectors.Apply(img, m, w, h, 1.0, 12);
        var b = MotionBlurFromVectors.Apply(img, m, w, h, 1.0, 12);
        Assert.Equal(a, b);
    }
}
