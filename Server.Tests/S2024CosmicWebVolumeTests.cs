// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #2024 — Cosmic Web S4: the CIC density grid and its emission-absorption raymarch (CPU reference,
// D3D11 / Vulkan kernels). Independent checks:
//   • the CIC deposit conserves mass exactly (grid total = particles x MassOne), including particles
//     on the box edges, and puts a particle's mass where the trilinear weights say (all in one cell
//     at its centre, half and half midway, an eighth each at a cell corner);
//   • the Gaussian smoothing keeps the total;
//   • the trilinear sample returns a cell's value at its centre and wraps periodically;
//   • a uniform slab: the raymarch's colour and transmittance match the closed form
//     B v^(1/gamma) (1 - e^(-k v L)) + bg e^(-k v L) for the chord length L;
//   • Vulkan matches the CPU per pixel within 2 units (when a Vulkan device is present; D3D11 is
//     checked by --cwvolprobe, as the test project cannot load the Windows-only backend);
//   • the calculator: volume frames differ from the splat, carry a depth AOV, report the backend;
//     settings survive the command line; the preset renders the same live, from the CLI and as a poster.

using System;
using System.Linq;
using FracturingFog;
using FracturingFog.Batch;
using FracturingFog.Calculators.Gpu;
using FracturingFog.Cli;
using FracturingFog.CosmicWeb;
using FracturingFog.Help;
using FracturingFog.Imaging;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S2024CosmicWebVolumeTests
{
    private static CosmicWebPoints Points(params (float X, float Y, float Z)[] p) => new()
    {
        Xyz = p.SelectMany(q => new[] { q.X, q.Y, q.Z }).ToArray(), Count = p.Length, BoxSize = 10,
    };

    private static CosmicWebPoints Random(int n, double box, ulong seed)
    {
        var rng = new SplitMix64(seed);
        var xyz = new float[3 * n];
        for (int i = 0; i < xyz.Length; i++) xyz[i] = (float)(rng.NextDouble() * box);
        // A few on the edges.
        xyz[0] = 0; xyz[1] = 0; xyz[2] = 0;
        xyz[3] = (float)Math.BitDecrement(box); xyz[4] = 0; xyz[5] = (float)(box / 2);
        return new CosmicWebPoints { Xyz = xyz, Count = n, BoxSize = box };
    }

    // ── Deposit ──

    [Theory]
    [InlineData(32)]
    [InlineData(64)]
    [InlineData(128)]
    public void Cic_Deposit_Conserves_Mass_Exactly(int g)
    {
        var pts = Random(100_003, 100, 17);
        var mass = DensityGrid.DepositCic(pts, g, TestContext.Current.CancellationToken);
        long sum = 0;
        foreach (long m in mass) sum += m;
        Assert.Equal((long)pts.Count * DensityGrid.MassOne, sum);
        Assert.True(mass.Count(m => m < 0) < mass.Length / 1000, "negative cells beyond rounding");
    }

    [Fact]
    public void Cic_Puts_Mass_Where_The_Trilinear_Weights_Say()
    {
        const int g = 10;   // box 10: cell k spans [k, k+1), centre k + 0.5
        long one = DensityGrid.MassOne;
        int I(int x, int y, int z) => (z * g + y) * g + x;

        var centre = DensityGrid.DepositCic(Points((3.5f, 4.5f, 5.5f)), g);
        Assert.Equal(one, centre[I(3, 4, 5)]);
        Assert.Equal(one, centre.Sum());

        var mid = DensityGrid.DepositCic(Points((4.0f, 4.5f, 5.5f)), g);   // halfway between x cells 3 and 4
        Assert.Equal(one / 2, mid[I(3, 4, 5)]);
        Assert.Equal(one / 2, mid[I(4, 4, 5)]);

        var corner = DensityGrid.DepositCic(Points((4.0f, 5.0f, 6.0f)), g);   // shared by 8 cells
        foreach (int dz in new[] { 5, 6 }) foreach (int dy in new[] { 4, 5 }) foreach (int dx in new[] { 3, 4 })
            Assert.Equal(one / 8, corner[I(dx, dy, dz)]);

        var wrap = DensityGrid.DepositCic(Points((0.0f, 0.5f, 0.5f)), g);   // halfway between x cells 9 and 0 (periodic)
        Assert.Equal(one / 2, wrap[I(9, 0, 0)]);
        Assert.Equal(one / 2, wrap[I(0, 0, 0)]);
    }

    [Fact]
    public void Smoothing_Keeps_The_Total()
    {
        int g = 32;
        var f = new float[g * g * g];
        var rng = new SplitMix64(3);
        for (int i = 0; i < f.Length; i++) f[i] = rng.NextDouble() < 0.01 ? 100f : 0f;
        double before = f.Sum(x => (double)x);
        DensityGrid.Smooth(f, g, 1.5);
        Assert.Equal(before, f.Sum(x => (double)x), before * 1e-5);
        Assert.True(f.Max() < 100f * 0.2f, "not smoothed");
    }

    [Fact]
    public void Trilinear_Sample_Hits_Cell_Centres_And_Wraps()
    {
        int g = 8;
        var v = new float[g * g * g];
        for (int i = 0; i < v.Length; i++) v[i] = i % 7;
        float C(int k) => (k + 0.5f) / g - 0.5f;   // cell k's centre in unit-cube coordinates
        for (int z = 0; z < g; z += 3) for (int y = 0; y < g; y += 2) for (int x = 0; x < g; x++)
            Assert.Equal(v[(z * g + y) * g + x], VolumeRaymarch.Sample(v, g, C(x), C(y), C(z)), 4);
        // Halfway between the last and first cells along x: their mean.
        float a = v[(1 * g + 2) * g + (g - 1)], b = v[(1 * g + 2) * g + 0];
        Assert.Equal(0.5f * (a + b), VolumeRaymarch.Sample(v, g, 0.5f, C(2), C(1)), 4);
        Assert.Equal(0.5f * (a + b), VolumeRaymarch.Sample(v, g, -0.5f, C(2), C(1)), 4);
    }

    // ── Raymarch ──

    [Theory]
    [InlineData(4.0, 1.0, 0.5, 1.0)]
    [InlineData(1.0, 0.8, 0.7, 2.2)]
    [InlineData(20.0, 0.5, 1.0, 0.5)]
    public void A_Uniform_Box_Matches_The_Closed_Form(double kappa, double brightness, float v, double gamma)
    {
        int g = 16, w = 64, h = 40;
        var grid = Enumerable.Repeat(v, g * g * g).ToArray();
        var lut = Enumerable.Repeat(0xFFFFFFFFu, 256).ToArray();   // white
        uint bg = 0xFF204060;
        // Looking straight down -x at the box from (2.2, 0, 0) (theta 0, phi pi/2): the centre rays cross
        // the whole unit cube, chord length 1 / cos(angle off axis).
        var cam = MathPlane3DCamera.For(2.2, 0.0, Math.PI / 2, (0, 0, 0), 1.0, w, h);
        var p = VolumeRaymarch.ParamsFor(cam, g, 1024, kappa, gamma, false, bg, brightness);
        var f = VolumeRaymarch.Render(p, grid, lut);
        foreach (var (px, py) in new[] { (w / 2, h / 2), (w / 2 - 3, h / 2 + 2), (w / 2 + 5, h / 2 - 4) })
        {
            var d = cam.RayDirection(px + 0.5, py + 0.5);
            double len = Math.Sqrt(d.X * d.X + d.Y * d.Y + d.Z * d.Z);
            double L = 1.0 / Math.Abs(d.X / len);   // chord through the x slab (the ray stays inside y, z)
            double T = Math.Exp(-kappa * v * L);
            double emit = brightness * Math.Pow(v, 1.0 / gamma) * (1 - T);
            int Ch(int s) => (int)((f.Colour[py * w + px] >> s) & 0xFF);
            double Exp(int s) => Math.Min(255, emit * 255 + T * ((bg >> s) & 0xFF));
            Assert.InRange(Ch(16), Exp(16) - 3, Exp(16) + 3);
            Assert.InRange(Ch(8), Exp(8) - 3, Exp(8) + 3);
            Assert.InRange(Ch(0), Exp(0) - 3, Exp(0) + 3);
        }
        // A ray that misses the box shows the background and has no depth.
        Assert.Equal(bg, f.Colour[0]);
        Assert.True(float.IsPositiveInfinity(f.Depth[0]));
        // Depth: where e^(-k v t') = 1/2 after entering at distance 2.2 - 0.5 (centre ray), to a step.
        if (kappa * v > Math.Log(2))
        {
            double expect = 1.7 + Math.Log(2) / (kappa * v);
            Assert.InRange(f.Depth[h / 2 * w + w / 2], expect - 0.01, expect + 0.01);
        }
    }

    [Fact]
    public void Vulkan_Matches_The_Cpu_When_Present()
    {
        using var k = FracturingFog.Rendering.Vulkan.CosmicWebVolumeVulkanKernel.TryCreateWithOwnContext();
        if (k == null) return;   // no Vulkan device
        var p = new FractalParameters
        {
            CosmicWebGenerator = CosmicWebGenerator.Zeldovich, CosmicWebGrid = 32, CosmicWebGrowth = 1.2, CosmicWebSeed = 5,
            CosmicWebVolumeGrid = 64, CosmicWebVolumeSmoothing = 0.5, CosmicWebVolumeOpacity = 15, CosmicWebVolumeBrightness = 20, CosmicWebGamma = 0.5,
        };
        var pts = CosmicWebCalculator.PointsFor(p, TestContext.Current.CancellationToken);
        var grid = CosmicWebCalculator.VolumeGridFor(pts, p, TestContext.Current.CancellationToken);
        var lut = VolumeRaymarch.BakeLut(new HsvPalette());
        foreach (bool depthColour in new[] { true, false })
        {
            p.CosmicWebColour = depthColour ? CosmicWebColour.Depth : CosmicWebColour.Density;
            var cam = CosmicWebCalculator.CameraFor(p, 1.0, 160, 100);
            var vp = CosmicWebCalculator.VolumeParamsFor(p, cam, grid.Grid, 0xFF000000u);
            var cpu = VolumeRaymarch.Render(vp, grid.Value, lut);
            var gpu = k.Render(vp, grid, lut, TestContext.Current.CancellationToken);
            Assert.NotNull(gpu);
            int off = 0;
            for (int i = 0; i < cpu.Colour.Length; i++)
            {
                int d = 0;
                for (int s = 0; s < 24; s += 8) d = Math.Max(d, Math.Abs((int)((cpu.Colour[i] >> s) & 0xFF) - (int)((gpu!.Colour[i] >> s) & 0xFF)));
                if (d > 2) off++;
                if (float.IsFinite(cpu.Depth[i])) Assert.InRange(gpu!.Depth[i], cpu.Depth[i] - 0.02f, cpu.Depth[i] + 0.02f);
            }
            Assert.True(off <= cpu.Colour.Length / 200, $"{off} pixels more than 2 units off");
        }
    }

    // ── Calculator, batch, presets, help ──

    private static CosmicWebCalculator Render(FractalParameters p, int w = 96, int h = 60, bool depth = false)
    {
        var c = new CosmicWebCalculator(w, h) { ColorMap = new HsvPalette(), FractalParameters = p, DepthRequested = depth };
        c.Calculate(TestContext.Current.CancellationToken);
        return c;
    }

    [Fact]
    public void The_Volume_Render_Differs_From_The_Splat_And_Carries_Depth()
    {
        CosmicWebVolumeBackends.Selection = CosmicWebVolumeBackends.Choice.Cpu;
        try
        {
            var p = new FractalParameters
            {
                CosmicWebGenerator = CosmicWebGenerator.Zeldovich, CosmicWebGrid = 32, CosmicWebSeed = 8,
                CosmicWebVolumeGrid = 32, CosmicWebVolumeBrightness = 20, CosmicWebGamma = 0.5,
            };
            var splat = Render(p);
            Assert.Null(splat.LastVolumeBackend);
            p.CosmicWebRender = CosmicWebRender.Volume;
            var vol = Render(p, depth: true);
            Assert.Equal("CPU", vol.LastVolumeBackend);
            Assert.False(splat.ColorBuffer.SequenceEqual(vol.ColorBuffer));
            Assert.True(vol.ColorBuffer.Distinct().Count() > 20, "flat volume");
            Assert.Contains("; volume 32^3 CIC", vol.LastPrecisionLabel);
            Assert.NotNull(vol.DepthBuffer);
            Assert.Contains(vol.DepthBuffer!, d => d != FracturingFog.Rendering.Lighting.ScreenSpacePost.DepthMiss);
            var again = Render(p);   // cached per point set (when no parallel test evicted the shared point cache, #2025)
            if (ReferenceEquals(vol.LastPoints, again.LastPoints)) Assert.Same(vol.LastVolumeGrid, again.LastVolumeGrid);
        }
        finally { CosmicWebVolumeBackends.Selection = CosmicWebVolumeBackends.Choice.Auto; }
    }

    private static FractalParameters ViaCommandLine(FractalParameters p)
    {
        var snap = RegionFractalParams.Snapshot(FractalType.CosmicWeb, p)!;
        var report = BatchCommandBuilder.BuildWithReport(new BatchCommandSnapshot
        {
            Fractal = FractalType.CosmicWeb, Parameters = p, FamilyParams = snap.ToKeyValues(),
        });
        Assert.DoesNotContain(report.Gaps, g => g.Contains("CosmicWeb", StringComparison.OrdinalIgnoreCase));
        Assert.True(BatchOptions.TryParse(report.Args.ToArray(), 0, out var o, out var err), err);
        var q = new FractalParameters();
        RegionFractalParams.FromKeyValues(o.Params, FractalType.CosmicWeb, out var perr)!.ApplyTo(q);
        Assert.Null(perr);
        return q;
    }

    [Fact]
    public void Volume_Settings_Survive_The_Command_Line()
    {
        var p = new FractalParameters
        {
            CosmicWebRender = CosmicWebRender.Volume, CosmicWebVolumeGrid = 64, CosmicWebVolumeSmoothing = 1.25,
            CosmicWebVolumeOpacity = 7.5, CosmicWebVolumeBrightness = 33, CosmicWebVolumeSteps = 200,
        };
        var q = ViaCommandLine(p);
        foreach (var prop in typeof(FractalParameters).GetProperties().Where(x => x.Name.StartsWith("CosmicWeb", StringComparison.Ordinal)))
            Assert.Equal(prop.GetValue(p), prop.GetValue(q));
    }

    [Fact]
    public void The_Volume_Preset_Renders_The_Same_Live_From_The_Command_Line_And_As_A_Poster()
    {
        CosmicWebVolumeBackends.Selection = CosmicWebVolumeBackends.Choice.Cpu;
        try
        {
            var region = FractalRegionLibrary.Instance.BuiltIns.Single(r => r.Name == "Cosmic Web - Zeldovich Volume");
            var p = new FractalParameters();
            region.Params!.ApplyTo(p);
            Assert.Equal(CosmicWebRender.Volume, p.CosmicWebRender);
            var live = Render(p, 48, 30);
            Assert.True(live.ColorBuffer.SequenceEqual(Render(ViaCommandLine(p), 48, 30).ColorBuffer), "--param differs");
            var poster = PosterRenderer.RenderToPixels(new PosterRequest
            {
                FractalType = FractalType.CosmicWeb, Width = 48, Height = 30, CenterX = 0, CenterY = 0, Zoom = region.Zoom,
                MaxIterations = live.MaxIterations, Quality = QualityPreset.Standard, FractalParameters = p, ColorMap = new HsvPalette(),
            }, TestContext.Current.CancellationToken, out _, out _);
            Assert.True(live.ColorBuffer.SequenceEqual(poster), "poster differs");
            Assert.True(live.ColorBuffer.Distinct().Count() > 5, "flat");
        }
        finally { CosmicWebVolumeBackends.Selection = CosmicWebVolumeBackends.Choice.Auto; }
    }

    [Fact]
    public void Backend_Choice_Parses_And_Help_Names_The_Sources()
    {
        Assert.Equal(CosmicWebVolumeBackends.Choice.Cpu, CosmicWebVolumeBackends.Parse("CPU"));
        Assert.Equal(CosmicWebVolumeBackends.Choice.D3D11, CosmicWebVolumeBackends.Parse("dx11"));
        Assert.Equal(CosmicWebVolumeBackends.Choice.Vulkan, CosmicWebVolumeBackends.Parse("vk"));
        Assert.Equal(CosmicWebVolumeBackends.Choice.Auto, CosmicWebVolumeBackends.Parse(null));
        Assert.Contains(EnvVarCatalog.All, e => e.Name == "FF_GPU_COSMIC_WEB");
        string help = HelpTextBundle.MathCosmicWebText.Replace("\r\n", " ").Replace("\n", " ");
        Assert.Contains("cloud-in-cell", help);
        Assert.Contains("Birdsall and Fuss", help);
        Assert.Contains("Cosmic Web - Zeldovich Volume", help);
    }
}
