// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1788 (#1665 S4) — --gpu-deep / --deep-route-calibration for --batch: the logic is
// FracturingFog.DeepRouteBatchSetup (Engine, tested there); this hands it the batch's
// own-device GPU kernel factory and stderr.

using System;

namespace FracturingFog.Batch
{
    internal static class BatchDeepRouteSetup
    {
        public static bool Apply(BatchOptions opts, out string? error)
            => DeepRouteBatchSetup.Apply(opts.GpuDeep, opts.DeepRoute, opts.Gpu, BatchGpuSetup.OpenOwnKernel,
                m => Console.Error.WriteLine("batch: " + m), out error);
    }
}
