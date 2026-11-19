// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1347 — the server engine (HostFractalRenderEngine) built every render with a fresh
// FractalParameters, so a region rendered through the server or a cluster tile (the tiles
// carry the region by name / inline JSON to the same engine) ignored its per-family
// parameters: Julia constant, 3D camera, relief, lighting, the Mandelbrot BLA tolerance …
// Now the engine builds them from the region (lighting, relief, ApplyFamilyParams), as
// region recall and --batch do. Checked against PosterRenderer (what --batch renders) with
// the region's parameters applied the same way, on an inline-JSON region:
//   • a Julia region with a non-default c;
//   • a deep Mandelbrot region (the p448 centre at 1e22, 13,440 iterations: the DD band,
//     plain BLA, where ε is not capped) with BlaTolerance 1e-2;
//   • the video path's frame (RenderOneFrame) for both regions.
// Each with the precondition that stock parameters render a different image. Mutant (by
// hand): RegionParameters returning stock defaults — caught by all four; the video frame's
// Mandelbrot calculator without BlaTolerance — caught by the deep frame.

using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Threading;
using FracturingFog.Imaging;
using FracturingFog.Models;
using FracturingFog.Server.Protocol;
using FracturingFog.ServerHost;
using SkiaSharp;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1347ServerRegionParamsTests
{
    private const int W = 96, H = 64;

    private sealed class Log : ISessionLog
    {
        public void Info(string line) { }
        public void Warn(string line) { }
        public void Err(string line) { }
    }

    private static FractalRegion JuliaRegion()
    {
        var p = new FractalParameters { JuliaC = new Complex(-0.8, 0.156) };
        return new FractalRegion
        {
            Name = "S1347 Julia", FractalType = FractalType.Julia, CenterX = 0, CenterY = 0, Zoom = 1.2, Iterations = 400,
            Params = RegionFractalParams.Snapshot(FractalType.Julia, p),
        };
    }

    private static FractalRegion DeepRegion()
    {
        var p = new FractalParameters { BlaTolerance = 1e-2 };
        return new FractalRegion
        {
            Name = "S1347 p448", FractalType = FractalType.Mandelbrot,
            CenterX = -0.7145519151951202, CenterXLo = -2.0790192891565988e-17, CenterX2 = 1.1753166641509792e-33, CenterX3 = -8.102457136962753e-50,
            CenterY = -0.35402073332318235, CenterYLo = 2.5477179573089096e-17, CenterY2 = -6.072997541261718e-34, CenterY3 = -1.8911088656798612e-50,
            Zoom = 1e22, Iterations = 13440,
            Params = RegionFractalParams.Snapshot(FractalType.Mandelbrot, p),
        };
    }

    private static uint[] Pixels(string path)
    {
        using var bmp = SKBitmap.Decode(path);
        Assert.NotNull(bmp);
        return bmp.Pixels.Select(p => (uint)p).ToArray();
    }

    private static FractalParameters Applied(FractalRegion r)
    {
        var fp = new FractalParameters();
        r.ApplyFamilyParams(fp);
        return fp;
    }

    private static uint[] Poster(FractalRegion r, FractalParameters fp, string path)
    {
        var req = new PosterRequest
        {
            FractalType = r.FractalType, Width = W, Height = H,
            CenterX = r.CenterX, CenterXLo = r.CenterXLo, CenterX2 = r.CenterX2, CenterX3 = r.CenterX3,
            CenterY = r.CenterY, CenterYLo = r.CenterYLo, CenterY2 = r.CenterY2, CenterY3 = r.CenterY3,
            Zoom = r.Zoom, MaxIterations = r.Iterations,
            ColorMap = ColorPalette.GetPaletteByName("HSV"), Quality = QualityPreset.Standard,
            FractalParameters = fp, Path = path, Format = ImageFileFormat.Png, Watermark = "", SubText = "",
        };
        PosterRenderer.RenderToFile(req, CancellationToken.None);
        return Pixels(path);
    }

    [Theory]
    [InlineData("julia")]
    [InlineData("deep")]
    public void A_Region_Renders_On_The_Server_With_Its_Parameters(string which)
    {
        var region = which == "julia" ? JuliaRegion() : DeepRegion();
        string dir = Directory.CreateTempSubdirectory("ff1347-").FullName;
        try
        {
            var req = new RenderRequestDto
            {
                FractalType = region.FractalType.ToString(), Width = W, Height = H, SuppressDecorations = true,
                ThemeName = "HSV", QualityName = "Standard",
                RegionJson = JsonSerializer.Serialize(region),
            };
            var got = Pixels(new HostFractalRenderEngine().RenderAsync(req, dir, new Log(), CancellationToken.None)
                .GetAwaiter().GetResult().FilePath);
            var want = Poster(region, Applied(region), Path.Combine(dir, "want.png"));
            var stock = Poster(region, new FractalParameters(), Path.Combine(dir, "stock.png"));
            Assert.NotEqual(want, stock);   // precondition: the parameter changes the image
            Assert.True(got.SequenceEqual(want), $"{which}: the server render differs from the region's poster render"
                + (got.SequenceEqual(stock) ? " (it is the stock-parameter render)" : ""));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Theory]
    [InlineData("julia")]
    [InlineData("deep")]   // the frame's own Mandelbrot calculator (BlaTolerance)
    public void The_Video_Frame_Takes_The_Region_Parameters(string which)
    {
        var r = which == "julia" ? JuliaRegion() : DeepRegion();
        var theme = ColorPalette.GetPaletteByName("HSV");
        uint[] Frame(FractalParameters? fp) => HostFractalRenderEngine.RenderOneFrame(r.FractalType, W, H,
            r.CenterX, r.CenterXLo, r.CenterX2, r.CenterX3, r.CenterY, r.CenterYLo, r.CenterY2, r.CenterY3,
            r.Zoom, r.Iterations, theme, QualityPreset.Standard, CancellationToken.None, fp: fp);
        var got = Frame(HostFractalRenderEngine.RegionParameters(r));
        Assert.NotEqual(Frame(null), got);   // precondition: stock parameters differ
        Assert.Equal(Frame(Applied(r)), got);
    }
}
