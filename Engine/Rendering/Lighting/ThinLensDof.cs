// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Rendering/Lighting/ThinLensDof.cs
//
// Roadmap slice S3 (3D-Rendering-Roadmap.md, parent #389 / #567): physically-
// based thin-lens depth of field for the 3D-fractal raymarch cameras. The CPU
// Mandelbulb calculator landed this inline first (PR #570); this extracts the
// per-pixel aperture-tap accumulation into ONE reusable helper so every other
// DE family (Mandelbox, Quaternion Julia / Mandelbrot, Bicomplex, Kleinian) can
// opt in with a single call, instead of five bespoke copies of the averaging
// loop.
//
// A pixel averages `dofN` primary rays whose origin is jittered across the
// aperture disc and re-aimed through the focal point (CameraDof.ThinLensRay), so
// occluded geometry / silhouettes bleed correctly and the bokeh integrates the
// real scene — replacing the screen-space depth gather. The first tap (s == 0)
// is the centre (pinhole) ray, so dofN == 1 or aperture 0 reproduces the single-
// ray render exactly. Seeded via ShadingPipeline.HashPair → deterministic, so
// the render is --batch-stable (the CPU-parity discipline the roadmap requires).
//
// The family supplies a ShadeRay delegate (trace + shade one primary ray,
// returning the LDR colour plus a linear-HDR sample for the tap so the taps
// average correctly with or without tonemap/bloom). The helper owns only the
// disc sampling + averaging + buffer write-back — the part that is identical
// across families.

namespace FracturingFog.Rendering.Lighting;

/// <summary>Trace + shade one primary ray for the thin-lens accumulator. Returns
/// the LDR (0xAARRGGBB) colour; <paramref name="hr"/>/<paramref name="hg"/>/
/// <paramref name="hb"/> carry a linear-HDR sample for the tap (a hit → the
/// shade's HDR; a sky miss → the backdrop as pseudo-linear) so the taps average
/// correctly whether or not a tonemap/bloom pass follows.</summary>
public delegate uint ShadeRayFn(
    double ox, double oy, double oz,
    double dx, double dy, double dz,
    out float hr, out float hg, out float hb);

/// <summary>Shared thin-lens DoF aperture-tap accumulator (roadmap S3, #567).</summary>
public static class ThinLensDof
{
    /// <summary>True when the LightingFx knobs arm the thin-lens path — the toggle
    /// is on, the aperture is open, and more than one sample is requested. Off →
    /// the caller keeps its single-ray path (byte-identical).</summary>
    public static bool IsActive(in LightingFxData fx)
        => fx.DofThinLens && fx.DofAperture > 0.0 && fx.DofSamples > 1;

    /// <summary>Number of aperture taps to average (≥ 2 when active).</summary>
    public static int SampleCount(in LightingFxData fx)
        => IsActive(in fx) ? System.Math.Max(2, fx.DofSamples) : 1;

    /// <summary>Resolve the focus distance: the explicit knob when set, else the
    /// camera distance (auto-focus the fractal centre).</summary>
    public static double FocusDistance(in LightingFxData fx, double cameraDistance)
        => fx.DofFocusDistance > 0.0 ? fx.DofFocusDistance : cameraDistance;

    /// <summary>Average <paramref name="dofN"/> aperture taps for one pixel and
    /// write the result into <paramref name="renderBuffer"/> (and, when present,
    /// the linear-HDR <paramref name="hdrBuf"/>). The centre ray is tap 0; taps
    /// 1..N-1 jitter the origin across the aperture disc (concentric-disc sample
    /// seeded by HashPair) and re-aim through the focal point. Reproduces the
    /// per-pixel arithmetic the Mandelbulb calculator shipped, so a family that
    /// routes its thin-lens branch here renders identically to a bespoke copy.</summary>
    public static void AccumulatePixel(
        int x, int y, int idx, int dofN, int seed,
        double camX, double camY, double camZ,
        double dirX, double dirY, double dirZ,
        double rX, double rY, double rZ,
        double uX, double uY, double uZ,
        double focusDist, double apertureRadius,
        uint[] renderBuffer, float[]? hdrBuf,
        ShadeRayFn shade)
    {
        double aR = 0, aG = 0, aB = 0;   // LDR accumulation (no-tonemap path)
        double hR = 0, hG = 0, hB = 0;   // linear-HDR accumulation (tonemap path)
        for (int s = 0; s < dofN; s++)
        {
            double lensX = 0.0, lensY = 0.0;
            if (s > 0)
            {
                var (l1, l2) = ShadingPipeline.HashPair(x, y, s, seed);
                (lensX, lensY) = CameraDof.ConcentricSampleDisk(l1, l2);
            }
            var (ox, oy, oz, ddx, ddy, ddz) = CameraDof.ThinLensRay(
                camX, camY, camZ, dirX, dirY, dirZ,
                rX, rY, rZ, uX, uY, uZ,
                focusDist, apertureRadius, lensX, lensY);
            uint c = shade(ox, oy, oz, ddx, ddy, ddz, out float hr, out float hg, out float hb);
            aR += (c >> 16) & 0xFF; aG += (c >> 8) & 0xFF; aB += c & 0xFF;
            hR += hr; hG += hg; hB += hb;
        }
        double inv = 1.0 / dofN;
        int R = (int)(aR * inv + 0.5), G = (int)(aG * inv + 0.5), B = (int)(aB * inv + 0.5);
        renderBuffer[idx] = 0xFF000000u
            | ((uint)System.Math.Clamp(R, 0, 255) << 16)
            | ((uint)System.Math.Clamp(G, 0, 255) << 8)
            | (uint)System.Math.Clamp(B, 0, 255);
        if (hdrBuf is not null)
        {
            hdrBuf[idx * 3] = (float)(hR * inv);
            hdrBuf[idx * 3 + 1] = (float)(hG * inv);
            hdrBuf[idx * 3 + 2] = (float)(hB * inv);
        }
    }
}
