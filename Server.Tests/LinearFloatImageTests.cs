// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Roadmap slice S2 (3D-Rendering-Roadmap.md, #389 / #396) — the CORE true-linear
// float intermediate (LinearFloatImage). Contract:
//   * FromBgra → ToBgra is a byte-for-byte round-trip (the intermediate in front
//     of an 8-bit source changes nothing).
//   * FromBgra → view transform → ToBgra matches the existing 8-bit
//     ViewTransformOps path byte-for-byte, for every operator and exposure —
//     same operator core, same encode.
//   * A producer that fills the buffer with real linear values above 1.0 gets
//     highlight ROLL-OFF, not the hard clip the 8-bit path is stuck with — the
//     recovery is the point of the linear intermediate.
//   * None is the no-op identity; alpha is passthrough.

using System;
using FracturingFog.Imaging;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class LinearFloatImageTests
{
    private static readonly ViewTransform[] Operators =
    {
        ViewTransform.Reinhard, ViewTransform.AcesFilmic,
        ViewTransform.AgX, ViewTransform.Filmic,
    };

    // A spread of straight-alpha BGRA pixels: full gray ramp + saturated colors +
    // varied alpha, so parity/round-trip is exercised across the gamut.
    private static uint[] SampleBuffer()
    {
        var list = new System.Collections.Generic.List<uint>();
        for (int v = 0; v <= 255; v++)
            list.Add(0xFF000000u | ((uint)v << 16) | ((uint)v << 8) | (uint)v);
        uint[] colors = { 0xFFFF0000u, 0xFF00FF00u, 0xFF0000FFu, 0x80AABBCCu, 0x00123456u, 0xC0FF8800u };
        list.AddRange(colors);
        return list.ToArray();
    }

    [Fact]
    public void FromBgra_ToBgra_RoundTrips_ByteIdentical()
    {
        var buf = SampleBuffer();
        var back = LinearFloatImage.FromBgra(buf, buf.Length, 1).ToBgra();
        Assert.Equal(buf, back);
    }

    [Fact]
    public void Matches_The_8bit_ViewTransform_Path_ByteIdentical()
    {
        float[] evs = { -2f, -1f, 0f, 1f, 3f };
        foreach (var op in Operators)
        foreach (var ev in evs)
        {
            var buf = SampleBuffer();

            // 8-bit reference path.
            var eightBit = (uint[])buf.Clone();
            ViewTransformOps.Apply(eightBit, eightBit.Length, op, ev);

            // Float-intermediate path.
            var viaFloat = LinearFloatImage.FromBgra(buf, buf.Length, 1)
                .ApplyViewTransform(op, ev)
                .ToBgra();

            Assert.Equal(eightBit, viaFloat);
        }
    }

    [Fact]
    public void None_Is_A_NoOp_Even_With_Exposure()
    {
        var buf = SampleBuffer();
        var img = LinearFloatImage.FromBgra(buf, buf.Length, 1);
        var before = img.ToBgra();
        img.ApplyViewTransform(ViewTransform.None, exposureEv: 4f);
        Assert.Equal(before, img.ToBgra());   // None ignores exposure, like the 8-bit gate
    }

    [Fact]
    public void Alpha_Is_Passthrough()
    {
        var img = new LinearFloatImage(3, 1);
        img.Alpha[0] = 0f; img.Alpha[1] = 0.5f; img.Alpha[2] = 1f;
        for (int i = 0; i < 9; i++) img.Rgb[i] = 0.3f;
        img.ApplyViewTransform(ViewTransform.AcesFilmic, 1f);
        var outp = img.ToBgra();
        Assert.Equal(0x00u, (outp[0] >> 24) & 0xFF);
        Assert.Equal(128u, (outp[1] >> 24) & 0xFF);   // 0.5*255+0.5 → 128
        Assert.Equal(0xFFu, (outp[2] >> 24) & 0xFF);
    }

    // The headroom test: a linear value ABOVE 1.0 tonemaps to a DIFFERENT (higher)
    // byte than the same value clamped to 1.0 first. The 8-bit path structurally
    // clamps at the source and cannot tell 1.0 from 4.0 apart — the float
    // intermediate can, and the shoulder keeps it below the hard clip.
    [Fact]
    public void Highlights_Above_One_Are_Rolled_Off_Not_Clipped()
    {
        foreach (var op in Operators)
        {
            var hi = new LinearFloatImage(1, 1);
            hi.Rgb[0] = hi.Rgb[1] = hi.Rgb[2] = 4.0f;   // 2 stops over white
            hi.Alpha[0] = 1f;
            hi.ApplyViewTransform(op, 0f);
            int hiV = (int)(hi.ToBgra()[0] & 0xFF);

            var one = new LinearFloatImage(1, 1);
            one.Rgb[0] = one.Rgb[1] = one.Rgb[2] = 1.0f;   // exactly white
            one.Alpha[0] = 1f;
            one.ApplyViewTransform(op, 0f);
            int oneV = (int)(one.ToBgra()[0] & 0xFF);

            // Reinhard/ACES/AgX/Filmic all keep rising past 1.0 (headroom used),
            Assert.True(hiV > oneV, $"{op}: headroom not used ({hiV} !> {oneV})");
            // and the shoulder holds it under a hard clip (no blow-out to 255).
            Assert.InRange(hiV, oneV + 1, 255);
        }
    }

    [Fact]
    public void Zero_Image_Encodes_To_Opaque_Black_Under_Any_Operator()
    {
        foreach (var op in Operators)
        {
            var img = new LinearFloatImage(4, 4);
            for (int i = 0; i < img.PixelCount; i++) img.Alpha[i] = 1f;
            img.ApplyViewTransform(op, 1f);
            foreach (var px in img.ToBgra())
                Assert.Equal(0xFF000000u, px);   // black stays black, alpha opaque
        }
    }

    [Fact]
    public void Is_Deterministic()
    {
        var buf = SampleBuffer();
        var a = LinearFloatImage.FromBgra(buf, buf.Length, 1).ApplyViewTransform(ViewTransform.AgX, 1.5f).ToBgra();
        var b = LinearFloatImage.FromBgra(buf, buf.Length, 1).ApplyViewTransform(ViewTransform.AgX, 1.5f).ToBgra();
        Assert.Equal(a, b);
    }

    // ── FromHdrByteScale: the relief HDR producer bridge ──────────────────────

    private static float[] AllNaN(int pixels)
    {
        var h = new float[pixels * 3];
        Array.Fill(h, float.NaN);
        return h;
    }

    // No HDR sample anywhere (all-NaN) → every pixel decodes the 8-bit fallback,
    // so the bridge reduces EXACTLY to FromBgra: the round-trip is byte-identical.
    [Fact]
    public void FromHdrByteScale_AllNaN_Equals_FromBgra_RoundTrip()
    {
        var buf = SampleBuffer();
        var viaHdr = LinearFloatImage.FromHdrByteScale(AllNaN(buf.Length), buf, buf.Length, 1).ToBgra();
        Assert.Equal(buf, viaHdr);
    }

    // With no captured HDR, the transform over the bridge matches the plain 8-bit
    // view-transform path byte-for-byte — a non-relief / sky pixel never regresses.
    [Fact]
    public void FromHdrByteScale_NoHdr_Matches_8bit_Transform()
    {
        float[] evs = { -1f, 0f, 2f };
        foreach (var op in Operators)
        foreach (var ev in evs)
        {
            var buf = SampleBuffer();
            var eightBit = (uint[])buf.Clone();
            ViewTransformOps.Apply(eightBit, eightBit.Length, op, ev);

            var viaHdr = LinearFloatImage.FromHdrByteScale(AllNaN(buf.Length), buf, buf.Length, 1)
                .ApplyViewTransform(op, ev).ToBgra();

            Assert.Equal(eightBit, viaHdr);
        }
    }

    // A byte-scale HDR value above 255 (linear > 1.0) is recovered: it tonemaps to a
    // higher byte than a clamped 255 sample, which the 8-bit buffer cannot represent.
    [Fact]
    public void FromHdrByteScale_Above255_Recovers_Highlight()
    {
        foreach (var op in Operators)
        {
            // Two pixels: [0] a 4×-white highlight (1020 byte-scale), [1] exactly white.
            var hdr = new float[] { 1020f, 1020f, 1020f, 255f, 255f, 255f };
            var fallback = new uint[] { 0xFFFFFFFFu, 0xFFFFFFFFu };   // both clip to white at 8-bit
            var outp = LinearFloatImage.FromHdrByteScale(hdr, fallback, 2, 1)
                .ApplyViewTransform(op, 0f).ToBgra();
            int hiV = (int)(outp[0] & 0xFF);
            int oneV = (int)(outp[1] & 0xFF);
            Assert.True(hiV > oneV, $"{op}: byte-scale headroom not recovered ({hiV} !> {oneV})");
        }
    }

    // Sky (NaN) and terrain (captured HDR) coexist: the sky pixel decodes the
    // fallback and matches the 8-bit path; the terrain pixel uses the captured value.
    [Fact]
    public void FromHdrByteScale_Mixes_Sky_Fallback_And_Terrain_Hdr()
    {
        // pixel 0 = terrain (captured 300 byte-scale), pixel 1 = sky (NaN → fallback).
        var hdr = new float[] { 300f, 300f, 300f, float.NaN, float.NaN, float.NaN };
        var fallback = new uint[] { 0xFF808080u, 0xFF204060u };
        var outp = LinearFloatImage.FromHdrByteScale(hdr, fallback, 2, 1)
            .ApplyViewTransform(ViewTransform.AcesFilmic, 0f).ToBgra();

        // Sky pixel == the plain 8-bit path on that same fallback pixel.
        uint skyRef = ViewTransformOps.ApplyToBgra(0xFF204060u, ViewTransform.AcesFilmic, 1f);
        Assert.Equal(skyRef, outp[1]);
        // Terrain pixel used the >255 capture → brighter than its clamped fallback would give.
        uint terrClamp = ViewTransformOps.ApplyToBgra(0xFF808080u, ViewTransform.AcesFilmic, 1f);
        Assert.True((outp[0] & 0xFF) > (terrClamp & 0xFF), "terrain HDR not applied");
    }
}
