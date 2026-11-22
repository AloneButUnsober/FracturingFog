// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Calculators/UserEquationCalculator.RealNewton.cs
//
// #1989 — real 2D Newton basins for User Equations (the toolchain gap #1943 recorded). When
// FractalParameters.UserEquationPotential is set, the User Equation stops being a complex
// escape-time map: the source is read as a real scalar potential Φ(x, y), and every pixel starts
// multivariate Newton-Raphson on ∇Φ = 0,
//
//     (x, y) ← (x, y) − H⁻¹ ∇Φ,   H the Hessian of Φ,
//
// with ∇Φ and H taken SYMBOLICALLY from the parsed source (RealExpression.Derivative, the
// equation language's syntax; compiled once per source text, as the #1994 User Potential does).
// A start converges when a step is shorter than 1e-12; its end point is matched to the nearest
// root within 1e-6 and coloured by that root's index through INewtonColorMap, IBilliardColorMap
// or the Okabe-Ito gate colours, exactly as the Three-Body Lagrange view (ThreeBodyCalculator,
// ThreeBodyScene.Newton) colours L1-L5. Zotos (2016) used this multivariate Newton-Raphson map on
// ∇Ω for the restricted three-body problem (Docs/Resources-Bibliography.md, #1943 entry).
//
// Roots: UserEquationNewtonRoots lists them ("x, y; x, y; ..."), so the colours stay put while you
// pan; empty discovers them from a coarse 48 x 27 pre-pass over the frame, clustered within the
// match radius and sorted by x then y (deterministic, but frame-dependent).
//
// The view is the Three-Body one: pixel centres, y up, pitch ViewCamera.PixelPitch; the User
// Equation rotation turns it about the centre. The iteration cap is MaxIterations. Double only.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.Interefaces;
using FracturingFog.Models;

namespace FracturingFog;

public sealed partial class UserEquationCalculator
{
    /// <summary>#1989 — per-pixel root index of the real-Newton mode (−1 = none). Empty otherwise.</summary>
    public sbyte[] RootBuffer { get; private set; } = Array.Empty<sbyte>();

    /// <summary>#1989 — the roots the last real-Newton render coloured by (listed or discovered); null otherwise.</summary>
    public (double X, double Y)[]? LastRealNewtonRoots { get; private set; }

    /// <summary>#1989 — why the last real-Newton render could not run (parse error), else empty.</summary>
    public string RealNewtonError { get; private set; } = string.Empty;

    /// <summary>Pixel-centre plane point, y up — the Three-Body / Math Plane framing — turned by <paramref name="cosA"/>, <paramref name="sinA"/>.</summary>
    public static (double X, double Y) RealNewtonPlaneAt(double px, double py, int width, int height,
                                                         double centerX, double centerY, double zoom, double cosA, double sinA)
    {
        double pitch = FracturingFog.ViewState.ViewCamera.PixelPitch(width, height, zoom);
        double dx = (px - width * 0.5) * pitch, dy = (py - height * 0.5) * pitch;
        double rx = dx * cosA - dy * sinA, ry = dx * sinA + dy * cosA;
        return (centerX + rx, -(centerY + ry));
    }

    private bool TryCalculateRealNewton(CancellationToken ct)
    {
        string? src = FractalParameters.UserEquationPotential?.Trim();
        LastRealNewtonRoots = null;
        if (string.IsNullOrEmpty(src)) { RealNewtonError = string.Empty; return false; }

        int width = Width, height = Height, n = width * height;
        if (RootBuffer.Length != n) RootBuffer = new sbyte[n];
        Array.Clear(NormalXBuffer); Array.Clear(NormalYBuffer); Array.Clear(TrapBuffer);
        Array.Clear(FinalZrBuffer); Array.Clear(FinalZiBuffer); Array.Clear(FinalDrBuffer); Array.Clear(FinalDiBuffer);
        Array.Fill(IterationBuffer, MaxIterations);   // #845 — nothing escaped: HE leaves it alone
        ColorMap.MaxIterations = MaxIterations;
        uint inSet = ColorMap.InSetColor | 0xFF000000u;

        var model = RealNewtonModel.For(src);
        bool rootsOk = RealNewtonModel.TryParseRoots(FractalParameters.UserEquationNewtonRoots, out var roots, out var rErr);
        if (model.Error != null || !rootsOk)
        {
            RealNewtonError = model.Error ?? rErr ?? "roots";
            Array.Fill(ColorBuffer, inSet); Array.Fill(RootBuffer, (sbyte)-1); Array.Clear(SmoothBuffer);
            return true;
        }

        int maxIt = Math.Clamp(MaxIterations, 1, 10000);
        double cx = CenterX, cy = CenterY, zoom = Zoom;
        double rot = FractalParameters.UserEquationRotationDegrees * Math.PI / 180.0;
        double cosA = Math.Cos(rot), sinA = Math.Sin(rot);
        if (roots.Length == 0) roots = DiscoverRoots(model, width, height, cx, cy, zoom, cosA, sinA, maxIt, ct);
        LastRealNewtonRoots = roots;
        RealNewtonError = string.Empty;

        int rootCount = Math.Max(1, roots.Length);
        var billiardMap = ColorMap as IBilliardColorMap;
        var newtonMap = ColorMap as INewtonColorMap;
        var mask = PixelMask is { } m && m.Length >= n ? m : null;
        const int shadeCap = ThreeBodyCalculator.ShadeCap;

        Parallel.For(0, height, new ParallelOptions { CancellationToken = ct }, y =>
        {
            if (ct.IsCancellationRequested) return;
            var v = new double[2];
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                int idx = row + x;
                if (mask != null && mask[idx] == 0) continue;
                var (px, py) = RealNewtonPlaneAt(x + 0.5, y + 0.5, width, height, cx, cy, zoom, cosA, sinA);
                var (ex, ey, k, ok) = model.Solve(px, py, maxIt, v);
                int gate = ok ? RealNewtonModel.Match(ex, ey, roots) : -1;
                RootBuffer[idx] = (sbyte)gate;
                SmoothBuffer[idx] = k;
                double t = Math.Clamp((double)k / maxIt, 0.0, 1.0);
                uint col;
                if (newtonMap != null)
                    col = gate < 0 ? inSet : unchecked((uint)newtonMap.MapNewton(gate, rootCount, k, maxIt, px, py)) | 0xFF000000u;
                else if (billiardMap != null)
                    col = unchecked((uint)billiardMap.MapBilliard(gate, rootCount, (int)Math.Round(t * shadeCap), shadeCap, (float)t)) | 0xFF000000u;
                else if (gate < 0)
                    col = inSet;
                else
                    col = unchecked((uint)BilliardColorHelper.Shade(BilliardColorHelper.GateColor(gate), (float)(1.0 - 0.75 * t))) | 0xFF000000u;
                ColorBuffer[idx] = col;
            }
        });
        return true;
    }

    /// <summary>Roots found from a 48 x 27 grid of starts over the frame: converged end points
    /// clustered within the match radius, sorted by x then y.</summary>
    internal static (double X, double Y)[] DiscoverRoots(RealNewtonModel model, int width, int height,
                                                         double cx, double cy, double zoom, double cosA, double sinA,
                                                         int maxIt, CancellationToken ct)
    {
        const int gw = 48, gh = 27;
        var found = new List<(double X, double Y)>();
        var v = new double[2];
        for (int j = 0; j < gh && !ct.IsCancellationRequested; j++)
            for (int i = 0; i < gw; i++)
            {
                double sx = (i + 0.5) * width / gw, sy = (j + 0.5) * height / gh;
                var (px, py) = RealNewtonPlaneAt(sx, sy, width, height, cx, cy, zoom, cosA, sinA);
                var (ex, ey, _, ok) = model.Solve(px, py, maxIt, v);
                if (!ok || RealNewtonModel.Match(ex, ey, found.ToArray()) >= 0) continue;
                found.Add((ex, ey));
                if (found.Count >= RealNewtonModel.MaxRoots) goto done;
            }
        done:
        // Keys on a match-radius grid, so a rounding-level difference in x never reorders roots
        // that share an abscissa (L1, L4, L5 at x = 0 for equal masses).
        static double Key(double u) => Math.Round(u / RealNewtonModel.MatchRadius);
        found.Sort((a, b) => Key(a.X) != Key(b.X) ? Key(a.X).CompareTo(Key(b.X)) : a.Y.CompareTo(b.Y));
        return found.ToArray();
    }
}
