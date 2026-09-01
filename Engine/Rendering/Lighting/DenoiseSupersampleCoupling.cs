// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Rendering/Lighting/DenoiseSupersampleCoupling.cs
//
// S4 (#402) — adaptive-supersample coupling. The guided À-Trous denoiser
// (ReliefDenoisePass) exists to buy equal quality for fewer Monte-Carlo samples;
// this is the policy that actually spends that budget. When the denoiser is on,
// the CPU relief raymarch's N×N anti-alias supersample is stepped down (the
// denoiser cleans the extra noise), cutting primary-ray cost by roughly the
// square of the reduction.
//
// Pure + deterministic; the raymarch consults it for its effective SS. When the
// coupling is off, or the denoiser is off, the supersample passes through
// unchanged, so the render is byte-identical to the pre-coupling path.

using System;

namespace FracturingFog.Rendering.Lighting;

/// <summary>Maps the authored anti-alias supersample to the effective supersample
/// the relief raymarch runs, stepping it down when the guided denoiser is active
/// (roadmap S4, #402). See the file header.</summary>
public static class DenoiseSupersampleCoupling
{
    /// <summary>Effective N×N supersample for the CPU relief raymarch. Returns
    /// <paramref name="supersample"/> unchanged unless <paramref name="adaptive"/> is
    /// set AND the denoiser is active (<paramref name="denoiseIterations"/> &gt; 0), in
    /// which case it is halved (rounded up, floored at 1): 4→2, 3→2, 2→1, 1→1 — so
    /// SS 4 (16 rays/px) drops to SS 2 (4 rays/px), a 4× primary-ray saving the
    /// denoiser pays back. The input is clamped to [1,4] to match the raymarch.</summary>
    public static int EffectiveSupersample(int supersample, int denoiseIterations, bool adaptive)
    {
        int ss = supersample < 1 ? 1 : (supersample > 4 ? 4 : supersample);
        if (!adaptive || denoiseIterations <= 0)
            return ss;
        return Math.Max(1, (ss + 1) / 2);
    }
}
