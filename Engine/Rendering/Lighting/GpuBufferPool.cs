// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// GpuBufferPool.cs
//
// P0 — device-side scratch buffer pool for GpuPostKernels. Allocate1D
// on ILGPU's OpenCL / Velocity backends costs ~50–200 µs per call; with
// 7 allocs per TryApplyToneMapBloom and 3 passes per frame, that's a
// measurable chunk of the GPU post-pass cost.
//
// Design — mirrors PostPassBufferPool:
//   • Keyed by (accelerator, size-class). Power-of-two size class so close
//     requests reuse the same bucket.
//   • Buffers live until accelerator change or app exit; ILGPU's own
//     finalizer cleans up on teardown.
//   • Lease struct returns the buffer on dispose so the existing
//     `using var dColor = ...` callsites keep their shape.
//   • Not thread-safe at the multi-thread level — GpuPostKernels is
//     single-threaded by design (one Accelerator, no concurrent kernel
//     submission). Lock guards against accidental cross-thread use.

using System;
using System.Collections.Generic;

using ILGPU;
using ILGPU.Runtime;

namespace FracturingFog.Rendering.Lighting;

internal static class GpuBufferPool
{
    private const int BucketMax = 4;

    private static readonly object _lock = new();
    private static Accelerator? _ownerAcc;
    private static readonly Dictionary<int, Stack<MemoryBuffer1D<float, Stride1D.Dense>>> _floatBuckets = new();
    private static readonly Dictionary<int, Stack<MemoryBuffer1D<uint,  Stride1D.Dense>>> _uintBuckets = new();

    private static int SizeClass(int n)
    {
        if (n <= 0) return 1;
        int s = 1;
        while (s < n) s <<= 1;
        return s;
    }

    /// <summary>
    /// Reset the pool when the accelerator changes (or first use). Releases
    /// every retained buffer — the old accelerator's device memory goes
    /// away with the accelerator itself.
    /// </summary>
    private static void EnsureOwner(Accelerator acc)
    {
        if (ReferenceEquals(_ownerAcc, acc)) return;
        foreach (var stack in _floatBuckets.Values)
            while (stack.Count > 0) { try { stack.Pop().Dispose(); } catch { } }
        foreach (var stack in _uintBuckets.Values)
            while (stack.Count > 0) { try { stack.Pop().Dispose(); } catch { } }
        _floatBuckets.Clear();
        _uintBuckets.Clear();
        _ownerAcc = acc;
    }

    public static FloatLease RentFloat(Accelerator acc, int minLength)
    {
        int cls = SizeClass(minLength);
        lock (_lock)
        {
            EnsureOwner(acc);
            if (_floatBuckets.TryGetValue(cls, out var stack) && stack.Count > 0)
                return new FloatLease(stack.Pop(), cls);
        }
        // Allocation outside the lock — Allocate1D may be slow on some drivers.
        var buf = acc.Allocate1D<float>(cls);
        return new FloatLease(buf, cls);
    }

    public static UintLease RentUint(Accelerator acc, int minLength)
    {
        int cls = SizeClass(minLength);
        lock (_lock)
        {
            EnsureOwner(acc);
            if (_uintBuckets.TryGetValue(cls, out var stack) && stack.Count > 0)
                return new UintLease(stack.Pop(), cls);
        }
        var buf = acc.Allocate1D<uint>(cls);
        return new UintLease(buf, cls);
    }

    internal static void ReturnFloat(MemoryBuffer1D<float, Stride1D.Dense> buf, int cls)
    {
        lock (_lock)
        {
            if (!_floatBuckets.TryGetValue(cls, out var stack))
            {
                stack = new Stack<MemoryBuffer1D<float, Stride1D.Dense>>(BucketMax);
                _floatBuckets[cls] = stack;
            }
            if (stack.Count < BucketMax) { stack.Push(buf); return; }
        }
        try { buf.Dispose(); } catch { }
    }

    internal static void ReturnUint(MemoryBuffer1D<uint, Stride1D.Dense> buf, int cls)
    {
        lock (_lock)
        {
            if (!_uintBuckets.TryGetValue(cls, out var stack))
            {
                stack = new Stack<MemoryBuffer1D<uint, Stride1D.Dense>>(BucketMax);
                _uintBuckets[cls] = stack;
            }
            if (stack.Count < BucketMax) { stack.Push(buf); return; }
        }
        try { buf.Dispose(); } catch { }
    }

    public readonly struct FloatLease : IDisposable
    {
        public MemoryBuffer1D<float, Stride1D.Dense> Buffer { get; }
        public ArrayView<float> View => Buffer.View;
        private readonly int _sizeClass;
        internal FloatLease(MemoryBuffer1D<float, Stride1D.Dense> buf, int cls) { Buffer = buf; _sizeClass = cls; }
        public void Dispose() => ReturnFloat(Buffer, _sizeClass);
    }

    public readonly struct UintLease : IDisposable
    {
        public MemoryBuffer1D<uint, Stride1D.Dense> Buffer { get; }
        public ArrayView<uint> View => Buffer.View;
        private readonly int _sizeClass;
        internal UintLease(MemoryBuffer1D<uint, Stride1D.Dense> buf, int cls) { Buffer = buf; _sizeClass = cls; }
        public void Dispose() => ReturnUint(Buffer, _sizeClass);
    }
}
