// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// WhiteLight.cs (#1597) — white-light (spectral) colouring for the Slit Cascade
// and Talbot Carpet intensity views. Real fringes in white light are coloured: each
// wavelength diffracts by a different amount. The renderers evaluate the intensity at
// K wavelengths across the visible band (380–720 nm, equal energy) and this class
// turns the K intensities into one colour.
//
// Sources:
// • Colour matching: the multi-lobe piecewise-Gaussian fit to the CIE 1931 2°
//   observer of Wyman, Sloan & Shirley, "Simple Analytic Approximations to the CIE
//   XYZ Color Matching Functions", JCGT 2(2), 1–11, 2013 (Table 1 coefficients;
//   Docs/Resources-Bibliography.md#wyman-cie-fit).
// • XYZ → OkLab: Björn Ottosson's Oklab M1 / M2 matrices (bibliography: Ottosson).
// • White-light self-imaging is known (Packross, Eschbach & Bryngdahl, Proc. SPIE
//   0492, 1985 — bibliography#polychromatic-self-imaging); FF's sampling and colour
//   mapping are its own.
//
// FF's choices: the white point is equal-energy light, balanced to neutral by a von
// Kries scaling of Ottosson's LMS (so a pixel where every wavelength is equally bright
// is exactly grey). Brightness is NOT the raw luminance: the renderer computes the
// photopic-weighted intensity I_eff = Σ ȳ(λ_k) I_k / Σ ȳ(λ_k) and puts it through the
// same log scale as the monochrome Intensity view, so one sample reproduces that view's
// brightness exactly; the spectrum only sets the hue.
//
// Two mappings. TrueSpectrum: CIE colour (mostly a red↔green axis — hard for the owner
// and other red/green colour-blind viewers). BlueAmber: the a = 0 axis of the Phase
// Wheel (#1591). Its strength is the true colour's OkLab chroma C (how far from white
// the mixture is); its direction is where the light's ȳ-weighted spectral centroid sits
// against white's: shorter = blue (b < 0), longer = amber (b > 0):
// b = clamp(C·tanh(Δλ / 15 nm), ±0.13). (A plain ȳ-weighted mean of per-wavelength b
// values was measured ~10× duller than the true spectrum, so it was not used.) A
// red + violet mixture has Δλ ≈ 0 and shows grey: the blue–amber axis has no purple.

using System;
using FracturingFog.Models;

namespace FracturingFog;

/// <summary>Spectral colouring for white light (#1597); see the file header.</summary>
public sealed class WhiteLight
{
    public const double MinNm = 380.0, MaxNm = 720.0, RefNm = 550.0;
    public const int MinSamples = 1, MaxSamples = 81;
    /// <summary>OkLab b of the bluest / most amber wavelength in the BlueAmber mapping
    /// (the Phase Wheel's b amplitude).</summary>
    public const double BlueAmberB = PhaseWheelBlueAmberMap.BAmp;

    /// <summary>Wavelengths sampled, nm (midpoints of K equal bins; K = 1 gives 550).</summary>
    public double[] Nm { get; }
    /// <summary>λ_k / 550 nm: how far each wavelength's pattern is scaled.</summary>
    public double[] Ratio { get; }
    private readonly double[] _yW;               // ȳ weights, normalised to sum 1
    private readonly double[] _l, _m, _s;        // per-sample LMS (balanced), ȳ-normalised
    private readonly double _whiteCentroidNm;    // ȳ-weighted mean wavelength of white light
    /// <summary>Δλ scale of the BlueAmber direction (nm).</summary>
    public const double CentroidScaleNm = 15.0;

    public int Count => Nm.Length;

    public WhiteLight(int samples)
    {
        int k = Math.Clamp(samples, MinSamples, MaxSamples);
        Nm = new double[k]; Ratio = new double[k];
        _yW = new double[k]; _l = new double[k]; _m = new double[k]; _s = new double[k];
        double ySum = 0, lw = 0, mw = 0, sw = 0;
        var xyz = new (double X, double Y, double Z)[k];
        for (int i = 0; i < k; i++)
        {
            double nm = k == 1 ? RefNm : MinNm + (i + 0.5) * (MaxNm - MinNm) / k;
            Nm[i] = nm; Ratio[i] = nm / RefNm;
            xyz[i] = (XBar(nm), YBar(nm), ZBar(nm));
            ySum += xyz[i].Y;
        }
        for (int i = 0; i < k; i++)
        {
            var (X, Y, Z) = xyz[i];
            _l[i] = 0.8189330101 * X + 0.3618667424 * Y - 0.1288597137 * Z;
            _m[i] = 0.0329845436 * X + 0.9293118715 * Y + 0.0361456387 * Z;
            _s[i] = 0.0482003018 * X + 0.2643662691 * Y + 0.6338517070 * Z;
            lw += _l[i]; mw += _m[i]; sw += _s[i];
        }
        // von Kries: equal-energy white → l = m = s, scaled so its luminance-weight sum is 1.
        double centroid = 0;
        for (int i = 0; i < k; i++)
        {
            _yW[i] = ySum > 0 ? xyz[i].Y / ySum : 1.0 / k;
            _l[i] /= lw; _m[i] /= mw; _s[i] /= sw;
            centroid += _yW[i] * Nm[i];
        }
        _whiteCentroidNm = centroid;
    }

    /// <summary>The photopic-weighted intensity Σ ȳ_k I_k / Σ ȳ_k: what the brightness
    /// scale sees (one sample = that sample's intensity).</summary>
    public double EffectiveIntensity(ReadOnlySpan<double> intensity)
    {
        double s = 0;
        for (int i = 0; i < intensity.Length; i++) s += _yW[i] * intensity[i];
        return s;
    }

    /// <summary>Colour of a pixel whose per-wavelength intensities are
    /// <paramref name="intensity"/>, at brightness <paramref name="k"/> ∈ [0, 1] (the
    /// monochrome view's log-scale value).</summary>
    public uint Colour(ReadOnlySpan<double> intensity, double k, WhiteLightMode mode)
    {
        if (!(k > 0.0)) return 0xFF000000u;
        k = Math.Min(1.0, k);
        var (L, a, b) = Chroma(intensity, mode);
        L *= k;
        double c = k;   // dim in proportion (L and chroma together) so dark colours stay in gamut
        GradientColorSpaces.OkLabToRgb((float)L, (float)(a * c), (float)(b * c), out float r, out float g, out float bl);
        static uint C(float v) => (uint)Math.Clamp((int)(v + 0.5f), 0, 255);
        return 0xFF000000u | (C(r) << 16) | (C(g) << 8) | C(bl);
    }

    /// <summary>The OkLab colour at full brightness (L ≈ 0.75 for white): hue and chroma
    /// from the spectrum, lightness fixed — brightness comes from k.</summary>
    public (double L, double a, double b) Chroma(ReadOnlySpan<double> intensity, WhiteLightMode mode)
    {
        const double LWhite = 0.75;
        double w = 0;
        for (int i = 0; i < intensity.Length; i++) w += _yW[i] * Math.Max(0.0, intensity[i]);
        if (!(w > 0)) return (0, 0, 0);
        double l = 0, m = 0, s = 0;
        for (int i = 0; i < intensity.Length; i++)
        {
            double v = Math.Max(0.0, intensity[i]);
            l += v * _l[i]; m += v * _m[i]; s += v * _s[i];
        }
        // Normalise to white's luminance scale, then into OkLab (Ottosson M2 after a cube root).
        double norm = 1.0 / Math.Max(1e-300, l + m + s) * 3.0;
        double lc = Math.Cbrt(l * norm), mc = Math.Cbrt(m * norm), sc = Math.Cbrt(s * norm);
        double L0 = 0.2104542553 * lc + 0.7936177850 * mc - 0.0040720468 * sc;
        double A0 = 1.9779984951 * lc - 2.4285922050 * mc + 0.4505937099 * sc;
        double B0 = 0.0259040371 * lc + 0.7827717662 * mc - 0.8086757660 * sc;
        // Keep the hue and chroma, put the lightness at LWhite (white has L0 = 1 here).
        double scale = L0 > 1e-9 ? LWhite / L0 : 0.0;
        if (mode == WhiteLightMode.BlueAmber)
        {
            double cen = 0;
            for (int i = 0; i < intensity.Length; i++) cen += _yW[i] * Math.Max(0.0, intensity[i]) * Nm[i];
            double dl = cen / w - _whiteCentroidNm;
            double chroma = Math.Sqrt(A0 * A0 + B0 * B0) * scale;
            return (LWhite, 0.0, Math.Clamp(chroma * Math.Tanh(dl / CentroidScaleNm), -BlueAmberB, BlueAmberB));
        }
        return (LWhite, A0 * scale, B0 * scale);
    }

    // ── CIE 1931 2° colour matching, Wyman–Sloan–Shirley multi-lobe fit (Table 1) ──

    private static double Lobe(double nm, double mu, double invLeft, double invRight)
    {
        double t = (nm - mu) * (nm < mu ? invLeft : invRight);
        return Math.Exp(-0.5 * t * t);
    }

    public static double XBar(double nm) =>
        0.362 * Lobe(nm, 442.0, 0.0624, 0.0374) + 1.056 * Lobe(nm, 599.8, 0.0264, 0.0323) - 0.065 * Lobe(nm, 501.1, 0.0490, 0.0382);
    public static double YBar(double nm) =>
        0.821 * Lobe(nm, 568.8, 0.0213, 0.0247) + 0.286 * Lobe(nm, 530.9, 0.0613, 0.0322);
    public static double ZBar(double nm) =>
        1.217 * Lobe(nm, 437.0, 0.0845, 0.0278) + 0.681 * Lobe(nm, 459.0, 0.0385, 0.0725);
}
