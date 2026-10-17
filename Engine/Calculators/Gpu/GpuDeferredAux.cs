// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// GpuDeferredAux.cs
//
// #1332 — lazy readback of a GPU-coloured frame's per-pixel outputs. A frame the
// GPU coloured end to end (a GPU palette) needs only its colour on the CPU to be
// shown; the iteration count, smooth count and final z / dz (24 of the 28 bytes a
// pixel used to cost in the GPU → CPU copy) are read by optional consumers only:
// histogram equalisation, relief, the HUD probe, the solid-frame rescue, region
// tools. The calculator runs IGpuKernel.RunDeferred and records the frame here;
// each buffer's getter calls Need, which copies that output from the GPU the first
// time anything reads it. The distance buffer (filled on the CPU from the final
// z / dz) is deferred the same way. Readers see the same bytes the eager path wrote.
//
// Ordering. The kernel keeps one pending frame and flushes it into its arrays
// before any other dispatch reuses its GPU buffers, so a frame can't be lost to a
// later dispatch, and a frame superseded by its own calculator's next dispatch is
// dropped (those arrays are about to be overwritten). A CPU writer reaching a
// buffer through its getter first materialises the pending output, then
// overwrites it: correct, and paid once on a GPU → CPU path change.

using System;
using System.Runtime.CompilerServices;
using System.Threading;
using FracturingFog.Rendering;

namespace FracturingFog.Calculators.Gpu;

internal sealed class GpuDeferredAux
{
    public const int Iterations = (int)GpuAuxOutputs.Iterations;
    public const int Smooth = (int)GpuAuxOutputs.Smooth;
    public const int FinalZ = (int)GpuAuxOutputs.FinalZ;
    /// <summary>The CPU-derived distance buffer (needs Iterations + FinalZ).</summary>
    public const int Distance = 8;

    private readonly object _gate = new();
    private int _pending;
    private IGpuKernel? _kernel;
    private int[]? _token;
    private Action? _fillDistance;

    /// <summary>The outputs not yet on the CPU (diagnostics / tests).</summary>
    public int Pending => Volatile.Read(ref _pending);

    /// <summary>Make <paramref name="part"/> current on the CPU (no-op when it is).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Need(int part)
    {
        if ((Volatile.Read(ref _pending) & part) != 0) Materialize(part);
    }

    /// <summary>Record a deferred frame: <paramref name="pending"/> from the kernel
    /// (whose frame is named by <paramref name="iterDst"/>), plus the distance fill
    /// when <paramref name="fillDistance"/> is given. The fill must write the
    /// distance buffer's backing array, not its getter.</summary>
    public void Defer(IGpuKernel kernel, int[] iterDst, GpuAuxOutputs pending, Action? fillDistance)
    {
        lock (_gate)
        {
            _kernel = kernel;
            _token = iterDst;
            _fillDistance = fillDistance;
            int p = (int)pending;
            if (fillDistance != null) p |= Distance;
            Volatile.Write(ref _pending, p);
        }
    }

    /// <summary>Forget a deferred frame: the caller is about to overwrite every
    /// deferred buffer (a new dispatch) or reallocated them.</summary>
    public void Discard()
    {
        if (Volatile.Read(ref _pending) == 0) return;
        lock (_gate)
        {
            Volatile.Write(ref _pending, 0);
            _kernel = null;
            _token = null;
            _fillDistance = null;
        }
    }

    private void Materialize(int part)
    {
        lock (_gate)
        {
            int pending = _pending;
            if ((pending & part) == 0) return;   // another thread got here first
            int gpu = part & (int)GpuAuxOutputs.All;
            if ((part & Distance) != 0) gpu |= (Iterations | FinalZ);   // the fill reads them
            gpu &= pending;
            if (gpu != 0)
            {
                try { _kernel!.ReadDeferred(_token!, (GpuAuxOutputs)gpu); }
                catch (Exception ex)
                {
                    // Device loss or a disposed kernel: the arrays keep what they held.
                    System.Diagnostics.Debug.WriteLine($"[GpuDeferredAux] deferred readback failed: {ex.Message}");
                }
                pending &= ~gpu;
                Volatile.Write(ref _pending, pending);
            }
            if ((part & Distance) != 0 && (pending & Distance) != 0)
            {
                _fillDistance!();
                Volatile.Write(ref _pending, pending & ~Distance);
            }
            if (Volatile.Read(ref _pending) == 0) { _kernel = null; _token = null; _fillDistance = null; }
        }
    }
}
