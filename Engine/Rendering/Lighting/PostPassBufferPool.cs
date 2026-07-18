// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// PostPassBufferPool.cs
//
// P0 — per-frame scratch buffer pool for the post-pass stages in
// ScreenSpacePost. The CPU bloom/DoF pyramid allocates ~150 MB/frame at
// 1080p (3× float[3·n] for bloom mips + 3× for DoF skewed-box passes +
// CoC buffer + emissive). All of it transient and overwrite-only — perfect
// pool candidates.
//
// Design
//   • Keyed by minimum element count. Rent rounds up to a size class so
//     similar requests hit the same bucket.
//   • Per-thread bucket lists (one stack per size class) avoid contention
//     under the Parallel.For driven post-passes.
//   • Bounded retention — at most BucketMax buffers per size class.
//     Resolution changes shed the old bucket on the next Clear() call so
//     we don't permanently retain a 4K buffer after the user resizes to 1K.
//   • Struct Lease returns the buffer on dispose. Use as:
//       using var lease = PostPassBufferPool.RentFloat(3 * n);
//       float[] buf = lease.Buffer;
//   • Pool only manages float[] and uint[] — the only post-pass scratch
//     types. Add more if the workload grows.

using System;
using System.Collections.Generic;
using System.Threading;

namespace FracturingFog.Rendering.Lighting;

public static class PostPassBufferPool
{
    private const int BucketMax = 4;

    // ThreadStatic keeps the hot path lock-free. Different worker threads
    // get their own buckets — fine because they'd compete on a shared lock
    // otherwise and the per-thread waste is bounded by BucketMax * sizes.
    [ThreadStatic]
    private static Dictionary<int, Stack<float[]>>? _floatBuckets;
    [ThreadStatic]
    private static Dictionary<int, Stack<uint[]>>? _uintBuckets;

    // Global statistics for the debug HUD / telemetry. Atomic counters
    // because they cross threads.
    private static long _floatHits;
    private static long _floatMisses;
    private static long _uintHits;
    private static long _uintMisses;

    public static long FloatHits => Interlocked.Read(ref _floatHits);
    public static long FloatMisses => Interlocked.Read(ref _floatMisses);
    public static long UintHits => Interlocked.Read(ref _uintHits);
    public static long UintMisses => Interlocked.Read(ref _uintMisses);

    // Power-of-two size class for the requested length. Keeps the bucket
    // count bounded — a 1920×1080 RGB buffer (6.2M floats) rounds to 8M;
    // a half-res mip rounds to 2M; quarter-res to 512K. Three buckets
    // cover all the post-pass shapes we care about.
    private static int SizeClass(int n)
    {
        if (n <= 0) return 1;
        int s = 1;
        while (s < n) s <<= 1;
        return s;
    }

    public static FloatLease RentFloat(int minLength)
    {
        int cls = SizeClass(minLength);
        _floatBuckets ??= new Dictionary<int, Stack<float[]>>();
        if (_floatBuckets.TryGetValue(cls, out var stack) && stack.Count > 0)
        {
            Interlocked.Increment(ref _floatHits);
            return new FloatLease(stack.Pop(), cls);
        }
        Interlocked.Increment(ref _floatMisses);
        return new FloatLease(new float[cls], cls);
    }

    public static UintLease RentUint(int minLength)
    {
        int cls = SizeClass(minLength);
        _uintBuckets ??= new Dictionary<int, Stack<uint[]>>();
        if (_uintBuckets.TryGetValue(cls, out var stack) && stack.Count > 0)
        {
            Interlocked.Increment(ref _uintHits);
            return new UintLease(stack.Pop(), cls);
        }
        Interlocked.Increment(ref _uintMisses);
        return new UintLease(new uint[cls], cls);
    }

    /// <summary>
    /// Rent a float[] and zero the leading <paramref name="zeroLength"/> entries.
    /// Use when the caller relies on a clean buffer (bloom emissive write paths
    /// skip unbloomed pixels — they read zero elsewhere).
    /// </summary>
    public static FloatLease RentFloatCleared(int minLength, int zeroLength)
    {
        var lease = RentFloat(minLength);
        Array.Clear(lease.Buffer, 0, Math.Min(zeroLength, lease.Buffer.Length));
        return lease;
    }

    internal static void ReturnFloat(float[] buf, int cls)
    {
        _floatBuckets ??= new Dictionary<int, Stack<float[]>>();
        if (!_floatBuckets.TryGetValue(cls, out var stack))
        {
            stack = new Stack<float[]>(BucketMax);
            _floatBuckets[cls] = stack;
        }
        if (stack.Count < BucketMax) stack.Push(buf);
    }

    internal static void ReturnUint(uint[] buf, int cls)
    {
        _uintBuckets ??= new Dictionary<int, Stack<uint[]>>();
        if (!_uintBuckets.TryGetValue(cls, out var stack))
        {
            stack = new Stack<uint[]>(BucketMax);
            _uintBuckets[cls] = stack;
        }
        if (stack.Count < BucketMax) stack.Push(buf);
    }

    /// <summary>
    /// Drop pooled buffers from the current thread. Called from
    /// <see cref="ScreenSpacePost.ClearGBuffer"/> when the host resizes so
    /// we don't retain old-resolution buffers indefinitely.
    /// </summary>
    public static void Clear()
    {
        _floatBuckets?.Clear();
        _uintBuckets?.Clear();
    }

    public readonly struct FloatLease : IDisposable
    {
        public float[] Buffer { get; }
        private readonly int _sizeClass;
        internal FloatLease(float[] buf, int cls) { Buffer = buf; _sizeClass = cls; }
        public void Dispose() { if (Buffer != null) ReturnFloat(Buffer, _sizeClass); }
    }

    public readonly struct UintLease : IDisposable
    {
        public uint[] Buffer { get; }
        private readonly int _sizeClass;
        internal UintLease(uint[] buf, int cls) { Buffer = buf; _sizeClass = cls; }
        public void Dispose() { if (Buffer != null) ReturnUint(Buffer, _sizeClass); }
    }
}
