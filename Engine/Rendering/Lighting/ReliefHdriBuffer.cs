// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// ReliefHdriBuffer.cs — Relief 3D Slice 4d-ii (#171): flatten an HdriImage into a
// single concatenated SSBO (with a small integer header) so the GPU relief kernel
// and the CPU parity twin sample the SAME uploaded buffer — parity by
// construction, no HdriImage type dependency in the backends.
//
// The buffer is uint-typed (StructuredBuffer<uint>), NOT float. The header ints
// (mip count, per-level element offset / width / height) are stored as plain
// uints and read directly; the RGB pixels are stored as their float bit-pattern
// (SingleToUInt32Bits) and recovered with asfloat on the GPU. This is deliberate:
// a float SSBO would store the small header ints as *denormal* floats, and D3D
// fp32 flushes denormals to zero on load, so asuint(gHdri[i]) would read 0 and the
// sampler would index garbage. Integer loads are never flushed, and asfloat of a
// normal-float bit-pattern is an exact bitcast — so both header and pixels survive.
//
// Buffer layout (uint[]):
//   [0]                     = MipLevels
//   [1 + 3L + 0]            = offset_L   (uint element index where level L's RGB
//                                         data starts — exact even past 2^24)
//   [1 + 3L + 1]            = width_L
//   [1 + 3L + 2]            = height_L
//     ... for L in 0 .. MipLevels-1 (header length = 1 + 3·MipLevels uints)
//   [offset_0 ..]           = level 0 RGB triples as float bits, then level 1, …
//
// The sampler mirrors HdriImage.Sample(dir, roughness) / SampleUvMip bit-for-bit
// (equirect projection, bilinear with u-wrap / v-clamp, roughness → mip via
// roughness²·(MipLevels−1), nearest mip below). The HLSL twin in
// ReliefRaymarchKernelSource.SampleHdri is a line-for-line port.

using System;

namespace FracturingFog.Rendering.Lighting;

/// <summary>Flatten + sample helper for the relief kernel's HDRI environment SRV.
/// See the file header for the buffer layout and the denormal-flush rationale.</summary>
public static class ReliefHdriBuffer
{
    /// <summary>Flatten <paramref name="img"/> (all mip levels) into one uint
    /// buffer with the integer header described in the file header.</summary>
    public static uint[] Flatten(HdriImage img)
    {
        if (img is null) throw new ArgumentNullException(nameof(img));
        int levels = img.MipLevels;
        int headerLen = 1 + 3 * levels;

        int total = headerLen;
        for (int l = 0; l < levels; l++)
            total += img.MipWidths[l] * img.MipHeights[l] * 3;

        var buf = new uint[total];
        buf[0] = (uint)levels;

        int off = headerLen;
        for (int l = 0; l < levels; l++)
        {
            int w = img.MipWidths[l], hgt = img.MipHeights[l];
            buf[1 + 3 * l + 0] = (uint)off;
            buf[1 + 3 * l + 1] = (uint)w;
            buf[1 + 3 * l + 2] = (uint)hgt;
            float[] src = img.MipData[l];
            int count = w * hgt * 3;
            for (int i = 0; i < count; i++)
                buf[off + i] = BitConverter.SingleToUInt32Bits(src[i]);
            off += count;
        }
        return buf;
    }

    /// <summary>Number of mip levels packed in <paramref name="buf"/>.</summary>
    public static int Levels(uint[] buf) => (int)buf[0];

    /// <summary>Header lookup for level <paramref name="lvl"/>.</summary>
    public static void MipInfo(uint[] buf, int lvl, out int off, out int w, out int h)
    {
        off = (int)buf[1 + 3 * lvl + 0];
        w = (int)buf[1 + 3 * lvl + 1];
        h = (int)buf[1 + 3 * lvl + 2];
    }

    /// <summary>Equirectangular, roughness-convolved sample of the packed HDRI.
    /// Twin of <see cref="HdriImage.Sample(double,double,double,double)"/>: picks a
    /// mip by roughness²·(levels−1) then bilinearly samples it. Returns linear
    /// RGB (unclamped). Direction is expected roughly unit-length.</summary>
    public static (double R, double G, double B) Sample(
        uint[] buf, double dx, double dy, double dz, double roughness)
    {
        double u = 0.5 + Math.Atan2(dz, dx) * (1.0 / (2.0 * Math.PI));
        double v = Math.Acos(Math.Clamp(dy, -1.0, 1.0)) * (1.0 / Math.PI);
        int levels = Levels(buf);
        if (roughness <= 0 || levels <= 1) return SampleUvMip(buf, u, v, 0);
        if (roughness > 1) roughness = 1;
        double level = roughness * roughness * (levels - 1);
        int lvl = (int)Math.Floor(level);
        if (lvl >= levels - 1) lvl = levels - 1;
        return SampleUvMip(buf, u, v, lvl);
    }

    /// <summary>Bilinear UV sample at a specific mip (u wraps, v clamps). Twin of
    /// <see cref="HdriImage.SampleUvMip"/>.</summary>
    public static (double R, double G, double B) SampleUvMip(uint[] buf, double u, double v, int mip)
    {
        int levels = Levels(buf);
        if (mip < 0) mip = 0; else if (mip >= levels) mip = levels - 1;
        MipInfo(buf, mip, out int off, out int mw, out int mh);

        u -= Math.Floor(u);
        if (v < 0) v = 0; else if (v > 1) v = 1;
        double fx = u * (mw - 1);
        double fy = v * (mh - 1);
        int x0 = (int)Math.Floor(fx);
        int y0 = (int)Math.Floor(fy);
        int x1 = x0 + 1; if (x1 >= mw) x1 = 0;
        int y1 = Math.Min(y0 + 1, mh - 1);
        double tx = fx - x0;
        double ty = fy - y0;
        int i00 = off + (y0 * mw + x0) * 3;
        int i10 = off + (y0 * mw + x1) * 3;
        int i01 = off + (y1 * mw + x0) * 3;
        int i11 = off + (y1 * mw + x1) * 3;
        double R = (1 - tx) * (1 - ty) * Px(buf, i00)   + tx * (1 - ty) * Px(buf, i10)
                 + (1 - tx) *      ty  * Px(buf, i01)   + tx *      ty  * Px(buf, i11);
        double G = (1 - tx) * (1 - ty) * Px(buf, i00+1) + tx * (1 - ty) * Px(buf, i10+1)
                 + (1 - tx) *      ty  * Px(buf, i01+1) + tx *      ty  * Px(buf, i11+1);
        double B = (1 - tx) * (1 - ty) * Px(buf, i00+2) + tx * (1 - ty) * Px(buf, i10+2)
                 + (1 - tx) *      ty  * Px(buf, i01+2) + tx *      ty  * Px(buf, i11+2);
        return (R, G, B);
    }

    private static float Px(uint[] buf, int i) => BitConverter.UInt32BitsToSingle(buf[i]);
}
