// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Engine/Interefaces/GpuPixelMask.cs
//
// #1394 part 1 — the pixel mask of a GPU dispatch (IGpuKernel.PixelMask): the
// solid-region passes (#1343, Mariani–Silver subdivision) compute only the grid
// lines, then only the tiles whose border is not uniformly in-set, so a frame whose
// interior is in-set never iterates it. The CPU paths have honoured the #1344 pixel
// mask since #1343; this is the GPU's.
//
// Layout (the kernels' gMask SRV, MandelbrotKernelSource.MaskSrv): word 0 is the
// header (1 = a mask is active; the D3D11 kernels leave t15 unbound without one,
// which reads 0), then one bit per pixel, row-major: pixel i is bit (i & 31) of
// word 1 + (i >> 5). A thread whose pixel is unmarked writes nothing, and the host
// copies only the marked pixels back (CopyMarked), so every unmarked element of
// the caller's arrays keeps what it held. A marked pixel runs the same instructions
// of the same compiled shader as without a mask, so it gets the same bytes.

using System;

namespace FracturingFog.Rendering
{
    /// <summary>#1394 — a per-pixel mask for <see cref="IGpuKernel.PixelMask"/>.</summary>
    public sealed class GpuPixelMask
    {
        public int Width { get; }
        public int Height { get; }

        /// <summary>The kernel buffer: the header word (1), then the pixel bits.</summary>
        public uint[] Words { get; }

        /// <summary>Marked pixels.</summary>
        public long Count { get; }

        private GpuPixelMask(int width, int height, uint[] words, long count)
        {
            Width = width; Height = height; Words = words; Count = count;
        }

        /// <summary>The words a mask of <paramref name="pixels"/> pixels needs.</summary>
        public static int WordCount(int pixels) => 1 + (pixels + 31) / 32;

        /// <summary>A mask marking the pixels whose byte in <paramref name="mark"/>
        /// (width × height) is non-zero.</summary>
        public static GpuPixelMask FromBytes(byte[] mark, int width, int height)
        {
            int n = width * height;
            if (mark.Length < n) throw new ArgumentException("mark shorter than width * height", nameof(mark));
            var words = new uint[WordCount(n)];
            words[0] = 1;
            long count = 0;
            for (int w = 0, i = 0; i < n; w++, i += 32)
            {
                int len = Math.Min(32, n - i);
                var span = mark.AsSpan(i, len);
                if (span.IndexOfAnyExcept((byte)0) < 0) continue;
                uint bits = 0;
                for (int k = 0; k < len; k++) if (span[k] != 0) bits |= 1u << k;
                words[1 + w] = bits;
                count += System.Numerics.BitOperations.PopCount(bits);
            }
            return new GpuPixelMask(width, height, words, count);
        }

        /// <summary>The mask applies to a dispatch of this size.</summary>
        public bool Fits(int width, int height) => width == Width && height == Height;

        public bool Marked(int i) => (Words[1 + (i >> 5)] & (1u << (i & 31))) != 0;

        /// <summary>dst[i] = src[i] for every marked pixel i &lt; n.</summary>
        public unsafe void CopyMarked<T>(T* src, T[] dst, int n) where T : unmanaged
            => CopyMarked(new ReadOnlySpan<T>(src, n), dst.AsSpan(0, n));

        /// <summary>dst[i] = src[i] for every marked pixel i &lt; n.</summary>
        public unsafe void CopyMarked<T>(T* src, T* dst, int n) where T : unmanaged
            => CopyMarked(new ReadOnlySpan<T>(src, n), new Span<T>(dst, n));

        /// <summary>dst[i] = src[i] for every marked pixel i &lt; dst.Length (≤ src.Length).</summary>
        public void CopyMarked<T>(ReadOnlySpan<T> src, Span<T> dst)
        {
            var words = Words;
            int n = dst.Length;
            for (int w = 0, i = 0; i < n; w++, i += 32)
            {
                uint bits = words[1 + w];
                if (bits == 0) continue;
                int len = Math.Min(32, n - i);
                if (bits == uint.MaxValue && len == 32)
                {
                    src.Slice(i, 32).CopyTo(dst.Slice(i, 32));
                    continue;
                }
                for (; bits != 0; bits &= bits - 1)
                {
                    int k = System.Numerics.BitOperations.TrailingZeroCount(bits);
                    if (k < len) dst[i + k] = src[i + k];
                }
            }
        }

        /// <summary>The marked pixels of a packed float4 (zr, zi, dr, di) output.</summary>
        public unsafe void ScatterMarkedFinalZD(float* src, float[] zr, float[] zi, float[] dr, float[] di, int n)
            => ScatterMarkedFinalZD(new ReadOnlySpan<float>(src, n * 4), zr, zi, dr, di, n);

        /// <summary>The marked pixels i &lt; n of a packed float4 (zr, zi, dr, di) output.</summary>
        public void ScatterMarkedFinalZD(ReadOnlySpan<float> src, float[] zr, float[] zi, float[] dr, float[] di, int n)
        {
            var words = Words;
            for (int w = 0, i = 0; i < n; w++, i += 32)
            {
                uint bits = words[1 + w];
                int len = Math.Min(32, n - i);
                for (; bits != 0; bits &= bits - 1)
                {
                    int k = System.Numerics.BitOperations.TrailingZeroCount(bits);
                    if (k >= len) break;
                    int p = i + k, b = p * 4;
                    zr[p] = src[b]; zi[p] = src[b + 1]; dr[p] = src[b + 2]; di[p] = src[b + 3];
                }
            }
        }
    }
}
