// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// DualOrbitEscapeCalculator.Interference.cs (#1126, epic #1114 S12)
//
// Path interference (Docs/Technical/DualOrbit-Coloring-RnD.md §3.D). Each orbit
// is a path with complex phase Φ = G + 2πi·θ — the log of its level-1 Böttcher
// coordinate (S6: G the Green function from the smooth count, θ the external
// angle by backward lifting; both bailout-independent). The two paths interfere
// with amplitude e^{iκΦ}, κ the "ħ" knob:
//
//     e^{iκΦ} = e^{iκG} · e^{−2πκθ}   — phase from G, amplitude from θ.
//
// The raw amplitude e^{−2πκθ} is not single-valued (θ → θ + 1 rescales it by
// e^{−2πκ}), so the raw |a + b|² has a seam wherever θ wraps. FF uses the
// NORMALISED two-path intensity with the amplitudes made symmetric about their
// mean — only Δθ = θ_c − θ_z (wrapped to [−½, ½)) and ΔG = G_c − G_z survive:
//
//     I = |a + b|² / (|a|² + |b|²) = 1 + cos(κ·ΔG) / cosh(2π·γ·κ·Δθ)   ∈ [0, 2]
//
// with a = e^{+x}e^{iκG_z}, b = e^{−x}e^{iκG_c}, x = π·γ·κ·Δθ (θ_z = −Δθ/2,
// θ_c = +Δθ/2, so e^{−2πγκθ} gives a the e^{+x}). cosh is even, so I
// is continuous across the Δθ = ±½ wrap. γ (DualOrbitInterferenceGamma) scales
// the imaginary part of Φ: γ = 1 is Φ as written; γ = 0 drops the θ damping
// (pure cos fringes on ΔG); larger γ = stronger "decoherence" away from the
// curves where the two external angles agree (Δθ = 0), the only places the
// fringes keep full visibility.
//
// Default γ = 0 (smoke render): Δθ inherits S6's branch cuts below the critical
// level G_s(0), so any γ > 0 shows them as straight seams and washes large
// regions flat. γ = 0 reads only ΔG (the Green functions: no cuts).
//
// PathInterference = I/2; PathInterferencePhase = arg(a + b)/2π (turns).
// Live where both orbits escape (two paths); a single bounded path has nothing to
// interfere with → interior. κ and γ are colour-only: the scalar is rebuilt in
// the colour pass from the cached smooth counts and lifted angles, so animating κ
// recolours without iterating ("fringes shimmer").

using System;
using System.Threading.Tasks;

namespace FracturingFog;

public sealed partial class DualOrbitEscapeCalculator
{
    internal static bool IsInterferenceField(DualOrbitField f)
        => f is DualOrbitField.PathInterference or DualOrbitField.PathInterferencePhase;

    /// <summary>The normalised two-path intensity I ∈ [0, 2] (see header).</summary>
    public static double InterferenceIntensity(double dG, double dTheta, double kappa, double gamma)
    {
        double w = Frac(dTheta + 0.5) - 0.5;
        return 1.0 + Math.Cos(kappa * dG) / Math.Cosh(Math.Min(700.0, 2.0 * Math.PI * gamma * kappa * Math.Abs(w)));
    }

    /// <summary>arg(a + b) / 2π in turns [0, 1) (see header).</summary>
    public static double InterferencePhase(double gZ, double gC, double dTheta, double kappa, double gamma)
    {
        double w = Frac(dTheta + 0.5) - 0.5;
        double x = Math.Clamp(Math.PI * gamma * kappa * w, -350.0, 350.0);
        double ea = Math.Exp(x), eb = Math.Exp(-x);
        double re = ea * Math.Cos(kappa * gZ) + eb * Math.Cos(kappa * gC);
        double im = ea * Math.Sin(kappa * gZ) + eb * Math.Sin(kappa * gC);
        return Frac(Math.Atan2(im, re) / (2.0 * Math.PI));
    }

    // Rebuild the interference scalar from the cache with the current κ, γ.
    private void RefreshInterference(in GeometryKey key)
    {
        int n = Width * Height;
        if (_thZ.Length != n) return;
        var fp = FractalParameters;
        double kappa = fp.DualOrbitInterferenceK, gamma = Math.Max(0.0, fp.DualOrbitInterferenceGamma);
        bool phase = key.Field == DualOrbitField.PathInterferencePhase;
        int maxIter = key.MaxIter;
        double twoLogR = 2.0 * Math.Log(key.Bailout);
        float[] smZ = _smZ, smC = _smC, thZ = _thZ, thC = _thC, sc = _scalar; byte[] flags = _flags;
        Parallel.For(0, n, i =>
        {
            byte f = flags[i];
            float tz = thZ[i], tc = thC[i];
            if ((f & FlagPairDead) != 0 || (f & FlagZEsc) == 0 || (f & FlagCEsc) == 0 || float.IsNaN(tz) || float.IsNaN(tc))
            { sc[i] = 0f; return; }
            // Level-1 Green functions from the smooth counts (as S6).
            double gZ = twoLogR * Math.Pow(2.0, -smZ[i]), gC = twoLogR * Math.Pow(2.0, -smC[i]);
            double v = phase
                ? InterferencePhase(gZ, gC, tc - tz, kappa, gamma)
                : 0.5 * InterferenceIntensity(gC - gZ, tc - tz, kappa, gamma);
            sc[i] = (float)Math.Clamp(v * maxIter, LiveFloor, maxIter);
        });
    }
}
