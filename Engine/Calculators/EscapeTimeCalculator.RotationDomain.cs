// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Calculators/EscapeTimeCalculator.RotationDomain.cs
//
// #1981 — rotation-domain colouring (RotationDomainColoring.cs; idea: pauldelbrot,
// fractalforums.org topic 5693) for this calculator's Julia, Phoenix and Magnet 1 / 2. A post-pass
// after the compute path, before the out-of-bounds surround and interior alpha, over the in-set
// pixels (iteration count at the cap). Each pixel is re-iterated from its own coordinate (warped
// as the cores warp it). Histogram equalization repaints in-set pixels, so it re-stamps the pass.

using System;
using System.Numerics;
using System.Threading;

using FracturingFog.Models;

namespace FracturingFog;

public sealed partial class EscapeTimeCalculator
{
    private uint[]? _rotationStamp;
    private float[]? _rotationDistPx;   // #1981 part 2 — for the adaptive AA mask
    private bool _rotationStamped;

    /// <summary>#1981 — per-class pixel counts of the last frame's pass (all 0 when off).</summary>
    public RotationDomainColoring.Stats LastRotationDomainStats { get; private set; }

    /// <summary>#1981 — the families the option covers, in the double tier.</summary>
    public static bool RotationDomainFamily(FractalType t)
        => t is FractalType.Julia or FractalType.Phoenix or FractalType.Magnet1 or FractalType.Magnet2;

    private void ApplyRotationDomain(CancellationToken ct)
    {
        _rotationStamped = false;
        LastRotationDomainStats = default;
        if (!FractalParameters.RotationDomainColoring || !RotationDomainFamily(FractalType)
            || Zoom >= DeepZoomThreshold) return;
        int w = Width, h = Height, n = w * h;
        if (n <= 0 || ColorBuffer.Length < n) return;
        if (_rotationStamp?.Length != n) _rotationStamp = new uint[n];
        if (_rotationDistPx?.Length != n) _rotationDistPx = new float[n];

        var s = RotationDomainColoring.Settings.From(FractalParameters);
        int maxIt = MaxIterations;
        double scale = (3.5 / Math.Max(w, h)) / Zoom;
        double cx0 = CenterX, cy0 = CenterY;
        var warp = MakeWarp(scale, w, h);
        double bail2 = CurrentEscapeRadius2();
        int[] iters = IterationBuffer;
        var type = FractalType;
        Complex juliaC = FractalParameters.EffectiveJuliaC;
        Complex phoenixP = FractalParameters.PhoenixP;
        double magEps = Math.Max(1e-12, FractalParameters.MagnetConvergenceEpsilon);

        Complex Coord(int x, int y)
        {
            double ox = (x - w * 0.5) * scale, oy = (y - h * 0.5) * scale;
            if (warp.Active) FractalDomainWarp.Apply(ref ox, ref oy, warp.HalfSpan, warp.Strength, warp.Frequency);
            return new Complex(cx0 + ox, cy0 + oy);
        }

        RotationDomainColoring.Result Classify(int x, int y)
        {
            Complex p = Coord(x, y);
            return type switch
            {
                FractalType.Julia => RotationDomainColoring.Classify(
                    new RotationDomainColoring.JuliaStep(juliaC, bail2), p, maxIt, false, s),
                FractalType.Phoenix => RotationDomainColoring.ClassifyPhoenix(p, phoenixP, bail2, maxIt, s),
                // Magnet's parameter plane iterates from z₀ = 0 (as MagnetOne/TwoKernel.InitState).
                FractalType.Magnet1 => RotationDomainColoring.Classify(
                    new RotationDomainColoring.Magnet1Step(p, bail2, magEps), Complex.Zero, maxIt, false, s),
                _ => RotationDomainColoring.Classify(
                    new RotationDomainColoring.Magnet2Step(p, bail2, magEps), Complex.Zero, maxIt, false, s),
            };
        }

        byte[]? mask = AaMask;
        LastRotationDomainStats = RotationDomainColoring.Fill(_rotationStamp, w, h, mask,
            idx => iters[idx] >= maxIt, Classify, scale, s, ct, _rotationDistPx);
        RotationDomainColoring.Stamp(_rotationStamp, ColorBuffer, mask, 255);   // interior alpha follows in Calculate
        _rotationStamped = true;
    }

    /// <summary>#1981 part 2 — lower bounds on the boundary distance (pixels) for the adaptive AA
    /// mask: the exterior distance estimate for Julia (d = 2) and Multibrot (d), and the
    /// rotation-domain distance when that colouring ran. Null for the other families.</summary>
    public float[]? AdaptiveAaBoundaryDistancePx()
    {
        int n = Width * Height;
        double deg = FractalType switch
        {
            FractalType.Julia => 2,
            FractalType.Multibrot => new FracturingFog.Models.FractalKernels.MultibrotKernel(FractalParameters.MultibrotExponent).Exponent,
            _ => 0,
        };
        float[]? b = deg >= 2
            ? FracturingFog.Rendering.AdaptiveAaDistance.Exterior(IterationBuffer, MaxIterations, DistanceBuffer,
                DistancePixelScale, FinalZrBuffer, FinalZiBuffer, deg)
            : null;
        if (_rotationStamped && _rotationDistPx is { } r && r.Length == n)
        {
            b ??= new float[n];
            FracturingFog.Rendering.AdaptiveAaDistance.MergeRotation(b, r);
        }
        return b;
    }

    // #1981 — after a histogram-equalization repaint of the in-set pixels.
    private void RestampRotationDomain()
    {
        if (_rotationStamped && _rotationStamp is { } st && st.Length == Width * Height)
            RotationDomainColoring.Stamp(st, ColorBuffer, null, InteriorAlpha);
    }
}
