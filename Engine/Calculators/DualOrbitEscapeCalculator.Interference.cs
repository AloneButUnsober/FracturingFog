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
// Which-path measurement η (#1567, epic #1565 S1; design:
// Docs/Technical/DoubleSlit-Fractal-RnD.md §3.1). Watching which slit a particle
// takes removes the cross term of |a + b|² (Feynman, Leighton & Sands, Lectures
// Vol. III ch. 1 §1-6); η ∈ [0, 1] scales the cross term by (1 − η), the
// off-diagonal of the two-path density matrix:
//
//     I_η = (|a|² + |b|² + 2(1 − η)·Re(a·b̅)) / (|a|² + |b|²)
//         = 1 + (1 − η)·cos(κ·ΔG) / cosh(2π·γ·κ·Δθ)
//
// η = 0 is the coherent intensity above (bit-identical); η = 1 is I ≡ 1, the
// fringes gone. Unlike γ (coherence lost only where the external angles
// differ), η removes coherence uniformly. A partly-mixed state has no single
// phase, so η leaves PathInterferencePhase alone.
//
// Angular-momentum winding ℓ (#1568, epic #1565 S2; design §3.2). Light with
// phase e^{iℓφ} carries orbital angular momentum (Allen, Beijersbergen, Spreeuw
// & Woerdman, Phys. Rev. A 45, 1992). Each path gets e^{i·2πℓ·θ} from its own
// lifted external angle θ (turns), so the paths become vortices round the set:
//
//     Same:     a·e^{+i2πℓθ_z},  b·e^{+i2πℓθ_c}  →  cross cos(κΔG + 2πℓ(θ_c − θ_z))
//     Opposite: a·e^{+i2πℓθ_z},  b·e^{−i2πℓθ_c}  →  cross cos(κΔG − 2πℓ(θ_z + θ_c))
//
// ℓ is an integer: a wrap θ → θ ± 1 then turns each winding by a whole 2πℓ,
// so both forms stay single-valued — and need no symmetric Δθ trick. The phase
// field keeps the common winding too (arg of the summed amplitude), which is
// where the |ℓ| spiral arms show. ℓ = 0 adds +0.0 everywhere: bit-identical to
// #1126. Both are colour-only. θ inherits S6's branch cuts below G_s(0), so the
// cuts show at ℓ ≠ 0 as they do at γ > 0.
//
// PathInterference = I/2; PathInterferencePhase = arg(a + b)/2π (turns).
// Live where both orbits escape (two paths); a single bounded path has nothing to
// interfere with → interior. κ and γ are colour-only: the scalar is rebuilt in
// the colour pass from the cached smooth counts and lifted angles, so animating κ
// recolours without iterating ("fringes shimmer"). η and ℓ are colour-only too.

using System;
using System.Threading.Tasks;

namespace FracturingFog;

public sealed partial class DualOrbitEscapeCalculator
{
    internal static bool IsInterferenceField(DualOrbitField f)
        => f is DualOrbitField.PathInterference or DualOrbitField.PathInterferencePhase;

    /// <summary>The normalised two-path intensity I ∈ [0, 2] (see header);
    /// <paramref name="whichPath"/> is η, the which-path measurement strength
    /// (0 = fully coherent, 1 = no fringes) (#1567).</summary>
    /// <paramref name="winding"/> is the extra cross-term phase (radians) from the
    /// angular-momentum winding, see <see cref="WindingPhases"/> (#1568).
    public static double InterferenceIntensity(double dG, double dTheta, double kappa, double gamma, double whichPath = 0.0, double winding = 0.0)
    {
        double w = Frac(dTheta + 0.5) - 0.5;
        double cross = Math.Cos(kappa * dG + winding) / Math.Cosh(Math.Min(700.0, 2.0 * Math.PI * gamma * kappa * Math.Abs(w)));
        return whichPath <= 0.0 ? 1.0 + cross : 1.0 + (1.0 - Math.Min(1.0, whichPath)) * cross;
    }

    /// <summary>arg(a + b) / 2π in turns [0, 1) (see header); <paramref name="windZ"/>
    /// / <paramref name="windC"/> are the per-path winding phases (radians, #1568).</summary>
    public static double InterferencePhase(double gZ, double gC, double dTheta, double kappa, double gamma,
        double windZ = 0.0, double windC = 0.0)
    {
        double w = Frac(dTheta + 0.5) - 0.5;
        double x = Math.Clamp(Math.PI * gamma * kappa * w, -350.0, 350.0);
        double ea = Math.Exp(x), eb = Math.Exp(-x);
        double re = ea * Math.Cos(kappa * gZ + windZ) + eb * Math.Cos(kappa * gC + windC);
        double im = ea * Math.Sin(kappa * gZ + windZ) + eb * Math.Sin(kappa * gC + windC);
        return Frac(Math.Atan2(im, re) / (2.0 * Math.PI));
    }

    /// <summary>Per-path winding phases (radians) for angular momentum ℓ and the
    /// two lifted external angles in turns (#1568); the cross-term phase is
    /// windC − windZ.</summary>
    public static (double windZ, double windC) WindingPhases(int ell, DualOrbitWindingMode mode, double thetaZ, double thetaC)
    {
        double k = 2.0 * Math.PI * ell;
        return (k * thetaZ, mode == DualOrbitWindingMode.Opposite ? -k * thetaC : k * thetaC);
    }

    // Rebuild the interference scalar from the cache with the current κ, γ, η, ℓ.
    private void RefreshInterference(in GeometryKey key)
    {
        int n = Width * Height;
        if (_thZ.Length != n) return;
        var fp = FractalParameters;
        double kappa = fp.DualOrbitInterferenceK, gamma = Math.Max(0.0, fp.DualOrbitInterferenceGamma);
        double eta = double.IsNaN(fp.DualOrbitWhichPath) ? 0.0 : Math.Clamp(fp.DualOrbitWhichPath, 0.0, 1.0);
        int ell = fp.DualOrbitAngularMomentum;
        var mode = fp.DualOrbitWindingMode;
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
            var (wz, wc) = WindingPhases(ell, mode, tz, tc);
            double v = phase
                ? InterferencePhase(gZ, gC, tc - tz, kappa, gamma, wz, wc)
                : 0.5 * InterferenceIntensity(gC - gZ, tc - tz, kappa, gamma, eta, wc - wz);
            sc[i] = (float)Math.Clamp(v * maxIter, LiveFloor, maxIter);
        });
    }
}
