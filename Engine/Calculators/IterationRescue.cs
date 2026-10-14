// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// IterationRescue.cs — SM-1 (#1267)
//
// A deep Mandelbrot view renders as one flat colour when its iteration cap is
// below the escape band: every pixel runs out of iterations before escaping,
// so the whole frame counts as in-set. It is not a precision problem. It
// happens when a region was saved with no iteration count (the quality preset's
// zoom formula then caps at 2048 for Standard, 256 for Draft) or with a count
// that was too low.
//
// The rescue runs only after a frame came out entirely in-set. It climbs the
// cap ×4 at a time on a small probe of the same view until some pixel escapes,
// then re-renders the real frame at that cap. A view that is genuinely all
// interior (inside a minibrot) finds no escape up to the ceiling and is left
// as rendered. The probe keeps the frame's aspect ratio, so it covers the same
// plane extent.

using System;
using System.Threading;

using FracturingFog.Models;

namespace FracturingFog;

/// <summary>SM-1 (#1267) — filled in by a render that ran the rescue.</summary>
public sealed class IterationRescueReport
{
    /// <summary>The cap the frame was re-rendered at, or 0 when it was left alone.</summary>
    public int RaisedTo { get; set; }
}

public static class IterationRescue
{
    /// <summary>Highest cap the climb tries: the Extreme preset's maximum.</summary>
    public static readonly int Ceiling = QualityPreset.Extreme.IterMax;

    /// <summary>Long side of the probe render, in pixels.</summary>
    public const int ProbeLongSide = 96;

    /// <summary>True when every pixel of the finished frame ran out of
    /// iterations (honours per-row caps when they are set).</summary>
    public static bool IsAllInSet(MandelbrotCalculator calc)
    {
        int w = calc.Width, h = calc.Height;
        int[] it = calc.IterationBuffer;
        if (w <= 0 || h <= 0 || it.Length < w * h) return false;
        int[]? perRow = calc.PerRowMaxIter;
        bool rowCaps = perRow != null && perRow.Length >= h;
        for (int y = 0; y < h; y++)
        {
            int cap = rowCaps && perRow![y] > 0 ? perRow[y] : calc.MaxIterations;
            int rb = y * w;
            for (int x = 0; x < w; x++)
                if (it[rb + x] < cap) return false;
        }
        return true;
    }

    /// <summary>Climbs the iteration cap on a small probe of <paramref name="src"/>'s
    /// view until some pixel escapes. Returns that cap, or 0 when nothing escapes
    /// up to <paramref name="ceiling"/> (or the climb was cancelled).</summary>
    public static int FindEscapingIterations(MandelbrotCalculator src, int ceiling, CancellationToken ct)
    {
        if (src.MaxIterations >= ceiling) return 0;
        int w = Math.Max(1, src.Width), h = Math.Max(1, src.Height);
        int pw = w >= h ? ProbeLongSide : Math.Max(8, ProbeLongSide * w / h);
        int ph = h >= w ? ProbeLongSide : Math.Max(8, ProbeLongSide * h / w);
        var probe = new MandelbrotCalculator(pw, ph)
        {
            CenterX = src.CenterX, CenterXLo = src.CenterXLo, CenterX2 = src.CenterX2, CenterX3 = src.CenterX3,
            CenterX4 = src.CenterX4, CenterX5 = src.CenterX5, CenterX6 = src.CenterX6, CenterX7 = src.CenterX7,
            CenterY = src.CenterY, CenterYLo = src.CenterYLo, CenterY2 = src.CenterY2, CenterY3 = src.CenterY3,
            CenterY4 = src.CenterY4, CenterY5 = src.CenterY5, CenterY6 = src.CenterY6, CenterY7 = src.CenterY7,
            Zoom = src.Zoom,
            // Single sample: the probe only needs iteration counts. A plain
            // palette keeps it off the slower orbit-theme path.
            Quality = src.Quality,
            ColorMap = new HsvPalette(),
            DisableAcceleration = src.DisableAcceleration,
            DisableSeriesApproximation = src.DisableSeriesApproximation,
            DisableDdBla = src.DisableDdBla,
        };

        long cap = src.MaxIterations;
        while (cap < ceiling)
        {
            cap = Math.Min((long)ceiling, Math.Max(cap * 4, 64));
            if (ct.IsCancellationRequested) return 0;
            probe.MaxIterations = (int)cap;
            probe.Calculate(ct);
            if (ct.IsCancellationRequested) return 0;
            if (!IsAllInSet(probe)) return (int)cap;
        }
        return 0;
    }

    /// <summary>When <paramref name="calc"/>'s finished frame is entirely in-set,
    /// finds a cap at which pixels escape and re-renders at it. Returns the new
    /// cap, or 0 when the frame was left as rendered.</summary>
    public static int TryRescue(MandelbrotCalculator calc, CancellationToken ct)
    {
        if (ct.IsCancellationRequested || !IsAllInSet(calc)) return 0;
        int found = FindEscapingIterations(calc, Ceiling, ct);
        if (found <= calc.MaxIterations || ct.IsCancellationRequested) return 0;
        calc.MaxIterations = found;
        calc.Calculate(ct);
        return found;
    }
}
