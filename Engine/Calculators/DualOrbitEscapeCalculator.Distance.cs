// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// DualOrbitEscapeCalculator.Distance.cs (#1118, epic #1114 S4)
//
// Per-orbit distance estimates and final-z decomposition
// (Docs/Technical/DualOrbit-Coloring-RnD.md §3.A).
//
// Distance estimate in the IMAGE plane, for any slice. With the escape-time
// potential G = ln|u_N| / 2^N, the distance to the orbit's boundary is
//
//     DE = ½ · G / |∇G| = ½ · ln|u_N| / |∇ ln|u_N||,
//
// the gradient taken in image coordinates. Each image axis is the real or
// imaginary part of one complex parameter p ∈ {s, c₀}; with u_p = ∂u_N/∂p,
// ∂ln|u|/∂(Re p) = Re(u_p / u) and ∂ln|u|/∂(Im p) = −Im(u_p / u). On the
// holomorphic slices (SxSy: p = s for both axes; CxCy: p = c₀) this is exactly
// the classic ½·|u|·ln|u| / |u_p| — a Koebe ¼ lower bound on the true distance to
// M (z-orbit, SxSy), M_c (c-orbit, SxSy) or the filled Julia set (c-orbit,
// CxCy). On the mixed slices (CxSx …) the map is not holomorphic in the image,
// so the same formula is an estimate, not a bound.
//
// Derivatives: z' = 2·z·z' + 1 (wrt s, z₀' = 0); c-orbit c' = 2·c·c' + 1 (wrt s,
// c₀' = 0, or 1 in the c = s control) and g = 2·c·g (wrt c₀, g₀ = 1). The
// z-orbit does not depend on c₀, so DistanceZ has no value on the c-plane slice.
//
// Fields: DistanceZ / DistanceC (DE in pixels, d/(d + DualOrbitDEScale)),
// DualOutline (ink lines on the M and M_c boundaries over the z escape time),
// BinaryXor (binary decomposition bit of each escape point, XOR-ed) and
// FinalAngleDelta (arg E_c − arg E_z, raw and bailout-dependent — the intrinsic
// version is ExternalAngleDelta). ComplexPlane map only.

using System;

using FracturingFog.Interefaces;

namespace FracturingFog;

public sealed partial class DualOrbitEscapeCalculator : IDistanceFieldSource
{
    /// <summary>#1118 — exterior distance estimate (image-plane units, 0 where none)
    /// of the orbit a distance field reads (DistanceZ / DistanceC; the z-orbit for
    /// DualOutline). Empty for other fields.</summary>
    public float[] DistanceBuffer { get; private set; } = Array.Empty<float>();

    /// <summary>Image-plane width of one pixel for the last calculation.</summary>
    public double DistancePixelScale { get; private set; } = 1.0;

    internal static bool IsDistanceField(DualOrbitField f)
        => f is DualOrbitField.DistanceZ or DualOrbitField.DistanceC or DualOrbitField.DualOutline;

    internal static bool IsDecompField(DualOrbitField f)
        => f is DualOrbitField.BinaryXor or DualOrbitField.FinalAngleDelta;

    // Run (identical escape arithmetic) carrying du/ds and du/dc₀.
    private static Orbit RunDeriv(double u0x, double u0y, double sx, double sy, int maxIter, in Bailout b,
        double ds0x, double dc0x, out double dsx, out double dsy, out double dcx, out double dcy)
    {
        double zx = u0x, zy = u0y;
        double ax = ds0x, ay = 0;          // du/ds
        double gx = dc0x, gy = 0;          // du/dc₀
        for (int n = 0; n < maxIter; n++)
        {
            double x2 = zx * zx, y2 = zy * zy;
            double r2 = x2 + y2;
            if (r2 > b.R2)
            {
                double logZn = Math.Log(r2) * 0.5;
                double nu = Math.Log(logZn / b.LogR) / Math.Log(2.0);
                dsx = ax; dsy = ay; dcx = gx; dcy = gy;
                return new Orbit(true, zx, zy, n - nu);
            }
            double nax = 2.0 * (zx * ax - zy * ay) + 1.0;
            ay = 2.0 * (zx * ay + zy * ax);
            ax = nax;
            double ngx = 2.0 * (zx * gx - zy * gy);
            gy = 2.0 * (zx * gy + zy * gx);
            gx = ngx;
            double nzx = x2 - y2 + sx;
            zy = 2.0 * zx * zy + sy;
            zx = nzx;
        }
        dsx = dsy = dcx = dcy = 0;
        return new Orbit(false, zx, zy, maxIter);
    }

    // Which complex parameter each image axis reads, and whether it is the
    // imaginary part: (xIsS, xIm, yIsS, yIm).
    private static (bool xS, bool xIm, bool yS, bool yIm) AxisParams(DualOrbitSliceAxes a) => a switch
    {
        DualOrbitSliceAxes.CxCy => (false, false, false, true),
        DualOrbitSliceAxes.CxSx => (false, false, true, false),
        DualOrbitSliceAxes.CxSy => (false, false, true, true),
        DualOrbitSliceAxes.CySx => (false, true, true, false),
        DualOrbitSliceAxes.CySy => (false, true, true, true),
        _ => (true, false, true, true),   // SxSy
    };

    /// <summary>Image-plane DE = ½·ln|u| / |∇ln|u||, or NaN where undefined
    /// (bounded orbit, no dependence on the image axes, non-finite).</summary>
    internal static double ImageDE(double ux, double uy, double dsx, double dsy, double dcx, double dcy,
        DualOrbitSliceAxes axes)
    {
        double r2 = ux * ux + uy * uy;
        if (!(r2 > 1.0)) return double.NaN;
        var (xS, xIm, yS, yIm) = AxisParams(axes);
        double gx = Partial(xS ? dsx : dcx, xS ? dsy : dcy, xIm);
        double gy = Partial(yS ? dsx : dcx, yS ? dsy : dcy, yIm);
        double g = Math.Sqrt(gx * gx + gy * gy);
        if (!(g > 0) || !double.IsFinite(g)) return double.NaN;
        return 0.25 * Math.Log(r2) / g;     // ½ · ln|u| / |∇ln|u||

        // ∂ln|u| along Re p = Re(u_p/u); along Im p = −Im(u_p/u).
        double Partial(double px, double py, bool im)
        {
            double re = (px * ux + py * uy) / r2, imq = (py * ux - px * uy) / r2;
            return im ? -imq : re;
        }
    }

    // Outline defaults: colour-blind-safe blue (M) / amber (M_c).
    internal const uint DefaultOutlineZ = 0xFF0072B2u, DefaultOutlineC = 0xFFE69F00u;

    // DualOutline colour: the base colour with each boundary's ink line laid on
    // (alpha 1 − DE_px / width, c line under the z line).
    internal static uint OutlineColor(uint baseColor, float deZpx, float deCpx, double width, uint lineZ, uint lineC)
    {
        uint col = baseColor;
        if (float.IsFinite(deCpx)) col = Lerp(col, lineC, 1.0 - deCpx / width);
        if (float.IsFinite(deZpx)) col = Lerp(col, lineZ, 1.0 - deZpx / width);
        return col;

        static uint Lerp(uint a, uint b, double t)
        {
            if (t <= 0) return a;
            if (t > 1) t = 1;
            uint Ch(int sh) => (uint)Math.Round(((a >> sh) & 0xFF) * (1 - t) + ((b >> sh) & 0xFF) * t);
            return (Math.Max(a >> 24, b >> 24) << 24) | (Ch(16) << 16) | (Ch(8) << 8) | Ch(0);
        }
    }

    // BinaryXor / FinalAngleDelta from the escape points.
    private static double DecompScalar(DualOrbitField f, in Orbit oz, in Orbit oc, int maxIter)
    {
        if (!oz.Escaped || !oc.Escaped) return 0.0;
        double v;
        if (f == DualOrbitField.BinaryXor)
        {
            // Class index k = XOR bit (0 / 1), coloured categorically by default:
            // evenly spaced palette values alias under cycling themes.
            bool bz = oz.Ey >= 0.0, bc = oc.Ey >= 0.0;
            v = bz ^ bc ? 1.0 : 0.0;
        }
        else
        {
            double d = (Math.Atan2(oc.Ey, oc.Ex) - Math.Atan2(oz.Ey, oz.Ex)) / (2.0 * Math.PI);
            v = Frac(d) * maxIter;
        }
        return Math.Max(LiveFloor, v);
    }
}
