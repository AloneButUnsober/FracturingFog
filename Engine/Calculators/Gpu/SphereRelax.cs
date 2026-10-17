// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// SphereRelax.cs
//
// #1335 — over-relaxed sphere tracing (after Keinert et al. 2014, "Enhanced
// Sphere Tracing"), shared by the DE raymarchers' primary march, CPU and ILGPU,
// so the two stay in lockstep. A plain march steps by the distance estimate r;
// this one steps by ω·r. While consecutive unbounding spheres overlap
// (|r_new| + r_prev ≥ the step just taken), the segment between them is free of
// surface, so the longer step was safe. When they stop overlapping the step may
// have jumped a surface: the march backs up to where the plain step would have
// landed and evaluates the DE there; no hit is tested at the overshot sample.
//
// Adaptive ω. The paper marches plainly for the rest of the ray after the first
// failure. With FF's DEs (close to true distances) the very first step from the
// camera usually overshoots, so that rule gained nothing (96–108% of the plain
// march's DE evaluations). Here a failure halves ω − 1 and each safe step grows ω
// back towards the requested value: 80–93% of the plain evaluations at ω 1.3–1.6.
//
// ω = 1 (FractalParameters.MarchRelaxation's default) is exactly the plain march:
// Step returns 1·r = r bit for bit and BackOff never fires, so every frame is
// byte-identical to the renderer before #1335.
//
// The loop shape, for a march along unit direction d from p with distance t:
//
//     var relax = new SphereRelax(omega);
//     for (step = 0; step < maxSteps; step++)
//     {
//         r = DE(p);
//         double back = relax.BackOff(r);
//         if (back > 0) { p -= d·back; t -= back; continue; }   // overshot: no hit test
//         if (r < eps) → hit
//         s = relax.Step(r); p += d·s; t += s;
//     }
//
// Blittable with no managed references or statics, so ILGPU kernels use it as is.

using System;

namespace FracturingFog.Calculators.Gpu;

/// <summary>#1335 — the over-relaxation state of one sphere-traced ray. See the
/// file header.</summary>
public struct SphereRelax
{
    /// <summary>The largest ω accepted (1.2–1.6 is the useful range; near 2 the
    /// march backs off on most steps).</summary>
    public const double MaxOmega = 2.0;

    private readonly double _target;   // the requested ω
    private double _omega;             // the current ω (adapts between 1 and _target)
    private double _prevR;             // |r| at the sample the last step left from
    private double _step;              // the last step taken
    private bool _backedOff;           // the last sample was an overshoot

    /// <summary>A ray marched with relaxation <paramref name="omega"/>, clamped to
    /// [1, <see cref="MaxOmega"/>] (NaN → 1).</summary>
    public SphereRelax(double omega)
    {
        _target = Clamp(omega);
        _omega = _target;
        _prevR = 0.0;
        _step = 0.0;
        _backedOff = false;
    }

    /// <summary>The ω <paramref name="omega"/> clamps to, as the constructor does
    /// (for hosts that pass it on to a GPU kernel).</summary>
    public static double Clamp(double omega) => omega > 1.0 ? (omega < MaxOmega ? omega : MaxOmega) : 1.0;

    /// <summary>Call first with the distance estimate <paramref name="r"/> at the
    /// new sample. Returns 0 normally. When the step that reached this sample
    /// overshot (its sphere and the previous one do not overlap), returns how far
    /// to move back along the ray — to where the plain step would have landed —
    /// and halves ω − 1; the caller moves back and evaluates the DE there before
    /// any hit test.</summary>
    public double BackOff(double r)
    {
        if (_omega > 1.0 && _step > 0.0 && Math.Abs(r) + _prevR < _step)
        {
            double back = _step - _prevR;
            _step = _prevR;
            _omega = 1.0 + (_omega - 1.0) * 0.5;
            _backedOff = true;
            return back;
        }
        return 0.0;
    }

    /// <summary>The step to take from a sample with distance estimate
    /// <paramref name="r"/>: ω·r (exactly r at ω = 1). A safe step (not straight
    /// after a back-off) first grows ω back towards the requested value.</summary>
    public double Step(double r)
    {
        _prevR = Math.Abs(r);
        if (!_backedOff && _omega < _target)
            _omega = Math.Min(_target, _omega + 0.1 * (_target - 1.0) + 0.02);
        _backedOff = false;
        _step = _omega * r;
        return _step;
    }
}
