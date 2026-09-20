// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Numerics;

namespace FracturingFog.Abstractions.Animation;

/// <summary>Precomputed Fatou-coordinate tables for the fast <c>g_α</c> evaluation (#918 SG1, the
/// S6 precompute engine). The two grids are <b>α-independent</b> — <c>Φ_att</c> over the attracting
/// basin (in the germ coordinate <c>w = z − 1/2</c>) and <c>Φ_rep⁻¹</c> over the cylinder value
/// <c>σ</c> — so one build serves every Lavaurs phase: a call maps <c>w → τ = Φ_att(w)</c>, adds
/// <c>α</c>, and maps <c>σ = τ + α → w' = Φ_rep⁻¹(σ)</c>, all by bilinear interpolation, then returns
/// <c>f(w') + 1/2</c>. This turns the millisecond direct <see cref="LavaursEngine"/> evaluation into
/// the ~µs render path (S6: ~116 000×). A sample is valid only when all four bracketing grid nodes
/// are valid, so the interpolated domain shrinks conservatively to the well-conditioned interior —
/// exactly the domain a caller wants for the word-tree's domain-restricted <c>g_α</c> generator.</summary>
public sealed class LavaursCoordinateTable
{
    readonly double _aReMin, _aReStep, _aImMin, _aImStep;
    readonly int _aNx, _aNy;
    readonly Complex[] _att; readonly bool[] _attOk;

    readonly double _sReMin, _sReStep, _sImMin, _sImStep;
    readonly int _sNx, _sNy;
    readonly Complex[] _inv; readonly bool[] _invOk;

    /// <summary>Build the tables with <paramref name="engine"/> over the given boxes (in the germ
    /// <c>w</c>-plane for <c>Φ_att</c>, and the <c>σ</c>-plane for <c>Φ_rep⁻¹</c>). The defaults span
    /// the measured basin/cylinder image (design doc §9 SG1: <c>τ ∈ Re[−0.5, 13.5] × Im[±7.5]</c>).
    /// Grid counts are node counts (cells + 1).</summary>
    public LavaursCoordinateTable(
        LavaursEngine engine,
        int attNx = 256, int attNy = 256,
        double wReMin = -1.30, double wReMax = 0.05, double wImMin = -0.75, double wImMax = 0.75,
        int invNx = 256, int invNy = 256,
        double sReMin = -0.6, double sReMax = 14.6, double sImMin = -8.0, double sImMax = 8.0)
    {
        if (engine == null) throw new ArgumentNullException(nameof(engine));
        attNx = global::System.Math.Max(2, attNx); attNy = global::System.Math.Max(2, attNy);
        invNx = global::System.Math.Max(2, invNx); invNy = global::System.Math.Max(2, invNy);

        _aNx = attNx; _aNy = attNy;
        _aReMin = wReMin; _aImMin = wImMin;
        _aReStep = (wReMax - wReMin) / (attNx - 1);
        _aImStep = (wImMax - wImMin) / (attNy - 1);
        _att = new Complex[attNx * attNy]; _attOk = new bool[attNx * attNy];
        for (int j = 0; j < attNy; j++)
            for (int i = 0; i < attNx; i++)
            {
                var w = new Complex(wReMin + i * _aReStep, wImMin + j * _aImStep);
                Complex tau = engine.PhiAtt(w);
                bool ok = double.IsFinite(tau.Real) && double.IsFinite(tau.Imaginary) && tau.Magnitude < 1e4;
                _att[j * attNx + i] = tau; _attOk[j * attNx + i] = ok;
            }

        _sNx = invNx; _sNy = invNy;
        _sReMin = sReMin; _sImMin = sImMin;
        _sReStep = (sReMax - sReMin) / (invNx - 1);
        _sImStep = (sImMax - sImMin) / (invNy - 1);
        _inv = new Complex[invNx * invNy]; _invOk = new bool[invNx * invNy];
        for (int j = 0; j < invNy; j++)
            for (int i = 0; i < invNx; i++)
            {
                var s = new Complex(sReMin + i * _sReStep, sImMin + j * _sImStep);
                bool ok = engine.TryPhiRepInv(s, out Complex wr);
                _inv[j * invNx + i] = wr; _invOk[j * invNx + i] = ok;
            }
    }

    static bool Bilinear(Complex[] grid, bool[] ok, int nx, int ny,
                         double reMin, double reStep, double imMin, double imStep,
                         double re, double im, out Complex value)
    {
        value = default;
        double fx = (re - reMin) / reStep, fy = (im - imMin) / imStep;
        int i0 = (int)global::System.Math.Floor(fx), j0 = (int)global::System.Math.Floor(fy);
        if (i0 < 0 || j0 < 0 || i0 >= nx - 1 || j0 >= ny - 1) return false;
        int a = j0 * nx + i0, b = a + 1, c = a + nx, d = c + 1;
        if (!ok[a] || !ok[b] || !ok[c] || !ok[d]) return false;
        double tx = fx - i0, ty = fy - j0;
        Complex top = grid[a] * (1 - tx) + grid[b] * tx;
        Complex bot = grid[c] * (1 - tx) + grid[d] * tx;
        value = top * (1 - ty) + bot * ty;
        return true;
    }

    /// <summary>The interpolated attracting Fatou coordinate <c>τ = Φ_att(z − 1/2)</c>, or false
    /// outside the tabulated basin interior.</summary>
    public bool TryPhiAtt(Complex z, out Complex tau)
    {
        Complex w = z - LavaursEngine.FixedPoint;
        return Bilinear(_att, _attOk, _aNx, _aNy, _aReMin, _aReStep, _aImMin, _aImStep,
                        w.Real, w.Imaginary, out tau);
    }

    /// <summary>The fast (bilinear) Lavaurs map <c>g_α</c> in the z-plane. Returns false outside the
    /// interpolable domain (a basin-boundary / ill-conditioned point) — the caller then applies
    /// only <c>f</c>.</summary>
    public bool TryGAlpha(Complex z, double alpha, out Complex gz)
    {
        gz = default;
        if (!TryPhiAtt(z, out Complex tau)) return false;
        Complex sigma = tau + alpha;
        if (!Bilinear(_inv, _invOk, _sNx, _sNy, _sReMin, _sReStep, _sImMin, _sImStep,
                      sigma.Real, sigma.Imaginary, out Complex wr)) return false;
        Complex gw = wr + wr * wr;                           // f(w') = w' + w'²
        if (!double.IsFinite(gw.Real)) return false;
        gz = gw + LavaursEngine.FixedPoint;
        return true;
    }
}
