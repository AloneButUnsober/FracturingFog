// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1255 — the default (plate) relief trace on a display-res field. A family with no
// hi-res field twin traces its field at the output size; the plate hit test compares
// the Lipschitz-scaled DE, so at 1080p every ray stopped at the box entry and an 8-hour
// scene export rendered a solid block where the small live window showed the relief.
// The trace grid is now capped (TraceGrid displayResField). Checked from terrain hit
// heights reconstructed from the depth AOV and the camera alone, against the same view
// rendered at the cap (where the trace is known good: the small-window look).

using System;
using FracturingFog;
using FracturingFog.Models;
using FracturingFog.Rendering;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class ReliefLegacyTraceCapTests
{
    private readonly ITestOutputHelper _out;
    public ReliefLegacyTraceCapTests(ITestOutputHelper output) => _out = output;

    // Seahorse valley: dense filaments, steep boundary cliffs.
    private const double Cx = -0.7453, Cy = 0.1127, Zoom = 60;

    private static MandelbrotCalculator Calc(int w, int h)
    {
        var c = new MandelbrotCalculator(w, h)
        {
            CenterX = Cx, CenterY = Cy, Zoom = Zoom, MaxIterations = 500, ColorMap = new MonoBandMap(),
        };
        c.Calculate(default);
        return c;
    }

    // The default plate look (Real height off), CPU trace.
    private static FractalParameters P() => new()
    {
        Relief2DEnabled = true,
        Relief2DRaymarch = true,
        Relief2DGpuRaymarch = false,
        Relief2DSupersample = 1,
        Relief2DTrueHeight = false,
    };

    private const int GW = 160, GH = 90;

    // World height of the terrain hit under each cell of a GW×GH grid of normalised
    // screen positions (NaN = sky), from the depth AOV and the camera alone. The
    // field is display-res (field dims = output dims), as for a family without a twin.
    private static float[] HitHeights(int w, int h, FractalParameters p, bool displayResField)
    {
        var c = Calc(w, h);
        var aov = new HeightfieldRaymarch2D.ReliefAovBuffers(w, h);
        var dst = new uint[w * h];
        HeightfieldRaymarch2D.Render(c.ColorBuffer, (float[])c.SmoothBuffer.Clone(), w, h, w, h, p, dst, out _,
            null, aov, displayResField: displayResField);
        double aspect = (double)w / h;
        var cam = HeightfieldRaymarch2D.BuildObliqueCamera(w, h, aspect, 0.35 * p.Relief2DHeightScale, 1.0, p);
        var g = new float[GW * GH];
        for (int gy = 0; gy < GH; gy++)
            for (int gx = 0; gx < GW; gx++)
            {
                int px = (int)((gx + 0.5) / GW * w), py = (int)((gy + 0.5) / GH * h);
                float d = aov.Depth[py * w + px];
                if (!(d > 0f && d < 9.9e5f)) { g[gy * GW + gx] = float.NaN; continue; }
                double b = (1.0 - 2.0 * (py + 0.5) / h) * cam.TanHalf;
                double a = (2.0 * (px + 0.5) / w - 1.0) * aspect * cam.TanHalf;
                double rx = cam.FX + cam.RX * a + cam.UX * b, ry = cam.FY + cam.UY * b, rz = cam.FZ + cam.RZ * a + cam.UZ * b;
                double il = 1.0 / Math.Sqrt(rx * rx + ry * ry + rz * rz);
                g[gy * GW + gx] = (float)(cam.CamY + ry * il * d);
            }
        return g;
    }

    // Mean |height difference| over cells both renders hit, as a fraction of full height.
    private static double MeanHeightGap(float[] a, float[] b, FractalParameters p)
    {
        double sum = 0; int n = 0;
        for (int i = 0; i < a.Length; i++)
            if (float.IsFinite(a[i]) && float.IsFinite(b[i])) { sum += Math.Abs(a[i] - b[i]); n++; }
        Assert.True(n > a.Length / 4, $"too few terrain hits ({n})");
        return sum / n / (0.35 * p.Relief2DHeightScale);
    }

    [Fact]
    public void TraceGrid_CapsOnlyTheDisplayResPlateField()
    {
        int cap = HeightfieldRaymarch2D.LegacyTraceShortAxisCap;
        var plate = P();
        // Display-res field above the cap → short axis brought down to the cap.
        Assert.Equal((960, cap, 0), HeightfieldRaymarch2D.TraceGrid(1920, 1080, 1920, 1080, plate, displayResField: true));
        Assert.Equal((960, cap, 0), HeightfieldRaymarch2D.TraceGrid(3840, 2160, 3840, 2160, plate, displayResField: true));
        // At / below the cap → the field as it is (byte-identical small windows).
        Assert.Equal((960, 540, 0), HeightfieldRaymarch2D.TraceGrid(960, 540, 960, 540, plate, displayResField: true));
        Assert.Equal((480, 270, 0), HeightfieldRaymarch2D.TraceGrid(480, 270, 480, 270, plate, displayResField: true));
        // A family with a twin (floor field) keeps its established plate look.
        Assert.Equal((1920, 1080, 0), HeightfieldRaymarch2D.TraceGrid(1920, 1080, 480, 270, plate));
        // Real height ignores the flag (it has its own canonical grid).
        var th = P(); th.Relief2DTrueHeight = true;
        Assert.Equal(HeightfieldRaymarch2D.TraceGrid(1920, 1080, 1920, 1080, th),
                     HeightfieldRaymarch2D.TraceGrid(1920, 1080, 1920, 1080, th, displayResField: true));
    }

    [Fact]
    public void CappedRender_AtOrBelowTheCap_IsByteIdentical()
    {
        const int w = 960, h = 540;
        var c = Calc(w, h);
        var a = new uint[w * h]; var b = new uint[w * h];
        HeightfieldRaymarch2D.Render(c.ColorBuffer, c.SmoothBuffer, w, h, w, h, P(), a, out _);
        HeightfieldRaymarch2D.Render(c.ColorBuffer, c.SmoothBuffer, w, h, w, h, P(), b, out _, displayResField: true);
        Assert.Equal(a, b);
    }

    // The bug: at 1080p the display-res plate trace stops on the box (the solid block),
    // so its terrain heights are far from the known-good trace at the cap. Capped, the
    // 1080p heights match the cap-size render.
    [Fact]
    public void HighResExport_MatchesTheSmallWindowTerrain_NotABlock()
    {
        var p = P();
        var reference = HitHeights(960, 540, p, displayResField: true);
        double gapFixed = MeanHeightGap(reference, HitHeights(1920, 1080, p, displayResField: true), p);
        double gapBug = MeanHeightGap(reference, HitHeights(1920, 1080, p, displayResField: false), p);
        _out.WriteLine($"1080p vs 540p height gap: capped {gapFixed:P2}, uncapped {gapBug:P2}");
        // Residual (measured ~7%) = the 1080p field is an area-average of a finer calc,
        // not the 540p calc, and the cone tolerance is per output pixel; the block
        // measured ~43%.
        Assert.True(gapFixed < 0.12, $"capped 1080p heights differ from the 540p trace by {gapFixed:P2} of full height");
        // Sensitivity: without the cap the 1080p trace really does collapse.
        Assert.True(gapBug > gapFixed * 3, $"sensitivity: uncapped {gapBug:P2} vs capped {gapFixed:P2}");
    }
}
