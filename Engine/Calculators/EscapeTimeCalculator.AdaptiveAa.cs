// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Calculators/EscapeTimeCalculator.AdaptiveAa.cs
//
// #1382 — adaptive anti-aliasing (#1344, Supersampling.AccumulateAlt) for the
// escape-time families of this calculator: Julia, Burning Ship, Tricorn, Multibrot,
// Phoenix, Magnet 1 / 2, Glynn and Spider. With a PixelMask set, Calculate runs one
// masked pass through the cores' #1394 mask (EscapeTimeCalculator.SolidRegions.cs):
// the scalar loops skip unmarked pixels, a SIMD batch runs when any lane is marked,
// the shallow GPU kernel takes the mask where it can (and otherwise computes the
// whole frame). A marked pixel gets exactly the bytes of a full frame: every core
// computes a pixel from its own coordinate alone, and the whole-frame passes after
// it (the out-of-bounds surround, interior alpha) are per pixel.
//
// A masked frame does not subdivide (#1394 solid regions): the mask already says
// which pixels to compute.

using System;
using System.Threading;

namespace FracturingFog;

public sealed partial class EscapeTimeCalculator : FracturingFog.Rendering.IAdaptiveAaCalculator
{
    /// <summary>#1382 — Width × Height bytes; when set, the next Calculate computes
    /// only the pixels whose byte is non-zero (see
    /// <see cref="FracturingFog.Rendering.IAdaptiveAaCalculator"/>).</summary>
    public byte[]? PixelMask { get; set; }

    public FracturingFog.Rendering.AdaptiveAa AdaptiveAa => FracturingFog.Rendering.AdaptiveAa.From(FractalParameters);

    /// <summary>Always: no pass of this calculator reads a neighbouring pixel or
    /// carries state across frames.</summary>
    public bool MaskExact => true;

    /// <summary>The set boundary for the mask (in-set pixels read MaxIterations).</summary>
    int[]? FracturingFog.Rendering.IAdaptiveAaCalculator.AdaptiveAaIterations => IterationBuffer;

    private byte[]? AaMask => PixelMask is { } m && m.Length == Width * Height ? m : null;

    private void CalculateMasked(byte[] mask, CancellationToken ct)
    {
        int w = Width, h = Height;
        var rows = new bool[h];
        for (int y = 0; y < h; y++)
            rows[y] = mask.AsSpan(y * w, w).IndexOfAnyExcept((byte)0) >= 0;
        var t = (ISolidRegionTarget)this;
        try { t.SolidPass(mask, rows, ct); }
        finally { t.SolidEnd(ct); }
    }
}
