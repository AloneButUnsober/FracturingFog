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
// PathInterferenceDomain (#1592) = both at once: the scalar (and Relief height) is
// I/2, and the colour pass paints hue = the phase on the colour-blind-safe Phase
// Wheel with brightness I/2 (Wegert-style phase portrait); the theme is not used.
// Live where both orbits escape (two paths); a single bounded path has nothing to
// interfere with → interior. κ and γ are colour-only: the scalar is rebuilt in
// the colour pass from the cached smooth counts and lifted angles, so animating κ
// recolours without iterating ("fringes shimmer"). η and ℓ are colour-only too.
//
// N-path interference (#1602, round 2 tracker #1605; design:
// Docs/Technical/DoubleSlit-Fractal-RnD.md §10.7). DualOrbitInterferencePaths = N
// paths, each a unit-phase amplitude a_k = e^{−2πγκ(w_k − w̄)}·e^{iφ_k}, summed and
// normalised as above:
//
//     I = |Σ a_k|² / Σ |a_k|²   ∈ [0, N],   scalar I/N,   I_η = 1 + (1 − η)(I − 1)
//
// (w_k the path's angle offset from the z-path in turns, w̄ their mean — for N = 2
// exactly the symmetric amplitudes above). With equal amplitudes and equal phase
// steps φ_k = φ_0 + kφ this is the N-slit grating function
// sin²(Nφ/2) / (N sin²(φ/2)) (Feynman, Leighton & Sands, Lectures Vol. I ch. 30
// §30-1, Eq. 30.3): the principal maxima stay where the two-path fringes peak
// (φ = 2πm) and narrow as 1/N (first zero at φ = 2π/N), with N − 2 weak secondary
// maxima between them. DualOrbitPathLayout:
//   Grating  — the ideal grating: φ_k = κ(G_z + kΔG) + wind_z + k(wind_c − wind_z),
//              w_k = k·Δθ (Δθ wrapped once). Built from the two cached orbits; no cost.
//   Ray      — real orbits seeded at 0, c, 2c, …: equal seed spacing, but G is not
//              linear in the seed, so the steps are unequal (a rippled, speckle-like
//              pattern rather than a grating).
//   Segment  — real orbits seeded at k·c/(N − 1): nearly equal steps ΔG/(N − 1), so the
//              principal maxima move out to κΔG = 2πm(N − 1) — mostly a broad lobe.
// A probe (R&D §10.7) also tried the issue's seeds on a small circle round c: the
// fringes wash out (the c-family adds almost in phase and swamps the z-path), so it
// was not built. Real paths: φ_k = κG_k + 2πℓθ_k (the c-family winds −ℓ in Opposite
// mode); an extra orbit that stays bounded has no Böttcher phase and counts as a
// blocked slit (amplitude 0). N = 2 runs the two-path code above (byte-identical).
//
// Wave themes (#1593; ColorGen `wave*` inputs, IWaveColorMap): on PathInterference
// and PathInterferencePhase a wave theme gets, per live pixel, waveIntensity =
// waveLevel = I/N (with η), wavePhase = arg Σa in turns, waveDeltaG = κ(G_c − G_z),
// waveDeltaTheta = θ_c − θ_z wrapped to [−½, ½), and the settings η, ℓ and N. The
// refresh pass then keeps both I/N and the phase in side buffers.

using System;
using System.Threading.Tasks;
using FracturingFog.Interefaces;
using FracturingFog.Models;

namespace FracturingFog;

public sealed partial class DualOrbitEscapeCalculator
{
    internal static bool IsInterferenceField(DualOrbitField f)
        => f is DualOrbitField.PathInterference or DualOrbitField.PathInterferencePhase or DualOrbitField.PathInterferenceDomain
            or DualOrbitField.PathInterferenceDetections;   // #1599: the scalar is I/2, as PathInterference

    // #1592 — per-pixel phase (turns) for the domain field's colour pass.
    private float[] _interfPhase = Array.Empty<float>();
    // #1593 — per-pixel I/N for a wave theme (empty when none is active).
    private float[] _interfI = Array.Empty<float>();

    /// <summary>#1593 — the active wave theme when the field is one a theme colours
    /// (PathInterference / PathInterferencePhase), else null.</summary>
    private IWaveColorMap? WaveTheme(in GeometryKey key)
        => !key.Layers && key.Field is DualOrbitField.PathInterference or DualOrbitField.PathInterferencePhase
            ? ColorMap as IWaveColorMap : null;

    /// <summary>#1593 — the wave sample of a live interference pixel (header).</summary>
    private WaveSample PairWave(int i, int paths, FractalParameters fp, double twoLogR)
    {
        double gZ = twoLogR * Math.Pow(2.0, -_smZ[i]), gC = twoLogR * Math.Pow(2.0, -_smC[i]);
        double v = _interfI[i];
        return new WaveSample
        {
            Intensity = v, Level = v, Phase = _interfPhase[i],
            DeltaG = fp.DualOrbitInterferenceK * (gC - gZ),
            DeltaTheta = Frac(_thC[i] - _thZ[i] + 0.5) - 0.5,
            WhichPath = double.IsNaN(fp.DualOrbitWhichPath) ? 0.0 : Math.Clamp(fp.DualOrbitWhichPath, 0.0, 1.0),
            AngularMomentum = fp.DualOrbitAngularMomentum,
            Paths = paths,
        };
    }
    // #1602 — extra real-orbit paths: smooth count and lifted angle (NaN = bounded),
    // [pixel · (N − 2) + j].
    private float[] _smX = Array.Empty<float>(), _thX = Array.Empty<float>();

    /// <summary>Largest N for <c>DualOrbitInterferencePaths</c> (#1602).</summary>
    public const int MaxInterferencePaths = 12;
    internal static int ClampPaths(int n) => Math.Clamp(n, 2, MaxInterferencePaths);

    /// <summary>#1602 — seed of extra path <paramref name="j"/> (0-based, after the z
    /// and c paths) as a multiple of the c-seed: Ray j + 2, Segment (j + 1)/(N − 1).</summary>
    public static double ExtraPathSeed(int j, int paths, bool ray) => ray ? j + 2 : (j + 1.0) / (paths - 1);

    /// <summary>#1602 — normalised N-path intensity I = |Σa|²/Σ|a|² ∈ [0, N] with
    /// a_k = e^{−2πγκ(w_k − w̄)}·e^{iφ_k} (see header), the which-path η applied, and
    /// arg Σa in turns. A NaN phase or offset is a blocked path (left out). Returns NaN
    /// when no path is open.</summary>
    public static double PathsIntensity(ReadOnlySpan<double> phase, ReadOnlySpan<double> offset,
        double gammaKappa, double whichPath, out double phaseTurns)
    {
        double mean = 0.0; int open = 0;
        for (int k = 0; k < phase.Length; k++)
            if (!double.IsNaN(phase[k]) && !double.IsNaN(offset[k])) { mean += offset[k]; open++; }
        phaseTurns = double.NaN;
        if (open == 0) return double.NaN;
        mean /= open;
        double re = 0.0, im = 0.0, norm = 0.0;
        for (int k = 0; k < phase.Length; k++)
        {
            if (double.IsNaN(phase[k]) || double.IsNaN(offset[k])) continue;
            double amp = Math.Exp(Math.Clamp(-2.0 * Math.PI * gammaKappa * (offset[k] - mean), -350.0, 350.0));
            re += amp * Math.Cos(phase[k]); im += amp * Math.Sin(phase[k]); norm += amp * amp;
        }
        phaseTurns = Frac(Math.Atan2(im, re) / (2.0 * Math.PI));
        double coh = (re * re + im * im) / norm;
        double eta = Math.Clamp(whichPath, 0.0, 1.0);
        return eta <= 0.0 ? coh : 1.0 + (1.0 - eta) * (coh - 1.0);
    }

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

    /// <summary>The cached scalar for an interference value <paramref name="v"/> in
    /// [0, 1] (I/N, or the phase in turns), clamped to [LiveFloor, maxIter]. #1591 — a
    /// phase just below one turn (≡ 0) rounds up to maxIter, which themes read as "in
    /// the set": wrap it to the floor. (The intensity reaches maxIter at its peak by
    /// design and is left alone.) Shared by the two-path and N-path passes (#1602).</summary>
    public static float InterferenceScalar(double v, int maxIter, bool phase)
    {
        float s = (float)Math.Clamp(v * maxIter, LiveFloor, maxIter);
        if (phase && s >= maxIter) s = (float)LiveFloor;
        return s;
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
        bool domain = key.Field == DualOrbitField.PathInterferenceDomain;   // #1592
        bool wave = WaveTheme(key) != null;   // #1593 — keep I/N and the phase for the theme
        if (wave) { if (_interfI.Length != n) _interfI = new float[n]; }
        else _interfI = Array.Empty<float>();
        if ((domain || wave) && _interfPhase.Length != n) _interfPhase = new float[n];
        float[] ph = _interfPhase, wi = _interfI;
        int maxIter = key.MaxIter;
        double twoLogR = 2.0 * Math.Log(key.Bailout);
        float[] smZ = _smZ, smC = _smC, thZ = _thZ, thC = _thC, sc = _scalar; byte[] flags = _flags;
        if (key.InterfPaths > 2) { RefreshPaths(key, phase, domain, wave, ph, wi, kappa, gamma, eta, ell, mode, twoLogR); return; }   // #1602
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
            if (domain || wave)
                ph[i] = (float)InterferencePhase(gZ, gC, tc - tz, kappa, gamma, wz, wc);
            if (wave)
                wi[i] = (float)(0.5 * InterferenceIntensity(gC - gZ, tc - tz, kappa, gamma, eta, wc - wz));
            sc[i] = InterferenceScalar(v, maxIter, phase);
        });
    }

    // #1602 — N > 2 paths (see header).
    private void RefreshPaths(in GeometryKey key, bool phase, bool domain, bool wave, float[] ph, float[] wi, double kappa, double gamma,
        double eta, int ell, DualOrbitWindingMode mode, double twoLogR)
    {
        int n = Width * Height, paths = key.InterfPaths, maxIter = key.MaxIter;
        bool grating = key.PathLayout == DualOrbitPathLayout.Grating;
        int extra = paths - 2;
        if (!grating && _smX.Length != n * extra) return;
        float[] smZ = _smZ, smC = _smC, thZ = _thZ, thC = _thC, smX = _smX, thX = _thX, sc = _scalar; byte[] flags = _flags;
        double windK = 2.0 * Math.PI * ell;
        Parallel.For(0, n, () => (new double[paths], new double[paths]), (i, _, buf) =>
        {
            var (phi, off) = buf;
            byte f = flags[i];
            float tz = thZ[i], tc = thC[i];
            if ((f & FlagPairDead) != 0 || (f & FlagZEsc) == 0 || (f & FlagCEsc) == 0 || float.IsNaN(tz) || float.IsNaN(tc))
            { sc[i] = 0f; return buf; }
            double gZ = twoLogR * Math.Pow(2.0, -smZ[i]), gC = twoLogR * Math.Pow(2.0, -smC[i]);
            var (wz, wc) = WindingPhases(ell, mode, tz, tc);
            double dTheta = Frac(tc - tz + 0.5) - 0.5;
            if (grating)
            {
                double step = kappa * (gC - gZ) + (wc - wz);
                for (int k = 0; k < paths; k++) { phi[k] = kappa * gZ + wz + k * step; off[k] = k * dTheta; }
            }
            else
            {
                phi[0] = kappa * gZ + wz; off[0] = 0.0;
                phi[1] = kappa * gC + wc; off[1] = dTheta;
                for (int j = 0; j < extra; j++)
                {
                    float t = thX[i * extra + j];
                    if (float.IsNaN(t)) { phi[j + 2] = double.NaN; off[j + 2] = double.NaN; continue; }   // blocked slit
                    double g = twoLogR * Math.Pow(2.0, -smX[i * extra + j]);
                    double w = mode == DualOrbitWindingMode.Opposite ? -windK * t : windK * t;
                    phi[j + 2] = kappa * g + w; off[j + 2] = Frac(t - tz + 0.5) - 0.5;
                }
            }
            // η changes only the intensity, never the phase (header).
            double inten = PathsIntensity(phi, off, gamma * kappa, eta, out double turns);
            double v = phase ? turns : inten / paths;
            if (domain || wave) ph[i] = (float)turns;
            if (wave) wi[i] = (float)(inten / paths);   // #1593
            sc[i] = InterferenceScalar(v, maxIter, phase);
            return buf;
        }, _ => { });
    }
}
