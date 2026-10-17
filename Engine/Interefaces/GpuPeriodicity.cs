// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Engine/Interefaces/GpuPeriodicity.cs
//
// #1342 — the periodicity early-out in the shallow GPU escape-time kernels
// (MandelbrotKernelSource.HlslEntry / HlslEntryOrbit, D3D11 + Vulkan). The CPU
// loops have had it for years: a Brent-style snapshot of z, compared every step,
// so a pixel caught on an attracting cycle stops after a period or two instead
// of running to the iteration cap. On the GPU the comparison is bit-exact on the
// whole iteration state, so it only fires when the kernel's own fp32 orbit
// repeats — an orbit that can then never escape. The result is exactly what the
// kernel gives running to the cap (iter / smooth / colour), only sooner.
//
// The mode picks a compiled variant (MandelbrotKernelSource.VariantKey /
// ForVariant, #define FF_PERIODICITY): Off compiles the plain loop, unchanged, so
// it times exactly as before. A per-step test on a cbuffer flag instead cost
// 15–50% on a GT 710 even with the check off. Both kernel assemblies link their
// own copy of MandelbrotKernelSource, so the knob lives here, in Engine, which
// both reference.
//
// On by default: on a GT 710 an exterior-heavy frame (seahorse 1e4, 2048 iter,
// 1080p) costs ~5% more and an all-exterior one up to ~12%, while in-set-heavy
// frames run 4–10× (minibrots, bulbs) to 30–400× (filled Julia sets) faster at
// caps of 10k–100k.

using System;

namespace FracturingFog.Rendering
{
    /// <summary>#1342 — mode of the GPU shallow-kernel periodicity check.</summary>
    public static class GpuPeriodicity
    {
        /// <summary>No check: every in-set pixel runs to the cap (the pre-#1342 kernel).</summary>
        public const int Off = 0;
        /// <summary>The check (default).</summary>
        public const int On = 1;
        /// <summary>The check, and a detected in-set pixel writes −(steps run) to
        /// the smooth output instead of 0, for tests and probes.</summary>
        public const int Diagnostic = 2;

        /// <summary>Process default: <see cref="On"/>, or <see cref="Off"/> when
        /// <c>FF_GPU_PERIODICITY=0</c>.</summary>
        public static readonly int Default =
            Environment.GetEnvironmentVariable("FF_GPU_PERIODICITY") == "0" ? Off : On;

        // 0 = no override; otherwise mode + 1. Thread-static like the other GPU
        // test knobs, so a test toggling it cannot leak into parallel tests.
        [ThreadStatic] private static int t_override;

        /// <summary>The mode the next SP dispatch on this thread uses.</summary>
        public static int Mode
        {
            get => t_override != 0 ? t_override - 1 : Default;
            set => t_override = Math.Clamp(value, Off, Diagnostic) + 1;
        }

        /// <summary>Drop this thread's override (back to <see cref="Default"/>).</summary>
        public static void Reset() => t_override = 0;
    }
}
