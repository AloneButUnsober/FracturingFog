// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// GpuMemoryStats.cs — #1162: process-wide count of ILGPU device bytes
// allocated by the GPU 3D calculators.
//
// ILGPU exposes an accelerator's total memory but not how much is in use, so
// the GPU calculators route their per-frame Allocate1D calls through this
// helper. The counter is cumulative (never decremented): callers take a
// before/after delta around the work they care about. The --bench GPU
// harness uses that delta both as its DeviceAlloc/op column and as proof
// the GPU path actually ran — a CPU fallback allocates nothing here.
//
// Cost is one Interlocked.Add per buffer (three per frame); negligible next
// to the device allocation itself.

using System.Runtime.CompilerServices;
using System.Threading;
using ILGPU;
using ILGPU.Runtime;

namespace FracturingFog.Calculators.Gpu;

public static class GpuMemoryStats
{
    private static long s_allocatedBytes;

    /// <summary>Total device bytes requested via <see cref="Allocate1D{T}"/>
    /// since process start. Cumulative — take deltas.</summary>
    public static long AllocatedBytes => Interlocked.Read(ref s_allocatedBytes);

    /// <summary>Counting wrapper over <c>Accelerator.Allocate1D</c>.</summary>
    public static MemoryBuffer1D<T, Stride1D.Dense> Allocate1D<T>(Accelerator accelerator, long length)
        where T : unmanaged
    {
        var buffer = accelerator.Allocate1D<T>(length);
        Interlocked.Add(ref s_allocatedBytes, length * Unsafe.SizeOf<T>());
        return buffer;
    }
}
