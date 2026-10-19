// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1410 (3D-look QR, phase B of #1408) - the QR extruded into the Relief 3D
// raymarch.
//
// "It scans" is the independent invariant: ZXing.Net decodes the SAVED,
// raymarched file back to the exact payload. CPU/GPU parity uses the existing
// relief pattern: the GPU kernel's CPU twin, plugged in at the kernel seam,
// against the CPU trace (an independent implementation), with the QR blocks
// injected into the field both consume.

using System;
using System.IO;
using System.Linq;
using FracturingFog;
using FracturingFog.Imaging;
using FracturingFog.Imaging.Qr;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1410QrReliefTests(ITestOutputHelper output)
{
    private const string Url = "https://example.org/fractal-qr";

    private static FractalParameters ReliefParams(double elevation = 45) => new()
    {
        Relief2DEnabled = true,
        Relief2DRaymarch = true,
        Relief2DCameraElevationDeg = elevation,
    };

    private static PosterRequest Poster(string path, QrCodeOptions qr, FractalParameters p, int side = 720) => new()
    {
        FractalType = FractalType.Mandelbrot,
        CenterX = -0.7453, CenterY = 0.1127,
        Zoom = 120,
        MaxIterations = 600,
        Width = side, Height = side,
        ColorMap = new PolarNight3DMap(),
        Quality = QualityPreset.Standard,
        FractalParameters = p,
        Path = path,
        Format = ImageFileFormat.Png,
        Qr = qr,
    };

    private static string TempPng() => Path.Combine(Path.GetTempPath(), "ff-qr-relief-" + Guid.NewGuid().ToString("N") + ".png");

    // ── end to end: a Mandelbrot relief poster, saved PNG ───────────────

    [Theory]
    [InlineData(45.0, 0.5)]   // the relief default camera: raised to the 60° minimum
    [InlineData(60.0, 1.0)]
    public void MandelbrotRelief_ExtrudedQr_SavedFileScans(double elevation, double blocks)
    {
        string path = TempPng();
        try
        {
            var qr = new QrCodeOptions { Payload = Url, Relief = true, ReliefHeight = blocks };
            var result = PosterRenderer.RenderToFile(Poster(path, qr, ReliefParams(elevation)), default);
            Assert.True(result.Qr!.Verify!.Ok, result.Qr.Verify.Summary);
            var px = QrVerifier.LoadBgra(path, out int w, out int h);
            Assert.Equal(Url, QrVerifier.Decode(px!, w, h));
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void LowestScanningElevation_50Degrees_Scans_WhenTheMinimumIsLowered()
    {
        // Measured: 50° (40° tilt) scans at 0.5 and 1 module blocks; 40° does not
        // (ZXing's detector rejects the trapezoid). The 60° default keeps a margin.
        string path = TempPng();
        try
        {
            var qr = new QrCodeOptions { Payload = Url, Relief = true, ReliefMinElevationDeg = 5 };
            var result = PosterRenderer.RenderToFile(Poster(path, qr, ReliefParams(50)), default);
            Assert.True(result.Qr!.Verify!.Ok, result.Qr.Verify.Summary);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void Extruded_IsReallyDifferentFromTheFlatQr_AndPerspectiveShows()
    {
        string a = TempPng(), b = TempPng();
        try
        {
            var flat = PosterRenderer.RenderToFile(Poster(a, new QrCodeOptions { Payload = Url }, ReliefParams()), default);
            var relief = PosterRenderer.RenderToFile(Poster(b, new QrCodeOptions { Payload = Url, Relief = true }, ReliefParams()), default);
            Assert.True(flat.Qr!.Verify!.Ok && relief.Qr!.Verify!.Ok);
            var pa = QrVerifier.LoadBgra(a, out int w, out int h)!;
            var pb = QrVerifier.LoadBgra(b, out _, out _)!;
            int differ = 0;
            for (int i = 0; i < pa.Length; i++)
                if (Math.Abs(QrVerifier.LumaOf(pa[i]) - QrVerifier.LumaOf(pb[i])) > 40) differ++;
            output.WriteLine($"pixels differing by > 40 luma: {100.0 * differ / pa.Length:F1}%");
            Assert.True(differ > pa.Length / 5, "the extruded code looks like the flat one");
            // Flat: the code is laid out on the image grid. Extruded: seen at an
            // angle, so its on-screen layout is not the flat one.
            Assert.NotEqual(flat.Qr.Layout, relief.Qr.Layout);
        }
        finally { try { File.Delete(a); File.Delete(b); } catch { } }
    }

    [Fact]
    public void ReliefRaymarchOff_FallsBackToTheFlatQr()
    {
        string path = TempPng();
        try
        {
            var p = new FractalParameters();   // no relief
            var qr = new QrCodeOptions { Payload = Url, Relief = true };
            var result = PosterRenderer.RenderToFile(Poster(path, qr, p, 480), default);
            Assert.True(result.Qr!.Verify!.Ok, result.Qr.Verify.Summary);
            // The flat layout: centred on the image grid.
            Assert.Equal(QrCompositor.Layout(480, 480, result.Qr.Code.Size, 0), result.Qr.Layout);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    // ── the injected field ─────────────────────────────────────────────

    private static (uint[] Albedo, float[] Field) Scene(int w, int h)
    {
        var albedo = new uint[w * h];
        var field = new float[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double u = (x + 0.5) / w - 0.5, v = (y + 0.5) / h - 0.5;
                field[y * w + x] = (float)(0.5 + 0.4 * Math.Sin(u * 17) * Math.Cos(v * 13));
                albedo[y * w + x] = 0xFF000000u | (uint)(100 + 90 * Math.Sin(u * 9)) << 16 | (uint)(110 + 80 * Math.Cos(v * 7)) << 8 | 0x70u;
            }
        return (albedo, field);
    }

    [Fact]
    public void Inject_FlattensTheTerrainUnderTheCode_AndRaisesDarkModules()
    {
        const int W = 240, H = 240;
        var (albedo, field) = Scene(W, H);
        // 4 px modules: the code + quiet zone + blend ring leave the corners untouched.
        var opt = new QrCodeOptions { Payload = "1410", Relief = true, ReliefTerrain = 0, ReliefHeight = 0.5, ModulePixels = 4 };
        var plan = QrReliefPlan.Prepare(albedo, W, H, opt);
        var hb = (float[])field.Clone();
        const double sy = 0.05;
        plan.Inject(hb, W, H, sy, 1.0);

        var L = plan.Layout;
        int m = L.ModulePixels;
        double ext = 0.5 * ((double)m / H) / sy;
        float? floor = null;
        for (int my = 0; my < plan.Code.Size; my++)
            for (int mx = 0; mx < plan.Code.Size; mx++)
            {
                int cx = L.OriginX + mx * m + m / 2, cy = L.OriginY + my * m + m / 2;
                float hv = hb[cy * W + cx];
                if (!plan.Code.Dark[my, mx]) { floor ??= hv; Assert.Equal(floor.Value, hv, 4); }
            }
        for (int my = 0; my < plan.Code.Size; my++)
            for (int mx = 0; mx < plan.Code.Size; mx++)
            {
                if (!plan.Code.Dark[my, mx]) continue;
                int cx = L.OriginX + mx * m + m / 2, cy = L.OriginY + my * m + m / 2;
                // A lone dark module's centre may sit on the bevel ramp's shoulder;
                // it is at least most of the way up, never above the full height.
                double rise = hb[cy * W + cx] - floor!.Value;
                Assert.InRange(rise, 0.6 * ext, ext + 1e-4);
            }
        // Far outside the code the terrain is untouched.
        Assert.Equal(field[2 * W + 2], hb[2 * W + 2]);
    }

    private sealed class TwinKernel : IReliefRaymarchKernel
    {
        public int Calls;
        public float MaxSeen;
        public void Run(in ReliefUniforms u, float[] hbuf, byte[]? keep, uint[] albedo, uint[] dst,
            float[]? aovNormalXyz = null, float[]? aovDepth = null)
        {
            Calls++;
            MaxSeen = hbuf.Max();
            ReliefRaymarchGpu.RenderCpuMirror(in u, hbuf, keep, albedo, dst, out _, out _, aovNormalXyz, aovDepth);
        }
        public void Dispose() { }
    }

    [Fact]
    public void GpuTwin_And_CpuTrace_RenderTheSameExtrudedCode()
    {
        const int W = 200, H = 200;
        var (albedo, field) = Scene(W, H);
        var opt = new QrCodeOptions { Payload = "1410", Relief = true, ReliefHeight = 0.8 };
        var plan = QrReliefPlan.Prepare(albedo, W, H, opt);
        var fx = LightingFxData.CreateDefault();
        fx.ShadowSteps = 32; fx.ShadowLightMask = 0x1; fx.ShadowSoftK = 8.0;
        fx.AoSamples = 5; fx.AoStrength = 0.8;
        var p = new FractalParameters
        {
            Relief2DEnabled = true, Relief2DRaymarch = true, Relief2DGpuRaymarch = true,
            Relief2DCameraElevationDeg = 60, Relief2DAutoShade = false, Lighting = fx,
        };

        float injectedMax = 0;
        void Inject(float[] hb, int hw, int hh, double sy, double aspect)
        {
            plan.Inject(hb, hw, hh, sy, aspect);
            injectedMax = hb.Max();
        }

        var twin = new TwinKernel();
        var gpu = new uint[W * H];
        HeightfieldRaymarch2D.Render(albedo, field, W, H, W, H, p, gpu, out _, twin, heightInjection: Inject);
        Assert.True(twin.Calls > 0, "the frame did not take the GPU branch");
        Assert.Equal(injectedMax, twin.MaxSeen);   // the kernel got the edited field

        var cpu = new uint[W * H];
        HeightfieldRaymarch2D.Render(albedo, field, W, H, W, H, p, cpu, out _, null, heightInjection: Inject);

        long s = 0;
        for (int i = 0; i < gpu.Length; i++)
            for (int k = 0; k < 24; k += 8)
                s += Math.Abs((int)((gpu[i] >> k) & 0xFF) - (int)((cpu[i] >> k) & 0xFF));
        double drift = s / (gpu.Length * 3.0);
        output.WriteLine($"twin vs CPU trace, extruded QR: mean drift {drift:F2}");
        Assert.True(drift < 1.5, $"twin vs CPU trace mean drift {drift:F2}");

        // Non-vacuity: the blocks change the frame.
        var plain = new uint[W * H];
        HeightfieldRaymarch2D.Render(albedo, field, W, H, W, H, p, plain, out _, null);
        Assert.False(plain.SequenceEqual(cpu));
    }

    [Fact]
    public void NoInjection_IsByteIdentical()
    {
        const int W = 120, H = 90;
        var (albedo, field) = Scene(W, H);
        var p = new FractalParameters { Relief2DEnabled = true, Relief2DRaymarch = true };
        var a = new uint[W * H];
        var b = new uint[W * H];
        HeightfieldRaymarch2D.Render(albedo, field, W, H, W, H, p, a, out _, null);
        HeightfieldRaymarch2D.Render(albedo, field, W, H, W, H, p, b, out _, null, heightInjection: null);
        Assert.Equal(a, b);
    }

    [Fact]
    public void HitPoint_InvertsProject()
    {
        var p = new FractalParameters { Relief2DCameraElevationDeg = 55, Relief2DCameraAzimuthDeg = 30 };
        foreach (bool ortho in new[] { false, true })
        {
            p.Relief2DCameraOrthographic = ortho;
            var cam = HeightfieldRaymarch2D.BuildObliqueCamera(320, 240, 320 / 240.0, 0.1, 1.0, p);
            var (x, y, z) = HeightfieldRaymarch2D.HitPoint(in cam, 320, 240, 320 / 240.0, 101, 77, 2.0);
            var (sx, sy) = QrReliefPlan.Project(in cam, 320, 240, 320 / 240.0, x, y, z);
            Assert.Equal(101.5, sx, 6);
            Assert.Equal(77.5, sy, 6);
        }
    }

    [Fact]
    public void CappedElevation_RaisesALowCamera_Only()
    {
        var opt = new QrCodeOptions { Payload = "x" };
        Assert.Equal(60, QrReliefPlan.CappedElevation(45, opt));
        Assert.Equal(75, QrReliefPlan.CappedElevation(75, opt));
        Assert.Equal(50, QrReliefPlan.CappedElevation(30, opt with { ReliefMinElevationDeg = 50 }));
    }

    // ── batch flags + Command builder ──────────────────────────────────

    [Fact]
    public void Builder_ReliefFlags_RoundTripThroughTheParser()
    {
        var qr = new QrCodeOptions
        {
            Payload = "https://example.org/relief",
            Relief = true, ReliefHeight = 1.25, ReliefTerrain = 0.4, ReliefMinElevationDeg = 55,
        };
        var report = FracturingFog.Cli.BatchCommandBuilder.BuildWithReport(
            new FracturingFog.Cli.BatchCommandSnapshot { Qr = qr, CenterX = -0.5, Zoom = 1 });
        var args = report.Args.Select(a => a == "<OUTPUT.png>" ? "out.png" : a).ToArray();
        Assert.Contains("--qr-relief", args);
        Assert.True(FracturingFog.Batch.BatchOptions.TryParse(args, 0, out var opts, out var err), err);
        Assert.Equal(qr, opts.BuildQrOptions());
    }

    [Fact]
    public void Builder_DefaultRelief_EmitsOnlyTheSwitch()
    {
        var args = FracturingFog.Cli.BatchCommandBuilder.BuildWithReport(new FracturingFog.Cli.BatchCommandSnapshot
            { Qr = new QrCodeOptions { Payload = "hi", Relief = true } }).Args;
        Assert.Single(args, a => a.StartsWith("--qr-relief", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("--qr-relief-height", "4")]
    [InlineData("--qr-relief-terrain", "1.2")]
    [InlineData("--qr-relief-min-elevation", "2")]
    public void Parser_RejectsBadReliefValues(string flag, string value)
    {
        var args = new[] { "--x", "0", "--y", "0", "--zoom", "1", "--out", "o.png", "--qr-payload", "x", "--qr-relief", flag, value };
        Assert.False(FracturingFog.Batch.BatchOptions.TryParse(args, 0, out _, out var err));
        Assert.Contains(flag, err);
    }

    [Fact]
    public void Parser_QrRelief_DoesNotTurnTheRaymarchOn()
    {
        // Like the Control Center export, the code is extruded only in a Relief 3D
        // raymarch view, so a seeded command reproduces the export exactly.
        var args = new[] { "--x", "0", "--y", "0", "--zoom", "1", "--out", "o.png", "--qr-payload", "x", "--qr-relief" };
        Assert.True(FracturingFog.Batch.BatchOptions.TryParse(args, 0, out var opts, out var err), err);
        Assert.True(opts.BuildQrOptions()!.Relief);
        Assert.False(opts.ReliefRaymarch);
    }

    // ── Control Center ─────────────────────────────────────────────────

    [Fact]
    public void ViewModel_MapsTheReliefControls()
    {
        var vm = new FracturingFog.UI.Avalonia.ViewModels.QrCodeViewModel { Payload = "x" };
        Assert.False(vm.Options!.Relief);
        vm.Relief = true; vm.ReliefHeight = 1.5; vm.ReliefTerrain = 0.3; vm.ReliefMinElevation = 2;
        var o = vm.Options!;
        Assert.True(o.Relief);
        Assert.Equal(1.5, o.ReliefHeight);
        Assert.Equal(0.3, o.ReliefTerrain);
        Assert.Equal(QrCodeOptions.MinReliefElevation, o.ReliefMinElevationDeg);
    }
}
