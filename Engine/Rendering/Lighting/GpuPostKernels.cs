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
// Shipped kernels
//   • Phase 12a — SSAO sample + composite fused (TryApplySsao).
//   • Phase 12b — Tonemap + bloom (threshold → 2-mip Gaussian pyramid →
//     upsample-add → composite + tonemap + gamma). TryApplyToneMapBloom.
//   • Phase 12c — Edge ink Sobel and Frei-Chen on normal buffer.
//     TryApplyEdgeInk (mode picks kernel).
//
// Deferred — volumetric in-scatter lives inside ShadingPipeline.Shade and
// calls SoftShadow + CloudSelfShadow with the calculator's
// DistanceEstimator delegate. ILGPU cannot invoke a managed delegate from
// a kernel, so a GPU volumetric port requires every fractal DE to be
// rewritten as a GPU kernel — a 7-calculator refactor beyond Phase 12.
// Left documented in Lighting-FX-Roadmap.md.

using System;

using ILGPU;
using ILGPU.Runtime;

namespace FracturingFog.Rendering.Lighting;

public static class GpuPostKernels
{
    private static Context? _ctx;
    private static Accelerator? _acc;
    private static Action<Index1D, ArrayView<float>, ArrayView<uint>, ArrayView<float>, int, int, int, float, float, float>? _ssaoKernel;
    private static Action<Index1D, ArrayView<float>, ArrayView<float>, int, float>? _thresholdKernel;
    private static Action<Index1D, ArrayView<float>, ArrayView<float>, int, int, int, int>? _downsampleKernel;
    private static Action<Index1D, ArrayView<float>, ArrayView<float>, int, int>? _blurHKernel;
    private static Action<Index1D, ArrayView<float>, ArrayView<float>, int, int>? _blurVKernel;
    private static Action<Index1D, ArrayView<float>, ArrayView<float>, int, int, int, int, float>? _upsampleAddKernel;
    private static Action<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<uint>, int, int, int, int, float, float>? _compositeKernel;
    private static Action<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<uint>, int, int, int, float, float, float, float, float>? _edgeKernel;
    private static bool _initFailed;
    private static readonly object _initLock = new();

    /// <summary>
    /// True when the GPU dispatcher has a live Accelerator + JIT'd kernels.
    /// Host UI can read this to surface "Using GPU post" indicator.
    /// </summary>
    public static bool IsAvailable => _ssaoKernel != null;

    // P6 — per-frame shared color buffer. Single CopyFromCPU at BeginFrame +
    // single CopyToCPU at EndFrame, instead of 3× round trips across SSAO /
    // tonemap-bloom / edge-ink dispatches. Saves ~0.5–1.5 ms per frame when
    // all three GPU passes are active.
    [System.ThreadStatic]
    private static bool _frameActive;
    [System.ThreadStatic]
    private static uint[]? _frameColorHost;
    [System.ThreadStatic]
    private static MemoryBuffer1D<uint, Stride1D.Dense>? _frameDColor;
    [System.ThreadStatic]
    private static int _frameDColorClass;
    [System.ThreadStatic]
    private static int _frameN;

    /// <summary>P6 — begin a bundled GPU post-pass frame. Lease a single
    /// device color buffer and prime it from <paramref name="colorBuffer"/>;
    /// subsequent <c>TryApply*</c> calls that target the same host buffer
    /// reuse the device buffer rather than allocating + copying again.
    /// Caller MUST pair every successful call with <see cref="EndFrame"/>.
    /// Falls silently to per-call mode if the accelerator isn't ready —
    /// callers don't need to gate.</summary>
    public static void BeginFrame(uint[] colorBuffer, int width, int height)
    {
        if (_frameActive) return; // nested begin — keep outer frame
        if (!TryInit() || _acc == null) return;
        int n = width * height;
        if (colorBuffer.Length < n) return;
        try
        {
            var lease = GpuBufferPool.RentUint(_acc, n);
            lease.Buffer.View.SubView(0, n).CopyFromCPU(colorBuffer);
            _frameDColor = lease.Buffer;
            _frameDColorClass = SizeClassOf(lease.Buffer);
            _frameColorHost = colorBuffer;
            _frameN = n;
            _frameActive = true;
        }
        catch
        {
            _frameActive = false;
            _frameDColor = null;
            _frameColorHost = null;
        }
    }

    /// <summary>P6 — finish a bundled frame. Single Synchronize + CopyToCPU
    /// flushes every queued kernel back to the host color buffer.</summary>
    public static void EndFrame()
    {
        if (!_frameActive || _acc == null || _frameDColor == null || _frameColorHost == null)
        {
            _frameActive = false;
            _frameDColor = null;
            _frameColorHost = null;
            return;
        }
        try
        {
            _acc.Synchronize();
            _frameDColor.View.SubView(0, _frameN).CopyToCPU(_frameColorHost);
        }
        catch { /* host already has fallback content from CPU paths */ }
        finally
        {
            GpuBufferPoolReturn(_frameDColor, _frameDColorClass);
            _frameDColor = null;
            _frameColorHost = null;
            _frameActive = false;
        }
    }

    // Hand-off helper: round-trips through GpuBufferPool's internal Return so
    // the shared buffer goes back to the pool for reuse on the next frame.
    private static void GpuBufferPoolReturn(MemoryBuffer1D<uint, Stride1D.Dense> buf, int cls)
    {
        // No public Return — we already have a UintLease via Rent. Use a
        // throwaway lease wrapping (buf, cls) and Dispose it.
        var lease = new GpuBufferPool.UintLease(buf, cls);
        lease.Dispose();
    }

    private static int SizeClassOf(MemoryBuffer1D<uint, Stride1D.Dense> buf)
    {
        int n = (int)buf.Length;
        int s = 1; while (s < n) s <<= 1; return s;
    }

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
                _ssaoKernel        = _acc.LoadAutoGroupedStreamKernel<Index1D, ArrayView<float>, ArrayView<uint>, ArrayView<float>, int, int, int, float, float, float>(SsaoKernel);
                _thresholdKernel   = _acc.LoadAutoGroupedStreamKernel<Index1D, ArrayView<float>, ArrayView<float>, int, float>(ThresholdKernel);
                _downsampleKernel  = _acc.LoadAutoGroupedStreamKernel<Index1D, ArrayView<float>, ArrayView<float>, int, int, int, int>(DownsampleKernel);
                _blurHKernel       = _acc.LoadAutoGroupedStreamKernel<Index1D, ArrayView<float>, ArrayView<float>, int, int>(BlurHorizontalKernel);
                _blurVKernel       = _acc.LoadAutoGroupedStreamKernel<Index1D, ArrayView<float>, ArrayView<float>, int, int>(BlurVerticalKernel);
                _upsampleAddKernel = _acc.LoadAutoGroupedStreamKernel<Index1D, ArrayView<float>, ArrayView<float>, int, int, int, int, float>(UpsampleAddKernel);
                _compositeKernel   = _acc.LoadAutoGroupedStreamKernel<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<uint>, int, int, int, int, float, float>(CompositeKernel);
                _edgeKernel        = _acc.LoadAutoGroupedStreamKernel<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<uint>, int, int, int, float, float, float, float, float>(EdgeKernel);
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
            // P6 — share dColor across passes within a BeginFrame/EndFrame
            // batch. Skips the colorBuffer round-trip (CopyFromCPU +
            // Synchronize + CopyToCPU) for the second and third GPU pass.
            bool useFrame = _frameActive && ReferenceEquals(colorBuffer, _frameColorHost);
            GpuBufferPool.UintLease ownColor = default;
            ArrayView<uint> colorView;
            if (useFrame)
            {
                colorView = _frameDColor!.View;
            }
            else
            {
                ownColor = GpuBufferPool.RentUint(_acc, n);
                ownColor.Buffer.View.SubView(0, n).CopyFromCPU(colorBuffer);
                colorView = ownColor.View;
            }
            using var dDepth = GpuBufferPool.RentFloat(_acc, n);

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
            using var dOffs = GpuBufferPool.RentFloat(_acc, 2 * kSamples);

            dDepth.Buffer.View.SubView(0, n).CopyFromCPU(depthBuffer);
            dOffs.Buffer.View.SubView(0, 2 * kSamples).CopyFromCPU(offsets);

            _ssaoKernel(n,
                dDepth.View, colorView, dOffs.View,
                width, height, kSamples,
                (float)radiusPixels, (float)strength, (float)worldRadius);
            if (!useFrame)
            {
                _acc.Synchronize();
                ownColor.Buffer.View.SubView(0, n).CopyToCPU(colorBuffer);
                ownColor.Dispose();
            }
            // Batched: color stays device-resident until EndFrame Synchronize.
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Phase 12b — GPU tonemap + bloom. Mirrors the CPU pyramid:
    ///   1. Threshold-pass HDR → emissive (full res).
    ///   2. Two-mip pyramid: box downsample + 5-tap separable Gaussian per mip.
    ///   3. Upsample-add mip1 + mip2 back into an emissive full-res accumulator.
    ///   4. Composite kernel: HDR + emissive·bloomStrength → tonemap → gamma →
    ///      byte color. Sky pixels (HDR = NaN) pass through ColorBuffer
    ///      unchanged.
    /// Returns false on any failure (caller falls back to CPU path).
    /// </summary>
    public static bool TryApplyToneMapBloom(
        uint[] colorBuffer,
        float[] hdrBuffer,
        int width, int height,
        bool wantBloom,
        bool wantTonemap,
        int toneMapOp,
        double exposure,
        double bloomThresholdByteScale,
        double bloomStrength)
    {
        if (!wantBloom && !wantTonemap) return true; // no-op success
        if (!TryInit() || _acc == null
            || _thresholdKernel == null || _downsampleKernel == null
            || _blurHKernel == null || _blurVKernel == null
            || _upsampleAddKernel == null || _compositeKernel == null) return false;

        int n = width * height;
        if (colorBuffer.Length < n) return false;
        if (hdrBuffer.Length < 3 * n) return false;

        int w1 = Math.Max(1, width / 2);
        int h1 = Math.Max(1, height / 2);
        int w2 = Math.Max(1, width / 4);
        int h2 = Math.Max(1, height / 4);

        try
        {
            // P6 — share dColor with batched frame when in BeginFrame scope.
            bool useFrame = _frameActive && ReferenceEquals(colorBuffer, _frameColorHost);
            GpuBufferPool.UintLease ownColor = default;
            ArrayView<uint> colorView;
            if (useFrame)
            {
                colorView = _frameDColor!.View;
            }
            else
            {
                ownColor = GpuBufferPool.RentUint(_acc, n);
                ownColor.Buffer.View.SubView(0, n).CopyFromCPU(colorBuffer);
                colorView = ownColor.View;
            }
            using var dHdr     = GpuBufferPool.RentFloat(_acc, 3 * n);
            using var dEmiss   = GpuBufferPool.RentFloat(_acc, 3 * n);     // full-res accumulator
            using var dMip1A   = GpuBufferPool.RentFloat(_acc, 3 * w1 * h1);
            using var dMip1B   = GpuBufferPool.RentFloat(_acc, 3 * w1 * h1);
            using var dMip2A   = GpuBufferPool.RentFloat(_acc, 3 * w2 * h2);
            using var dMip2B   = GpuBufferPool.RentFloat(_acc, 3 * w2 * h2);

            dHdr.Buffer.View.SubView(0, 3 * n).CopyFromCPU(hdrBuffer);

            if (wantBloom)
            {
                // 1. Threshold → emissive full-res. Pixels above luminance threshold
                //    carry their HDR colour through, others go to zero.
                _thresholdKernel(n, dHdr.View, dEmiss.View, n, (float)bloomThresholdByteScale);

                // 2a. Downsample full → mip1 (box 2×2).
                _downsampleKernel(w1 * h1, dEmiss.View, dMip1A.View, width, height, w1, h1);
                // 2b. Separable blur on mip1: dMip1A → dMip1B (horizontal), dMip1B → dMip1A (vertical).
                _blurHKernel(w1 * h1, dMip1A.View, dMip1B.View, w1, h1);
                _blurVKernel(w1 * h1, dMip1B.View, dMip1A.View, w1, h1);

                // 2c. Downsample mip1 → mip2.
                _downsampleKernel(w2 * h2, dMip1A.View, dMip2A.View, w1, h1, w2, h2);
                _blurHKernel(w2 * h2, dMip2A.View, dMip2B.View, w2, h2);
                _blurVKernel(w2 * h2, dMip2B.View, dMip2A.View, w2, h2);

                // 3. Upsample-add. CPU code weights: emissive(1.0), mip1(0.7), mip2(0.5).
                //    Emissive already contains the threshold pass result at weight 1.0.
                _upsampleAddKernel(n, dMip1A.View, dEmiss.View, w1, h1, width, height, 0.7f);
                _upsampleAddKernel(n, dMip2A.View, dEmiss.View, w2, h2, width, height, 0.5f);
            }

            // 4. Composite: HDR + emissive·bloomStrength → tonemap → gamma → byte color.
            //    wantBloom/wantTonemap baked into ints so the kernel doesn't carry bool.
            _compositeKernel(n,
                dHdr.View, dEmiss.View, colorView,
                width, height, toneMapOp,
                wantBloom ? 1 : 0,
                (float)exposure, (float)bloomStrength);
            if (!useFrame)
            {
                _acc.Synchronize();
                ownColor.Buffer.View.SubView(0, n).CopyToCPU(colorBuffer);
                ownColor.Dispose();
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Phase 12c — GPU edge ink (Sobel + Frei-Chen). Modulates ColorBuffer in
    /// place. EdgeKernelMode 0 = Sobel, 1 = Frei-Chen.
    /// Returns false on any failure (caller falls back to CPU path).
    /// </summary>
    public static bool TryApplyEdgeInk(
        uint[] colorBuffer,
        float[] depthBuffer,
        float[] normalBuffer,
        int width, int height,
        double strength, double threshold,
        uint inkColor,
        int kernelMode)
    {
        if (strength <= 0) return true;
        if (!TryInit() || _acc == null || _edgeKernel == null) return false;

        int n = width * height;
        if (colorBuffer.Length < n || depthBuffer.Length < n || normalBuffer.Length < 3 * n) return false;

        try
        {
            // P6 — share dColor with batched frame when in BeginFrame scope.
            bool useFrame = _frameActive && ReferenceEquals(colorBuffer, _frameColorHost);
            GpuBufferPool.UintLease ownColor = default;
            ArrayView<uint> colorView;
            if (useFrame)
            {
                colorView = _frameDColor!.View;
            }
            else
            {
                ownColor = GpuBufferPool.RentUint(_acc, n);
                ownColor.Buffer.View.SubView(0, n).CopyFromCPU(colorBuffer);
                colorView = ownColor.View;
            }
            using var dDepth  = GpuBufferPool.RentFloat(_acc, n);
            using var dNormal = GpuBufferPool.RentFloat(_acc, 3 * n);
            dDepth.Buffer.View.SubView(0, n).CopyFromCPU(depthBuffer);
            dNormal.Buffer.View.SubView(0, 3 * n).CopyFromCPU(normalBuffer);

            float inkR = ((inkColor >> 16) & 0xFFu);
            float inkG = ((inkColor >>  8) & 0xFFu);
            float inkB = ( inkColor        & 0xFFu);

            _edgeKernel(n,
                dDepth.View, dNormal.View, colorView,
                width, height, kernelMode,
                (float)strength, (float)threshold,
                inkR, inkG, inkB);
            if (!useFrame)
            {
                _acc.Synchronize();
                ownColor.Buffer.View.SubView(0, n).CopyToCPU(colorBuffer);
                ownColor.Dispose();
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ── Kernels ───────────────────────────────────────────────────────────

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

    /// <summary>Bloom step 1 — bright pass. emiss[i3..i3+2] = hdr if luma>thresh else 0.
    /// Sky pixels (hdr.R is NaN) → zero. n = pixel count.</summary>
    private static void ThresholdKernel(
        Index1D idx,
        ArrayView<float> hdr,
        ArrayView<float> emiss,
        int n,
        float thresholdByteScale)
    {
        int i = idx.X;
        if (i >= n) return;
        int i3 = i * 3;
        float r = hdr[i3];
        // NaN check via self-compare. NaN != NaN is true on every IEEE impl.
        // ILGPU kernel — float.IsNaN call would emit unsupported intrinsic.
#pragma warning disable CS1718
        if (r != r) { emiss[i3] = 0f; emiss[i3 + 1] = 0f; emiss[i3 + 2] = 0f; return; }
#pragma warning restore CS1718
        float g = hdr[i3 + 1];
        float b = hdr[i3 + 2];
        float luma = 0.299f * r + 0.587f * g + 0.114f * b;
        if (luma > thresholdByteScale)
        {
            emiss[i3] = r; emiss[i3 + 1] = g; emiss[i3 + 2] = b;
        }
        else
        {
            emiss[i3] = 0f; emiss[i3 + 1] = 0f; emiss[i3 + 2] = 0f;
        }
    }

    /// <summary>Bloom step 2a — box downsample. dst[w*y+x] = average of src
    /// 2×2 block at (2x, 2y). One dispatch covers all dst pixels.</summary>
    private static void DownsampleKernel(
        Index1D idx,
        ArrayView<float> src,
        ArrayView<float> dst,
        int srcW, int srcH,
        int dstW, int dstH)
    {
        int i = idx.X;
        if (i >= dstW * dstH) return;
        int x = i % dstW;
        int y = i / dstW;
        int sx0 = x * 2; if (sx0 >= srcW) sx0 = srcW - 1;
        int sy0 = y * 2; if (sy0 >= srcH) sy0 = srcH - 1;
        int sx1 = sx0 + 1; if (sx1 >= srcW) sx1 = srcW - 1;
        int sy1 = sy0 + 1; if (sy1 >= srcH) sy1 = srcH - 1;
        int i00 = (sy0 * srcW + sx0) * 3;
        int i10 = (sy0 * srcW + sx1) * 3;
        int i01 = (sy1 * srcW + sx0) * 3;
        int i11 = (sy1 * srcW + sx1) * 3;
        int d = i * 3;
        dst[d]     = 0.25f * (src[i00]     + src[i10]     + src[i01]     + src[i11]);
        dst[d + 1] = 0.25f * (src[i00 + 1] + src[i10 + 1] + src[i01 + 1] + src[i11 + 1]);
        dst[d + 2] = 0.25f * (src[i00 + 2] + src[i10 + 2] + src[i01 + 2] + src[i11 + 2]);
    }

    /// <summary>Bloom step 2b/2c — 5-tap horizontal Gaussian. Kernel matches
    /// CPU [0.06, 0.244, 0.392, 0.244, 0.06].</summary>
    private static void BlurHorizontalKernel(
        Index1D idx,
        ArrayView<float> src,
        ArrayView<float> dst,
        int w, int h)
    {
        int i = idx.X;
        if (i >= w * h) return;
        int x = i % w;
        int y = i / w;
        float r = 0f, g = 0f, b = 0f;
        for (int t = -2; t <= 2; t++)
        {
            int sx = x + t; if (sx < 0) sx = 0; else if (sx >= w) sx = w - 1;
            int si = (y * w + sx) * 3;
            float kv = TapWeight(t);
            r += src[si] * kv;
            g += src[si + 1] * kv;
            b += src[si + 2] * kv;
        }
        int o = i * 3;
        dst[o] = r; dst[o + 1] = g; dst[o + 2] = b;
    }

    /// <summary>Bloom step 2b/2c — 5-tap vertical Gaussian.</summary>
    private static void BlurVerticalKernel(
        Index1D idx,
        ArrayView<float> src,
        ArrayView<float> dst,
        int w, int h)
    {
        int i = idx.X;
        if (i >= w * h) return;
        int x = i % w;
        int y = i / w;
        float r = 0f, g = 0f, b = 0f;
        for (int t = -2; t <= 2; t++)
        {
            int sy = y + t; if (sy < 0) sy = 0; else if (sy >= h) sy = h - 1;
            int si = (sy * w + x) * 3;
            float kv = TapWeight(t);
            r += src[si] * kv;
            g += src[si + 1] * kv;
            b += src[si + 2] * kv;
        }
        int o = i * 3;
        dst[o] = r; dst[o + 1] = g; dst[o + 2] = b;
    }

    /// <summary>Bloom step 3 — bilinear sample <paramref name="src"/> (mip
    /// w×h) and add into <paramref name="dst"/> (full-res W×H) at the
    /// corresponding pixel, weighted by <paramref name="weight"/>.</summary>
    private static void UpsampleAddKernel(
        Index1D idx,
        ArrayView<float> src,
        ArrayView<float> dst,
        int srcW, int srcH,
        int dstW, int dstH,
        float weight)
    {
        int i = idx.X;
        if (i >= dstW * dstH) return;
        int x = i % dstW;
        int y = i / dstW;
        float sx = (float)x * ((float)srcW / (float)dstW);
        float sy = (float)y * ((float)srcH / (float)dstH);
        int x0 = (int)MathF.Floor(sx);
        int y0 = (int)MathF.Floor(sy);
        if (x0 < 0) x0 = 0; else if (x0 >= srcW) x0 = srcW - 1;
        if (y0 < 0) y0 = 0; else if (y0 >= srcH) y0 = srcH - 1;
        int x1 = x0 + 1; if (x1 >= srcW) x1 = srcW - 1;
        int y1 = y0 + 1; if (y1 >= srcH) y1 = srcH - 1;
        float fx = sx - (float)x0; if (fx < 0f) fx = 0f; else if (fx > 1f) fx = 1f;
        float fy = sy - (float)y0; if (fy < 0f) fy = 0f; else if (fy > 1f) fy = 1f;
        int i00 = (y0 * srcW + x0) * 3;
        int i10 = (y0 * srcW + x1) * 3;
        int i01 = (y1 * srcW + x0) * 3;
        int i11 = (y1 * srcW + x1) * 3;
        float w00 = (1f - fx) * (1f - fy) * weight;
        float w10 = fx        * (1f - fy) * weight;
        float w01 = (1f - fx) * fy        * weight;
        float w11 = fx        * fy        * weight;
        int o = i * 3;
        dst[o]     += src[i00]     * w00 + src[i10]     * w10 + src[i01]     * w01 + src[i11]     * w11;
        dst[o + 1] += src[i00 + 1] * w00 + src[i10 + 1] * w10 + src[i01 + 1] * w01 + src[i11 + 1] * w11;
        dst[o + 2] += src[i00 + 2] * w00 + src[i10 + 2] * w10 + src[i01 + 2] * w01 + src[i11 + 2] * w11;
    }

    /// <summary>Bloom step 4 — HDR + emissive·bloomStrength → tonemap → gamma
    /// → byte color. <paramref name="op"/> matches the CPU ToneMapOperator
    /// enum: 0 None, 1 Reinhard, 2 ReinhardExtended, 3 Aces. Sky pixels
    /// (hdr.R = NaN) leave the ColorBuffer untouched.</summary>
    private static void CompositeKernel(
        Index1D idx,
        ArrayView<float> hdr,
        ArrayView<float> emiss,
        ArrayView<uint> color,
        int width, int height,
        int op,
        int wantBloom,
        float exposure,
        float bloomStrength)
    {
        int i = idx.X;
        if (i >= width * height) return;
        int i3 = i * 3;
        float hr = hdr[i3];
        // NaN check via self-compare — see ThresholdKernel.
#pragma warning disable CS1718
        if (hr != hr) return; // sky — keep ColorBuffer byte
#pragma warning restore CS1718
        float hg = hdr[i3 + 1];
        float hb = hdr[i3 + 2];
        if (wantBloom != 0)
        {
            hr += emiss[i3]     * bloomStrength;
            hg += emiss[i3 + 1] * bloomStrength;
            hb += emiss[i3 + 2] * bloomStrength;
        }
        float linR = hr / 255f * exposure;
        float linG = hg / 255f * exposure;
        float linB = hb / 255f * exposure;
        float tmR, tmG, tmB;
        if (op == 1)
        {
            tmR = linR / (1f + linR);
            tmG = linG / (1f + linG);
            tmB = linB / (1f + linB);
        }
        else if (op == 2)
        {
            const float Lw2 = 16f;
            tmR = linR * (1f + linR / Lw2) / (1f + linR);
            tmG = linG * (1f + linG / Lw2) / (1f + linG);
            tmB = linB * (1f + linB / Lw2) / (1f + linB);
        }
        else if (op == 3)
        {
            tmR = AcesScalarF(linR);
            tmG = AcesScalarF(linG);
            tmB = AcesScalarF(linB);
        }
        else
        {
            tmR = linR; tmG = linG; tmB = linB;
        }
        if (op != 0)
        {
            // Gamma 2.2 encode after tonemap.
            if (tmR < 0f) tmR = 0f; else if (tmR > 1f) tmR = 1f;
            if (tmG < 0f) tmG = 0f; else if (tmG > 1f) tmG = 1f;
            if (tmB < 0f) tmB = 0f; else if (tmB > 1f) tmB = 1f;
            tmR = MathF.Pow(tmR, 1f / 2.2f);
            tmG = MathF.Pow(tmG, 1f / 2.2f);
            tmB = MathF.Pow(tmB, 1f / 2.2f);
        }
        else
        {
            if (tmR < 0f) tmR = 0f; else if (tmR > 1f) tmR = 1f;
            if (tmG < 0f) tmG = 0f; else if (tmG > 1f) tmG = 1f;
            if (tmB < 0f) tmB = 0f; else if (tmB > 1f) tmB = 1f;
        }
        float fR = tmR * 255f; if (fR < 0f) fR = 0f; else if (fR > 255f) fR = 255f;
        float fG = tmG * 255f; if (fG < 0f) fG = 0f; else if (fG > 255f) fG = 255f;
        float fB = tmB * 255f; if (fB < 0f) fB = 0f; else if (fB > 255f) fB = 255f;
        uint R = (uint)fR;
        uint G = (uint)fG;
        uint B = (uint)fB;
        color[i] = 0xFF000000u | (R << 16) | (G << 8) | B;
    }

    /// <summary>Phase 12c — edge-ink kernel. <paramref name="kernelMode"/>:
    /// 0 = Sobel, 1 = Frei-Chen. Skip sky neighbours so silhouettes don't
    /// saturate; reads + writes per-pixel.</summary>
    private static void EdgeKernel(
        Index1D idx,
        ArrayView<float> depth,
        ArrayView<float> normal,
        ArrayView<uint> color,
        int width, int height,
        int kernelMode,
        float strength, float threshold,
        float inkR, float inkG, float inkB)
    {
        int i = idx.X;
        if (i >= width * height) return;
        int x = i % width;
        int y = i / width;
        if (x <= 0 || y <= 0 || x >= width - 1 || y >= height - 1) return;
        if (depth[i] > 1e30f) return;

        int i00 = (y - 1) * width + (x - 1);
        int i10 = (y - 1) * width +  x;
        int i20 = (y - 1) * width + (x + 1);
        int i01 =  y      * width + (x - 1);
        int i21 =  y      * width + (x + 1);
        int i02 = (y + 1) * width + (x - 1);
        int i12 = (y + 1) * width +  x;
        int i22 = (y + 1) * width + (x + 1);

        if (depth[i00] > 1e30f || depth[i10] > 1e30f || depth[i20] > 1e30f
            || depth[i01] > 1e30f || depth[i21] > 1e30f
            || depth[i02] > 1e30f || depth[i12] > 1e30f || depth[i22] > 1e30f) return;

        // Indices into normal buffer (3 floats / pixel).
        int n00 = i00 * 3, n10 = i10 * 3, n20 = i20 * 3;
        int n01 = i01 * 3,                n21 = i21 * 3;
        int n02 = i02 * 3, n12 = i12 * 3, n22 = i22 * 3;

        float sumProj2 = 0f;
        const float s = 1.4142135f;          // √2
        const float k = 0.35355339f;         // 1/(2√2)

        for (int c = 0; c < 3; c++)
        {
            float v00 = normal[n00 + c];
            float v10 = normal[n10 + c];
            float v20 = normal[n20 + c];
            float v01 = normal[n01 + c];
            float v21 = normal[n21 + c];
            float v02 = normal[n02 + c];
            float v12 = normal[n12 + c];
            float v22 = normal[n22 + c];

            if (kernelMode == 1)
            {
                float p1 = (v00 + s * v10 + v20 - v02 - s * v12 - v22) * k;
                float p2 = (v00 + s * v01 + v02 - v20 - s * v21 - v22) * k;
                float p3 = (-v10 + s * v20 + v01 - v21 - s * v02 + v12) * k;
                float p4 = (s * v00 - v10 - v01 + v21 + v12 - s * v22) * k;
                sumProj2 += p1 * p1 + p2 * p2 + p3 * p3 + p4 * p4;
            }
            else
            {
                float gx = (-v00 + v20) + 2f * (-v01 + v21) + (-v02 + v22);
                float gy = (-v00 - 2f * v10 - v20) + (v02 + 2f * v12 + v22);
                sumProj2 += gx * gx + gy * gy;
            }
        }
        float mag = MathF.Sqrt(sumProj2);
        if (mag <= threshold) return;

        float range = 1f - threshold;
        if (range < 1e-3f) range = 1e-3f;
        float alpha = (mag - threshold) / range;
        if (alpha < 0f) alpha = 0f; else if (alpha > 1f) alpha = 1f;
        alpha *= strength;
        if (alpha <= 0f) return;

        uint d = color[i];
        float dR = (float)((d >> 16) & 0xFFu);
        float dG = (float)((d >>  8) & 0xFFu);
        float dB = (float)( d        & 0xFFu);
        float oR = dR * (1f - alpha) + inkR * alpha;
        float oG = dG * (1f - alpha) + inkG * alpha;
        float oB = dB * (1f - alpha) + inkB * alpha;
        if (oR < 0f) oR = 0f; else if (oR > 255f) oR = 255f;
        if (oG < 0f) oG = 0f; else if (oG > 255f) oG = 255f;
        if (oB < 0f) oB = 0f; else if (oB > 255f) oB = 255f;
        color[i] = 0xFF000000u | ((uint)oR << 16) | ((uint)oG << 8) | (uint)oB;
    }

    /// <summary>5-tap Gaussian weights matching the CPU kernel.</summary>
    private static float TapWeight(int t)
    {
        // [0.06, 0.244, 0.392, 0.244, 0.06]
        if (t == -2 || t == 2) return 0.06f;
        if (t == -1 || t == 1) return 0.244f;
        return 0.392f;
    }

    /// <summary>Narkowicz 2015 ACES fit (float). Mirrors CPU AcesScalar.</summary>
    private static float AcesScalarF(float x)
    {
        const float A = 2.51f, B = 0.03f, C = 2.43f, D = 0.59f, E = 0.14f;
        float v = (x * (A * x + B)) / (x * (C * x + D) + E);
        if (v < 0f) v = 0f; else if (v > 1f) v = 1f;
        return v;
    }

    /// <summary>Release the static accelerator + context. Optional — process
    /// exit reclaims everything regardless. Tests or hosts that recycle the
    /// engine may want to call this explicitly.</summary>
    public static void Dispose()
    {
        lock (_initLock)
        {
            _ssaoKernel = null;
            _thresholdKernel = null;
            _downsampleKernel = null;
            _blurHKernel = null;
            _blurVKernel = null;
            _upsampleAddKernel = null;
            _compositeKernel = null;
            _edgeKernel = null;
            _acc?.Dispose(); _acc = null;
            _ctx?.Dispose(); _ctx = null;
            _initFailed = false;
        }
    }
}
