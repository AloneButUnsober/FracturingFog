// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Engine/Rendering/HeadlessGpu.cs
//
// #1611 — the GPU compute kernel a headless render (--batch) attaches to the
// calculators it builds. The live shell gets its kernel from the window's
// renderer (FractalRenderHost.UseGpuCompute); a headless process has no
// renderer, so the batch entry builds one kernel on its own device (D3D11 on
// Windows, Vulkan elsewhere) and sets Kernel once. Null (the default, and in the
// live shell) leaves every calculator on the CPU, as before.
//
// Attach does what the live host's UseGpuCompute setter does for the same three
// calculator kinds: the kernel plus the per-calculator switch. Deep-zoom
// perturbation stays behind its own opt-in (MandelbrotCalculator.UseGpuPerturbation,
// FF_GPU_PERTURB), exactly as in the live shell.

namespace FracturingFog.Rendering
{
    public static class HeadlessGpu
    {
        /// <summary>The kernel headless renders compute on; null = CPU.</summary>
        public static IGpuKernel? Kernel { get; set; }

        /// <summary>The calculator <see cref="Attach"/> last handed the kernel to, so
        /// the batch can report its GPU route (a CPU fallback and why) after a render
        /// whose calculators it never sees.</summary>
        public static object? LastAttached { get; private set; }

        /// <summary>Hands <see cref="Kernel"/> to a calculator that can use it
        /// (Mandelbrot, the escape-time families, the Buddhabrot family). A no-op
        /// with no kernel, for null, and for every other calculator.</summary>
        public static void Attach(object? calculator)
        {
            if (Kernel is not { } k) return;
            switch (calculator)
            {
                case MandelbrotCalculator m:    m.GpuKernel = k; m.UseGpuCompute = true; break;
                case EscapeTimeCalculator e:    e.GpuKernel = k; e.UseGpuCompute = true; break;
                case BuddhaFamilyCalculator b:  b.GpuKernel = k; b.UseGpuCompute = true; break;
                default: return;
            }
            LastAttached = calculator;
        }
    }
}
