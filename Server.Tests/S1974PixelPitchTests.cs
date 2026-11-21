// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Linq;
using System.Text.Json;
using FracturingFog;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.ViewState;
using Xunit;

namespace FracturingFog.Server.Tests;

// #1974 — ten 2D families rendered with pitch (4 / width) / zoom while ViewCamera (box / wheel
// zoom, pan) uses 3.5 / (max(w, h) x zoom), so input landed at 3.5/4 of the distance from the
// centre. Independent checks:
//   • pan invariant: ViewCamera.TranslateByPixels by (dx, dy) pixels must move every family's
//     image by exactly (dx, dy) pixels (landscape and portrait); with the old pitch the image
//     moved 0.875 x as far on a landscape view and the overlap disagrees;
//   • where a family exposes its plane mapping (the billiards' PlaneAt / WorldAt, Math Plane's
//     ToPixel and pointer mapping), it equals ViewCamera.WorldFromScreen at corners and centre,
//     and a box zoom puts the boxed plane point at the new centre;
//   • saved regions convert once: a legacy-convention zoom of an affected family is x 3.5/4 on
//     load, other families and current files are untouched, and load-save-load converts once;
//   • the default view of each family frames the same as before on a landscape view.
public sealed class S1974PixelPitchTests
{
    public static readonly TheoryData<FractalType> Families = new()
    {
        FractalType.Apollonian, FractalType.BilliardRays, FractalType.ChaoticBilliard, FractalType.JulibrotPair,
        FractalType.IndrasPearls, FractalType.MathPlane, FractalType.PrecisionField, FractalType.RandomTile,
        FractalType.SlitCascade, FractalType.TalbotCarpet,
    };

    private static IFractalCalculator Make(FractalType t, int w, int h) => t switch
    {
        FractalType.Apollonian => new ApollonianCalculator(w, h),
        FractalType.BilliardRays => new BilliardRaysCalculator(w, h) { FractalParameters = new FractalParameters { BilliardRaysCount = 20_000 } },
        FractalType.ChaoticBilliard => new ChaoticBilliardCalculator(w, h),
        FractalType.JulibrotPair => new DualOrbitEscapeCalculator(w, h),
        FractalType.IndrasPearls => new IndrasPearlsCalculator(w, h),
        FractalType.MathPlane => new MathPlaneCalculator(w, h),
        FractalType.PrecisionField => new PrecisionFieldCalculator(w, h),
        FractalType.RandomTile => new RandomTileCalculator(w, h),
        FractalType.SlitCascade => new SlitCascadeCalculator(w, h),
        FractalType.TalbotCarpet => new TalbotCarpetCalculator(w, h),
        _ => throw new ArgumentOutOfRangeException(nameof(t)),
    };

    private static uint[] Render(FractalType t, int w, int h, double cx, double cy, double zoom)
    {
        var c = Make(t, w, h);
        c.CenterX = cx; c.CenterY = cy; c.Zoom = zoom; c.MaxIterations = 256;
        c.ColorMap = new HsvPalette();
        c.Calculate(TestContext.Current.CancellationToken);
        return (uint[])c.ColorBuffer.Clone();
    }

    private static double Mismatch(uint[] a, uint[] b, int w, int h, int dx, int dy)
    {
        // b(x, y) against a(x + dx, y + dy) over the overlap, a few pixels in from the edges.
        int n = 0, bad = 0;
        for (int y = 2; y < h - 2; y++)
            for (int x = 2; x < w - 2; x++)
            {
                int ax = x + dx, ay = y + dy;
                if (ax < 2 || ay < 2 || ax >= w - 2 || ay >= h - 2) continue;
                n++;
                if (!Close(a[ay * w + ax], b[y * w + x])) bad++;
            }
        return (double)bad / Math.Max(1, n);
    }

    private static bool Close(uint p, uint q)
    {
        for (int s = 0; s < 24; s += 8)
            if (Math.Abs((int)((p >> s) & 0xFF) - (int)((q >> s) & 0xFF)) > 24) return false;
        return true;
    }

    [Theory]
    [MemberData(nameof(Families))]
    public void A_Camera_Pan_Moves_The_Image_By_Exactly_That_Many_Pixels(FractalType t)
    {
        // Random Tile regenerates its packing for every view (SupportsZoomPan is false), so a pan
        // cannot move its image; its pitch is checked by the default-framing test below.
        if (t == FractalType.RandomTile)
        {
            Assert.False(Make(t, 8, 8).SupportsZoomPan);
            return;
        }
        foreach (var (w, h) in new[] { (96, 64), (64, 96) })
        {
            var vs = new FractalViewState { FractalType = t };
            vs.SnapToFractalDefault(t);
            if (t == FractalType.PrecisionField)
            {
                // The default view is almost flat (the tiers agree nearly everywhere); seahorse
                // valley is dense with the boundary filaments where they part.
                vs.CenterX = -0.7453; vs.CenterY = 0.1127; vs.Zoom = 60;
            }
            double cx = vs.CenterX, cy = vs.CenterY, zoom = vs.Zoom;
            var cam = new ViewCamera(vs);
            const int dx = 11, dy = -7;
            var before = cam.WorldFromScreen(w / 2.0 + dx, h / 2.0 + dy, w, h);
            cam.TranslateByPixels(dx, dy, w, h);
            // The camera's own claim: the new centre is the old world point (dx, dy) pixels off,
            // so new pixel p shows old pixel p + (dx, dy).
            Assert.InRange(Math.Abs(vs.CenterX - (double)before.Re), 0.0, 1e-12);
            Assert.InRange(Math.Abs(vs.CenterY - (double)before.Im), 0.0, 1e-12);

            var a = Render(t, w, h, cx, cy, zoom);
            var b = Render(t, w, h, vs.CenterX, vs.CenterY, zoom);
            double shifted = Mismatch(a, b, w, h, dx, dy);
            double unshifted = Mismatch(a, b, w, h, 0, 0);
            TestContext.Current.TestOutputHelper?.WriteLine($"{t} {w}x{h}: mismatch shifted {shifted:P1}, unshifted {unshifted:P1}");
            // Where the old pitch (4 / width) would have moved the image: 0.875 x as far.
            int ox = (int)Math.Round(dx * ViewCamera.PlaneExtent * w / (4.0 * Math.Max(w, h)));
            int oy = (int)Math.Round(dy * ViewCamera.PlaneExtent * w / (4.0 * Math.Max(w, h)));
            double old = Mismatch(a, b, w, h, ox, oy);
            TestContext.Current.TestOutputHelper?.WriteLine($"  old-pitch shift ({ox}, {oy}): mismatch {old:P1}");
            // Math Plane keeps its tick labels inside the frame, so a few label pixels stay put.
            double allowed = t == FractalType.MathPlane ? 0.05 : 0.02;
            Assert.True(unshifted > 0.05, $"{t} {w}x{h}: the view is too flat to test ({unshifted:P1})");
            Assert.True(shifted < allowed, $"{t} {w}x{h}: the image did not move with the camera ({shifted:P1} of the overlap differs)");
            Assert.True(old > Math.Max(3 * shifted, 0.02), $"{t} {w}x{h}: cannot tell the camera's shift from the old pitch's ({old:P1} vs {shifted:P1})");
        }
    }

    [Theory]
    [InlineData(0.3, -0.2, 1.7, 400, 225)]
    [InlineData(-1.25, 0.5, 9.0, 225, 400)]
    public void Billiard_Plane_Mapping_Is_The_Cameras(double cx, double cy, double zoom, int w, int h)
    {
        var vs = new FractalViewState { FractalType = FractalType.ChaoticBilliard, CenterX = cx, CenterY = cy, Zoom = zoom };
        var cam = new ViewCamera(vs);
        double pitch = ViewCamera.PixelPitch(w, h, zoom);
        Assert.Equal(cam.Scale(w, h), pitch, 15);
        foreach (var (x, y) in new[] { (0, 0), (w - 1, 0), (0, h - 1), (w - 1, h - 1), (w / 2, h / 2), (w / 3, h / 5) })
        {
            var world = cam.WorldFromScreen(x + 0.5, y + 0.5, w, h);
            var (px, py) = ChaoticBilliardCalculator.PlaneAt(x + 0.5, y + 0.5, w, h, cx, cy, zoom);
            Assert.InRange(Math.Abs((double)world.Re - px), 0.0, 1e-9 * pitch);
            Assert.InRange(Math.Abs((double)world.Im - py), 0.0, 1e-9 * pitch);
            var (wx, wy) = BilliardRaysCalculator.WorldAt(x + 0.5, y + 0.5, w, h, cx, cy, zoom);
            Assert.InRange(Math.Abs(wx - px), 0.0, 1e-12);
            Assert.InRange(Math.Abs(wy + py), 0.0, 1e-12);   // Billiard Rays' world is y-up
            // Math Plane: pointer and render mappings (world y-up too).
            var (mx, my) = MathPlaneSceneEdit.ClientToWorld(x + 0.5, y + 0.5, w, h, cx, cy, zoom);
            Assert.InRange(Math.Abs(mx - px), 0.0, 1e-9 * pitch);
            Assert.InRange(Math.Abs(my + py), 0.0, 1e-9 * pitch);
            var (tx, ty) = MathPlaneCalculator.ToPixel(mx, my, w, h, cx, cy, zoom);
            Assert.InRange(Math.Abs(tx - (x + 0.5)), 0.0, 1e-6);
            Assert.InRange(Math.Abs(ty - (y + 0.5)), 0.0, 1e-6);
        }
    }

    [Fact]
    public void A_Box_Zoom_Centres_The_Boxed_Plane_Point()
    {
        const int w = 400, h = 225;
        var vs = new FractalViewState { FractalType = FractalType.ChaoticBilliard, CenterX = 0, CenterY = 0, Zoom = 0.875 };
        var cam = new ViewCamera(vs);
        // A box near the right edge, where the old pitch missed by an eighth of the distance.
        double mx = w - 30.5, my = 40.5;
        var (px, py) = ChaoticBilliardCalculator.PlaneAt(mx, my, w, h, 0, 0, 0.875);
        cam.BoxZoomToPoint(mx, my, 8.0, w, h, 1e-6, 1e12);
        Assert.InRange(Math.Abs(vs.CenterX - px), 0.0, 1e-12);
        Assert.InRange(Math.Abs(vs.CenterY - py), 0.0, 1e-12);
        var (qx, qy) = ChaoticBilliardCalculator.PlaneAt(w / 2.0, h / 2.0, w, h, vs.CenterX, vs.CenterY, vs.Zoom);
        Assert.InRange(Math.Abs(qx - px), 0.0, 1e-12);
        Assert.InRange(Math.Abs(qy - py), 0.0, 1e-12);
    }

    [Fact]
    public void Saved_Regions_Convert_Once()
    {
        // A region file written before #1974 has no ZoomConvention.
        string Legacy(FractalType t, double zoom) =>
            $"{{\"Name\":\"r\",\"CenterX\":0.25,\"CenterY\":-0.5,\"Zoom\":{zoom.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"FractalType\":{(int)t}}}";

        var billiard = JsonSerializer.Deserialize<FractalRegion>(Legacy(FractalType.ChaoticBilliard, 2.0))!;
        Assert.Equal(1.75, billiard.Zoom, 12);
        Assert.Equal(0.25, billiard.CenterX);
        Assert.Equal(-0.5, billiard.CenterY);
        Assert.Equal(FractalRegion.CurrentZoomConvention, billiard.ZoomConvention);

        var mandel = JsonSerializer.Deserialize<FractalRegion>(Legacy(FractalType.Mandelbrot, 2.0))!;
        Assert.Equal(2.0, mandel.Zoom);   // Mandelbrot always used the camera's scale

        // Save and load again: no second conversion.
        var again = JsonSerializer.Deserialize<FractalRegion>(JsonSerializer.Serialize(billiard))!;
        Assert.Equal(1.75, again.Zoom, 12);

        // A region made now (built-ins, new saves) is current and round-trips unchanged.
        var fresh = new FractalRegion { Name = "n", FractalType = FractalType.TalbotCarpet, Zoom = 1.6625 };
        Assert.Equal(1.6625, JsonSerializer.Deserialize<FractalRegion>(JsonSerializer.Serialize(fresh))!.Zoom);
        foreach (var t in Families.Select(d => d.Data))
            Assert.True(ViewCamera.UsedLegacyPitch(t), t.ToString());
        Assert.False(ViewCamera.UsedLegacyPitch(FractalType.Mandelbrot));
        Assert.False(ViewCamera.UsedLegacyPitch(FractalType.BlackHoleShadow));
    }

    [Theory]
    [MemberData(nameof(Families))]
    public void Default_Views_Frame_The_Same_Landscape_Width_As_Before(FractalType t)
    {
        // Old default zooms (pitch 4 / width): the visible width was 4 / zoom on any landscape view.
        double oldZoom = t switch
        {
            FractalType.Apollonian => 2.0, FractalType.ChaoticBilliard => 1.0, FractalType.BilliardRays => 0.6,
            FractalType.SlitCascade => 0.25, FractalType.TalbotCarpet => 1.9, FractalType.MathPlane => 0.5,
            FractalType.PrecisionField => 1.0, FractalType.JulibrotPair => 1.0, FractalType.IndrasPearls => 0.6,
            FractalType.RandomTile => 1.0, _ => double.NaN,
        };
        var vs = new FractalViewState();
        vs.SnapToFractalDefault(t);
        double newWidth = ViewCamera.PixelPitch(1920, 1080, vs.Zoom) * 1920;
        Assert.Equal(4.0 / oldZoom, newWidth, 9);
        Assert.Equal(vs.Zoom, MiniMapDefaults.For(t).Zoom, 12);
    }
}
