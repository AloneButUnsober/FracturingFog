// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1959 (epic #1253, D14) — Nova past the double loop: from DeepZoomThreshold the frame renders on the
// generic perturbation engine (Engine/Perturbation) with R2Plan.Nova — the quotient difference (#1911)
// for the division, a parameter-plane z₀ = NovaZ0 (≠ 0, so no rebasing: glitch tests and secondary
// references), and the engine's step stop |z_{n+1} − z_n|² < 1e-12 (Convergence.OnStep), the double
// loop's test. Julia mode is the dynamic plane. The basin is the root sector of the converged z and
// the smooth count the double loop's log interpolation (NovaSmooth), so FinishPixel colours a pixel
// exactly as the double loop does from the same values. Below the threshold nothing changes. Plain
// Newton (no c) stays on the double loop. Design: Docs/Technical/Deep-Zoom-All-Families-DesignPlan.md
// (D14). The Nova formula: Paul Derbyshire (Docs/Resources-Bibliography.md).

using System;
using System.Threading;
using FracturingFog.FFMath;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Perturbation;

namespace FracturingFog;

public sealed partial class NewtonCalculator : IDeepCentre, IDeepZoom
{
    /// <summary>#1959 — Nova renders on the engine from here (as EscapeTimeCalculator's families).</summary>
    public const double DeepZoomThreshold = 1e11;

    /// <summary>#1959 — false keeps Nova on the double loop at any zoom (tests compare them).</summary>
    public bool AllowPerturbation { get; set; } = true;

    /// <summary>"SP", or "PT" when the last frame ran on the perturbation engine.</summary>
    public string LastPrecisionLabel { get; private set; } = "SP";

    /// <summary>The engine's diagnostics for the last deep frame.</summary>
    public PerturbationStats LastPerturbationStats { get; private set; }

    // ── IDeepCentre: the double head is CenterX / CenterY; the tail limbs and the exact centre are
    //    kept while the head is unchanged since SetDeepCentre (a writer of CenterX alone moved the view). ──
    private DeepComplex _deep;
    private double _deepStampX = double.NaN, _deepStampY = double.NaN;
    private ExactCentre? _deepExact;

    public void SetDeepCentre(DeepComplex centre, ExactCentre? exact)
    {
        CenterX = centre.Re.X0; CenterY = centre.Im.X0;
        _deep = centre; _deepStampX = CenterX; _deepStampY = CenterY;
        _deepExact = exact;
    }

    private bool DeepCurrent => _deepStampX.Equals(CenterX) && _deepStampY.Equals(CenterY);

    public DeepComplex GetDeepCentre() => DeepCurrent ? _deep : new DeepComplex(new OD(CenterX), new OD(CenterY));

    public ExactCentre? CenterExact => DeepCurrent ? _deepExact : null;

    // ── IDeepZoom ──
    private double _zoomLog10 = double.NaN, _zoomLog10Stamp;

    public double ZoomLog10 => !double.IsNaN(_zoomLog10) && _zoomLog10Stamp.Equals(Zoom) ? _zoomLog10 : Math.Log10(Zoom);

    public void SetZoomLog10(double log10)
    {
        Zoom = FracturingFog.ViewState.ZoomLog.FromLog10(log10);
        _zoomLog10 = log10;
        _zoomLog10Stamp = Zoom;
    }

    // The plan, per (d, R, z₀ or Julia c).
    private (int D, double R, bool Julia, double A, double B, R2Plan Plan)? _novaPlan;

    private R2Plan NovaPlan(int d, double relax)
    {
        bool julia = FractalParameters.NovaJuliaMode;
        var p = julia ? FractalParameters.NovaJuliaC : FractalParameters.NovaZ0;
        if (_novaPlan is { } k && k.D == d && k.R.Equals(relax) && k.Julia == julia && k.A.Equals(p.Real) && k.B.Equals(p.Imaginary))
            return k.Plan;
        var plan = julia ? R2Plan.Nova(d, relax, (0, 0), (p.Real, p.Imaginary)) : R2Plan.Nova(d, relax, (p.Real, p.Imaginary));
        _novaPlan = (d, relax, julia, p.Real, p.Imaginary, plan);
        return plan;
    }

    private bool TryCalculateDeep(int d, double relax, int maxIter, INewtonColorMap? newtonMap, System.Threading.CancellationToken ct)
    {
        if (!AllowPerturbation || Zoom < DeepZoomThreshold) return false;
        var plan = NovaPlan(d, relax);
        double zl = ZoomLog10;
        int prec = BigFloat.PrecisionForZoom(zl);
        var centre = CenterExact is { } e ? e.WithPrecision(prec) : ExactCentre.FromDeep(GetDeepCentre(), prec);
        double scale = (3.5 / Math.Max(Width, Height)) / Zoom;
        double pxLog10 = Math.Log10(3.5 / Math.Max(Width, Height)) - zl;
        FloatExp? pixelFx = pxLog10 < -280 ? FloatExp.FromLog10(pxLog10) : null;
        int width = Width;
        const double eps2 = 1e-12;   // the double loop's tolerance
        byte[]? aaMask = PixelMask is { } pm && pm.Length == width * Height ? pm : null;

        // Not converged by maxIter (or out past the 1e300 bailout): basin −1, as the double loop leaves it.
        PerturbationEngine.PixelSink sink = (idx, iter, zr, zi, _, _) =>
            FinishPixel(idx, idx % width, idx / width, -1, iter, 0f, zr, zi, d, maxIter, true, newtonMap);
        var converge = PerturbationEngine.Convergence.OnStep(eps2, (idx, iter, zr, zi, prev2, step2) =>
            FinishPixel(idx, idx % width, idx / width, RootSector(zr, zi, d), iter,
                        NovaSmooth(iter, double.IsNaN(prev2) ? double.MaxValue : prev2, step2, eps2), zr, zi, d, maxIter, true, newtonMap));
        LastPerturbationStats = PerturbationEngine.Render(plan, centre, scale, width, Height, maxIter, sink, ct,
            marked: aaMask != null ? i => aaMask[i] != 0 : null, converge: converge, pixelScale: pixelFx);
        LastPrecisionLabel = "PT";
        return true;
    }
}
