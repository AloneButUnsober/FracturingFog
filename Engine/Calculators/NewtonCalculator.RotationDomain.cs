// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Calculators/NewtonCalculator.RotationDomain.cs
//
// #1981 — rotation-domain colouring (RotationDomainColoring.cs; idea: pauldelbrot,
// fractalforums.org topic 5693) for Newton and Nova in the double loop: a post-pass over the
// pixels that found no root (basin −1). Nova's parameter plane starts at z₀ (1 by default, a
// critical point: N′ = 0), so its product starts at the first iterate. The deep engine (Nova from
// DeepZoomFrom) does not run it. The HE recolour leaves non-converged pixels alone, so the stamp
// needs no re-apply there.

using System;
using System.Numerics;
using System.Threading;

namespace FracturingFog;

public sealed partial class NewtonCalculator
{
    private uint[]? _rotationStamp;

    /// <summary>#1981 — per-class pixel counts of the last frame's pass (all 0 when off).</summary>
    public RotationDomainColoring.Stats LastRotationDomainStats { get; private set; }

    private void ApplyRotationDomain(int d, double R, int maxIter, byte[]? mask, CancellationToken ct)
    {
        LastRotationDomainStats = default;
        if (!FractalParameters.RotationDomainColoring) return;
        int w = Width, h = Height, n = w * h;
        if (n <= 0 || ColorBuffer.Length < n) return;
        if (_rotationStamp?.Length != n) _rotationStamp = new uint[n];

        var s = RotationDomainColoring.Settings.From(FractalParameters);
        double scale = (3.5 / Math.Max(w, h)) / Zoom;
        double cx0 = CenterX, cy0 = CenterY;
        bool nova = FractalType == FractalType.Nova;
        bool novaJulia = nova && FractalParameters.NovaJuliaMode;
        Complex z0 = FractalParameters.NovaZ0, juliaC = FractalParameters.NovaJuliaC;
        int[] basins = BasinBuffer;

        RotationDomainColoring.Result Classify(int x, int y)
        {
            var p = new Complex(cx0 + (x - w * 0.5) * scale, cy0 + (y - h * 0.5) * scale);
            if (nova && !novaJulia)
                return RotationDomainColoring.Classify(new RotationDomainColoring.NewtonStep(d, R, p), z0, maxIter, true, s);
            var add = novaJulia ? juliaC : Complex.Zero;
            return RotationDomainColoring.Classify(new RotationDomainColoring.NewtonStep(d, R, add), p, maxIter, false, s);
        }

        LastRotationDomainStats = RotationDomainColoring.Fill(_rotationStamp, w, h, mask,
            idx => basins[idx] < 0, Classify, scale, s, ct);
        RotationDomainColoring.Stamp(_rotationStamp, ColorBuffer, mask, InteriorAlpha);   // as FinishPixel scales in-set
    }
}
