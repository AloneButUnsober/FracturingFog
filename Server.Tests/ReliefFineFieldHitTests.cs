// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1027 — on a fine height field (a maximised window's 1920×1080 field) the relief
// raymarch compared the Lipschitz-scaled distance with the hit tolerance. The field's
// steepest cliff makes the Lipschitz factor tiny, so every ray "hit" on its first
// sample — the slab-top entry point — and the relief rendered as a flat plate with
// the fractal painted on. Independent check: recompute each pixel's ray and its
// entry into the terrain box from the camera alone, and require that most terrain
// hits land well below that entry point.
// A field finer than the output is now downsampled to it first (see
// FieldDownsample_* below), which also keeps a small window's look in line with a
// large one's.

using System;
using FracturingFog;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class ReliefFineFieldHitTests
{
    // Output close to the field size (under the 1.25× downsample threshold) so the
    // trace runs on the full fine field, as a maximised window does.
    private const int W = 1320, H = 825, FW = 1600, FH = 1000;

    private static FractalParameters P(bool skip = true) => new()
    {
        Relief2DEnabled = true,
        Relief2DRaymarch = true,
        Relief2DGpuRaymarch = false,
        Relief2DHiResField = false,
        Relief2DSupersample = 1,
        Relief2DEmptySkip = skip,
    };

    // Fraction of terrain hits whose depth is within a hair of the ray's entry
    // into the terrain box (the flat-plate signature).
    private static double EntryHitFraction(FractalParameters p)
    {
        var calc = new MandelbrotCalculator(FW, FH)
        {
            CenterX = -0.75, CenterY = 0.1, Zoom = 1.6, MaxIterations = 300, ColorMap = new MonoBandMap(),
        };
        calc.Calculate(default);
        var alb = new uint[W * H];
        Array.Fill(alb, 0xFFC0C0C0u);
        var aov = new HeightfieldRaymarch2D.ReliefAovBuffers(W, H);
        HeightfieldRaymarch2D.Render(alb, calc.SmoothBuffer, W, H, FW, FH, p, new uint[W * H], out _, null, aov);

        // Peak mode, full-size field: sy·maxH = 0.35·HeightScale whatever the content.
        double aspect = (double)W / H;
        var cam = HeightfieldRaymarch2D.BuildObliqueCamera(W, H, aspect, 0.35 * p.Relief2DHeightScale, 1.0, p);
        int hits = 0, atEntry = 0;
        for (int py = 0; py < H; py++)
            for (int px = 0; px < W; px++)
            {
                float d = aov.Depth[py * W + px];
                if (!(d > 0f && d < 9.9e5f)) continue;
                double ndcx = 2.0 * (px + 0.5) / W - 1.0, ndcy = 1.0 - 2.0 * (py + 0.5) / H;
                double a = ndcx * aspect * cam.TanHalf, b = ndcy * cam.TanHalf;
                double rx = cam.FX + cam.RX * a + cam.UX * b;
                double ry = cam.FY + cam.UY * b;
                double rz = cam.FZ + cam.RZ * a + cam.UZ * b;
                double il = 1.0 / Math.Sqrt(rx * rx + ry * ry + rz * rz);
                rx *= il; ry *= il; rz *= il;
                double t0 = 0, t1 = double.MaxValue;
                if (!Slab(cam.CamX, rx, -cam.Bx, cam.Bx, ref t0, ref t1)
                    || !Slab(cam.CamY, ry, 0.0, cam.By, ref t0, ref t1)
                    || !Slab(cam.CamZ, rz, -cam.Bz, cam.Bz, ref t0, ref t1)) continue;   // floor hit
                hits++;
                if (Math.Abs(d - (Math.Max(t0, 0) + cam.Eps0)) < 1e-4) atEntry++;
            }
        Assert.True(hits > W * H / 10, $"too few terrain hits ({hits})");
        return atEntry / (double)hits;
    }

    private static bool Slab(double o, double r, double lo, double hi, ref double t0, ref double t1)
    {
        if (Math.Abs(r) < 1e-12) return o >= lo && o <= hi;
        double a = (lo - o) / r, b = (hi - o) / r;
        if (a > b) (a, b) = (b, a);
        t0 = Math.Max(t0, a); t1 = Math.Min(t1, b);
        return t0 <= t1;
    }

    [Fact]
    public void FineField_RaysReachTheTerrain_NotTheBoxTop()
    {
        double atEntry = EntryHitFraction(P());
        Assert.True(atEntry < 0.05, $"{atEntry:P1} of terrain hits stopped at the box-top entry (flat plate)");
    }

    // A field more than 1.25× finer than the output is traced on an output-sized
    // grid (area-downsampled); coarser or equal fields are traced as they are.
    [Fact]
    public void FieldDownsample_TargetsTheOutputGrid()
    {
        Assert.Equal((1800, 1080), HeightfieldRaymarch2D.FieldTargetDims(1800, 1080, 1800, 1080));
        Assert.Equal((1800, 1080), HeightfieldRaymarch2D.FieldTargetDims(1800, 1080, 1600, 960));   // 1.125× — kept
        Assert.Equal((640, 384), HeightfieldRaymarch2D.FieldTargetDims(1800, 1080, 640, 384));
        Assert.Equal((320, 240), HeightfieldRaymarch2D.FieldTargetDims(320, 240, 1920, 1080));     // coarser — kept
    }
}
