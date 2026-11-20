// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1907 (epic #1253, D2) — Julia and Multibrot past double: from DeepZoomThreshold they render on the
// generic perturbation engine (Engine/Perturbation) with a PowerPlan, from the full centre the host
// hands over (IDeepCentre, #1906). Below the threshold the SP / SIMD cores run as before. Design:
// Docs/Technical/Deep-Zoom-All-Families-DesignPlan.md §4.1, §9 (D2). #1909 (D4) adds Tricorn, on an
// R2Plan (conj(z)² + c as a real map, with the 2×2 matrix BLA): its deep derivative is the true
// ∂z/∂Re(c), where TricornKernel's shallow one tracks z² + c's ("as if Mandelbrot"). #1910 (D5) adds
// Burning Ship on the same plan type, with the diffabs folds; BurningShipKernel's shallow derivative
// is its own sign-weighted form, the deep one the true Jacobian column. #1911 (D6) adds Magnet 1 / 2
// (division: the quotient difference; MagnetConvergence's stop at z = 1 through the engine's
// Convergence). The kernels clamp |den|² at 1e-12 by a pole; the deep path divides exactly. #1912 (D7)
// adds Phoenix and Spider on TwoStatePlan (a δ for z and one for z_{n−1} / the evolving c); Phoenix's
// old scalar "PT" tier (D-3.16, from 1e10) retires — it rebuilt z in double from a double reference,
// so it resolved no deeper than the SP path. #1913 (D8) adds Glynn (z^1.5 + c, the dynamic plane) on an
// R2Plan with the elementary-function differences and BigFloatMath's reference.

using System;
using System.Threading;
using FracturingFog.FFMath;
using FracturingFog.Interefaces;
using FracturingFog.Perturbation;

namespace FracturingFog;

public sealed partial class EscapeTimeCalculator : IDeepCentre, IDeepZoom
{
    // ── #1921 (D2b): the zoom past double, and the derivative's distance unit ──

    private double _zoomLog10 = double.NaN, _zoomLog10Stamp;

    /// <inheritdoc/>
    public double ZoomLog10
        => !double.IsNaN(_zoomLog10) && _zoomLog10Stamp.Equals(Zoom) ? _zoomLog10 : Math.Log10(Zoom);

    /// <inheritdoc/>
    public void SetZoomLog10(double log10)
    {
        Zoom = FracturingFog.ViewState.ZoomLog.FromLog10(log10);
        _zoomLog10 = log10;
        _zoomLog10Stamp = Zoom;
    }

    // MandelbrotCalculator's #1783 / #1843 distance unit, on the deep path only (the SP cores'
    // frames keep complex-plane units: _distExp2 = 0). DistanceBuffer, FinalDr / Di and
    // DistancePixelScale are in units of 2^−DistanceUnitExp2 complex-plane units (a derivative's
    // unit is 2^e); a colour map gets that unit only if it opts in (IColorMapDistanceUnit), else
    // complex-plane units, bit for bit as before.
    private int _distExp2, _mapDistExp2;
    private double _distPixelScale;

    /// <summary>#1921 — the distance unit's exponent for the last frame (0: complex-plane units).</summary>
    public int DistanceUnitExp2 => _distExp2;

    private void ResetDistanceUnit() { _distExp2 = 0; _mapDistExp2 = 0; _distPixelScale = 0; }

    // The deep frame's unit, and the pixel scale its map gets.
    private void SetDistanceUnit(double pxLog10)
    {
        (_distPixelScale, _distExp2) = MandelbrotCalculator.DistanceUnit(LastPixelScale, pxLog10);
        bool optIn = _distExp2 != 0 && ColorMap is IColorMapDistanceUnit u && u.TakesDistanceUnit;
        _mapDistExp2 = optIn ? 0 : _distExp2;
        if (_distExp2 != 0 && ColorMap is IColorMapWithPixelScale pxs) pxs.PixelScale = optIn ? _distPixelScale : LastPixelScale;
    }

    /// <summary>#1907 — Julia and Multibrot switch to the perturbation engine at this zoom. The SP
    /// core still resolves pixels here (~3e-13 apart, ~1e3 ulps), so the two agree at the switch.</summary>
    public const double DeepZoomThreshold = 1e11;

    /// <summary>"SP", or "PT" when the last frame ran on the perturbation engine.</summary>
    public string LastPrecisionLabel { get; private set; } = "SP";

    /// <summary>The engine's diagnostics for the last deep frame (default otherwise).</summary>
    public PerturbationStats LastPerturbationStats { get; private set; }

    /// <summary>#1907 — the deep path is off when false (tests compare it with the SP core).</summary>
    public bool AllowPerturbation { get; set; } = true;

    // ── IDeepCentre (#1906): this calculator keeps all sixteen limbs and the exact centre. ──
    private double _deepX4, _deepX5, _deepX6, _deepX7, _deepY4, _deepY5, _deepY6, _deepY7;
    private ExactCentre? _deepExact;
    private DeepComplex _deepExactStamp;

    public void SetDeepCentre(DeepComplex centre, ExactCentre? exact)
    {
        OD re = centre.Re, im = centre.Im;
        CenterX = re.X0; CenterXLo = re.X1; CenterX2 = re.X2; CenterX3 = re.X3;
        CenterY = im.X0; CenterYLo = im.X1; CenterY2 = im.X2; CenterY3 = im.X3;
        _deepX4 = re.X4; _deepX5 = re.X5; _deepX6 = re.X6; _deepX7 = re.X7;
        _deepY4 = im.X4; _deepY5 = im.X5; _deepY6 = im.X6; _deepY7 = im.X7;
        _deepExact = exact;
        _deepExactStamp = centre;
    }

    public DeepComplex GetDeepCentre() => new(
        new OD(CenterX, CenterXLo, CenterX2, CenterX3, _deepX4, _deepX5, _deepX6, _deepX7),
        new OD(CenterY, CenterYLo, CenterY2, CenterY3, _deepY4, _deepY5, _deepY6, _deepY7));

    public ExactCentre? CenterExact
        => _deepExact is { } e && ExactCentre.SameLimbs(_deepExactStamp, GetDeepCentre()) ? e : null;

    // Tricorn's plan compiles its delegates once (R2Plan is immutable and thread-safe).
    private static readonly Lazy<R2Plan> TricornPlan = new(R2Plan.Tricorn);
    private static readonly Lazy<R2Plan> BurningShipPlan = new(R2Plan.BurningShip);   // #1910
    private static readonly Lazy<R2Plan> Magnet1Plan = new(R2Plan.Magnet1);           // #1911
    private static readonly Lazy<R2Plan> Magnet2Plan = new(R2Plan.Magnet2);

    // The plan for the deep path, or null when this family / zoom stays on the SP core.
    private IPerturbationPlan? DeepPlan()
    {
        if (!AllowPerturbation || Zoom < DeepZoomThreshold) return null;
        return FractalType switch
        {
            FractalType.Julia => new PowerPlan(2, parameterPlane: false,
                FractalParameters.EffectiveJuliaC.Real, FractalParameters.EffectiveJuliaC.Imaginary),
            FractalType.Multibrot => new PowerPlan(Math.Max(2, FractalParameters.MultibrotExponent), parameterPlane: true),
            FractalType.Tricorn => TricornPlan.Value,   // #1909
            FractalType.BurningShip => BurningShipPlan.Value,   // #1910: the diffabs folds
            FractalType.Magnet1 => Magnet1Plan.Value,           // #1911: the quotient difference
            FractalType.Magnet2 => Magnet2Plan.Value,
            FractalType.Glynn => GlynnPlan(FractalParameters.GlynnC.Real, FractalParameters.GlynnC.Imaginary),   // #1913
            _ => null,
        };
    }

    // #1913 — Glynn's plan, per c (its delegates compile once per c).
    private (double Cr, double Ci, R2Plan Plan)? _glynn;
    private R2Plan GlynnPlan(double cr, double ci)
    {
        if (_glynn is { } g && g.Cr == cr && g.Ci == ci) return g.Plan;
        var plan = R2Plan.Glynn(cr, ci);
        _glynn = (cr, ci, plan);
        return plan;
    }

    // #1912 — Phoenix and Spider carry a second state (z_{n−1}; the evolving c).
    private (double P0, double P1, double D, TwoStatePlan Plan)? _twoState;

    private TwoStatePlan? DeepTwoStatePlan()
    {
        if (!AllowPerturbation || Zoom < DeepZoomThreshold) return null;
        double a, b = 0, d = 0;
        switch (FractalType)
        {
            case FractalType.Phoenix: a = FractalParameters.PhoenixP.Real; b = FractalParameters.PhoenixP.Imaginary; break;
            case FractalType.Spider: a = double.NaN; d = FractalParameters.SpiderCDecay; break;
            default: return null;
        }
        if (_twoState is { } t && t.P0.Equals(a) && t.P1 == b && t.D == d) return t.Plan;
        var plan = FractalType == FractalType.Phoenix ? TwoStatePlan.Phoenix(a, b) : TwoStatePlan.Spider(d);
        _twoState = (a, b, d, plan);
        return plan;
    }

    // Render the frame on the engine when DeepPlan applies; false leaves it to the SP cores.
    private bool TryCalculateDeep(CancellationToken ct)
    {
        LastPrecisionLabel = "SP";
        var plan = DeepPlan();
        var two = plan == null ? DeepTwoStatePlan() : null;
        if (plan == null && two == null) return false;

        double zl = ZoomLog10;   // #1921: past double, the log zoom
        int prec = BigFloat.PrecisionForZoom(zl);
        var centre = CenterExact is { } e ? e.WithPrecision(prec) : ExactCentre.FromDeep(GetDeepCentre(), prec);
        double scale = (3.5 / Math.Max(Width, Height)) / Zoom;
        // #1921 — the pixel scale as a FloatExp past the double-δ tier, and the distance unit.
        double pxLog10 = Math.Log10(3.5 / Math.Max(Width, Height)) - zl;
        FloatExp? pixelFx = pxLog10 < -280 ? FloatExp.FromLog10(pxLog10) : null;
        SetDistanceUnit(pxLog10);
        int unitE = _distExp2;
        int maxIt = MaxIterations;
        int[]? perRow = PerRowMaxIter is { } pr && pr.Length >= Height ? pr : null;
        var map = ColorMap;
        int width = Width;
        // A tighter BlaTolerance still wins; the engine's ceiling is PerturbationEngine.DefaultBlaEpsilon.
        double eps = FractalParameters.BlaTolerance > 0 && double.IsFinite(FractalParameters.BlaTolerance)
            ? Math.Min(FractalParameters.BlaTolerance, PerturbationEngine.DefaultBlaEpsilon) : PerturbationEngine.DefaultBlaEpsilon;

        // #1911 — Magnet: no dz/dc (the kernels leave it 0; normals flat), and with MagnetConvergence
        // a second stop at z = 1 coloured as CalculateMagnet colours it.
        bool magnet = FractalType is FractalType.Magnet1 or FractalType.Magnet2;
        PerturbationEngine.Convergence? converge = null;
        if (magnet && FractalParameters.MagnetConvergence)
        {
            double convEps = Math.Max(1e-12, FractalParameters.MagnetConvergenceEpsilon), logEps = Math.Log(convEps);
            converge = new PerturbationEngine.Convergence(1.0, 0.0, convEps, (idx, iter, zr, zi, dPrev) =>
            {
                if (_batchCount is { } bcc) bcc[idx]++;
                double dCur = Math.Sqrt((zr - 1.0) * (zr - 1.0) + zi * zi);
                FillMagnetConverged(idx, iter, zr, zi, dPrev, dCur, convEps, logEps, maxIt, map);
            });
        }

        // Spider tracks no dz/dc (CalculateSpider passes 0); Phoenix's is D-3.16's, the tangent here.
        bool flatDerivative = magnet || FractalType is FractalType.Spider or FractalType.Glynn;   // GlynnKernel tracks none
        PerturbationEngine.PixelSink sink = (idx, iter, zr, zi, dvr, dvi) =>
        {
            if (_batchCount is { } bcs) bcs[idx]++;
            if (flatDerivative) { dvr = 0; dvi = 0; }
            IterationBuffer[idx] = iter;
            FillAuxAndColor(idx, iter, maxIt, zr, zi, dvr, dvi, map);
            // Phase 2.1 (the video per-row cap): a pixel that ran to its row's cap is in set.
            if (perRow != null && iter >= perRow[idx / width] && perRow[idx / width] < maxIt)
                IterationBuffer[idx] = maxIt;
        };
        LastPerturbationStats = two != null
            ? PerturbationEngine.RenderTwoState(two, centre, scale, Width, Height, maxIt, sink, ct, perRow, _mask != null ? Marked : null,
                pixelScale: pixelFx, derivExp2: unitE)   // #1957
            : PerturbationEngine.Render(plan!, centre, scale, Width, Height, maxIt, sink,
                ct, perRow, _mask != null ? Marked : null, useBla: true, blaEpsilon: eps, converge: converge,
                pixelScale: pixelFx, derivExp2: unitE);
        LastPrecisionLabel = "PT";
        return true;
    }
}
