// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// DualOrbitEscapeCalculator.Frame.cs (#1119, epic #1114 S5)
//
// Co-moving orbit trap (Docs/Technical/DualOrbit-Coloring-RnD.md §3.A): the trap
// rides the OTHER orbit. Each step the measured orbit's point m_k is expressed in
// a frame centred on the frame orbit's point f_k,
//
//     w_k = (m_k − f_k) · e^{−i·arg f_k} [rotate] / |f_k| [scale] · e^{−iα}
//
// and fed to the unchanged trap sampler — all 19 built-in shapes reuse as-is.
//   TrapC = the c-orbit measured in the z-orbit's frame,
//   TrapZ = the z-orbit measured in the c-orbit's frame,
//   TrapDelta = their difference.
// α (DualOrbitTrapAngle) turns the shape in any frame, Fixed included (Fixed:
// f ≡ 0, so only α applies).
//
// Sampling convention (as S3): after the escape test, while the measured orbit
// is bounded — and, in the co-moving frame, while the frame orbit is too (past
// its escape it has no frame). Fixed frame: k ≥ 1 as S3. Co-moving: k ≥ 2,
// because c_1 − z_1 = c_0² for EVERY s (D_1 = D_0·σ_0 = c_0·c_0): the first
// relative point carries no dynamics, and sampling it stamps a constant trap
// distance (unrotated) or a pure function of arg s (rotated) over the image. Where f_k = 0 the rotation / scale is the
// identity. Fixed frame with α = 0 never reaches this path (byte-identical S3).

using System;

using FracturingFog.Interefaces;
using FracturingFog.Models;

namespace FracturingFog;

public sealed partial class DualOrbitEscapeCalculator
{
    private readonly record struct TrapFrame(bool CoMoving, bool Rotate, bool Scale, double CosA, double SinA);

    private static TrapFrame FrameFor(DualOrbitTrapFrame frame, bool rotate, bool scale, double angleDeg)
    {
        double a = angleDeg * (Math.PI / 180.0);
        return new TrapFrame(frame == DualOrbitTrapFrame.CoMoving, rotate, scale, Math.Cos(a), Math.Sin(a));
    }

    // True when the trap fields need the framed sampler instead of RunSampled.
    private static bool NeedsFrame(DualOrbitTrapFrame frame, double angleDeg)
        => frame == DualOrbitTrapFrame.CoMoving || angleDeg != 0.0;

    // Both orbits in lockstep (identical arithmetic to Run), sampling the trap:
    // accZ ← the z-orbit in its frame, accC ← the c-orbit in its frame.
    private static void RunFramed(double cx0, double cy0, double sx, double sy, int maxIter, in Bailout b,
        IOrbitAwareColorMap sampler, in TrapFrame fr, bool wantZ, bool wantC,
        ref OrbitAccumulator accZ, ref OrbitAccumulator accC)
    {
        double zx = 0.0, zy = 0.0, cx = cx0, cy = cy0;
        bool zLive = true, cLive = true;
        int first = fr.CoMoving ? 2 : 1;
        for (int n = 0; n < maxIter && (zLive || cLive); n++)
        {
            if (zLive && zx * zx + zy * zy > b.R2) zLive = false;
            if (cLive && cx * cx + cy * cy > b.R2) cLive = false;
            if (n >= first)
            {
                // Co-moving: measured and frame orbit both bounded. Fixed: the
                // measured orbit bounded, frame = origin.
                if (wantZ && zLive && (!fr.CoMoving || cLive))
                {
                    Transform(zx, zy, fr.CoMoving ? cx : 0.0, fr.CoMoving ? cy : 0.0, fr, out double wx, out double wy);
                    sampler.Sample(ref accZ, wx, wy, sx, sy, n);
                }
                if (wantC && cLive && (!fr.CoMoving || zLive))
                {
                    Transform(cx, cy, fr.CoMoving ? zx : 0.0, fr.CoMoving ? zy : 0.0, fr, out double wx, out double wy);
                    sampler.Sample(ref accC, wx, wy, sx, sy, n);
                }
            }
            if (zLive)
            {
                double nzx = zx * zx - zy * zy + sx;
                zy = 2.0 * zx * zy + sy;
                zx = nzx;
            }
            if (cLive)
            {
                double ncx = cx * cx - cy * cy + sx;
                cy = 2.0 * cx * cy + sy;
                cx = ncx;
            }
        }
    }

    // w = (m − f)·conj(f/|f|) [rotate] / |f| [scale], then turned by −α.
    internal static void FrameTransform(double mx, double my, double fx, double fy,
        bool coMoving, bool rotate, bool scale, double angleDeg, out double wx, out double wy)
        => Transform(mx, my, coMoving ? fx : 0.0, coMoving ? fy : 0.0,
            FrameFor(coMoving ? DualOrbitTrapFrame.CoMoving : DualOrbitTrapFrame.Fixed, rotate, scale, angleDeg),
            out wx, out wy);

    private static void Transform(double mx, double my, double fx, double fy, in TrapFrame fr,
        out double wx, out double wy)
    {
        double dx = mx - fx, dy = my - fy;
        if (fr.CoMoving && (fr.Rotate || fr.Scale))
        {
            double r = Math.Sqrt(fx * fx + fy * fy);
            if (r > 0.0)
            {
                if (fr.Rotate)
                {
                    double ux = fx / r, uy = fy / r;
                    double rx = dx * ux + dy * uy;
                    dy = dy * ux - dx * uy;
                    dx = rx;
                }
                if (fr.Scale) { dx /= r; dy /= r; }
            }
        }
        wx = dx * fr.CosA + dy * fr.SinA;
        wy = dy * fr.CosA - dx * fr.SinA;
    }
}
