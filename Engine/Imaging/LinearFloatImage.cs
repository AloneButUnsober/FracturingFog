// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Imaging/LinearFloatImage.cs
//
// Roadmap slice S2 (3D-Rendering-Roadmap.md, parent #389 / #396) — the CORE
// true-linear / float intermediate. FF composites and tonemaps on the 8-bit BGRA
// display buffer today: the render is already clipped to [0,1] before the view
// transform ever sees it, so the filmic operators are a LOOK (highlight-shaped
// contrast) rather than real HDR recovery. This type is the linear-light float
// buffer the pipeline is meant to live in: straight linear RGB with unbounded
// headroom (values > 1.0 survive), a passthrough alpha plane, and a single
// encode back to display-referred BGRA.
//
// Parity discipline (the seam-before-consumer pattern used across the roadmap):
//   * FromBgra → (optional view transform) → ToBgra reproduces the existing
//     8-bit ViewTransformOps path BYTE-FOR-BYTE, because both route through the
//     SAME operator core (ViewTransformOps.Tonemap) and the SAME encode
//     (ViewTransformOps.Encode). So dropping this intermediate in front of an
//     8-bit source changes nothing — the default look is preserved.
//   * When a producer fills the buffer with real linear values above 1.0 (the
//     relief HDR scratch, a full-float 2D composite, an EXR read-back), the
//     tonemap rolls those highlights off instead of hard-clipping them — the
//     recovery the 8-bit path structurally cannot do. Those producers wire in as
//     follow-ups; this slice lands the intermediate + its parity guarantee.

using System;

namespace FracturingFog.Imaging;

/// <summary>A linear-light HDR image: interleaved straight RGB (<c>Rgb[3i+0..2]</c>,
/// unbounded ≥0) plus a passthrough alpha plane, with encode/decode to the 8-bit
/// BGRA display buffer. The S2 core intermediate (#389 / #396).</summary>
public sealed class LinearFloatImage
{
    /// <summary>Interleaved linear RGB, length <c>Width*Height*3</c>. Not clamped —
    /// carries real highlight headroom above 1.0.</summary>
    public float[] Rgb { get; }

    /// <summary>Straight alpha in [0,1], length <c>Width*Height</c>. Never
    /// tonemapped — passed through the view transform untouched.</summary>
    public float[] Alpha { get; }

    public int Width { get; }
    public int Height { get; }
    public int PixelCount => Width * Height;

    public LinearFloatImage(int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "LinearFloatImage: size must be positive.");
        Width = width;
        Height = height;
        Rgb = new float[(long)width * height * 3];
        Alpha = new float[(long)width * height];
    }

    /// <summary>Decode an 8-bit straight-alpha BGRA buffer into a linear-light
    /// float image (sRGB EOTF per channel, alpha carried straight). The exact
    /// inverse of <see cref="ToBgra"/> at 8-bit precision, so a round-trip is a
    /// no-op — this is the parity anchor for putting the intermediate in front of
    /// an existing 8-bit render.</summary>
    public static LinearFloatImage FromBgra(uint[] bgra, int width, int height)
        => FromBgra(bgra, bgra, width, height);

    /// <summary>Decode an 8-bit straight-alpha BGRA buffer into a linear-light float
    /// image, taking the RGB from <paramref name="color"/> but the straight alpha
    /// (coverage) byte from a SEPARATE <paramref name="coverage"/> buffer. The live
    /// path (FractalRenderHost) grades into a <c>dst</c> whose alpha was force-set to
    /// 0xFF, so the true authored coverage lives in the source buffer — this mirrors
    /// the coverage/rgb split <see cref="FracturingFog.Rendering.Interior2DBackgroundCompositor.Composite"/>
    /// takes. Passing the same array for both (the export path) reads alpha from the
    /// colour buffer, exactly like the single-argument overload.</summary>
    public static LinearFloatImage FromBgra(uint[] color, uint[] coverage, int width, int height)
    {
        if (color == null) throw new ArgumentNullException(nameof(color));
        if (coverage == null) throw new ArgumentNullException(nameof(coverage));
        long n = (long)width * height;
        if (color.Length < n) throw new ArgumentException("LinearFloatImage.FromBgra: colour buffer smaller than width*height.");
        if (coverage.Length < n) throw new ArgumentException("LinearFloatImage.FromBgra: coverage buffer smaller than width*height.");
        var img = new LinearFloatImage(width, height);
        for (int i = 0; i < n; i++)
        {
            uint p = color[i];
            int j = i * 3;
            img.Rgb[j] = ViewTransformOps.SrgbToLinear(((p >> 16) & 0xFF) / 255f);
            img.Rgb[j + 1] = ViewTransformOps.SrgbToLinear(((p >> 8) & 0xFF) / 255f);
            img.Rgb[j + 2] = ViewTransformOps.SrgbToLinear((p & 0xFF) / 255f);
            img.Alpha[i] = ((coverage[i] >> 24) & 0xFF) / 255f;
        }
        return img;
    }

    /// <summary>Build a linear-light image from the engine's PRE-CLAMP HDR beauty
    /// buffer (byte-scale 0..∞, 3 floats / pixel — <see cref="FracturingFog.Rendering.Lighting.HeightfieldRaymarch2D.ReliefAovBuffers.HdrBeauty"/>
    /// and the <c>ScreenSpacePost</c> convention) plus the 8-bit fallback beauty for
    /// the pixels that carry no HDR sample. A channel of <c>NaN</c> is that sentinel
    /// (sky / ray-miss): those pixels decode the fallback BGRA (sRGB → linear), so a
    /// buffer with NO captured HDR reduces EXACTLY to <see cref="FromBgra"/> and the
    /// view transform matches the plain 8-bit path byte-for-byte. Written pixels use
    /// <c>value / 255</c> as their linear value — the same free-form byte-scale ⇒
    /// linear mapping the existing HDR tonemap path uses — so a highlight above 255
    /// (linear > 1.0) survives into the intermediate with real headroom instead of
    /// the clamp the 8-bit beauty already applied. Alpha always comes from the
    /// fallback (the HDR plane carries none).</summary>
    public static LinearFloatImage FromHdrByteScale(float[] hdrByteScale, uint[] fallbackBgra, int width, int height)
    {
        if (hdrByteScale == null) throw new ArgumentNullException(nameof(hdrByteScale));
        if (fallbackBgra == null) throw new ArgumentNullException(nameof(fallbackBgra));
        long n = (long)width * height;
        if (hdrByteScale.Length < n * 3) throw new ArgumentException("LinearFloatImage.FromHdrByteScale: HDR buffer smaller than width*height*3.");
        if (fallbackBgra.Length < n) throw new ArgumentException("LinearFloatImage.FromHdrByteScale: fallback buffer smaller than width*height.");
        var img = new LinearFloatImage(width, height);
        for (int i = 0; i < n; i++)
        {
            int j = i * 3;
            float hr = hdrByteScale[j];
            uint p = fallbackBgra[i];
            img.Alpha[i] = ((p >> 24) & 0xFF) / 255f;
            if (float.IsNaN(hr))
            {
                // No HDR sample here → decode the 8-bit fallback, so no-HDR pixels
                // land identically to the plain 8-bit view-transform path.
                img.Rgb[j] = ViewTransformOps.SrgbToLinear(((p >> 16) & 0xFF) / 255f);
                img.Rgb[j + 1] = ViewTransformOps.SrgbToLinear(((p >> 8) & 0xFF) / 255f);
                img.Rgb[j + 2] = ViewTransformOps.SrgbToLinear((p & 0xFF) / 255f);
            }
            else
            {
                img.Rgb[j] = hr / 255f;
                img.Rgb[j + 1] = hdrByteScale[j + 1] / 255f;
                img.Rgb[j + 2] = hdrByteScale[j + 2] / 255f;
            }
        }
        return img;
    }

    /// <summary>Encode this linear image back to 8-bit straight-alpha BGRA (sRGB
    /// OETF per channel via <see cref="ViewTransformOps.Encode"/>, alpha rounded).
    /// Highlights above 1.0 are saturated by the encode — apply a view transform
    /// first (<see cref="ApplyViewTransform"/>) to roll them off instead.</summary>
    public uint[] ToBgra()
    {
        long n = PixelCount;
        var outp = new uint[n];
        for (int i = 0; i < n; i++)
        {
            int j = i * 3;
            byte R = ViewTransformOps.Encode(Rgb[j]);
            byte G = ViewTransformOps.Encode(Rgb[j + 1]);
            byte B = ViewTransformOps.Encode(Rgb[j + 2]);
            byte A = (byte)Math.Clamp(Alpha[i] * 255f + 0.5f, 0f, 255f);
            outp[i] = ((uint)A << 24) | ((uint)R << 16) | ((uint)G << 8) | B;
        }
        return outp;
    }

    /// <summary>Apply a view transform + exposure to this image IN LINEAR LIGHT,
    /// in place (values above 1.0 are rolled off, not clipped). Convenience over
    /// <see cref="ViewTransformOps.ApplyLinear"/>; <see cref="ViewTransform.None"/>
    /// leaves the buffer untouched.</summary>
    public LinearFloatImage ApplyViewTransform(ViewTransform transform, float exposureEv = 0f)
    {
        ViewTransformOps.ApplyLinear(Rgb, PixelCount, transform, exposureEv);
        return this;
    }
}
