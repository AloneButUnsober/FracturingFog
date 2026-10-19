// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1409 (3D-look QR, phase A of #1408) - the Bevel style.
//
// "It scans" is the independent invariant: ZXing.Net decodes the image back to
// the exact payload. The lighting checks measure the image itself (which side
// of a block is brighter, which side of it the floor is shadowed) against the
// light's direction, not the shader's own numbers.

using System;
using System.IO;
using System.Linq;
using FracturingFog;
using FracturingFog.Imaging;
using FracturingFog.Imaging.Qr;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1409QrBevelTests
{
    private const string Url = "https://github.com/AloneButUnsober/FracturingFog/issues/1409";

    /// <summary>Busy, colourful, full-range image (as in the #1305 tests).</summary>
    private static uint[] Busy(int w, int h)
    {
        var px = new uint[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double u = (x - w / 2.0) / w, v = (y - h / 2.0) / h;
                double r = Math.Sqrt(u * u + v * v), a = Math.Atan2(v, u);
                double hue = (a / (2 * Math.PI) + r * 3) % 1.0;
                if (hue < 0) hue += 1;
                double val = Math.Clamp(0.5 + 0.5 * Math.Sin(r * 140) * Math.Cos(a * 9), 0, 1);
                double c = val * 0.9, hp = hue * 6, x2 = c * (1 - Math.Abs(hp % 2 - 1)), m = val - c;
                (double rr, double gg, double bb) = (int)hp switch
                {
                    0 => (c, x2, 0.0), 1 => (x2, c, 0.0), 2 => (0.0, c, x2),
                    3 => (0.0, x2, c), 4 => (x2, 0.0, c), _ => (c, 0.0, x2),
                };
                px[y * w + x] = 0xFF000000u | (uint)((rr + m) * 255) << 16 | (uint)((gg + m) * 255) << 8 | (uint)((bb + m) * 255);
            }
        return px;
    }

    private static uint[] Flat(int w, int h, uint c) => Enumerable.Repeat(c, w * h).ToArray();

    // ── scans ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0.05, 0.2, QrErrorCorrection.H, false, 135.0, 30.0)]
    [InlineData(0.35, 0.2, QrErrorCorrection.H, false, 135.0, 30.0)]
    [InlineData(2.0, 0.2, QrErrorCorrection.H, false, 315.0, 5.0)]     // tall blocks, grazing light
    [InlineData(0.35, 0.45, QrErrorCorrection.M, false, 45.0, 60.0)]   // widest bevel
    [InlineData(0.35, 0.05, QrErrorCorrection.L, false, 270.0, 20.0)]  // narrowest bevel
    [InlineData(0.6, 0.2, QrErrorCorrection.H, true, 135.0, 30.0)]     // inverted
    public void BusyImage_Bevel_PassesEveryRobustnessPass(double height, double width, QrErrorCorrection ecc,
        bool invert, double az, double el)
    {
        const int W = 900, H = 640;
        var px = Busy(W, H);
        var opt = new QrCodeOptions
        {
            Payload = Url, ErrorCorrection = ecc, Style = QrStyle.Bevel, Invert = invert,
            BevelHeight = height, BevelWidth = width, BevelShadow = 1, BevelSpecular = 1,
        };
        var (_, layout) = QrExport.Apply(px, W, H, opt, QrBevelLighting.FromAngles(az, el));
        var result = QrVerifier.Verify(px, W, H, opt.Payload, layout.ModulePixels);
        Assert.True(result.Ok, result.Summary);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ThemeLights_Phong_And_Pbr_Scan(bool pbr)
    {
        // The #1305 busy-image size. (On this synthetic image the plain Steer
        // style itself fails the small pass at some square sizes, e.g. 640 and
        // 800, so a size sweep would test the image, not the bevel.)
        const int W = 900, H = 640;
        var px = Busy(W, H);
        var opt = new QrCodeOptions { Payload = Url, Style = QrStyle.Bevel, BevelHeight = 1 };
        FracturingFog.Interefaces.IColorMap map = pbr ? new CopperSheenPbr3D() : new PolarNight3DMap();
        var lit = QrBevelLighting.Resolve(opt, map);
        Assert.Equal("theme", lit.Source);
        var (_, layout) = QrExport.Apply(px, W, H, opt, lit);
        var v = QrVerifier.Verify(px, W, H, opt.Payload, layout.ModulePixels);
        Assert.True(v.Ok, v.Summary + " m=" + layout.ModulePixels);
    }

    // ── centres stay readable ──────────────────────────────────────────

    [Theory]
    [InlineData(0.35, 135.0, 30.0)]
    [InlineData(2.0, 200.0, 3.0)]
    public void ModuleCentres_AreInTheirCoreBand_AfterShading(double height, double az, double el)
    {
        const int W = 800, H = 800;
        var px = Busy(W, H);
        var opt = new QrCodeOptions { Payload = Url, Style = QrStyle.Bevel, BevelHeight = height, BevelShadow = 1, BevelSpecular = 1 };
        var code = QrExport.Encode(opt, 2);
        var L = QrCompositor.Composite(px, W, H, code, opt, QrBevelLighting.FromAngles(az, el));
        int m = L.ModulePixels, dot = Math.Max(1, (int)Math.Round(m * opt.DotFraction)), lo = (m - dot) / 2;
        for (int my = 0; my < code.Size; my++)
            for (int mx = 0; mx < code.Size; mx++)
                for (int dy = lo; dy < lo + dot; dy++)
                    for (int dx = lo; dx < lo + dot; dx++)
                    {
                        int luma = QrVerifier.LumaOf(px[(L.OriginY + my * m + dy) * W + L.OriginX + mx * m + dx]);
                        if (code.Dark[my, mx]) Assert.True(luma <= QrCompositor.DarkCoreMax, $"dark ({mx},{my}) centre luma {luma}");
                        else Assert.True(luma >= QrCompositor.LightCoreMin, $"light ({mx},{my}) centre luma {luma}");
                    }
    }

    // ── lighting follows the light ─────────────────────────────────────

    /// <summary>Over every isolated dark data module (light on both sides along
    /// x): mean luma of its left vs right bevel, and of the floor just left vs
    /// just right of it.</summary>
    private static (double BevelL, double BevelR, double FloorL, double FloorR) SidesAlongX(
        uint[] px, int w, QrLayout L, QrCode code)
    {
        int m = L.ModulePixels, band = Math.Max(1, m / 5);
        double bl = 0, br = 0, fl = 0, fr = 0; int n = 0;
        for (int my = 1; my < code.Size - 1; my++)
            for (int mx = 1; mx < code.Size - 1; mx++)
            {
                if (!code.Dark[my, mx] || code.IsFunction[my, mx] || code.Dark[my, mx - 1] || code.Dark[my, mx + 1]) continue;
                int x0 = L.OriginX + mx * m, y0 = L.OriginY + my * m;
                for (int dy = m / 3; dy < m - m / 3; dy++)
                    for (int k = 0; k < band; k++)
                    {
                        int row = (y0 + dy) * w;
                        bl += QrVerifier.LumaOf(px[row + x0 + k]);
                        br += QrVerifier.LumaOf(px[row + x0 + m - 1 - k]);
                        fl += QrVerifier.LumaOf(px[row + x0 - 1 - k]);
                        fr += QrVerifier.LumaOf(px[row + x0 + m + k]);
                        n++;
                    }
            }
        Assert.True(n > 0);
        return (bl / n, br / n, fl / n, fr / n);
    }

    [Theory]
    [InlineData(180.0)]   // light from the left
    [InlineData(0.0)]     // light from the right
    public void LitBevel_FacesTheLight_AndTheShadowFallsAway(double az)
    {
        const int W = 900, H = 900;
        var px = Flat(W, H, 0xFF808080u);
        var opt = new QrCodeOptions { Payload = Url, Style = QrStyle.Bevel, BevelHeight = 0.6, BevelShadow = 1, BevelSpecular = 0 };
        var code = QrExport.Encode(opt, 0);
        var L = QrCompositor.Composite(px, W, H, code, opt, QrBevelLighting.FromAngles(az, 25));
        var s = SidesAlongX(px, W, L, code);
        bool fromLeft = az == 180.0;
        double litSide = fromLeft ? s.BevelL : s.BevelR, darkSide = fromLeft ? s.BevelR : s.BevelL;
        double shadowFloor = fromLeft ? s.FloorR : s.FloorL, openFloor = fromLeft ? s.FloorL : s.FloorR;
        Assert.True(litSide > darkSide + 8, $"lit bevel {litSide:F1} vs far bevel {darkSide:F1}");
        Assert.True(shadowFloor < openFloor - 8, $"shadowed floor {shadowFloor:F1} vs open floor {openFloor:F1}");
    }

    [Fact]
    public void ThemeKeyLight_SetsTheDirection()
    {
        // Polar Night 3D: key from the upper RIGHT -> lit right bevels, floor shadow on the left.
        const int W = 900, H = 900;
        var px = Flat(W, H, 0xFF808080u);
        var opt = new QrCodeOptions { Payload = Url, Style = QrStyle.Bevel, BevelHeight = 0.6, BevelShadow = 1, BevelSpecular = 0 };
        var map = new PolarNight3DMap();
        var lit = QrBevelLighting.Resolve(opt, map);
        var key = map.ExportKeyLight;
        Assert.True(key.Lx > 0 && key.Ly > 0, "test premise: Polar Night's key is upper right");
        Assert.Equal(key.Lx, lit.Lights[0].X, 3);
        Assert.Equal(key.Ly, lit.Lights[0].Y, 3);
        var code = QrExport.Encode(opt, 0);
        var L = QrCompositor.Composite(px, W, H, code, opt, lit);
        var s = SidesAlongX(px, W, L, code);
        Assert.True(s.BevelR > s.BevelL, $"right bevel {s.BevelR:F1} vs left {s.BevelL:F1}");
        Assert.True(s.FloorL < s.FloorR, $"left floor {s.FloorL:F1} vs right {s.FloorR:F1}");
    }

    [Fact]
    public void NoThemeLights_OrExplicit_UsesTheAngles()
    {
        var opt = new QrCodeOptions { Payload = "x", Style = QrStyle.Bevel };
        var flat = QrBevelLighting.Resolve(opt, ColorPalette.BuiltIns[0]);
        Assert.StartsWith("default", flat.Source);
        Assert.Single(flat.Lights);
        Assert.True(flat.Lights[0].X < 0 && flat.Lights[0].Y > 0, "default light is upper left");

        var expl = QrBevelLighting.Resolve(opt with { BevelLightFromTheme = false, BevelAzimuthDeg = 0, BevelElevationDeg = 45 },
            new PolarNight3DMap());
        Assert.Equal("explicit", expl.Source);
        Assert.Equal(Math.Sqrt(0.5), expl.Lights[0].X, 6);
        Assert.Equal(0, expl.Lights[0].Y, 6);
    }

    [Fact]
    public void RotatedSave_TurnsTheThemeLightsClockwise()
    {
        var opt = new QrCodeOptions { Payload = "x", Style = QrStyle.Bevel };
        var map = new PolarNight3DMap();   // key upper right
        var up = QrBevelLighting.Resolve(opt, map).Lights[0];
        var turned = QrBevelLighting.Resolve(opt, map, rotatedClockwise: true).Lights[0];
        // Upper right turned 90° clockwise is lower right.
        Assert.Equal(up.Y, turned.X, 6);
        Assert.Equal(-up.X, turned.Y, 6);
        Assert.True(turned.X > 0 && turned.Y < 0);
    }

    [Fact]
    public void Chamfer_IsZeroOnUnsetPixels_AndGrowsInward()
    {
        // 5x5 set block inside a 7x7 field.
        var set = new bool[49];
        for (int y = 1; y < 6; y++) for (int x = 1; x < 6; x++) set[y * 7 + x] = true;
        var d = QrBevel.Chamfer(set, 7, 7);
        Assert.Equal(0f, d[0]);
        Assert.Equal(1f, d[1 * 7 + 1]);
        Assert.Equal(1f, d[1 * 7 + 3]);
        Assert.Equal(3f, d[3 * 7 + 3]);
    }

    // ── batch flags + Command builder ──────────────────────────────────

    [Fact]
    public void Builder_BevelFlags_RoundTripThroughTheParser()
    {
        var qr = new QrCodeOptions
        {
            Payload = "https://example.org/bevel",
            Style = QrStyle.Bevel,
            BevelHeight = 0.8, BevelWidth = 0.3, BevelSpecular = 0.25, BevelShadow = 0.9,
            BevelLightFromTheme = false, BevelAzimuthDeg = 210, BevelElevationDeg = 12.5,
        };
        var report = FracturingFog.Cli.BatchCommandBuilder.BuildWithReport(
            new FracturingFog.Cli.BatchCommandSnapshot { Qr = qr, CenterX = -0.5, Zoom = 1 });
        var args = report.Args.Select(a => a == "<OUTPUT.png>" ? "out.png" : a).ToArray();
        Assert.Contains("--qr-bevel-light", args);
        Assert.True(FracturingFog.Batch.BatchOptions.TryParse(args, 0, out var opts, out var err), err);
        Assert.Equal(qr, opts.BuildQrOptions());
    }

    [Fact]
    public void Builder_DefaultBevel_EmitsOnlyTheStyle_AndKnobsOnlyForBevel()
    {
        var bevel = FracturingFog.Cli.BatchCommandBuilder.BuildWithReport(new FracturingFog.Cli.BatchCommandSnapshot
            { Qr = new QrCodeOptions { Payload = "hi", Style = QrStyle.Bevel } }).Args;
        Assert.DoesNotContain(bevel, a => a.StartsWith("--qr-bevel", StringComparison.Ordinal));
        Assert.Contains("bevel", bevel);

        // Bevel knobs set but another style: nothing bevel is emitted.
        var steer = FracturingFog.Cli.BatchCommandBuilder.BuildWithReport(new FracturingFog.Cli.BatchCommandSnapshot
            { Qr = new QrCodeOptions { Payload = "hi", BevelHeight = 1.5, BevelLightFromTheme = false } }).Args;
        Assert.DoesNotContain(steer, a => a.StartsWith("--qr-bevel", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("--qr-bevel-light", "135")]
    [InlineData("--qr-bevel-light", "135,95")]
    [InlineData("--qr-bevel-light", "sun")]
    [InlineData("--qr-bevel-height", "3")]
    [InlineData("--qr-bevel-width", "0.5")]
    [InlineData("--qr-bevel-shadow", "1.5")]
    [InlineData("--qr-bevel-specular", "-0.1")]
    public void Parser_RejectsBadBevelValues(string flag, string value)
    {
        var args = new[] { "--x", "0", "--y", "0", "--zoom", "1", "--out", "o.png", "--qr-payload", "x", "--qr-style", "bevel", flag, value };
        Assert.False(FracturingFog.Batch.BatchOptions.TryParse(args, 0, out _, out var err));
        Assert.Contains(flag, err);
    }

    [Theory]
    [InlineData("theme", true, 135.0, 30.0)]
    [InlineData(" Theme ", true, 135.0, 30.0)]
    [InlineData("-90, 45", false, 270.0, 45.0)]
    [InlineData("400,1", false, 40.0, 1.0)]
    public void BevelLightSpec_Parses(string spec, bool theme, double az, double el)
    {
        Assert.True(QrCodeOptions.TryParseBevelLight(spec, out bool t, out double a, out double e));
        Assert.Equal(theme, t);
        Assert.Equal(az, a, 6);
        Assert.Equal(el, e, 6);
    }

    // ── Control Center ─────────────────────────────────────────────────

    [Fact]
    public void ViewModel_BevelStyle_ShowsItsControls_AndMapsThem()
    {
        var vm = new FracturingFog.UI.Avalonia.ViewModels.QrCodeViewModel { Payload = "x" };
        Assert.False(vm.IsBevel);
        vm.StyleIndex = FracturingFog.UI.Avalonia.ViewModels.QrCodeViewModel.BevelStyleIndex;
        Assert.True(vm.IsBevel);
        Assert.Equal("Bevel (3D)", vm.StyleChoices[vm.StyleIndex]);
        vm.BevelHeight = 0.7; vm.BevelWidth = 0.25; vm.BevelSpecular = 0.3; vm.BevelShadow = 0.6;
        Assert.False(vm.BevelLightExplicit);
        vm.BevelLightFromTheme = false;
        Assert.True(vm.BevelLightExplicit);
        vm.BevelAzimuth = -45; vm.BevelElevation = 120;

        var o = vm.Options!;
        Assert.Equal(QrStyle.Bevel, o.Style);
        Assert.Equal(0.7, o.BevelHeight);
        Assert.Equal(0.25, o.BevelWidth);
        Assert.Equal(0.3, o.BevelSpecular);
        Assert.Equal(0.6, o.BevelShadow);
        Assert.False(o.BevelLightFromTheme);
        Assert.Equal(315, o.BevelAzimuthDeg);
        Assert.Equal(89, o.BevelElevationDeg);
    }

    // ── end to end: a real Mandelbrot poster with a 3D theme, saved PNG ──

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MandelbrotPoster_3DTheme_SavedFileScans(bool pbr)
    {
        string path = Path.Combine(Path.GetTempPath(), "ff-qr-bevel-" + Guid.NewGuid().ToString("N") + ".png");
        var req = new PosterRequest
        {
            FractalType = FractalType.Mandelbrot,
            CenterX = -0.7453, CenterY = 0.1127,
            Zoom = 120,
            MaxIterations = 600,
            Width = 720, Height = 720,
            ColorMap = pbr ? new CopperSheenPbr3D() : (FracturingFog.Interefaces.IColorMap)new PolarNight3DMap(),
            Quality = QualityPreset.Standard,
            FractalParameters = new FractalParameters(),
            Path = path,
            Format = ImageFileFormat.Png,
            Watermark = "Seahorse Valley",
            SubText = "Fracturing Fog test",
            Qr = new QrCodeOptions { Payload = "https://example.org/fractal-qr", Style = QrStyle.Bevel },
        };
        try
        {
            var result = PosterRenderer.RenderToFile(req, default);
            Assert.True(result.Qr!.Verify!.Ok, result.Qr.Verify.Summary);
            var px = QrVerifier.LoadBgra(path, out int w, out int h);
            Assert.Equal("https://example.org/fractal-qr", QrVerifier.Decode(px!, w, h));
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }
}
