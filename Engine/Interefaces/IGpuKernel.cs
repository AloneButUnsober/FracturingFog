// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Engine/Interefaces/IGpuKernel.cs
//
// Interface boundary between the cross-platform engine (calculators) and
// the platform-specific GPU compute backend. Extracted from
// MandelbrotGpuKernel in Phase X.0 / Slice 0.1b so the calculator fleet no
// longer holds a direct reference to a Vortice D3D11-bound type. The
// concrete D3D11 implementation (MandelbrotGpuKernel) lives in the
// Windows-only FracturingFog.Rendering.D3D assembly.
//
// Cross-platform compute backends (an ILGPU-based kernel, a Silk.NET
// compute-shader path, a future Metal/Vulkan backend) implement this same
// interface and slot in at the same calculator boundary.

using System;

namespace FracturingFog.Rendering
{
    /// <summary>
    /// Which fractal escape-time recurrence the GPU kernel evaluates.
    /// Kept narrow on purpose — only the families the GPU shader hard-codes
    /// today. CPU calculators support a much wider catalogue via their own
    /// IFractalKernel dispatch.
    /// </summary>
    public enum FractalKind
    {
        Mandelbrot = 0,
        Julia = 1,
        BurningShip = 2,
        Tricorn = 3,
        /// <summary>#1173-I — z^d + c; param0 = the integer exponent d.</summary>
        Multibrot = 4,
        /// <summary>#1173-I — z^2 + c + p·z_prev; (param0, param1) = p.</summary>
        Phoenix = 5,
    }

    /// <summary>#1173-I — the #253 cross-fractal domain warp
    /// (<c>FractalDomainWarp.Apply</c>) for a GPU frame: each pixel's offset from the
    /// view centre is displaced before it iterates. <see cref="Strength"/> 0 = off
    /// (the default; the kernel's un-warped c is untouched). <see cref="K"/> is the
    /// field's angular rate (3 · frequency, 3 when the frequency is ≤ 0) and
    /// <see cref="HalfSpan"/> half the longer view span in plane units.</summary>
    public readonly record struct GpuDomainWarp(float Strength, float K, float HalfSpan)
    {
        public bool Active => Strength != 0f && HalfSpan > 0f;
    }

    /// <summary>#838 — one Buddhabrot-family uniform sample batch
    /// (<see cref="IGpuKernel.RunBuddhaBatch"/>, and its CPU twin
    /// <see cref="BuddhaUniformSampler"/>). <see cref="Samples"/> c values are drawn
    /// over the fixed domain from a stream keyed by (<see cref="Seed"/>,
    /// <see cref="Batch"/>, sample index); hits go to three bands split at
    /// <see cref="Low"/> / <see cref="Mid"/> on the class iteration. #1218 — an
    /// escaping orbit is drawn only when it escapes at or after
    /// <see cref="MinIter"/>.</summary>
    public readonly record struct GpuBuddhaBatch(
        int Width, int Height, double Scale, double MidX, double MidY,
        int MaxOrbit, bool InSet, bool HighDefinition, int Low, int Mid,
        uint Seed, int Batch, int Samples, int MinIter = 0);

    /// <summary>
    /// Per-pixel SP escape-time compute backend exposed to the calculator
    /// fleet. Implementations are thread-affine — a single caller drives
    /// Run() from the calc thread; serialisation with the renderer's
    /// immediate context is the implementation's concern.
    ///
    /// Phase 4 of the Mandelbrot GPU path adds the optional <c>colorDst</c>
    /// out-buffer: when non-null AND <see cref="HasGpuPalette"/> is true,
    /// the kernel writes packed BGRA directly and the calculator skips its
    /// CPU palette pass.
    /// </summary>
    public interface IGpuKernel : IDisposable
    {
        /// <summary>True when SetPalette has been called with a non-null,
        /// HLSL/SPIR-V-compatible palette AND the implementation cached a
        /// compiled colour-emitting shader for it.</summary>
        bool HasGpuPalette { get; }

        /// <summary>Last dispatch latency in milliseconds (host-side wall
        /// clock around the Map/Dispatch/Unmap cycle). For perf overlays.</summary>
        double LastDispatchMs { get; }

        /// <summary>Last readback latency in milliseconds.</summary>
        double LastReadbackMs { get; }

        /// <summary>Activate a GPU-side palette evaluator. Pass null to
        /// disable the colour write path and fall back to CPU palette pass.
        /// Multiple palettes are compiled lazily and cached by PaletteId.</summary>
        void SetPalette(FracturingFog.Interefaces.IGpuHlslPalette? palette);

        /// <summary>Run the SP escape-time kernel. Output buffers are
        /// filled in-place; callers must size them to width*height before
        /// the call. #1173-J — <paramref name="trapDst"/>, when non-null and the
        /// active palette is an orbit palette (<see cref="IGpuOrbitPalette"/>), receives
        /// the per-pixel orbit-trap minimum the CPU orbit path writes to
        /// <c>TrapBuffer</c> (0 for in-set pixels); otherwise it is left untouched.</summary>
        void Run(
            int width, int height,
            double centerX, double centerY,
            double scale, int maxIter, double bailout2,
            int[] iterDst, float[] smoothDst,
            float[] finalZrDst, float[] finalZiDst,
            float[] finalDrDst, float[] finalDiDst,
            int[]? perRowMaxIter = null,
            FractalKind kind = FractalKind.Mandelbrot,
            float param0 = 0f, float param1 = 0f,
            uint[]? colorDst = null,
            float[]? trapDst = null,
            GpuDomainWarp warp = default);

        /// <summary>True when this backend can run the deep-zoom PERTURBATION
        /// kernel (<see cref="RunPerturb"/>) — i.e. the device has FP64 support
        /// and the double perturbation shader compiled. Backends that only carry
        /// the shallow FP32 escape-time kernel return false, and the calculator
        /// keeps deep zoom on the CPU. See issue #82 / dev-plan §14.</summary>
        bool SupportsPerturbation => false;

        /// <summary>#1173-M — short backend name for the HUD / status bar
        /// ("D3D11", "Vulkan").</summary>
        string BackendLabel => GetType().Name;

        /// <summary>Run the deep-zoom perturbation kernel over a precomputed
        /// reference orbit (Hi-limb doubles, length <paramref name="refLen"/>),
        /// the GPU twin of <c>MandelbrotCalculator.ComputePixelPTRebased</c>.
        /// Fills <paramref name="iterDst"/> + smooth + finalZD (zr,zi,drv,div)
        /// so the caller drives colour/dist/normal writeback itself. dc for
        /// pixel (x,y) = (offsetX0 + x, offsetY0 + y) · scale. Only valid when
        /// <see cref="SupportsPerturbation"/> is true.</summary>
        void RunPerturb(
            int width, int height,
            double scale, int maxIter, double escapeRadius2,
            double offsetX0, double offsetY0,
            double[] refZr, double[] refZi, int refLen,
            int[] iterDst, float[] smoothDst,
            float[] finalZrDst, float[] finalZiDst,
            float[] finalDrDst, float[] finalDiDst)
            => throw new NotSupportedException(
                "This GPU kernel has no perturbation path (SupportsPerturbation is false).");

        /// <summary>#88 / G4.5 — true when this backend can run the perturbation kernel
        /// with a Series-Approximation prelude (<see cref="RunPerturbSA"/>).</summary>
        bool SupportsPerturbationSA => false;

        /// <summary>#88 / G4.5 — <see cref="RunPerturb"/> with a Series-Approximation
        /// prelude: each pixel first skips analytically to iteration k with the uploaded
        /// <c>SeriesApproximation</c> coefficients (A/B/C/D, length ≥ refLen + 1; k from
        /// the same FindSkip test as the CPU, valid up to <paramref name="safeMax"/>),
        /// then runs the same rebased δ loop. Only valid when
        /// <see cref="SupportsPerturbationSA"/> is true. <paramref name="safeMax"/> 0
        /// disables the SA skip. #88 / G4.5b — <paramref name="blaCoeffs"/>
        /// (<c>BlaTable.GpuCoefficients</c>: 5 doubles per entry, A re/im, B re/im, r²)
        /// with <paramref name="blaLevels"/> &gt; 1 adds the BLA skip inside the loop;
        /// null / 0 leaves it off.</summary>
        void RunPerturbSA(
            int width, int height,
            double scale, int maxIter, double escapeRadius2,
            double offsetX0, double offsetY0,
            double[] refZr, double[] refZi, int refLen,
            double saTolerance, int safeMax,
            double[] aR, double[] aI, double[] bR, double[] bI,
            double[] cR, double[] cI, double[] dR, double[] dI,
            int[] iterDst, float[] smoothDst,
            float[] finalZrDst, float[] finalZiDst,
            float[] finalDrDst, float[] finalDiDst,
            double[]? blaCoeffs = null, int blaLevels = 0)
            => throw new NotSupportedException(
                "This GPU kernel has no SA perturbation path (SupportsPerturbationSA is false).");

        /// <summary>#838 / G4.7 — true when this backend can run the Buddhabrot
        /// uniform sample pass (<see cref="RunBuddhaBatch"/>).</summary>
        bool SupportsBuddhabrot => false;

        /// <summary>#838 / G4.7 — run one Buddhabrot-family uniform sample batch on the
        /// GPU and ADD its hits into the three band histograms (W x H each). Only valid
        /// when <see cref="SupportsBuddhabrot"/> is true.</summary>
        void RunBuddhaBatch(in GpuBuddhaBatch batch, uint[] hitsR, uint[] hitsG, uint[] hitsB)
            => throw new NotSupportedException("This GPU kernel has no Buddhabrot path (SupportsBuddhabrot is false).");

        /// <summary>#607 / G4.6 — true when this backend can run the orbit-accumulating
        /// perturbation kernel (<see cref="RunPerturbOrbit"/>).</summary>
        bool SupportsPerturbationOrbit => false;

        /// <summary>#607 / G4.6 — <see cref="RunPerturb"/> that also accumulates the
        /// orbit-colouring inputs in <paramref name="orbitMask"/> (<c>GpuOrbitInputs</c>
        /// bits) on the reconstructed z = Z[m] + δ, the GPU twin of the CPU deep orbit
        /// path. Writes their means to <paramref name="orbitDst"/>: one float per
        /// mask'd input, in bit order, per pixel (0 for in-set pixels).
        /// <paramref name="centerRe"/>/<paramref name="centerIm"/> is the view centre
        /// (the triangle-inequality sample's c = centre + dc). Only valid when
        /// <see cref="SupportsPerturbationOrbit"/> is true.</summary>
        void RunPerturbOrbit(
            int width, int height,
            double scale, int maxIter, double escapeRadius2,
            double offsetX0, double offsetY0,
            double[] refZr, double[] refZi, int refLen,
            int orbitMask, double centerRe, double centerIm,
            int[] iterDst, float[] smoothDst,
            float[] finalZrDst, float[] finalZiDst,
            float[] finalDrDst, float[] finalDiDst,
            float[] orbitDst)
            => throw new NotSupportedException(
                "This GPU kernel has no orbit perturbation path (SupportsPerturbationOrbit is false).");
    }
}
