// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #2013 — host-side unpacking of a GPU frame's readback, shared by the D3D11 and
// Vulkan kernels. The packed final z / dz/dc float4 (16 bytes a pixel) scattered to
// four arrays on one thread: ~45 ms of a 4K deep frame's readback on an i5-13420H.

using System;
using System.Threading.Tasks;

namespace FracturingFog.Rendering
{
    public static unsafe class GpuReadback
    {
        /// <summary>Pixels per parallel chunk: below two chunks the scatter stays on the caller.</summary>
        public const int Chunk = 1 << 18;

        /// <summary>Unpacks <paramref name="n"/> packed (zr, zi, dr, di) float4s from
        /// <paramref name="src"/> into the four arrays, in parallel chunks.</summary>
        public static void ScatterFinalZD(float* src, float* zr, float* zi, float* dr, float* di, int n)
        {
            if (n <= 0) return;
            int chunks = (n + Chunk - 1) / Chunk;
            if (chunks < 2) { Scatter(src, zr, zi, dr, di, 0, n); return; }
            nint s = (nint)src, r = (nint)zr, i = (nint)zi, d = (nint)dr, e = (nint)di;
            Parallel.For(0, chunks, c =>
            {
                int from = c * Chunk, to = Math.Min(n, from + Chunk);
                Scatter((float*)s, (float*)r, (float*)i, (float*)d, (float*)e, from, to);
            });
        }

        private static void Scatter(float* src, float* zr, float* zi, float* dr, float* di, int from, int to)
        {
            for (int k = from; k < to; k++)
            {
                int b = k * 4;
                zr[k] = src[b + 0]; zi[k] = src[b + 1];
                dr[k] = src[b + 2]; di[k] = src[b + 3];
            }
        }
    }
}
