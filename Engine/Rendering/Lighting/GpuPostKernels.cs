// GpuPostKernels.cs
//
// Phase 12 — ILGPU compute kernels for the heavyweight CPU post-passes. The
// CPU paths in ScreenSpacePost stay authoritative; the GPU dispatcher just
// runs the same algorithm on whichever Accelerator the host has (CUDA / OpenCL /
// Velocity / managed CPU JIT — same fallback chain as UserBulbGpuCalculator).
// Any failure falls through to the CPU path; bit-identity isn't preserved
// (float precision differs) but visual output matches at the resolution the
// user sees.
//
// MVP scope (Phase 12a)
//   • SSAO sample + composite fused into one kernel. Skip the bilateral blur
//     used on CPU — the per-pixel-rotated Vogel disk + GPU's higher sample
//     count keeps the noise floor low enough to read clean.
//   • Single static dispatcher; one Context + Accelerator pair leaked at
//     process exit. Lifetime matches UserBulbGpuCalculator's own one-shot
//     init pattern.
//
// Future (Phase 12b/c)
//   • Tonemap + bloom GPU pass (downsample + separable blur + composite).
//   • Volumetric in-scatter GPU kernel — gates god-rays with shadow per
//     step; biggest single-effect speedup available.
//   • Reflection probe (Phase 16) GPU pass once the CPU prototype lands.

using System;

using ILGPU;
using ILGPU.Runtime;

namespace FracturingFog.Rendering.Lighting;

public static class GpuPostKernels
{
    private static Context? _ctx;
    private static Accelerator? _acc;
    private static Action<Index1D, ArrayView<float>, ArrayView<uint>, ArrayView<float>, int, int, int, float, float, float>? _ssaoKernel;
    private static bool _initFailed;
    private static readonly object _initLock = new();

    /// <summary>
    /// True when the GPU dispatcher has a live Accelerator + JIT'd kernels.
    /// Host UI can read this to surface "Using GPU post" indicator.
    /// </summary>
    public static bool IsAvailable => _ssaoKernel != null;

    private static bool TryInit()
    {
        if (_ssaoKernel != null) return true;
        if (_initFailed) return false;
        lock (_initLock)
        {
            if (_ssaoKernel != null) return true;
            if (_initFailed) return false;
            try
            {
                _ctx = Context.Create(b => b.Default());
                // Prefer GPU; fall through to managed-CPU JIT on machines without
                // CUDA / OpenCL. Same fallback ladder as UserBulbGpuCalculator.
                _acc = _ctx.GetPreferredDevice(preferCPU: false).CreateAccelerator(_ctx);
                _ssaoKernel = _acc.LoadAutoGroupedStreamKernel<Index1D, ArrayView<float>, ArrayView<uint>, ArrayView<float>, int, int, int, float, float, float>(SsaoKernel);
                return true;
            }
            catch
            {
                _initFailed = true;
                return false;
            }
        }
    }

    /// <summary>
    /// GPU SSAO + composite. Returns false on any failure (caller falls back
    /// to the CPU path in ScreenSpacePost.ApplySsao). Single-kernel fused
    /// design — no bilateral blur; the per-pixel IGH rotation noise is
    /// low-amplitude at the sample counts a GPU run is happy with.
    /// </summary>
    public static bool TryApplySsao(
        uint[] colorBuffer,
        float[] depthBuffer,
        int width, int height,
        int samples,
        double radiusPixels,
        double strength,
        double worldRadius)
    {
        if (samples <= 0) return true; // no-op success
        if (!TryInit() || _acc == null || _ssaoKernel == null) return false;

        int n = width * height;
        if (colorBuffer.Length < n || depthBuffer.Length < n) return false;

        try
        {
            using var dColor = _acc.Allocate1D<uint>(n);
            using var dDepth = _acc.Allocate1D<float>(n);

            // Vogel-disk offsets pre-computed on host; the kernel just rotates
            // them per-pixel via IGH. 64 = max samples accepted by the kernel.
            int kSamples = Math.Min(samples, 64);
            var offsets = new float[2 * kSamples];
            const double goldenAngle = 2.39996323;
            for (int s = 0; s < kSamples; s++)
            {
                double r = Math.Sqrt((s + 0.5) / kSamples);
                double a = s * goldenAngle;
                offsets[s * 2]     = (float)(r * Math.Cos(a));
                offsets[s * 2 + 1] = (float)(r * Math.Sin(a));
            }
            using var dOffs = _acc.Allocate1D<float>(2 * kSamples);

            dColor.CopyFromCPU(colorBuffer);
            dDepth.CopyFromCPU(depthBuffer);
            dOffs.CopyFromCPU(offsets);

            _ssaoKernel(n,
                dDepth.View, dColor.View, dOffs.View,
                width, height, kSamples,
                (float)radiusPixels, (float)strength, (float)worldRadius);
            _acc.Synchronize();
            dColor.CopyToCPU(colorBuffer);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ── Kernel ────────────────────────────────────────────────────────────

    private static void SsaoKernel(
        Index1D idx,
        ArrayView<float> depth,
        ArrayView<uint> color,
        ArrayView<float> offs,
        int width, int height, int samples,
        float radiusPx, float strength, float worldRadius)
    {
        int i = idx.X;
        if (i >= width * height) return;

        float d0 = depth[i];
        // PositiveInfinity check — float comparison with the literal works
        // inside ILGPU kernels (Float32.PositiveInfinity is a constant).
        if (d0 > 1e30f) return;     // sky pixel — leave color as-is

        int x = i % width;
        int y = i / width;

        // Interleaved-gradient hash (Jorge Jimenez 2014) — per-pixel rotation
        // that decorrelates adjacent samples. fract(x) = x − floor(x).
        float a = 0.06711056f * (float)x + 0.00583715f * (float)y;
        float a1 = a - MathF.Floor(a);
        float b = 52.9829189f * a1;
        float rot = b - MathF.Floor(b);
        float ang = rot * 6.2831853f;
        float cosR = MathF.Cos(ang);
        float sinR = MathF.Sin(ang);

        float occl = 0f;
        int valid = 0;
        for (int s = 0; s < samples; s++)
        {
            float ox = offs[s * 2];
            float oy = offs[s * 2 + 1];
            float rx = ox * cosR - oy * sinR;
            float ry = ox * sinR + oy * cosR;
            int sx = (int)((float)x + rx * radiusPx);
            int sy = (int)((float)y + ry * radiusPx);
            if (sx < 0 || sy < 0 || sx >= width || sy >= height) continue;
            float dS = depth[sy * width + sx];
            if (dS > 1e30f) continue;
            valid++;
            float delta = d0 - dS;
            if (delta > 0f && delta < worldRadius)
            {
                float wt = 1f - delta / worldRadius;
                occl += wt;
            }
        }
        float ao = valid > 0 ? 1f - strength * (occl / (float)valid) : 1f;
        if (ao < 0f) ao = 0f; else if (ao > 1f) ao = 1f;

        uint c = color[i];
        float R = (float)((c >> 16) & 0xFFu) * ao;
        float G = (float)((c >>  8) & 0xFFu) * ao;
        float B = (float)( c        & 0xFFu) * ao;
        uint Ri = (uint)(R < 0f ? 0f : (R > 255f ? 255f : R));
        uint Gi = (uint)(G < 0f ? 0f : (G > 255f ? 255f : G));
        uint Bi = (uint)(B < 0f ? 0f : (B > 255f ? 255f : B));
        color[i] = 0xFF000000u | (Ri << 16) | (Gi << 8) | Bi;
    }

    /// <summary>Release the static accelerator + context. Optional — process
    /// exit reclaims everything regardless. Tests or hosts that recycle the
    /// engine may want to call this explicitly.</summary>
    public static void Dispose()
    {
        lock (_initLock)
        {
            _ssaoKernel = null;
            _acc?.Dispose(); _acc = null;
            _ctx?.Dispose(); _ctx = null;
            _initFailed = false;
        }
    }
}
