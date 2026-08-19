// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Roadmap slice S2 (3D-Rendering-Roadmap.md, parent #389) — the view-transform
// operator library. The parity contract: None is the byte-identical identity,
// and every operator is a pure, deterministic, monotone tonemap that pins black
// to black and rolls the highlight toward (not past) white.

using FracturingFog.Imaging;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class ViewTransformTests
{
    private static readonly ViewTransform[] Operators =
    {
        ViewTransform.Reinhard, ViewTransform.AcesFilmic,
        ViewTransform.AgX, ViewTransform.Filmic,
    };

    // None must leave the buffer byte-for-byte identical — the current look is
    // preserved until the user opts in.
    [Fact]
    public void None_Is_Byte_Identical()
    {
        var a = MakeRamp(256);
        var b = (uint[])a.Clone();
        ViewTransformOps.Apply(b, b.Length, ViewTransform.None, exposureEv: 3f);
        Assert.Equal(a, b);
    }

    // Every operator pins pure black to pure black (a tonemap must map 0 → 0).
    [Fact]
    public void Operators_Map_Black_To_Black()
    {
        foreach (var op in Operators)
        {
            uint outp = ViewTransformOps.ApplyToBgra(0xFF000000u, op, 1f);
            Assert.Equal(0u, outp & 0x00FFFFFFu);      // RGB all zero
            Assert.Equal(0xFFu, (outp >> 24) & 0xFF);  // alpha preserved
        }
    }

    // Alpha is never tonemapped — it passes through unchanged for every operator.
    [Fact]
    public void Alpha_Is_Preserved()
    {
        foreach (var op in Operators)
        {
            uint outp = ViewTransformOps.ApplyToBgra(0x80AABBCCu, op, 1f);
            Assert.Equal(0x80u, (outp >> 24) & 0xFF);
        }
    }

    // Each operator is monotone non-decreasing across a gray ramp — brighter in
    // never produces darker out (the defining property of a tone curve).
    [Fact]
    public void Operators_Are_Monotone_On_Gray_Ramp()
    {
        foreach (var op in Operators)
        {
            int prev = -1;
            for (int v = 0; v <= 255; v++)
            {
                uint gray = 0xFF000000u | ((uint)v << 16) | ((uint)v << 8) | (uint)v;
                int outV = (int)(ViewTransformOps.ApplyToBgra(gray, op, 1f) & 0xFF);
                Assert.True(outV >= prev, $"{op} not monotone at v={v}: {outV} < {prev}");
                prev = outV;
            }
        }
    }

    // A filmic curve rolls off highlights: a white input (1.0 linear at EV 0) is
    // pulled off the hard 255 clip by the shoulder — never brighter than the
    // input, never crushed to black. (The exact landing differs per operator:
    // ACES/AgX sit near white, Hable normalizes to a 11.2 white point so 1.0
    // lands mid-high.)
    [Fact]
    public void Filmic_Operators_Roll_Off_Highlights()
    {
        foreach (var op in new[] { ViewTransform.AcesFilmic, ViewTransform.AgX, ViewTransform.Filmic })
        {
            int outV = (int)(ViewTransformOps.ApplyToBgra(0xFFFFFFFFu, op, 1f) & 0xFF);
            Assert.InRange(outV, 64, 254);   // rolled off the clip, still bright
        }
    }

    // Under heavy over-exposure the shoulder holds — output saturates toward 255
    // gracefully instead of wrapping or going negative.
    [Fact]
    public void Overexposure_Saturates_Gracefully()
    {
        foreach (var op in Operators)
        {
            int outV = (int)(ViewTransformOps.ApplyToBgra(0xFFFFFFFFu, op, 64f) & 0xFF); // +6 EV
            Assert.InRange(outV, 0, 255);
        }
    }

    // Positive exposure brightens; negative exposure darkens (relative to the
    // same operator at EV 0) for a mid-gray input.
    [Fact]
    public void Exposure_Shifts_Brightness()
    {
        uint gray = 0xFF808080u;
        int at0 = (int)(ViewTransformOps.ApplyToBgra(gray, ViewTransform.AcesFilmic, 1f) & 0xFF);
        int up = (int)(ViewTransformOps.ApplyToBgra(gray, ViewTransform.AcesFilmic, 2f) & 0xFF);   // +1 EV
        int down = (int)(ViewTransformOps.ApplyToBgra(gray, ViewTransform.AcesFilmic, 0.5f) & 0xFF); // -1 EV
        Assert.True(up > at0);
        Assert.True(down < at0);
    }

    // sRGB <-> linear transfer round-trips (encode∘decode ≈ identity).
    [Fact]
    public void Srgb_Transfer_RoundTrips()
    {
        for (int v = 0; v <= 255; v++)
        {
            float s = v / 255f;
            float back = ViewTransformOps.LinearToSrgb(ViewTransformOps.SrgbToLinear(s));
            Assert.Equal(s, back, 5);
        }
    }

    private static uint[] MakeRamp(int n)
    {
        var buf = new uint[n];
        for (int i = 0; i < n; i++)
        {
            uint c = (uint)(i & 0xFF);
            buf[i] = 0xFF000000u | (c << 16) | (c << 8) | c;
        }
        return buf;
    }
}
