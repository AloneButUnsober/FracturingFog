// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// InteriorAlphaStamp.cs — shared global-interior-alpha post-pass (#96 / #97)
//
// The interior (in-set) region of an escape-time fractal is the set of pixels
// that never escaped: IterationBuffer[idx] >= MaxIterations (the same invariant
// the interior colour pass relies on). The global InteriorAlpha knob
// (FractalParameters.InteriorAlpha, 0..255) scales the alpha byte of every such
// pixel so the in-set region can go translucent and composite over the chosen
// Interior2DBackground.
//
// This was originally private to MandelbrotCalculator (#96). #97 lifts it here
// so every in-scope escape-time family — MandelbrotCalculator,
// EscapeTimeCalculator (Julia / BurningShip / Tricorn / Multibrot / Phoenix /
// Magnet1 / Magnet2 / Glynn / Spider), the Newton / Halley / Secant / TearDrop
// calcs, and the generated calcs — reuses one implementation instead of
// copy-paste. The alpha is multiplied (a * ia / 255), not clobbered, so a future
// per-theme authored interior alpha composes multiplicatively; RGB is untouched.
//
// InteriorAlpha == 255 is an opaque no-op (early return), so gating the call on
// `InteriorAlpha < 255` at the call site keeps the fully-opaque path
// byte-identical to before the feature existed.

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace FracturingFog;

internal static class InteriorAlphaStamp
{
    /// <summary>Scale the alpha byte of every in-set pixel
    /// (<paramref name="iterationBuffer"/>[idx] &gt;= <paramref name="maxIterations"/>)
    /// by <paramref name="interiorAlpha"/>/255. No-op when the alpha is fully
    /// opaque (255) or the buffers are empty.</summary>
    public static void Apply(
        uint[] colorBuffer,
        int[] iterationBuffer,
        int width,
        int height,
        int maxIterations,
        int interiorAlpha,
        ParallelOptions po,
        CancellationToken ct)
    {
        if (maxIterations <= 0) return;
        int clamped = Math.Clamp(interiorAlpha, 0, 255);
        if (clamped >= 255) return;                       // opaque — nothing to do
        if (colorBuffer == null || iterationBuffer == null) return;
        int w = width, h = height;
        if (w <= 0 || h <= 0) return;

        uint ia = (uint)clamped;
        int count = h;
        po.CancellationToken = ct;
        Parallel.ForEach(Partitioner.Create(0, h), po, range =>
        {
            for (int y = range.Item1; y < range.Item2; y++)
            {
                if (ct.IsCancellationRequested) return;
                int rowBase = y * w;
                for (int x = 0; x < w; x++)
                {
                    int idx = rowBase + x;
                    if (iterationBuffer[idx] < maxIterations) continue;   // exterior pixel
                    uint c = colorBuffer[idx];
                    uint a = (c >> 24) & 0xFFu;
                    uint na = (a * ia) / 255u;                            // scale authored alpha
                    colorBuffer[idx] = (c & 0x00FFFFFFu) | (na << 24);
                }
            }
        });
    }
}
