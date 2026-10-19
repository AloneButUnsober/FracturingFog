// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1411 (3D-look QR, phase C of #1408) - the 3D-printable QR tile.
//
// Two independent invariants, both on the FILE that was written (read back with
// the existing STL / 3MF readers, not the in-memory tile):
//   - MeshValidator: closed 2-manifold, consistently and outwardly wound;
//   - ZXing decodes a top-down raster of the read-back mesh to the payload (by
//     height for STL, as a two-material print shows it; by colour for 3MF).

using System;
using System.IO;
using System.Linq;
using FracturingFog;
using FracturingFog.Export;
using FracturingFog.Imaging;
using FracturingFog.Imaging.Qr;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1411QrMeshTests(ITestOutputHelper output)
{
    private const string Url = "https://example.org/fractal-qr";

    private static uint[] Render(int side, out int w, out int h)
    {
        var req = new PosterRequest
        {
            FractalType = FractalType.Mandelbrot,
            CenterX = -0.7453, CenterY = 0.1127, Zoom = 120, MaxIterations = 600,
            Width = side, Height = side,
            ColorMap = ColorPalette.BuiltIns[0],
            Quality = QualityPreset.Standard,
            FractalParameters = new FractalParameters(),
            Path = "", Format = ImageFileFormat.Png,
        };
        return PosterRenderer.RenderDisplayPixels(req, default, out w, out h);
    }

    private static string Temp(string ext) => Path.Combine(Path.GetTempPath(), "ff-qr-tile-" + Guid.NewGuid().ToString("N") + ext);

    [Fact]
    public void StlTile_ReadBack_IsAClosedOutwardSolid_AndScansByHeight()
    {
        var px = Render(600, out int w, out int h);
        var opt = new QrCodeOptions { Payload = Url };
        string path = Temp(".stl");
        try
        {
            var r = QrMesh.Export(px, w, h, opt, path);
            output.WriteLine(r.Summary);
            Assert.True(r.Report.IsClosedManifold, r.Report.Summary());
            Assert.True(r.Scan.Ok, r.Scan.Summary);

            var (pos, tris) = StlMeshReader.ReadBinary(path);
            var rep = MeshValidator.Validate(pos, tris, 1e-4);
            Assert.True(rep.IsClosedManifold, rep.Summary());
            Assert.True(rep.SignedVolume > 0, "the tile is wound inside-out");
            // Z-up print frame, millimetres: 60 x 60 footprint, base + module tall.
            Assert.Equal(60, rep.SizeX, 3);
            Assert.Equal(60, rep.SizeY, 3);
            Assert.Equal(3, rep.SizeZ, 3);
            Assert.Equal(0, rep.MinZ, 6);

            // Independent scan of the FILE: back to Y-up and rasterise by height.
            var yUp = pos.Select(p => (p.X, p.Z, 60 - p.Y)).ToList();
            int side = 49 * 8;   // generous: the raster sizes itself from the tile
            var img = QrMesh.RenderTopDown(yUp, null, tris, 60, side, 2.5, out int s);
            Assert.Equal(Url, QrVerifier.Decode(img, s, s));
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void ThreeMfTile_ReadBack_IsMillimetres_Closed_AndScansByColour()
    {
        var px = Render(600, out int w, out int h);
        var opt = new QrCodeOptions { Payload = Url, MeshSizeMm = 80, MeshHeightMm = 1.5, MeshTerrainMm = 3 };
        string path = Temp(".3mf");
        try
        {
            var r = QrMesh.Export(px, w, h, opt, path);
            output.WriteLine(r.Summary);
            Assert.True(r.Report.IsClosedManifold, r.Report.Summary());
            Assert.True(r.Scan.Ok, r.Scan.Summary);

            var (pos, cols, tris, unit) = ThreeMfMeshReader.Read(path);
            Assert.Equal("millimeter", unit);
            var rep = MeshValidator.Validate(pos, tris, 1e-4);
            Assert.True(rep.IsClosedManifold, rep.Summary());
            Assert.True(rep.SignedVolume > 0);
            Assert.Equal(80, rep.SizeX, 3);
            Assert.True(cols.Count > 0);
            Assert.True(cols.Distinct().Count() > 2, "the 3MF carries no fractal colour");
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void ObjTile_IsWritten_WithVertexColour()
    {
        var px = Render(400, out int w, out int h);
        string path = Temp(".obj");
        try
        {
            var r = QrMesh.Export(px, w, h, new QrCodeOptions { Payload = "1411" }, path);
            Assert.True(r.Report.IsClosedManifold && r.Scan.Ok, r.Summary);
            var lines = File.ReadLines(path).ToList();
            var v = lines.First(l => l.StartsWith("v ", StringComparison.Ordinal)).Split(' ');
            Assert.Equal(7, v.Length);   // v x y z r g b
            Assert.Equal(r.Triangles, lines.Count(l => l.StartsWith("f ", StringComparison.Ordinal)));
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void StlWithTallTerrain_HonestlyFailsTheHeightScan()
    {
        // Terrain taller than a module: geometry alone cannot tell modules from
        // terrain, so the STL (no colour) check must say it does not scan.
        var px = Render(400, out int w, out int h);
        string path = Temp(".stl");
        try
        {
            var r = QrMesh.Export(px, w, h, new QrCodeOptions { Payload = Url, MeshTerrainMm = 3 }, path);
            Assert.True(r.Report.IsClosedManifold, r.Report.Summary());
            Assert.False(r.Scan.Ok);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void RemoveSaddles_LeavesNoDiagonalOnlyContact()
    {
        // 3x3 cells: a checkerboard 2x2 in the top-left (diagonal-only contact).
        const int n = 3;
        var hgt = new double[] { 3, 2, 2, 2, 3, 2, 2, 2, 2 };
        int raised = QrMesh.RemoveSaddles(hgt, n);
        Assert.True(raised >= 1);
        for (int j = 1; j < n; j++)
            for (int i = 1; i < n; i++)
            {
                double a = hgt[(j - 1) * n + i - 1], b = hgt[(j - 1) * n + i], c = hgt[j * n + i], d = hgt[j * n + i - 1];
                Assert.False(Math.Min(a, c) > Math.Max(b, d) || Math.Min(b, d) > Math.Max(a, c), $"saddle left at corner ({i},{j})");
            }
        Assert.Equal(0, QrMesh.RemoveSaddles(hgt, n));   // idempotent
    }

    [Fact]
    public void Poster_KeepPreQrPixels_ReturnsTheBufferBeforeTheQr()
    {
        string path = Temp(".png");
        try
        {
            var req = new PosterRequest
            {
                FractalType = FractalType.Mandelbrot, CenterX = -0.5, Zoom = 0.8, MaxIterations = 200,
                Width = 300, Height = 300, ColorMap = ColorPalette.BuiltIns[0], Quality = QualityPreset.Standard,
                FractalParameters = new FractalParameters(), Path = path, Format = ImageFileFormat.Png,
                Qr = new QrCodeOptions { Payload = "x" }, KeepPreQrPixels = true,
            };
            var result = PosterRenderer.RenderToFile(req, default);
            Assert.NotNull(result.PreQrPixels);
            Assert.Equal(300 * 300, result.PreQrPixels!.Length);
            // Without a QR in it, it does not decode.
            Assert.Null(QrVerifier.Decode(result.PreQrPixels, 300, 300));
            Assert.Null(PosterRenderer.RenderToFile(req with { KeepPreQrPixels = false }, default).PreQrPixels);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    // ── batch flags + Command builder ──────────────────────────────────

    [Fact]
    public void Builder_MeshFlags_RoundTripThroughTheParser()
    {
        var qr = new QrCodeOptions { Payload = "https://example.org/tile", MeshSizeMm = 75, MeshBaseMm = 3, MeshHeightMm = 1.6, MeshTerrainMm = 2.5 };
        var report = FracturingFog.Cli.BatchCommandBuilder.BuildWithReport(
            new FracturingFog.Cli.BatchCommandSnapshot { Qr = qr, QrMeshPath = "tile.3mf", CenterX = -0.5, Zoom = 1 });
        var args = report.Args.Select(a => a == "<OUTPUT.png>" ? "out.png" : a).ToArray();
        Assert.Contains("--qr-mesh", args);
        Assert.True(FracturingFog.Batch.BatchOptions.TryParse(args, 0, out var opts, out var err), err);
        Assert.Equal("tile.3mf", opts.QrMesh);
        Assert.Equal(qr, opts.BuildQrOptions());
    }

    [Fact]
    public void Builder_LiveSeed_EmitsNoMesh_AndDefaultsEmitOnlyThePath()
    {
        var qr = new QrCodeOptions { Payload = "hi", MeshSizeMm = 90 };
        var seed = FracturingFog.Cli.BatchCommandBuilder.BuildWithReport(new FracturingFog.Cli.BatchCommandSnapshot { Qr = qr }).Args;
        Assert.DoesNotContain(seed, a => a.StartsWith("--qr-mesh", StringComparison.Ordinal));
        var withPath = FracturingFog.Cli.BatchCommandBuilder.BuildWithReport(new FracturingFog.Cli.BatchCommandSnapshot
            { Qr = new QrCodeOptions { Payload = "hi" }, QrMeshPath = "t.stl" }).Args;
        Assert.Single(withPath, a => a.StartsWith("--qr-mesh", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("--qr-mesh", "tile.ply")]
    [InlineData("--qr-mesh-size", "5")]
    [InlineData("--qr-mesh-base", "0.1")]
    [InlineData("--qr-mesh-height", "11")]
    [InlineData("--qr-mesh-terrain", "-1")]
    public void Parser_RejectsBadMeshValues(string flag, string value)
    {
        var args = new System.Collections.Generic.List<string> { "--x", "0", "--y", "0", "--zoom", "1", "--out", "o.png", "--qr-payload", "x" };
        if (flag != "--qr-mesh") { args.Add("--qr-mesh"); args.Add("t.stl"); }
        args.Add(flag); args.Add(value);
        Assert.False(FracturingFog.Batch.BatchOptions.TryParse(args.ToArray(), 0, out _, out var err));
        Assert.Contains(flag, err);
    }

    // ── Control Center ─────────────────────────────────────────────────

    [Fact]
    public void ViewModel_MapsTheMeshControls_AndGatesTheButton()
    {
        var vm = new FracturingFog.UI.Avalonia.ViewModels.QrCodeViewModel { Payload = "x" };
        vm.MeshSize = 1000; vm.MeshBase = 2.5; vm.MeshHeight = 0.6; vm.MeshTerrain = 1.25;
        var o = vm.Options!;
        Assert.Equal(QrCodeOptions.MaxMeshSizeMm, o.MeshSizeMm);
        Assert.Equal(2.5, o.MeshBaseMm);
        Assert.Equal(0.6, o.MeshHeightMm);
        Assert.Equal(1.25, o.MeshTerrainMm);

        bool can = true;
        using var sub = vm.MeshExportCommand.CanExecute.Subscribe(v => can = v);
        Assert.False(can);   // no host hook yet
        vm.MeshExportRequested = (_, _) => System.Threading.Tasks.Task.FromResult<FracturingFog.UI.Avalonia.ViewModels.QrExportOutcome?>(null);
        vm.HooksAttached();
        Assert.True(can);
    }
}
