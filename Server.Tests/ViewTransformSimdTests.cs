// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Roadmap slice S2 (#396) — SIMD ApplyLinear. Reinhard / ACES / Filmic are
// per-channel functions of only *, +, / (no transcendentals), so the Vector<float>
// pass over the flat linear-RGB array is BYTE-IDENTICAL to the scalar loop. These
// assert EXACT equality (not a tolerance) against a scalar oracle built from the
// public per-pixel Tonemap — the same arithmetic the pre-SIMD ApplyLinear ran — over
// a buffer whose channel count is NOT a multiple of the vector width (exercises the
// scalar tail) and whose values include real >1 headroom. AgX (channel-mixing, log2)
// stays on the scalar path and is checked for correctness, not for a SIMD twin.

using System;
using FracturingFog.Imaging;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class ViewTransformSimdTests
{
    // 1001 pixels => 3003 channels — deliberately not a multiple of any SIMD width,
    // so both the vector body and the scalar tail run. Values span 0..~8 (headroom).
    private static float[] MakeBuffer(int pixels = 1001)
    {
        var rng = new Random(20260904);
        var rgb = new float[pixels * 3];
        for (int i = 0; i < rgb.Length; i++)
            rgb[i] = (float)(rng.NextDouble() * 8.0); // linear, some > 1.0
        return rgb;
    }

    // Scalar oracle: exactly the per-pixel path the pre-SIMD ApplyLinear used.
    private static float[] ScalarOracle(float[] src, ViewTransform t, float ev)
    {
        var o = (float[])src.Clone();
        float expMul = MathF.Pow(2f, ev);
        int pixels = o.Length / 3;
        for (int i = 0; i < pixels; i++)
        {
            int j = i * 3;
            float r = o[j] * expMul, g = o[j + 1] * expMul, b = o[j + 2] * expMul;
            ViewTransformOps.Tonemap(t, ref r, ref g, ref b);
            o[j] = r; o[j + 1] = g; o[j + 2] = b;
        }
        return o;
    }

    [Theory]
    [InlineData(ViewTransform.Reinhard, 0f)]
    [InlineData(ViewTransform.Reinhard, 1.5f)]
    [InlineData(ViewTransform.AcesFilmic, 0f)]
    [InlineData(ViewTransform.AcesFilmic, -2f)]
    [InlineData(ViewTransform.Filmic, 0f)]
    [InlineData(ViewTransform.Filmic, 2.5f)]
    public void ApplyLinear_PerChannelOps_ByteIdentical_To_Scalar(ViewTransform t, float ev)
    {
        var src = MakeBuffer();
        var expected = ScalarOracle(src, t, ev);

        var actual = (float[])src.Clone();
        ViewTransformOps.ApplyLinear(actual, actual.Length / 3, t, ev);

        // Exact — bit-for-bit, not a tolerance.
        for (int i = 0; i < expected.Length; i++)
            Assert.Equal(expected[i], actual[i]);
    }

    [Fact]
    public void ApplyLinear_Agx_Matches_PerPixel_Tonemap()
    {
        var src = MakeBuffer();
        var expected = ScalarOracle(src, ViewTransform.AgX, 0.5f);

        var actual = (float[])src.Clone();
        ViewTransformOps.ApplyLinear(actual, actual.Length / 3, ViewTransform.AgX, 0.5f);

        for (int i = 0; i < expected.Length; i++)
            Assert.Equal(expected[i], actual[i]);
    }

    [Fact]
    public void ApplyLinear_None_IsNoOp()
    {
        var src = MakeBuffer(64);
        var actual = (float[])src.Clone();
        ViewTransformOps.ApplyLinear(actual, actual.Length / 3, ViewTransform.None);
        Assert.Equal(src, actual);
    }

    [Fact]
    public void ApplyLinear_ShortBuffer_BelowVectorWidth_StillCorrect()
    {
        // 1 pixel (3 channels) — likely below the vector width, forcing the tail.
        var src = new float[] { 2f, 0.5f, 0.1f };
        var expected = ScalarOracle(src, ViewTransform.AcesFilmic, 0f);
        var actual = (float[])src.Clone();
        ViewTransformOps.ApplyLinear(actual, 1, ViewTransform.AcesFilmic, 0f);
        for (int i = 0; i < 3; i++) Assert.Equal(expected[i], actual[i]);
    }
}
