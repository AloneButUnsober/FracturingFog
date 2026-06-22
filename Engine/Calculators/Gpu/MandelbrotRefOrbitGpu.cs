// MandelbrotRefOrbitGpu.cs
//
// Wave 2.12 (D-6.27) — GPU reference orbit for the Mandelbrot perturbation
// path. Iterates Z_{n+1} = Z² + C on a single ILGPU thread and writes the
// orbit back to the host. Sequential dependency — no per-step parallelism;
// the win is offloading the QD chain from the CPU thread so the per-pixel
// PT inner loop can overlap.
//
// Kernel signature uses a packed RefOrbitSlot struct (8 doubles per slot,
// holding the 4 limbs of zr + zi) so the typed kernel loader stays at 4
// generic params instead of 11. Single ArrayView<RefOrbitSlot> output is
// cleaner than 8 parallel ArrayView<double>.
//
// First cut iterates in plain Hi-only doubles to validate the kernel path
// end-to-end. The QD upgrade (using GpuQDMath) lives in a follow-on slice
// once we confirm whether ILGPU 1.5.3 will JIT the deep tuple chains in
// Renorm5/ThreeSum or whether a struct-output rewrite of those primitives
// is required.

using System;
using System.Linq;

using ILGPU;
using ILGPU.Runtime;

namespace FracturingFog.Calculators.Gpu;

/// <summary>Packed reference-orbit slot — 8 doubles per iter holding the
/// 4 limbs of (zr, zi). Used as the output element type of the GPU ref-orbit
/// kernel. Field order must stay blittable (no reference types).</summary>
public struct RefOrbitSlot
{
    public double ZrX0, ZrX1, ZrX2, ZrX3;
    public double ZiX0, ZiX1, ZiX2, ZiX3;
}

/// <summary>Kernel parameters for the QD reference-orbit pass.</summary>
public struct MandelbrotRefOrbitParams
{
    // QD center C = (cx, cy). All 4 limbs packed inline.
    public double CxX0, CxX1, CxX2, CxX3;
    public double CyX0, CyX1, CyX2, CyX3;
    public int MaxIter;
    public double EscapeRadius2;  // |Z|² escape threshold (Hi-limb only)
}

/// <summary>
/// Single-orbit Mandelbrot reference-orbit GPU kernel + host shim.
/// </summary>
public sealed class MandelbrotRefOrbitGpu : IDisposable
{
    private Action<Index1D, ArrayView<RefOrbitSlot>, ArrayView<int>, MandelbrotRefOrbitParams>? _kernel;
    private bool _initFailed;
    public string LastError { get; private set; } = string.Empty;

    // Private FP64-capable accelerator. The shared GpuAcceleratorHost picks
    // the ILGPU-preferred non-CPU device, which on dev machines with an
    // Intel iGPU lands on Intel UHD OpenCL — no FP64 support, our kernel
    // can't compile. Walk devices CUDA → OpenCL(FP64-capable) → CPU
    // explicitly so the QD ref orbit always lands on a 64-bit-FP backend.
    private Context? _ownCtx;
    private Accelerator? _ownAcc;
    public string SelectedDeviceLabel { get; private set; } = string.Empty;

    private bool TryAcquireFp64(out Accelerator acc)
    {
        if (_ownAcc != null) { acc = _ownAcc; return true; }
        try
        {
            _ownCtx = Context.Create(b => b.Default());
            var devices = _ownCtx.Devices.ToList();
            Device? picked = devices.FirstOrDefault(d => d.AcceleratorType == AcceleratorType.Cuda);
            if (picked == null)
            {
                // OpenCL devices vary in FP64 support — without a cheap probe
                // we'd JIT-fail same as before. Skip OpenCL by default; fall
                // straight to CPU which always has FP64 (slower than CUDA
                // but still uses ILGPU's vectorized backend).
                picked = devices.FirstOrDefault(d => d.AcceleratorType == AcceleratorType.CPU);
            }
            if (picked == null)
            {
                LastError = "GPU ref-orbit: no CUDA or CPU device available.";
                _ownCtx.Dispose(); _ownCtx = null;
                acc = null!;
                return false;
            }
            _ownAcc = picked.CreateAccelerator(_ownCtx);
            SelectedDeviceLabel = $"{picked.AcceleratorType} — {picked.Name}";
            acc = _ownAcc;
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"GPU ref-orbit accelerator init failed: {ex.Message}";
            _ownAcc?.Dispose(); _ownAcc = null;
            _ownCtx?.Dispose(); _ownCtx = null;
            acc = null!;
            return false;
        }
    }

    private bool TryInit()
    {
        if (_kernel != null) return true;
        if (_initFailed) return false;
        if (!TryAcquireFp64(out var acc))
        {
            _initFailed = true;
            return false;
        }
        try
        {
            _kernel = acc.LoadAutoGroupedStreamKernel<
                Index1D, ArrayView<RefOrbitSlot>, ArrayView<int>, MandelbrotRefOrbitParams>(RefOrbitKernel);
            return true;
        }
        catch (Exception ex)
        {
            // Surface the inner exception chain too — ILGPU's outer message
            // is often "internal compiler error" with the actual cause (FP64
            // unsupported, device mismatch) one or two levels in.
            string detail = ex.Message;
            var inner = ex.InnerException;
            int depth = 0;
            while (inner != null && depth < 3)
            {
                detail += "  ←  " + inner.Message;
                inner = inner.InnerException;
                depth++;
            }
            LastError = $"GPU ref-orbit kernel load failed: {detail}";
            _initFailed = true;
            return false;
        }
    }

    /// <summary>Compute the reference orbit on the GPU. Host arrays must be
    /// sized to at least <paramref name="maxIter"/> + 1 slots (matches the
    /// CPU calculator's <c>EnsureRefOrbitCapacity(maxIter)</c> contract).
    /// On success returns <c>orbitLen = n</c> (escape iter, or maxIter if
    /// no escape). Failure returns false; caller falls back to CPU.</summary>
    public bool Compute(
        double cxX0, double cxX1, double cxX2, double cxX3,
        double cyX0, double cyX1, double cyX2, double cyX3,
        int maxIter, double escapeRadius2,
        double[] refZrX0, double[] refZrX1, double[] refZrX2, double[] refZrX3,
        double[] refZiX0, double[] refZiX1, double[] refZiX2, double[] refZiX3,
        out int orbitLen, out bool escaped)
    {
        orbitLen = 0;
        escaped = false;
        if (!TryInit() || _kernel == null) return false;
        if (!TryAcquireFp64(out var acc)) return false;

        int slots = maxIter + 1;
        if (refZrX0.Length < slots || refZiX0.Length < slots)
        {
            LastError = $"GPU ref-orbit: host buffers too small ({refZrX0.Length} < {slots}).";
            return false;
        }

        try
        {
            using var dSlots = acc.Allocate1D<RefOrbitSlot>(slots);
            using var dInfo = acc.Allocate1D<int>(2);

            var p = new MandelbrotRefOrbitParams
            {
                CxX0 = cxX0, CxX1 = cxX1, CxX2 = cxX2, CxX3 = cxX3,
                CyX0 = cyX0, CyX1 = cyX1, CyX2 = cyX2, CyX3 = cyX3,
                MaxIter = maxIter,
                EscapeRadius2 = escapeRadius2,
            };

            // Single sequential workload — launch one thread.
            _kernel(1, dSlots.View, dInfo.View, p);
            acc.Synchronize();

            int[] info = new int[2];
            dInfo.CopyToCPU(info);
            int n = info[0];
            if (n < 0 || n > maxIter)
            {
                LastError = $"GPU ref-orbit returned invalid orbitLen={n}";
                return false;
            }
            int copyLen = n + 1;

            RefOrbitSlot[] hostSlots = new RefOrbitSlot[slots];
            dSlots.CopyToCPU(hostSlots);
            for (int k = 0; k < copyLen; k++)
            {
                refZrX0[k] = hostSlots[k].ZrX0;
                refZrX1[k] = hostSlots[k].ZrX1;
                refZrX2[k] = hostSlots[k].ZrX2;
                refZrX3[k] = hostSlots[k].ZrX3;
                refZiX0[k] = hostSlots[k].ZiX0;
                refZiX1[k] = hostSlots[k].ZiX1;
                refZiX2[k] = hostSlots[k].ZiX2;
                refZiX3[k] = hostSlots[k].ZiX3;
            }
            orbitLen = n;
            escaped = info[1] != 0;
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"GPU ref-orbit run failed: {ex.Message}";
            return false;
        }
    }

    private static void RefOrbitKernel(
        Index1D idx,
        ArrayView<RefOrbitSlot> slots,
        ArrayView<int> info,
        MandelbrotRefOrbitParams p)
    {
        // Single-thread kernel — guard against accidental multi-launch.
        if (idx != 0) return;

        // First cut — Hi-only ref orbit. QD upgrade tracked in a follow-on
        // slice; see file comment for ILGPU 1.5.3 tuple-chain caveat.
        double cxH = p.CxX0;
        double cyH = p.CyX0;
        double zrH = 0.0;
        double ziH = 0.0;

        int maxIter = p.MaxIter;
        double er2 = p.EscapeRadius2;

        int n = 0;
        for (n = 0; n < maxIter; n++)
        {
            RefOrbitSlot s = default;
            s.ZrX0 = zrH; s.ZiX0 = ziH;
            slots[n] = s;
            if (zrH * zrH + ziH * ziH >= er2) break;

            double newZi = 2.0 * zrH * ziH + cyH;
            double newZr = zrH * zrH - ziH * ziH + cxH;
            zrH = newZr;
            ziH = newZi;
        }
        {
            RefOrbitSlot s = default;
            s.ZrX0 = zrH; s.ZiX0 = ziH;
            slots[n] = s;
        }
        info[0] = n;
        info[1] = (n < maxIter) ? 1 : 0;
    }

    public void Dispose()
    {
        _kernel = null;
        _ownAcc?.Dispose(); _ownAcc = null;
        _ownCtx?.Dispose(); _ownCtx = null;
    }
}
