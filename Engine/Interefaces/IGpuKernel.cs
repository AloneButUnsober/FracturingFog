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
    }

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
        /// the call.</summary>
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
            uint[]? colorDst = null);
    }
}
