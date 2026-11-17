// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1836 (epic #1428, C3) — zoom past double's range (log10 zoom). Checked:
//   • Up to double's range nothing changes: the zoom text is exactly "G6", the iteration
//     count is the old formula, a camera zoom / pan gives the same doubles and limbs as the
//     old expressions (re-derived here, not called).
//   • Past it the log carries the zoom: the view state, the text (exact expected strings),
//     the Zoom box parse, the iteration count (no int overflow), region JSON.
//   • The camera at 1e500: the world point under the cursor stays under it through a wheel
//     zoom, a pan moves the centre by exactly its pixels, a box zoom centres the box — each
//     measured in pixels against a scale built from decimal text (not the camera's log).
//   • An exact centre (#1832) survives a pan and a wheel zoom at shallow zoom too (before
//     #1836 any camera move dropped it), digits past OD included.

using System;
using System.Text.Json;
using FracturingFog.FFMath;
using FracturingFog.Models;
using FracturingFog.ViewState;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1836LogZoomTests
{
    private const int W = 1000, H = 800;

    // ── text ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0.13)]
    [InlineData(1.0)]
    [InlineData(123456.789)]
    [InlineData(4.65e64)]
    [InlineData(1e100)]
    [InlineData(1.7e308)]
    public void Up_To_Double_The_Zoom_Text_Is_G6(double zoom)
    {
        Assert.Equal(zoom.ToString("G6", System.Globalization.CultureInfo.InvariantCulture),
            ZoomLog.Format(zoom, Math.Log10(zoom)));
        Assert.Equal(zoom.ToString("G6", System.Globalization.CultureInfo.InvariantCulture),
            ZoomLog.Format(zoom, double.NaN));
    }

    [Fact]
    public void Past_Double_The_Zoom_Text_Comes_From_The_Log()
    {
        Assert.Equal("2.5E+1000", ZoomLog.Format(double.MaxValue, 1000 + Math.Log10(2.5)));
        Assert.Equal("1E+500", ZoomLog.Format(double.MaxValue, 500));
        Assert.Equal("1.23457E+400", ZoomLog.Format(double.MaxValue, 400 + Math.Log10(1.234567)));
        // A mantissa that rounds up to 10 carries into the exponent.
        Assert.Equal("1E+1001", ZoomLog.Format(double.MaxValue, 1000 + Math.Log10(9.9999996)));
    }

    [Fact]
    public void The_Zoom_Box_Reads_Text_Past_Double()
    {
        Assert.True(ZoomLog.TryParse("1e500", out double z, out double l));
        Assert.Equal(double.MaxValue, z);
        Assert.Equal(500, l, 12);
        Assert.True(ZoomLog.TryParse(" 2.5E+1000 ", out _, out l));
        Assert.Equal(1000 + Math.Log10(2.5), l, 10);
        // Shown text reads back to the same log (6 digits).
        Assert.True(ZoomLog.TryParse(ZoomLog.FormatLog10(777.123456), out _, out l));
        Assert.Equal(777.123456, l, 5);
        // In range: exactly what double reads.
        Assert.True(ZoomLog.TryParse("4.65e64", out z, out l));
        Assert.Equal(double.Parse("4.65e64", System.Globalization.CultureInfo.InvariantCulture), z);
        Assert.Equal(Math.Log10(z), l);
        foreach (var bad in new[] { "0", "-1", "abc", "1e", "", "NaN", "-1e500", "1e99999999999" })
            Assert.False(ZoomLog.TryParse(bad, out _, out _), bad);
    }

    // ── view state ────────────────────────────────────────────────────────────

    [Fact]
    public void The_View_State_Carries_The_Log_Past_Double_Until_Zoom_Is_Written()
    {
        var s = new FractalViewState();
        s.SetZoomLog10(500);
        Assert.Equal(double.MaxValue, s.Zoom);
        Assert.Equal(500, s.ZoomLog10);
        Assert.True(s.ZoomBeyondDouble);

        s.Zoom = 1e10;   // a plain write wins
        Assert.Equal(10, s.ZoomLog10, 12);
        Assert.False(s.ZoomBeyondDouble);

        s.SetZoomLog10(50);
        Assert.Equal(Math.Pow(10, 50), s.Zoom);
        Assert.Equal(50, s.ZoomLog10);
    }

    // ── iterations ────────────────────────────────────────────────────────────

    [Fact]
    public void Iterations_From_The_Log_Are_The_Old_Formula_And_Hold_Past_Double()
    {
        foreach (var q in QualityPreset.All)
        {
            foreach (double z in new[] { 0.13, 1.0, 7.5, 1e6, 3.3e25, 1e100, 1e300 })
            {
                int old = z <= 1.0 ? q.IterBase
                    : Math.Clamp(q.IterBase + (int)(Math.Log10(z) * q.IterPerDecade), q.IterBase, q.IterMax);
                Assert.Equal(old, q.ComputeIterations(z));
                Assert.Equal(old, q.ComputeIterationsLog10(Math.Log10(z)));
            }
            long raw400 = q.IterBase + (long)(400.0 * q.IterPerDecade);
            Assert.Equal((int)Math.Clamp(raw400, q.IterBase, q.IterMax), q.ComputeIterationsLog10(400));
            Assert.Equal(q.IterMax, q.ComputeIterationsLog10(1e12));   // no int overflow
            Assert.Equal(q.IterBase, q.ComputeIterationsLog10(double.NaN));
        }
    }

    // ── camera, up to double: unchanged ───────────────────────────────────────

    [Fact]
    public void Up_To_Double_The_Camera_Is_The_Old_Double_And_OD_Math()
    {
        var s = new FractalViewState();
        s.SetCenter(new DeepComplex(new OD(-0.743643887037151, 1.1e-17, 0, 0, 0, 0, 0, 0), new OD(0.131825904205330, -3e-18, 0, 0, 0, 0, 0, 0)));
        s.Zoom = 3.7e40;
        var cam = new ViewCamera(s);
        Assert.False(cam.ExactNavigation);

        // Old wheel expression: anchor at old scale, zoom = clamp(z·f), anchor − offset·newScale.
        double px = 712, py = 233, f = 1.25;
        double s0 = 3.5 / (W * s.Zoom);
        var anchor = s.GetCenter().Translate((px - W * 0.5) * s0, (py - H * 0.5) * s0);
        double zNew = Math.Clamp(s.Zoom * f, 1e-3, 1e100);
        double s1 = 3.5 / (W * zNew);
        var expect = anchor.Translate(-(px - W * 0.5) * s1, -(py - H * 0.5) * s1);

        cam.ZoomAboutScreen(px, py, f, W, H, 1e-3, 1e100);
        Assert.Equal(zNew, s.Zoom);
        Assert.Equal(expect, s.GetCenter());

        // Old pan expression.
        var before = s.GetCenter();
        cam.TranslateByPixels(-17, 9, W, H);
        Assert.Equal(before.Translate(-17 * s1, 9 * s1), s.GetCenter());

        // Key zoom: the old clamp(z·f), centre untouched.
        var c = s.GetCenter();
        cam.ZoomBy(0.8, 1e-3, 1e100);
        Assert.Equal(Math.Clamp(zNew * 0.8, 1e-3, 1e100), s.Zoom);
        Assert.Equal(c, s.GetCenter());
    }

    // ── camera, past double ───────────────────────────────────────────────────

    // A view at 1e500 (log set exactly), centre 0.25 + 1e-480 i·… (beyond OD).
    private static FractalViewState Deep(out ViewCamera cam)
    {
        var s = new FractalViewState();
        Assert.True(ExactCentre.TryParse(
            "-0.10109636384562" + new string('3', 480) + "7",
            "0.95628651080914" + new string('8', 480) + "1", out var c, BigFloat.PrecisionForZoom(500)));
        s.SetCenterExact(c);
        s.SetZoomLog10(500);
        cam = new ViewCamera(s);
        return s;
    }

    // One pixel at log10 zoom zl, from decimal text: 3.5 / max(W,H) · 10^-zl (zl integer).
    private static BigFloat PixelFromText(int zl, int prec)
        => BigFloat.Parse($"3.5e-{zl}", prec) / BigFloat.FromDouble(Math.Max(W, H), prec);

    private static double InPixels(BigFloat a, BigFloat b, BigFloat pixel, int prec)
        => BigFloat.Div(BigFloat.Sub(a, b, prec), pixel, prec).ToDouble();

    [Fact]
    public void Past_Double_The_Wheel_Zooms_On_The_Log_And_Keeps_The_Cursor_Point()
    {
        var s = Deep(out var cam);
        Assert.True(cam.ExactNavigation);
        int prec = s.CenterExact!.Value.Precision;
        double px = 803, py = 151;
        var before = cam.ExactWorldFromScreen(px, py, W, H);

        cam.ZoomAboutScreen(px, py, 10, W, H, 1e-3, double.PositiveInfinity);
        Assert.Equal(501, s.ZoomLog10, 12);
        Assert.Equal(double.MaxValue, s.Zoom);
        Assert.NotNull(s.CenterExact);
        var after = cam.ExactWorldFromScreen(px, py, W, H);
        var pix = PixelFromText(501, prec + 64);
        Assert.InRange(InPixels(after.Re, before.Re, pix, prec + 64), -1e-6, 1e-6);
        Assert.InRange(InPixels(after.Im, before.Im, pix, prec + 64), -1e-6, 1e-6);

        // The clamp works on the log too.
        cam.ZoomAboutScreen(px, py, 1e9, W, H, 1e-3, double.MaxValue);
        Assert.Equal(ZoomLog.MaxDoubleLog10, s.ZoomLog10, 12);
    }

    [Fact]
    public void Past_Double_A_Pan_Moves_The_Exact_Centre_By_Its_Pixels()
    {
        var s = Deep(out var cam);
        var c0 = s.CenterExact!.Value;
        int prec = c0.Precision + 64;
        cam.TranslateByPixels(37, -12.5, W, H);
        var c1 = s.CenterExact!.Value;
        var pix = PixelFromText(500, prec);
        Assert.Equal(37, InPixels(c1.Re, c0.Re, pix, prec), 9);
        Assert.Equal(-12.5, InPixels(c1.Im, c0.Im, pix, prec), 9);
        Assert.Equal(500, s.ZoomLog10);   // a pan leaves the zoom
    }

    [Fact]
    public void Past_Double_A_Box_Zoom_Centres_The_Box_And_Key_Zoom_Leaves_The_Centre()
    {
        var s = Deep(out var cam);
        var mid = cam.ExactWorldFromScreen(120, 640, W, H);
        cam.BoxZoomToPoint(120, 640, 4, W, H, 1e-3, double.PositiveInfinity);
        Assert.Equal(mid, s.CenterExact);
        Assert.Equal(500 + Math.Log10(4), s.ZoomLog10, 12);

        var c = s.CenterExact;
        cam.ZoomBy(0.5, 1e-3, double.PositiveInfinity);
        Assert.Equal(500 + Math.Log10(2), s.ZoomLog10, 12);
        Assert.Equal(c, s.CenterExact);
    }

    [Fact]
    public void An_Exact_Centre_Survives_A_Shallow_Pan_And_Wheel_With_Its_Digits()
    {
        var s = new FractalViewState();
        string x = "0.25" + new string('0', 195) + "1234567";   // 0.25 + 1.234567e-199: past OD
        Assert.True(ExactCentre.TryParse(x, "-0.5", out var c));
        s.SetCenterExact(c);
        s.Zoom = 1e10;
        var cam = new ViewCamera(s);
        Assert.True(cam.ExactNavigation);

        cam.TranslateByPixels(3, 0, W, H);
        Assert.NotNull(s.CenterExact);
        cam.TranslateByPixels(-3, 0, W, H);
        Assert.Equal(c.Re, s.CenterExact!.Value.Re);   // there and back, exactly

        cam.ZoomAboutScreen(W * 0.5, H * 0.5, 2, W, H, 1e-3, 1e100);   // about the centre
        Assert.NotNull(s.CenterExact);
        Assert.Equal(c.Re, s.CenterExact!.Value.Re);
        Assert.Equal(2e10, s.Zoom);
    }

    // ── region JSON ───────────────────────────────────────────────────────────

    [Fact]
    public void A_Region_Carries_The_Log_Past_Double_And_Nothing_New_Below()
    {
        var s = new FractalViewState();
        s.SetZoomLog10(612.5);
        var r = new FractalRegion();
        r.CopyZoomFrom(s);
        string json = JsonSerializer.Serialize(r);
        Assert.Contains("\"ZoomLog10\":612.5", json);
        var back = JsonSerializer.Deserialize<FractalRegion>(json)!;
        var v = new FractalViewState();
        back.CopyZoomTo(v);
        Assert.Equal(612.5, v.ZoomLog10);
        Assert.Equal(612.5, back.ZoomLog10Value);

        s.Zoom = 1e50;
        r.CopyZoomFrom(s);
        json = JsonSerializer.Serialize(r);
        Assert.DoesNotContain("ZoomLog10", json);
        back = JsonSerializer.Deserialize<FractalRegion>(json)!;
        back.CopyZoomTo(v);
        Assert.Equal(1e50, v.Zoom);
        Assert.False(v.ZoomBeyondDouble);
    }

    [Fact]
    public void A_Damaged_Region_Log_Is_Dropped_And_Zoom_Rules()
    {
        // Not past double: dropped on load, Zoom rules.
        var back = JsonSerializer.Deserialize<FractalRegion>("{\"Zoom\":1e20,\"ZoomLog10\":200}")!;
        Assert.Null(back.ZoomLog10);
        Assert.Equal(1e20, back.Zoom);
        // Valid: Zoom is rewritten from it (saturated).
        back = JsonSerializer.Deserialize<FractalRegion>("{\"Zoom\":5,\"ZoomLog10\":400}")!;
        Assert.Equal(double.MaxValue, back.Zoom);
        Assert.Equal(400, back.ZoomLog10Value);
        // A non-positive zoom without a log leaves the view's zoom (region apply's old rule).
        var v = new FractalViewState { Zoom = 77 };
        new FractalRegion { Zoom = 0 }.CopyZoomTo(v);
        Assert.Equal(77, v.Zoom);
    }
}
