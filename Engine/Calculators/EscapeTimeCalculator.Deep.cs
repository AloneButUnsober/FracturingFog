// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1907 (epic #1253, D2) — Julia and Multibrot past double: from DeepZoomThreshold they render on the
// generic perturbation engine (Engine/Perturbation) with a PowerPlan, from the full centre the host
// hands over (IDeepCentre, #1906). Below the threshold the SP / SIMD cores run as before. Design:
// Docs/Technical/Deep-Zoom-All-Families-DesignPlan.md §4.1, §9 (D2).

using System;
using System.Threading;
using FracturingFog.FFMath;
using FracturingFog.Interefaces;
using FracturingFog.Perturbation;

namespace FracturingFog;

public sealed partial class EscapeTimeCalculator : IDeepCentre
{
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

    // The plan for the deep path, or null when this family / zoom stays on the SP core.
    private PowerPlan? DeepPlan()
    {
        if (!AllowPerturbation || Zoom < DeepZoomThreshold) return null;
        return FractalType switch
        {
            FractalType.Julia => new PowerPlan(2, parameterPlane: false,
                FractalParameters.EffectiveJuliaC.Real, FractalParameters.EffectiveJuliaC.Imaginary),
            FractalType.Multibrot => new PowerPlan(Math.Max(2, FractalParameters.MultibrotExponent), parameterPlane: true),
            _ => null,
        };
    }

    // Render the frame on the engine when DeepPlan applies; false leaves it to the SP cores.
    private bool TryCalculateDeep(CancellationToken ct)
    {
        LastPrecisionLabel = "SP";
        var plan = DeepPlan();
        if (plan == null) return false;

        int prec = BigFloat.PrecisionForZoom(Math.Log10(Zoom));
        var centre = CenterExact is { } e ? e.WithPrecision(prec) : ExactCentre.FromDeep(GetDeepCentre(), prec);
        double scale = (3.5 / Math.Max(Width, Height)) / Zoom;
        int maxIt = MaxIterations;
        int[]? perRow = PerRowMaxIter is { } pr && pr.Length >= Height ? pr : null;
        var map = ColorMap;
        int width = Width;
        // A tighter BlaTolerance still wins; the engine's ceiling is PerturbationEngine.DefaultBlaEpsilon.
        double eps = FractalParameters.BlaTolerance > 0 && double.IsFinite(FractalParameters.BlaTolerance)
            ? Math.Min(FractalParameters.BlaTolerance, PerturbationEngine.DefaultBlaEpsilon) : PerturbationEngine.DefaultBlaEpsilon;

        LastPerturbationStats = PerturbationEngine.Render(plan, centre, scale, Width, Height, maxIt,
            (idx, iter, zr, zi, dvr, dvi) =>
            {
                if (_batchCount is { } bcs) bcs[idx]++;
                IterationBuffer[idx] = iter;
                FillAuxAndColor(idx, iter, maxIt, zr, zi, dvr, dvi, map);
                // Phase 2.1 (the video per-row cap): a pixel that ran to its row's cap is in set.
                if (perRow != null && iter >= perRow[idx / width] && perRow[idx / width] < maxIt)
                    IterationBuffer[idx] = maxIt;
            },
            ct, perRow, _mask != null ? Marked : null, useBla: true, blaEpsilon: eps);
        LastPrecisionLabel = "PT";
        return true;
    }
}
