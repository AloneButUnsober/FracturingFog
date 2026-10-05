// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// DualOrbitEscapeCalculator.Lic.cs (#1128, epic #1114 S14)
//
// Line integral convolution (Cabral & Leedom 1993) as a colour post-process:
// a fixed white-noise texture is averaged along the streamline of an
// orientation field through each pixel, giving a "flow" texture that
// modulates the rendered colour. Revisits #868 (glyph arrows) with a texture.
//
// Orientation sources (DualOrbitLicSource):
//   SeparationDirection  arg(c_N − z_N) at the end of the lockstep run (S14
//                        Jacobian pass) — needs iteration data (geometry).
//   FieldGradient        ∇ of the coloured scalar (central differences on the
//                        cached scalar: GreenRatio's gradient when the field is
//                        GreenRatio, and so on) — colour-only.
//   FieldContour         ⊥ the gradient: flow ALONG the level sets — colour-only.
// The issue's third source, the dominant singular vector, is not offered: the
// SxSy and CxCy slices are holomorphic in their image coordinate, so the
// image-plane block of the Jacobian is conformal (both real singular values
// equal) and has no preferred stretching direction to follow.
//
// Orientation, not direction: streamlines step 1 px, flipping the field where
// it opposes the previous step (sign-agnostic), and stop at the border or at a
// pixel with no orientation (NaN). The kernel is a box of half-length
// DualOrbitLicLength; the mean is contrast-normalised by its own expected
// spread (uniform noise, n samples: σ = 1/√(12n)) and mixes into the colour by
// DualOrbitLicStrength. Length / strength and the gradient sources recolour
// from the cache.

using System;
using System.Threading.Tasks;

namespace FracturingFog;

public sealed partial class DualOrbitEscapeCalculator
{
    private float[] _sepDir = Array.Empty<float>();   // arg(c_N − z_N), NaN = none

    /// <summary>#1128 — the orientation field (radians, NaN = none) the last LIC
    /// pass followed; empty when LIC is off.</summary>
    public float[] LicOrientation { get; private set; } = Array.Empty<float>();

    /// <summary>#1128 — the LIC value per pixel ([0, 1], NaN = none) of the last pass.</summary>
    public float[] LicValue { get; private set; } = Array.Empty<float>();

    /// <summary>Deterministic white noise in [0, 1) for pixel index i.</summary>
    public static float LicNoise(int i)
    {
        uint h = (uint)i * 0x9E3779B1u;
        h ^= h >> 16; h *= 0x85EBCA6Bu; h ^= h >> 13; h *= 0xC2B2AE35u; h ^= h >> 16;
        return (h >> 8) * (1.0f / 16777216f);
    }

    /// <summary>#1128 — line integral convolution of <paramref name="noise"/>
    /// along the orientation field <paramref name="theta"/> (radians; NaN = no
    /// orientation): the mean of the noise over the streamline of ±
    /// <paramref name="halfLength"/> unit steps. Returns the mean and the sample
    /// count per pixel (count 0 / NaN where the pixel has no orientation).</summary>
    public static float[] Lic(float[] theta, float[] noise, int width, int height, int halfLength, int[]? counts = null)
    {
        int n = width * height;
        var outp = new float[n];
        Parallel.For(0, height, y =>
        {
            for (int x = 0; x < width; x++)
            {
                int i = y * width + x;
                float t0 = theta[i];
                if (float.IsNaN(t0)) { outp[i] = float.NaN; if (counts != null) counts[i] = 0; continue; }
                double sum = noise[i]; int cnt = 1;
                for (int dir = -1; dir <= 1; dir += 2)
                {
                    double px = x + 0.5, py = y + 0.5;
                    double ux = dir * Math.Cos(t0), uy = dir * Math.Sin(t0);
                    for (int k = 0; k < halfLength; k++)
                    {
                        px += ux; py += uy;
                        int ix = (int)Math.Floor(px), iy = (int)Math.Floor(py);
                        if (ix < 0 || iy < 0 || ix >= width || iy >= height) break;
                        int j = iy * width + ix;
                        float tj = theta[j];
                        if (float.IsNaN(tj)) break;
                        sum += noise[j]; cnt++;
                        double vx = Math.Cos(tj), vy = Math.Sin(tj);
                        if (vx * ux + vy * uy < 0) { vx = -vx; vy = -vy; }
                        ux = vx; uy = vy;
                    }
                }
                outp[i] = (float)(sum / cnt);
                if (counts != null) counts[i] = cnt;
            }
        });
        return outp;
    }

    // Modulate ColorBuffer by the LIC texture (colour-only).
    private void ApplyLic(in GeometryKey key)
    {
        var fp = FractalParameters;
        var src = fp.DualOrbitLicSource;
        int n = Width * Height, w = Width, h = Height;
        if (src == DualOrbitLicSource.Off || n == 0) { LicOrientation = Array.Empty<float>(); LicValue = Array.Empty<float>(); return; }

        var theta = new float[n];
        if (src == DualOrbitLicSource.SeparationDirection)
        {
            if (_sepDir.Length != n) { LicOrientation = Array.Empty<float>(); LicValue = Array.Empty<float>(); return; }
            Array.Copy(_sepDir, theta, n);
        }
        else
        {
            bool contour = src == DualOrbitLicSource.FieldContour;
            float[] sc = _scalar;
            Parallel.For(0, h, y =>
            {
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    if (sc[i] <= 0 || x == 0 || y == 0 || x == w - 1 || y == h - 1) { theta[i] = float.NaN; continue; }
                    float l = sc[i - 1], r = sc[i + 1], u = sc[i - w], d = sc[i + w];
                    if (l <= 0 || r <= 0 || u <= 0 || d <= 0) { theta[i] = float.NaN; continue; }
                    double gx = r - l, gy = d - u;
                    if (gx * gx + gy * gy < 1e-18) { theta[i] = float.NaN; continue; }
                    double a = Math.Atan2(gy, gx);
                    theta[i] = (float)(contour ? a + 0.5 * Math.PI : a);
                }
            });
        }

        var noise = new float[n];
        for (int i = 0; i < n; i++) noise[i] = LicNoise(i);
        int half = Math.Clamp(fp.DualOrbitLicLength, 1, 256);
        var counts = new int[n];
        var lic = Lic(theta, noise, w, h, half, counts);
        double strength = Math.Clamp(fp.DualOrbitLicStrength, 0.0, 1.0);
        Parallel.For(0, n, i =>
        {
            float v = lic[i];
            if (float.IsNaN(v)) return;
            // Contrast-normalise the box mean: z ~ N(0, 1) for uniform noise.
            double z = (v - 0.5) * Math.Sqrt(12.0 * counts[i]);
            double t = Math.Clamp(0.5 + 0.18 * z, 0.0, 1.0);
            double k = (1.0 - strength) + strength * (0.25 + 1.25 * t);
            uint c = ColorBuffer[i];
            uint Ch(int sh) => (uint)Math.Clamp(((c >> sh) & 0xFF) * k + 0.5, 0, 255);
            ColorBuffer[i] = (c & 0xFF000000u) | (Ch(16) << 16) | (Ch(8) << 8) | Ch(0);
        });
        LicOrientation = theta;
        LicValue = lic;
    }
}
