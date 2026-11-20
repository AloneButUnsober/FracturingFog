// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1908 (epic #1253, D3) — a User Equation past double: from EscapeTimeCalculator.DeepZoomThreshold
// a polynomial equation renders on PerturbationEngine with an AstPlan derived from its AST. The
// double interpreter (this file's sibling) keeps everything else: equations outside the polynomial
// subset, a z₀ seed, a bailout condition, and orbit-aware themes (they sample every iterate, which
// the engine's BLA skips). Colouring mirrors the interpreter's plain branch: in-set fill, or smooth
// iteration, the Hubbard–Douady normal from the exact dz/dc, the nine-parameter Map, the out-of-
// bounds surround. Design: Docs/Technical/Deep-Zoom-All-Families-DesignPlan.md §5.

using System;
using System.Threading;
using FracturingFog.CalculatorGen;
using FracturingFog.FFMath;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Perturbation;

namespace FracturingFog;

public sealed partial class UserEquationCalculator : IDeepZoom
{
    // #1957 — the zoom past double (IDeepZoom, as EscapeTimeCalculator's #1921).
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

    /// <summary>"SP", or "PT" when the last frame ran on the perturbation engine.</summary>
    public string LastPrecisionLabel { get; private set; } = "SP";

    /// <summary>The engine's diagnostics for the last deep frame.</summary>
    public PerturbationStats LastPerturbationStats { get; private set; }

    /// <summary>Why the last frame at depth stayed on the double interpreter (null when it did not,
    /// or the zoom was shallow).</summary>
    public string? DeepRefusal { get; private set; }

    /// <summary>#1908 — the deep path is off when false (tests compare it with the interpreter).</summary>
    public bool AllowPerturbation { get; set; } = true;

    private SandboxExpression? _planFor;
    private double _planBailout2;
    private IPerturbationPlan? _plan;
    private TwoStatePlan? _plan2;   // #1912 — an equation that reads prev
    private string? _planWhy;

    private bool TryCalculateDeep(SandboxExpression sbx, SandboxExpression? seed, SandboxExpression? cond, CancellationToken ct)
    {
        LastPrecisionLabel = "SP";
        DeepRefusal = null;
        if (!AllowPerturbation || Zoom < EscapeTimeCalculator.DeepZoomThreshold) return false;
        if (seed != null) { DeepRefusal = "a z0 seed"; return false; }
        if (cond != null) { DeepRefusal = "a bailout condition"; return false; }
        if (ColorMap is IOrbitAwareColorMap) { DeepRefusal = "an orbit-aware theme"; return false; }

        double bailout2 = FractalParameters.EscapeRadius > 0.0
            ? FractalParameters.EscapeRadius * FractalParameters.EscapeRadius : 1024.0;
        if (!ReferenceEquals(_planFor, sbx) || _planBailout2 != bailout2)
        {
            var lowered = CalcGenLowering.Lower(sbx);
            _plan = null; _plan2 = null;
            if (lowered.Ok && TwoStatePlan.UsesPrev(lowered.Ast!)) _plan2 = TwoStatePlan.TryCreatePrev(lowered.Ast!, bailout2, out _planWhy);
            else _plan = lowered.Ok ? AstPlan.TryCreateAny(lowered.Ast!, bailout2, out _planWhy) : null;   // #1909: conj / re / im too
            if (!lowered.Ok) _planWhy = lowered.Refusal;
            _planFor = sbx; _planBailout2 = bailout2;
        }
        if (_plan == null && _plan2 == null) { DeepRefusal = _planWhy ?? "not a polynomial in z, c, conj, re and im"; return false; }

        ColorMap.MaxIterations = MaxIterations;
        double zl = ZoomLog10;   // #1957: past double, the log zoom
        int prec = BigFloat.PrecisionForZoom(zl);
        var centre = CenterExact is { } e ? e.WithPrecision(prec) : ExactCentre.FromDeep(GetDeepCentre(), prec);
        double scale = (3.5 / Math.Max(Width, Height)) / Zoom;
        // #1957 — the pixel as a FloatExp past the double-δ tier; dz/dc from the engine in the distance
        // unit (2^ue: MandelbrotCalculator's #1783, 0 below ~1e27), turned back into complex-plane units
        // for the buffers and the map, as before.
        double pxLog10 = Math.Log10(3.5 / Math.Max(Width, Height)) - zl;
        FloatExp? pixelFx = pxLog10 < -280 ? FloatExp.FromLog10(pxLog10) : null;
        int ue = MandelbrotCalculator.DistanceUnit(scale, pxLog10).Exp2;
        double normalFloor = ue == 0 ? 1e-12 : Math.ScaleB(1e-12, -ue);
        int maxIt = MaxIterations, width = Width;
        double rot = FractalParameters.UserEquationRotationDegrees * Math.PI / 180.0;
        double cosA = Math.Cos(rot), sinA = Math.Sin(rot);
        bool skipJacobian = FractalParameters.UserEquationSkipJacobian;
        uint? oobColor = ColorMap.OutOfBoundsColor;
        double escapeRadius = Math.Sqrt(bailout2);
        uint inSet = ColorMap.InSetColor;
        if (InteriorAlpha < 255)
        {
            uint a = (inSet >> 24) & 0xFFu;
            uint na = (a * (uint)InteriorAlpha) / 255u;
            inSet = (inSet & 0x00FFFFFFu) | (na << 24);
        }
        byte[]? aaMask = PixelMask is { } pm && pm.Length == width * Height ? pm : null;   // #1382
        double eps = FractalParameters.BlaTolerance > 0 && double.IsFinite(FractalParameters.BlaTolerance)
            ? Math.Min(FractalParameters.BlaTolerance, PerturbationEngine.DefaultBlaEpsilon) : PerturbationEngine.DefaultBlaEpsilon;
        double cx0 = CenterX, cy0 = CenterY;

        PerturbationEngine.PixelSink sink = (idx, iter, zr, zi, dzr, dzi) =>
            {
                if (iter >= maxIt)
                {
                    IterationBuffer[idx] = maxIt;
                    NormalXBuffer[idx] = 0f; NormalYBuffer[idx] = 0f;
                    SmoothBuffer[idx] = 0f; TrapBuffer[idx] = 0f;
                    ColorBuffer[idx] = inSet;
                    return;
                }
                double mag = Math.Sqrt(zr * zr + zi * zi);
                float smooth = (float)(iter + 1.0 - Math.Log2(Math.Max(1e-10, Math.Log2(Math.Max(mag, 1.0 + 1e-10)))));
                float nx = 0f, ny = 0f;
                double dR = skipJacobian ? 0 : dzr, dI = skipJacobian ? 0 : dzi;
                if (!skipJacobian)
                {
                    double u = zr * dzr + zi * dzi, v = -(zr * dzi - zi * dzr);   // Re / −Im (z̄ · dz/dc)
                    double m = Math.Sqrt(u * u + v * v);
                    if (m > normalFloor && double.IsFinite(m)) { nx = (float)(u / m); ny = (float)(v / m); }
                }
                NormalXBuffer[idx] = nx; NormalYBuffer[idx] = ny;
                SmoothBuffer[idx] = smooth;
                IterationBuffer[idx] = iter;
                FinalZrBuffer[idx] = (float)zr; FinalZiBuffer[idx] = (float)zi;
                float fdr = MandelbrotCalculator.MapDz(dR, ue), fdi = MandelbrotCalculator.MapDz(dI, ue);   // #1957: complex-plane units
                FinalDrBuffer[idx] = fdr; FinalDiBuffer[idx] = fdi;
                TrapBuffer[idx] = 0f;
                ColorBuffer[idx] = (uint)ColorMap.Map(smooth, 0f, maxIt, nx, ny, (float)zr, (float)zi, fdr, fdi);
                if (oobColor is uint oob)
                {
                    int x = idx % width, y = idx / width;
                    double dx = (x - width * 0.5) * scale, dy = (y - Height * 0.5) * scale;
                    if (IColorMap.IsOutOfBounds(cx0 + dx * cosA - dy * sinA, cy0 + dx * sinA + dy * cosA, escapeRadius))
                    {
                        ColorBuffer[idx] = oob; IterationBuffer[idx] = maxIt;
                        NormalXBuffer[idx] = 0f; NormalYBuffer[idx] = 0f; SmoothBuffer[idx] = 0f;
                    }
                }
            };
        Func<int, bool>? marked = aaMask != null ? i => aaMask[i] != 0 : null;
        Func<double, double, (double, double)>? warp = rot != 0 ? (dx, dy) => (dx * cosA - dy * sinA, dx * sinA + dy * cosA) : null;
        LastPerturbationStats = _plan2 != null
            ? PerturbationEngine.RenderTwoState(_plan2, centre, scale, width, Height, maxIt, sink, ct, rowMaxIter: null, marked: marked, warp: warp,
                pixelScale: pixelFx, derivExp2: ue, linearWarp: true)
            : PerturbationEngine.Render(_plan!, centre, scale, width, Height, maxIt, sink,
                ct, rowMaxIter: null, marked: marked, useBla: true, blaEpsilon: eps, warp: warp,
                pixelScale: pixelFx, derivExp2: ue, linearWarp: true);
        LastPrecisionLabel = "PT";
        return true;
    }
}
