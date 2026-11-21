// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Linq;
using FracturingFog;
using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.Models;
using FracturingFog.Rendering;
using Xunit;

namespace FracturingFog.Server.Tests;

// #1969 — anti-aliasing the black hole shadows' lensed sky (adaptive AA through
// IAdaptiveAaCalculator). Independent references:
//   • massless holes + pinhole camera: every ray is straight, so the box-filtered sky over a
//     pixel is computed here from the camera geometry alone (32 x 32 sub-pixel directions into
//     BlackHoleSkies), without the tracer; supersampled frames must approach it;
//   • near a real shadow edge, an 8 x 8 full supersample is the reference for the moire measure;
//   • a masked frame gives every marked pixel exactly a full frame's bytes (sentinel-filled
//     buffers prove the unmarked ones are not traced);
//   • without sub-pixel samples nothing changes, and the setting rides the region snapshot / --param.
public sealed class S1969BlackHoleSkyAaTests
{
    private const int W = 64, H = 36;

    private static BlackHoleShadowCalculator Calc(FractalParameters p, double zoom, double cx = 0, double cy = 0, int w = W, int h = H)
        => new(w, h) { FractalParameters = p, ColorMap = new HsvPalette(), Zoom = zoom, CenterX = cx, CenterY = cy };

    private static uint[] Render(FractalParameters p, double zoom, int aaSamples, out SupersampleStats st, double cx = 0, double cy = 0)
    {
        var c = Calc(p, zoom, cx, cy);
        c.Calculate(TestContext.Current.CancellationToken);
        st = Supersampling.AccumulateAlt(c, aaSamples, TestContext.Current.CancellationToken);
        return (uint[])c.ColorBuffer.Clone();
    }

    private static double MeanAbsError(uint[] a, uint[] b, Func<int, bool>? where = null)
    {
        double sum = 0; int n = 0;
        for (int i = 0; i < a.Length; i++)
        {
            if (where != null && !where(i)) continue;
            for (int sh = 0; sh < 24; sh += 8)
                sum += Math.Abs((int)((a[i] >> sh) & 0xFF) - (int)((b[i] >> sh) & 0xFF));
            n += 3;
        }
        return n == 0 ? 0 : sum / n;
    }

    // A massless pair: one zero-mass hole, nothing is captured, every ray goes straight.
    private static FractalParameters MasslessChecker(bool adaptive = false) => new()
    {
        BlackHoleMass1 = 0.0, BlackHoleMass2 = 0.0,
        BlackHoleProjection = BlackHoleProjection.Pinhole, BlackHoleCameraDistance = 50.0,
        BlackHoleSky = BlackHoleSky.Checker, BlackHoleSkyDivisions = 48,
        AdaptiveAa = adaptive,
    };
    private const double MasslessZoom = 0.025;

    // The box-filtered sky over each pixel from the camera geometry alone (no tracer).
    private static uint[] AnalyticBoxFilteredSky(FractalParameters p, double zoom, int k)
    {
        var scene = BlackHoleShadowCalculator.Scene.Build(p, zoom, BlackHoleShadowCalculator.PixelPitch(W, H, zoom));
        var outBuf = new uint[W * H];
        Span<double> s = stackalloc double[6];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                long r = 0, g = 0, b = 0;
                for (int j = 0; j < k; j++)
                    for (int i = 0; i < k; i++)
                    {
                        var (u, v) = BlackHoleShadowCalculator.PlaneAt(x + (i + 0.5) / k, y + (j + 0.5) / k, W, H, 0, 0, zoom);
                        var (dx, dy, dz) = scene.Camera.Direction(u, v);
                        s[3] = dx; s[4] = dy; s[5] = dz;
                        uint c = scene.SkyColour(s);
                        b += c & 0xFF; g += (c >> 8) & 0xFF; r += (c >> 16) & 0xFF;
                    }
                long n = (long)k * k;
                outBuf[y * W + x] = 0xFF000000u | (uint)((r + n / 2) / n) << 16 | (uint)((g + n / 2) / n) << 8 | (uint)((b + n / 2) / n);
            }
        return outBuf;
    }

    [Fact]
    public void Massless_Pinhole_Supersampling_Approaches_The_Box_Filtered_Sky()
    {
        var p = MasslessChecker();
        var probe = Calc(p, MasslessZoom);
        probe.Calculate(TestContext.Current.CancellationToken);
        Assert.All(probe.FateBuffer, f => Assert.Equal(1, f));   // one massless hole: everything escapes

        var reference = AnalyticBoxFilteredSky(p, MasslessZoom, 32);
        double e1 = MeanAbsError(Render(p, MasslessZoom, 1, out _), reference);
        double e16 = MeanAbsError(Render(p, MasslessZoom, 16, out var full), reference);
        double eAd = MeanAbsError(Render(MasslessChecker(adaptive: true), MasslessZoom, 16, out var ad), reference);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"mean |error| vs analytic box filter: 1 sample {e1:F2}, 4x4 {e16:F2}, adaptive 4x4 {eAd:F2} " +
            $"({ad.PixelSamples} of {full.PixelSamples} pixel-samples, {ad.Marked} of {ad.Pixels} marked)");
        Assert.True(e16 < e1 / 3, $"4x4 error {e16:F2} vs one sample {e1:F2}");
        Assert.True(eAd < e1 / 3, $"adaptive error {eAd:F2} vs one sample {e1:F2}");
        Assert.True(eAd <= e16 + 1.0, $"adaptive {eAd:F2} much worse than full {e16:F2}");
    }

    [Fact]
    public void Adaptive_AA_Cuts_The_Moire_Near_A_Shadow_Edge_At_A_Fraction_Of_The_Cost()
    {
        // The lensed starfield preset's camera with a fine checker: the sky crowds into the
        // band round the shadows.
        FractalParameters P(bool adaptive) => new()
        {
            BlackHoleProjection = BlackHoleProjection.Pinhole, BlackHoleCameraDistance = 30.0,
            BlackHoleSky = BlackHoleSky.Checker, BlackHoleSkyDivisions = 24, AdaptiveAa = adaptive,
        };
        const double zoom = 0.11;
        var reference = Render(P(false), zoom, 64, out _);   // 8 x 8 full grid
        var c0 = Calc(P(false), zoom);
        c0.Calculate(TestContext.Current.CancellationToken);
        var one = (uint[])c0.ColorBuffer.Clone();
        var fate = (sbyte[])c0.FateBuffer.Clone();

        // Escaped pixels within 3 pixels of a shadow: the lensed band.
        bool NearEdge(int i)
        {
            if (fate[i] != 2) return false;
            int x = i % W, y = i / W;
            for (int dy = -3; dy <= 3; dy++)
                for (int dx = -3; dx <= 3; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx >= 0 && ny >= 0 && nx < W && ny < H && fate[ny * W + nx] != 2) return true;
                }
            return false;
        }
        int band = Enumerable.Range(0, W * H).Count(NearEdge);
        Assert.True(band > 50, $"only {band} pixels in the lensed band");

        double e1 = MeanAbsError(one, reference, NearEdge);
        double eFull = MeanAbsError(Render(P(false), zoom, 16, out var full), reference, NearEdge);
        double eAd = MeanAbsError(Render(P(true), zoom, 16, out var ad), reference, NearEdge);
        double cost = (double)ad.PixelSamples / full.PixelSamples;
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"lensed band ({band} px) mean |error| vs 8x8: 1 sample {e1:F2}, 4x4 {eFull:F2}, adaptive {eAd:F2}; " +
            $"adaptive cost {cost:P0} of full ({ad.Marked} of {ad.Pixels} marked)");
        Assert.True(eFull < e1 / 2, $"4x4 {eFull:F2} vs one sample {e1:F2}");
        Assert.True(eAd < e1 / 2, $"adaptive {eAd:F2} vs one sample {e1:F2}");
        Assert.True(cost < 0.8, $"adaptive took {cost:P0} of the full grid's pixel-samples");
    }

    [Fact]
    public void A_Masked_Frame_Traces_Only_Marked_Pixels_And_Matches_The_Full_Frame_There()
    {
        var p = new FractalParameters { BlackHoleProjection = BlackHoleProjection.Pinhole, BlackHoleSky = BlackHoleSky.Stars };
        var full = Calc(p, 0.14, cx: 0.013, cy: -0.021);
        full.Calculate(TestContext.Current.CancellationToken);

        var masked = Calc(p, 0.14, cx: 0.013, cy: -0.021);
        masked.Calculate(TestContext.Current.CancellationToken);   // allocate buffers
        Array.Fill(masked.ColorBuffer, 0x12345678u);
        var mask = new byte[W * H];
        var rng = new Random(1969);
        for (int i = 0; i < mask.Length; i++) mask[i] = (byte)(rng.Next(3) == 0 ? 1 : 0);
        masked.PixelMask = mask;
        Assert.True(masked.MaskExact);
        masked.Calculate(TestContext.Current.CancellationToken);
        masked.PixelMask = null;
        for (int i = 0; i < mask.Length; i++)
            Assert.Equal(mask[i] != 0 ? full.ColorBuffer[i] : 0x12345678u, masked.ColorBuffer[i]);
    }

    [Fact]
    public void Passes_That_Read_Neighbours_Fall_Back_To_The_Full_Grid()
    {
        Assert.True(Calc(new FractalParameters(), 0.14).MaskExact);
        Assert.False(Calc(new FractalParameters { BlackHoleSolidBlocks = true }, 0.14).MaskExact);
        Assert.True(Calc(new FractalParameters { BlackHoleSolidBlocks = true, BlackHoleChannel = BlackHoleChannel.Steps }, 0.14).MaskExact);
        double deep = BlackHoleShadowCalculator.DefaultZoom * 1e12;
        Assert.False(Calc(new FractalParameters(), deep).MaskExact);
        Assert.True(Calc(new FractalParameters { BlackHoleDdEdges = false }, deep).MaskExact);
    }

    [Fact]
    public void Without_Sub_Pixel_Samples_Nothing_Changes()
    {
        var p = new FractalParameters { BlackHoleProjection = BlackHoleProjection.Pinhole, BlackHoleSky = BlackHoleSky.Checker, AdaptiveAa = true };
        var c = Calc(p, 0.11);
        c.Calculate(TestContext.Current.CancellationToken);
        var before = (uint[])c.ColorBuffer.Clone();
        var st = Supersampling.AccumulateAlt(c, 1, TestContext.Current.CancellationToken);
        Assert.Equal(before, c.ColorBuffer);
        Assert.Equal(W * H, (int)st.PixelSamples);
    }

    [Fact]
    public void Adaptive_Setting_Rides_The_Region_Snapshot_And_The_Command_Line()
    {
        Assert.True(AdaptiveAaCapabilities.Supports(FractalType.BlackHoleShadow));
        var p = new FractalParameters { AdaptiveAa = true, AdaptiveAaThreshold = 20, AdaptiveAaTolerance = 3 };
        var snap = RegionFractalParams.Snapshot(FractalType.BlackHoleShadow, p)!;
        var report = BatchCommandBuilder.BuildWithReport(new BatchCommandSnapshot
        {
            Fractal = FractalType.BlackHoleShadow, Parameters = p, FamilyParams = snap.ToKeyValues(),
        });
        Assert.True(BatchOptions.TryParse(report.Args.ToArray(), 0, out var o, out var err), err);
        var q = new FractalParameters();
        RegionFractalParams.FromKeyValues(o.Params, FractalType.BlackHoleShadow, out var perr)!.ApplyTo(q);
        Assert.Null(perr);
        Assert.True(q.AdaptiveAa);
        Assert.Equal(20, q.AdaptiveAaThreshold);
        Assert.Equal(3, q.AdaptiveAaTolerance);
    }
}
