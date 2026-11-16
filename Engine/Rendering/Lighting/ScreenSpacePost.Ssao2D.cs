// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// ScreenSpacePost.Ssao2D.cs
//
// #1448 — the flat Mandelbrot view's 2D SSAO (Phase 8b, ApplySsao2D) for every
// caller. It used to run in the live view only (FractalRenderHost, after the AA
// pass), so --batch, export and scene video silently left it out although the SSAO
// settings reach them through --lighting-preset (they are LightingFxData fields).
// Every Mandelbrot path now calls this at the same point: after the quality preset's
// AA (MSAA, then TAA on a still) and adaptive contrast, before relief, brightness /
// contrast and the interior-alpha composite.
//
// The pass reads neighbouring pixels, so it runs on the finished (averaged) colour
// and never on a single sample. Its depth comes from the calculator's smooth /
// iteration buffers as they are after the AA pass, the same in every caller (the
// shared Supersampling code leaves them identical), so the result is byte-identical
// across live, batch and export.

using FracturingFog.Models;

namespace FracturingFog.Rendering.Lighting;

public static partial class ScreenSpacePost
{
    /// <summary>#1448 — the 2D SSAO of <paramref name="fp"/>'s Lighting & FX on
    /// <paramref name="calc"/>'s finished colour buffer (no-op with SsaoSamples 0 or no
    /// parameters). Best-effort: a failing pass leaves the frame as it was. Returns
    /// whether the pass ran.</summary>
    public static bool ApplySsao2D(MandelbrotCalculator calc, FractalParameters? fp)
    {
        if (fp == null || fp.Lighting.SsaoSamples <= 0) return false;
        try
        {
            var fx = fp.Lighting;
            ApplySsao2D(calc.ColorBuffer, calc.SmoothBuffer, calc.IterationBuffer, calc.MaxIterations,
                calc.Width, calc.Height, in fx);
            return true;
        }
        catch { return false; }   // SSAO is best-effort — never fail the frame
    }
}
