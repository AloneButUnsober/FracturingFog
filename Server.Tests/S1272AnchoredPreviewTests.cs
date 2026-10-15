// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1272 (SM-11 residual) — the deep-zoom drag looked "jumpy": every drag step
// re-rendered the ¼ preview centred on the new view, so its coarse grid sampled
// different points of the fractal each step and thin features flickered. The
// preview is now rendered on a lattice anchored in world space and drawn with
// the exact sub-texel offset (PreviewLattice). Checks:
//   • geometry — each preview centre is on the anchor's lattice, within half a
//     texel of the view, and every screen pixel samples the texel whose WORLD
//     point is its own (computed in OD, not via the map formula);
//   • the resample is exact for an identity map and bilinear on a ramp;
//   • a simulated drag through the real calculator: anchored previews are about
//     as steady as full-resolution frames, while previews centred on the view
//     (the old behaviour) shimmer heavily.

using System;
using System.Threading;
using FracturingFog;
using FracturingFog.FFMath;
using FracturingFog.Models;
using FracturingFog.Rendering;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1272AnchoredPreviewTests
{
    private const int W = 192, H = 128, F = 4, PW = W / F, PH = H / F;

    [Fact]
    public void Snapped_Preview_Samples_The_Same_World_Point_As_The_Screen_Pixel()
    {
        var anchor = new DeepComplex(new OD(-0.743643887037151) + 1.3e-20, new OD(0.13182590420533) - 4.1e-21);
        double zoom = 1e22;
        double s = (3.5 / Math.Max(W, H)) / zoom, sp = (3.5 / Math.Max(PW, PH)) / zoom;
        var rng = new Random(1272);
        for (int t = 0; t < 200; t++)
        {
            double ox = (rng.NextDouble() - 0.5) * 2000, oy = (rng.NextDouble() - 0.5) * 2000;   // screen px
            var centre = anchor.Translate(ox * s, oy * s);
            var snap = PreviewLattice.Snap(centre, anchor, s, sp, W, H, PW, PH);
            Assert.NotNull(snap);
            var (p, map) = snap!.Value;

            // On the lattice: a whole number of texels from the anchor…
            double lx = (p.Re - anchor.Re).X0 / sp, ly = (p.Im - anchor.Im).X0 / sp;
            Assert.Equal(Math.Round(lx), lx, 6);
            Assert.Equal(Math.Round(ly), ly, 6);
            // …and within half a texel of the view centre.
            Assert.True(Math.Abs((centre.Re - p.Re).X0) <= 0.5 * sp * (1 + 1e-9));
            Assert.True(Math.Abs((centre.Im - p.Im).X0) <= 0.5 * sp * (1 + 1e-9));

            // Screen pixel (x, y) and the texel it samples are the same world point.
            foreach (var (x, y) in new[] { (0, 0), (W - 1, H - 1), (W / 2, H / 3), (17, 99) })
            {
                double u = map.Ax + x * map.K, v = map.Ay + y * map.K;
                var screenWorld = centre.Translate((x - W * 0.5) * s, (y - H * 0.5) * s);
                var texelWorld = p.Translate((u - PW * 0.5) * sp, (v - PH * 0.5) * sp);
                Assert.True(Math.Abs((screenWorld.Re - texelWorld.Re).X0) < 1e-6 * s);
                Assert.True(Math.Abs((screenWorld.Im - texelWorld.Im).X0) < 1e-6 * s);
            }
        }
        // Far from the anchor the caller is told to restart the lattice.
        Assert.Null(PreviewLattice.Snap(anchor.Translate(2e6 * sp, 0), anchor, s, sp, W, H, PW, PH));
    }

    [Fact]
    public void Resample_Is_Exact_For_Identity_And_Bilinear_On_A_Ramp()
    {
        var src = new uint[8 * 4];
        var rng = new Random(5);
        for (int i = 0; i < src.Length; i++) src[i] = (uint)rng.Next();
        var dst = new uint[8 * 4];
        PreviewLattice.Resample(src, 8, 4, new PreviewLattice.Map(0, 0, 1), dst, 8, 4);
        Assert.Equal(src, dst);

        // A ramp: blue channel = 10·column. Sampling at u = 1.25 gives 12.5 → 13 (rounded).
        var ramp = new uint[4 * 2];
        for (int y = 0; y < 2; y++) for (int x = 0; x < 4; x++) ramp[y * 4 + x] = 0xFF000000u | (uint)(10 * x);
        var one = new uint[1];
        PreviewLattice.Resample(ramp, 4, 2, new PreviewLattice.Map(1.25, 0.5, 1), one, 1, 1);
        Assert.Equal(13u, one[0] & 0xFF);
        Assert.Equal(0xFFu, one[0] >> 24);
        // Off the edge clamps to the last texel.
        PreviewLattice.Resample(ramp, 4, 2, new PreviewLattice.Map(9, 0, 1), one, 1, 1);
        Assert.Equal(30u, one[0] & 0xFF);
    }

    private static uint[] Render(DeepComplex c, int w, int h, double zoom)
    {
        var calc = new MandelbrotCalculator(w, h)
        {
            Quality = QualityPreset.Extreme,
            CenterX = c.Re.X0, CenterXLo = c.Re.X1, CenterX2 = c.Re.X2, CenterX3 = c.Re.X3,
            CenterY = c.Im.X0, CenterYLo = c.Im.X1, CenterY2 = c.Im.X2, CenterY3 = c.Im.X3,
            Zoom = zoom, MaxIterations = 2000, ColorMap = new HsvPalette(),
        };
        calc.Calculate(CancellationToken.None);
        return (uint[])calc.ColorBuffer.Clone();
    }

    /// <summary>Mean per-channel |Δ| between consecutive screen frames after
    /// undoing a pan of <paramref name="step"/> px (0 = a pure translation).</summary>
    private static double Jitter(uint[] a, uint[] b, int step)
    {
        double sum = 0; long n = 0;
        for (int y = 6; y < H - 6; y++)
            for (int x = 6 + step; x < W - 6; x++)
            {
                uint p = a[y * W + x], q = b[y * W + x - step];
                for (int sh = 0; sh < 24; sh += 8) sum += Math.Abs((int)((p >> sh) & 255) - (int)((q >> sh) & 255));
                n += 3;
            }
        return sum / n;
    }

    private static double Drag(bool anchored, bool full, int step)
    {
        var start = new DeepComplex(new OD(-0.743643887037151), new OD(0.13182590420533));
        double zoom = 1e6;
        double s = (3.5 / Math.Max(W, H)) / zoom, sp = (3.5 / Math.Max(PW, PH)) / zoom;
        uint[]? prev = null; double sum = 0; int cnt = 0;
        for (int k = 0; k < 5; k++)
        {
            var centre = start.Translate(k * step * s, 0);
            uint[] screen;
            if (full) screen = Render(centre, W, H, zoom);
            else
            {
                // Anchored: one lattice for the whole drag. Old behaviour: the
                // preview centred on the view (anchor = the view itself).
                var (p, map) = PreviewLattice.Snap(centre, anchored ? start : centre, s, sp, W, H, PW, PH)!.Value;
                var preview = Render(p, PW, PH, zoom);
                screen = new uint[W * H];
                PreviewLattice.Resample(preview, PW, PH, map, screen, W, H);
            }
            if (prev != null) { sum += Jitter(prev, screen, step); cnt++; }
            prev = screen;
        }
        return sum / cnt;
    }

    [Theory]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(13)]
    public void Anchored_Previews_Are_As_Steady_As_Full_Frames_During_A_Drag(int step)
    {
        double fullRes = Drag(anchored: false, full: true, step);
        double centred = Drag(anchored: false, full: false, step);
        double anchored = Drag(anchored: true, full: false, step);
        Assert.True(centred > 8, $"precondition: view-centred previews should shimmer (got {centred:F1})");
        Assert.True(anchored <= Math.Max(2.0, 3 * fullRes),
            $"anchored previews jitter {anchored:F1} (full-res frames {fullRes:F1}, view-centred previews {centred:F1})");
    }
}
