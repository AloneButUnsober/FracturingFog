// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Engine/Interefaces/GpuDeferredReadback.cs
//
// #1332 — whether a GPU-coloured SP frame leaves its iteration / smooth / final-z
// outputs on the GPU until a CPU reader asks (IGpuKernel.RunDeferred, read through
// Calculators.Gpu.GpuDeferredAux). Readers get the same bytes either way; only the
// cost moves. Measured on a GT 710, the copy-out of those outputs was 17 ms of a
// 37 ms 1080p frame (69 of 144 ms at 4K). On by default; FF_GPU_DEFER_READBACK=0
// restores the eager readback.

using System;

namespace FracturingFog.Rendering
{
    /// <summary>#1332 — the deferred-readback switch for GPU-coloured SP frames.</summary>
    public static class GpuDeferredReadback
    {
        /// <summary>Process default: on, or off when <c>FF_GPU_DEFER_READBACK=0</c>.</summary>
        public static readonly bool Default =
            Environment.GetEnvironmentVariable("FF_GPU_DEFER_READBACK") != "0";

        // 0 = no override, 1 = off, 2 = on. Thread-static like the other GPU test
        // knobs, so a test toggling it cannot leak into parallel tests.
        [ThreadStatic] private static int t_override;

        /// <summary>Whether the next SP dispatch on this thread defers.</summary>
        public static bool Enabled
        {
            get => t_override != 0 ? t_override == 2 : Default;
            set => t_override = value ? 2 : 1;
        }

        /// <summary>Drop this thread's override (back to <see cref="Default"/>).</summary>
        public static void Reset() => t_override = 0;
    }
}
