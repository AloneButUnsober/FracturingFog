// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1028 — canonical field for Real height. The terrain is shaped on one fixed grid
// (short axis = the field floor) whatever the window, so a small and a large window
// show the same heights. Checked from terrain hit heights reconstructed from the
// render's depth AOV and the camera alone, with the field sized the way the host
// sizes it (FractalRenderHost.HiResReliefFieldDims).

using System;
using System.Linq;
using System.Text.Json;
using FracturingFog;
using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.Imaging;
using FracturingFog.Models;
using FracturingFog.Rendering;
using FracturingFog.Rendering.Lighting;
using FracturingFog.UI.Avalonia.ViewModels;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class ReliefCanonicalFieldTests
{
    // Seahorse valley: dense filaments, where the window size moved the height most.
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

    private static FractalParameters P(bool trueHeight = true, bool canonical = true) => new()
    {
        Relief2DEnabled = true,
        Relief2DRaymarch = true,
        Relief2DGpuRaymarch = false,
        Relief2DSupersample = 1,
        Relief2DTrueHeight = trueHeight,
        Relief2DCanonicalField = canonical,
    };

    // The field the host traces for a w×h window.
    private static (float[] F, int W, int H) HostField(int w, int h, FractalParameters p)
    {
        int fw = w, fh = h;
        if (FractalRenderHost.HiResReliefFieldDims(w, h, p, out int a, out int b)) { fw = a; fh = b; }
        return ((float[])Calc(fw, fh).SmoothBuffer.Clone(), fw, fh);
    }

    private const int GW = 160, GH = 90;

    // World height of the terrain hit under each cell of a GW×GH grid of normalised
    // screen positions (NaN = sky), from the depth AOV and the camera alone.
    private static float[] HitHeights(int w, int h, FractalParameters p)
    {
        var alb = Calc(w, h).ColorBuffer;
        var (f, fw, fh) = HostField(w, h, p);
        var aov = new HeightfieldRaymarch2D.ReliefAovBuffers(w, h);
        var dst = new uint[w * h];
        HeightfieldRaymarch2D.Render(alb, f, w, h, fw, fh, p, dst, out _, null, aov);
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
    public void SmallAndLargeWindows_ShowTheSameHeights()
    {
        var on = P();
        double gapOn = MeanHeightGap(HitHeights(480, 270, on), HitHeights(1280, 720, on), on);
        var off = P(canonical: false);
        double gapOff = MeanHeightGap(HitHeights(480, 270, off), HitHeights(1280, 720, off), off);
        Assert.True(gapOn < 0.008, $"canonical: 270p vs 720p heights differ by {gapOn:P2} of full height");
        Assert.True(gapOff > gapOn * 1.5, $"sensitivity: output-size shaping {gapOff:P2} vs canonical {gapOn:P2}");
    }

    // The height reference (what renders at full height) comes from the one grid,
    // so it is the same for every window — below, at and above the floor.
    [Fact]
    public void HeightReference_IsTheSameAtEveryWindowSize()
    {
        var p = P(); p.Relief2DHeightMode = ReliefHeightMode.Robust;
        double? first = null;
        foreach (var (w, h) in new[] { (480, 270), (1280, 720), (1920, 1080), (2560, 1440) })
        {
            var (f, fw, fh) = HostField(w, h, p);
            var m = HeightfieldRaymarch2D.MeasureHeightNormalization(f, fw, fh, p, w, h);
            Assert.NotNull(m);
            first ??= m!.Value.Reference;
            Assert.Equal(first.Value, m!.Value.Reference, 9);
        }
    }

    [Fact]
    public void FieldDims_RaiseSmallWindows_AndCapLargeOnesOnlyForRealHeight()
    {
        Assert.True(FractalRenderHost.HiResReliefFieldDims(960, 540, P(trueHeight: false), out int w, out int h));
        Assert.Equal((1920, 1080), (w, h));
        Assert.False(FractalRenderHost.HiResReliefFieldDims(1920, 1080, P(), out _, out _));
        Assert.False(FractalRenderHost.HiResReliefFieldDims(2560, 1440, P(trueHeight: false), out _, out _));
        Assert.False(FractalRenderHost.HiResReliefFieldDims(2560, 1440, P(canonical: false), out _, out _));
        Assert.True(FractalRenderHost.HiResReliefFieldDims(3840, 2160, P(), out w, out h));
        Assert.Equal((1920, 1080), (w, h));
    }

    [Fact]
    public void TraceGrid_IsTheCanonicalGrid_WithAnAntiAliasBlurForSmallWindows()
    {
        // Plate look: the field as it is.
        Assert.Equal((2560, 1440, 0), HeightfieldRaymarch2D.TraceGrid(2560, 1440, 480, 270, P(trueHeight: false)));
        // Canonical off: the #1027 grid (downsampled to the output).
        var (fw, fh) = HeightfieldRaymarch2D.FieldTargetDims(1920, 1080, 480, 270);
        Assert.Equal((fw, fh, 0), HeightfieldRaymarch2D.TraceGrid(1920, 1080, 480, 270, P(canonical: false)));
        // Canonical: the floor grid; 4 cells per output pixel → a 2-cell blur.
        Assert.Equal((1920, 1080, 2), HeightfieldRaymarch2D.TraceGrid(1920, 1080, 480, 270, P()));
        Assert.Equal((1920, 1080, 0), HeightfieldRaymarch2D.TraceGrid(2560, 1440, 2560, 1440, P()));
        Assert.Equal((1920, 1080, 0), HeightfieldRaymarch2D.TraceGrid(1920, 1080, 1920, 1080, P()));
        // A coarser field is kept, never upsampled.
        Assert.Equal((640, 360, 0), HeightfieldRaymarch2D.TraceGrid(640, 360, 640, 360, P()));
    }

    [Fact]
    public void DefaultPlateLook_IgnoresTheSetting()
    {
        var a = new uint[320 * 180]; var b = new uint[320 * 180];
        var c = Calc(320, 180);
        HeightfieldRaymarch2D.Render(c.ColorBuffer, c.SmoothBuffer, 320, 180, P(trueHeight: false, canonical: true), a);
        HeightfieldRaymarch2D.Render(c.ColorBuffer, c.SmoothBuffer, 320, 180, P(trueHeight: false, canonical: false), b);
        Assert.Equal(a, b);
    }

    // The poster / batch path sizes a large output's field the same way.
    [Fact]
    public void Poster_LargeOutput_UsesTheFloorField()
    {
        static PosterRequest Req(FractalParameters p) => new()
        {
            FractalType = FractalType.Mandelbrot, CenterX = -0.5, CenterY = 0, Zoom = 1.0, MaxIterations = 100,
            Width = 2560, Height = 1440, ColorMap = ColorPalette.BuiltIns[0], Quality = QualityPreset.Standard,
            FractalParameters = p, Format = ImageFileFormat.Png,
        };
        var got = PosterRenderer.ResolveReliefField(Req(P()), 2560, 1440, default, out int fw, out int fh);
        Assert.NotNull(got);
        Assert.Equal((1920, 1080), (fw, fh));
        Assert.Null(PosterRenderer.ResolveReliefField(Req(P(canonical: false)), 2560, 1440, default, out _, out _));
    }

    // ── parity ──────────────────────────────────────────────────────────────

    [Fact]
    public void Batch_Builder_Region_Dialog_CarryTheSetting()
    {
        string[] argv = { "FracturingFog", "--batch", "--fractal", "Mandelbrot", "--x", "-0.5", "--y", "0", "--zoom", "1",
                          "--relief-raymarch", "--relief-true-height", "--relief-no-canonical-field", "--out", "o.png" };
        Assert.True(BatchOptions.TryParse(argv, 2, out var o, out var e), e);
        Assert.True(o.ReliefNoCanonicalField);

        var snap = new BatchCommandSnapshot
        {
            Fractal = FractalType.Mandelbrot, ReliefEnabled = true, ReliefRaymarch = true,
            ReliefTrueHeight = true, ReliefCanonicalField = false,
        };
        var report = BatchCommandBuilder.BuildWithReport(snap);
        var back = new[] { "FracturingFog", "--batch" }.Concat(report.Args.Select(a => a == "<OUTPUT.png>" ? "o.png" : a)).ToArray();
        Assert.True(BatchOptions.TryParse(back, 2, out var o2, out var e2), e2);
        Assert.True(o2.ReliefNoCanonicalField);
        Assert.DoesNotContain("--relief-no-canonical-field", BatchCommandBuilder.Build(new BatchCommandSnapshot
        {
            Fractal = FractalType.Mandelbrot, ReliefEnabled = true, ReliefRaymarch = true, ReliefTrueHeight = true,
        }));

        var src = new FractalParameters { Relief2DEnabled = true, Relief2DCanonicalField = false };
        Assert.False(src.Clone().Relief2DCanonicalField);
        var dst = new FractalParameters();
        JsonSerializer.Deserialize<Relief3DSettings>(JsonSerializer.Serialize(Relief3DSettings.Snapshot(src)))!.ApplyTo(dst);
        Assert.False(dst.Relief2DCanonicalField);

        var p = new FractalParameters();
        var vm = new FractalParamsViewModel(FractalType.Mandelbrot, p) { Relief2DCanonicalField = false };
        Assert.False(p.Relief2DCanonicalField);
        Assert.True(new FractalParameters().Relief2DCanonicalField);
    }
}
