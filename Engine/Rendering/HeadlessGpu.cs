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
//
// #1640 — with --gpu auto the device opens lazily (KernelFactory): on the first
// Attach of a calculator that can dispatch on it, so a run that never uses the GPU
// (a deep frame without FF_GPU_PERTURB, a shallow frame below fp32 precision, a
// family with no GPU kernel) doesn't pay the 0.1-1 s device open.

namespace FracturingFog.Rendering
{
    public static class HeadlessGpu
    {
        private static readonly object s_gate = new();
        private static IGpuKernel? s_kernel;
        private static System.Func<IGpuKernel?>? s_factory;

        /// <summary>The kernel headless renders compute on; null = CPU. Reading it
        /// opens the device if <see cref="KernelFactory"/> is pending.</summary>
        public static IGpuKernel? Kernel
        {
            get
            {
                if (s_kernel != null || s_factory == null) return s_kernel;
                lock (s_gate)
                {
                    if (s_factory is { } f) { s_factory = null; s_kernel = f(); }
                    return s_kernel;
                }
            }
            set { lock (s_gate) { s_kernel = value; s_factory = null; } }
        }

        /// <summary>#1640 — opens the kernel on first use instead of up front; it runs
        /// at most once. Setting it clears <see cref="Kernel"/>.</summary>
        public static System.Func<IGpuKernel?>? KernelFactory
        {
            get => s_factory;
            set { lock (s_gate) { s_kernel = null; s_factory = value; } }
        }

        /// <summary>#1640 — why the last <see cref="Attach"/> left a calculator on the
        /// CPU without opening the device (null when it attached, or had nothing to
        /// attach).</summary>
        public static string? LastNotOpened { get; private set; }

        /// <summary>The calculator <see cref="Attach"/> last handed the kernel to, so
        /// the batch can report its GPU route (a CPU fallback and why) after a render
        /// whose calculators it never sees.</summary>
        public static object? LastAttached { get; private set; }

        /// <summary>Hands <see cref="Kernel"/> to a calculator that can use it
        /// (Mandelbrot, the escape-time families, the Buddhabrot family). A no-op
        /// with no kernel, for null, and for every other calculator.</summary>
        public static void Attach(object? calculator)
        {
            LastNotOpened = null;
            if (s_kernel == null && s_factory == null) return;
            if (calculator is not (MandelbrotCalculator or EscapeTimeCalculator or BuddhaFamilyCalculator)) return;
            // #1640 — a Mandelbrot view the GPU can't draw stays on the CPU without
            // opening the device (the calculator is built for this one view).
            if (s_kernel == null && calculator is MandelbrotCalculator mv && mv.CpuOnlyView() is { } why)
            {
                LastNotOpened = why;
                return;
            }
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
