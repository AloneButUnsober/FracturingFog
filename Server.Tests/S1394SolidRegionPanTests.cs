// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1394 part 4 — solid-region skipping on #1330 pan frames: CalculatePanned subdivides
// the exposed strips (each at least 2 · SolidTileSize each way), with the kept pixels as
// tile borders. The view: the period-3 minibrot through the deep perturbation path
// (HPZoomThreshold 1), on the anchored lattice. The independent reference is a FRESH
// calculator rendering the whole frame at the new offset with the skip OFF:
//   • every pixel no frame of the sequence filled (this frame's fills, plus the kept
//     pixels earlier frames filled, shifted along) is byte-identical in every buffer;
//   • every filled pixel the reference finds in-set is byte-identical, and none escapes
//     on this view;
//   • the pan frame fills only exposed pixels, computes no kept one, and computes fewer
//     pixels than it exposed (precondition: it filled some);
//   • a pan whose strips are all too narrow runs no solid pass (the #1330 strips as before);
//   • Auto (on by default) gives the same exactness, and declines a strip of exterior
//     whatever the kept pixels beside it hold;
//   • with the skip off a pan computes exactly its strips, as before.

using System;
using System.Linq;
using System.Threading;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1394SolidRegionPanTests(ITestOutputHelper output)
{
    private const int W = 320, H = 200, MaxIter = 3000;
    private const double Cx = -1.7548776662466927, Zoom = 60;

    private static readonly IColorMap Hsv = new HsvPalette();

    private static MandelbrotCalculator Calc(int offX, int offY, bool skip, bool auto) => new(W, H)
    {
        Quality = new QualityPreset { Name = "perturb", AllowHighPrecision = true, HPZoomThreshold = 1 },
        CenterX = Cx, CenterY = 0, Zoom = Zoom, MaxIterations = MaxIter, ColorMap = Hsv, UseGpuCompute = false,
        ImageWidth = W, ImageHeight = H, SubRectOffsetX = offX, SubRectOffsetY = offY, LatticeAligned = true,
        SolidRegionSkip = skip, SolidRegionAuto = auto, CaptureFilledMask = true,
    };

    private static string? PixelDiff(MandelbrotCalculator a, MandelbrotCalculator b, int i)
    {
        static bool F(float[] x, float[] y, int i) => BitConverter.SingleToInt32Bits(x[i]) == BitConverter.SingleToInt32Bits(y[i]);
        if (a.IterationBuffer[i] != b.IterationBuffer[i]) return "iteration";
        if (a.ColorBuffer[i] != b.ColorBuffer[i]) return "colour";
        if (!F(a.SmoothBuffer, b.SmoothBuffer, i)) return "smooth";
        if (!F(a.DistanceBuffer, b.DistanceBuffer, i)) return "distance";
        if (!F(a.NormalXBuffer, b.NormalXBuffer, i)) return "normalX";
        if (!F(a.NormalYBuffer, b.NormalYBuffer, i)) return "normalY";
        if (!F(a.FinalZrBuffer, b.FinalZrBuffer, i)) return "finalZr";
        if (!F(a.FinalZiBuffer, b.FinalZiBuffer, i)) return "finalZi";
        if (!F(a.FinalDrBuffer, b.FinalDrBuffer, i)) return "finalDr";
        if (!F(a.FinalDiBuffer, b.FinalDiBuffer, i)) return "finalDi";
        return null;
    }

    // The pixels a pan by (dx, dy) exposes: new (x, y) has no old pixel (x + dx, y + dy).
    private static bool Exposed(int x, int y, int dx, int dy)
        => x + dx < 0 || x + dx >= W || y + dy < 0 || y + dy >= H;

    private enum Strip { Narrow, Fits, Fills }

    // The minibrot (about 230 px across here) sits at screen (160 − offX, 100 − offY); the
    // frame starts at offset (300, 0), the minibrot off the left edge. Pans whose strips
    // fit the grid (150-160 px) and bring the minibrot in (Fills), fit but expose only
    // exterior (Fits), combine a fitting column strip with a narrow row strip, or leave
    // only narrow strips.
    private static readonly (int Dx, int Dy, Strip S)[] Pans =
    {
        (-150, 0, Strip.Fills), (-150, 0, Strip.Fills), (0, -140, Strip.Fits), (0, 140, Strip.Fills),
        (5, -3, Strip.Narrow), (-160, 40, Strip.Fits), (160, -40, Strip.Fills),
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Pan_Frames_Subdivide_Their_Strips_With_The_Full_Frame_Exactness(bool auto)
    {
        int ox = 300, oy = 0;
        var a = Calc(ox, oy, skip: true, auto);
        a.Calculate(CancellationToken.None);
        Assert.NotEqual("SP", a.LastPrecisionLabel);   // precondition: the deep path
        // Every pixel some frame filled (kept pixels carry their fill along).
        var everFilled = (byte[])a.LastFilledMask!.Clone();
        long panFilled = 0, panInSet = 0;
        foreach (var (dx, dy, strip) in Pans)
        {
            Assert.True(a.CalculatePanned(dx, dy, CancellationToken.None), $"pan ({dx},{dy}) wasn't reused");
            ox += dx; oy += dy;
            MandelbrotCalculator.ShiftBuffer(everFilled, W, H, dx, dy);
            int exposed = 0, computedExposed = 0;
            var filled = a.LastFilledMask;
            var counts = a.LastBatchComputeCount;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int i = y * W + x;
                    bool e = Exposed(x, y, dx, dy);
                    if (e) { exposed++; everFilled[i] = 0; }
                    if (filled != null)
                    {
                        Assert.False(filled[i] != 0 && !e, $"pan ({dx},{dy}): kept pixel ({x},{y}) filled");
                        Assert.False(counts![i] != 0 && !e, $"pan ({dx},{dy}): kept pixel ({x},{y}) computed");
                        if (e && counts[i] != 0) computedExposed++;
                        if (filled[i] != 0) everFilled[i] = 1;
                    }
                }

            var fresh = Calc(ox, oy, skip: false, auto: false);
            fresh.Calculate(CancellationToken.None);
            int inSet = fresh.IterationBuffer.Take(W * H).Count(i => i >= MaxIter);
            int inSetExposed = Enumerable.Range(0, W * H).Count(i => Exposed(i % W, i / W, dx, dy) && fresh.IterationBuffer[i] >= MaxIter);
            Assert.True(inSet < W * H * 9 / 10, $"precondition: {inSet} in-set pixels at ({ox},{oy})");
            if (strip == Strip.Fills)
                Assert.True(inSetExposed > exposed / 10, $"precondition: pan ({dx},{dy}) exposes only {inSetExposed} in-set pixels");
            int wrong = 0;
            for (int i = 0; i < W * H; i++)
            {
                if (everFilled[i] != 0 && fresh.IterationBuffer[i] < MaxIter) { wrong++; continue; }
                string? d = PixelDiff(a, fresh, i);
                Assert.True(d == null, $"pan ({dx},{dy}) to ({ox},{oy}): {d} differs at ({i % W},{i / W}){(everFilled[i] != 0 ? " (filled)" : "")}");
            }
            Assert.Equal(0, wrong);

            output.WriteLine($"auto {auto} pan ({dx},{dy}): exposed {exposed}, passes {a.LastFrameSolidPasses}, filled {a.LastFrameFilledPixels}, " +
                             $"computed {computedExposed}, in set {inSetExposed}{(a.LastFrameSolidDeclined ? ", declined" : "")}, costly share {a.LastFrameSolidCostlyShare:P1}");
            if (strip == Strip.Narrow) { Assert.Equal(0, a.LastFrameSolidPasses); Assert.Equal(0, a.LastFrameFilledPixels); continue; }
            Assert.True(a.LastFrameSolidPasses > 0, $"pan ({dx},{dy}): no solid pass");
            if (strip == Strip.Fits)
            {
                // Auto measures the strips' own rows, not the kept pixels beside them.
                if (auto) Assert.True(a.LastFrameSolidDeclined, $"pan ({dx},{dy}): Auto subdivided an exterior strip ({a.LastFrameSolidCostlyShare:P1})");
                continue;
            }
            Assert.False(a.LastFrameSolidDeclined, $"pan ({dx},{dy}): Auto declined (precondition: a costly strip)");
            Assert.True(a.LastFrameFilledPixels > 0, $"pan ({dx},{dy}): the strip filled nothing");
            Assert.True(computedExposed + a.LastFrameFilledPixels >= exposed, $"pan ({dx},{dy}): some exposed pixel was neither computed nor filled");
            Assert.True(computedExposed < exposed, $"pan ({dx},{dy}): computed every exposed pixel");
            panFilled += a.LastFrameFilledPixels;
            panInSet += inSetExposed;
        }
        // A 64-px grid fills about half a 230-px minibrot's exposed interior.
        Assert.True(panFilled > panInSet / 3, $"the pans filled only {panFilled} of {panInSet} exposed in-set pixels");
    }

    [Fact]
    public void A_Pan_With_The_Skip_Off_Runs_No_Solid_Pass()
    {
        var a = Calc(0, 0, skip: false, auto: true);
        a.Calculate(CancellationToken.None);
        Assert.True(a.CalculatePanned(150, 0, CancellationToken.None));
        Assert.Equal(0, a.LastFrameSolidPasses);
        Assert.Equal((long)150 * H, a.LastFrameComputedPixels);
    }
}
