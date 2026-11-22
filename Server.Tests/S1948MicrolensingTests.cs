// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1948 — microlensing magnification maps. Independent checks:
//   • a single point lens follows Paczyński's A(u) = (u² + 2) / (u √(u² + 4)) (ring averages,
//     within shot noise);
//   • the mean magnification over a large view inside a star field matches 1 / ((1 − κ)² − γ²)
//     (Schneider, Ehlers & Falco), averaged over seeds;
//   • ray conservation: the deposited counts equal an independent recount of the shot rays that
//     land in the window;
//   • the lens map: no stars = the mean linear map; one star = the point-mass deflection;
//   • the star field carries exactly κ_* R² of mass; renders are deterministic by seed;
//   • settings survive the snapshot and the command line; presets render the same live, from the
//     command line and as a poster; the animation's frames equal the stills; help says it is not a
//     fractal and names the sources.

using System;
using System.Collections.Generic;
using System.Linq;
using FracturingFog;
using FracturingFog.Abstractions.Animation;
using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.Help;
using FracturingFog.Imaging;
using FracturingFog.Lensing;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1948MicrolensingTests
{
    private static readonly string[] Presets =
    {
        "Microlensing - Caustic Network", "Microlensing - Single Lens", "Microlensing - Single Lens with Shear", "Microlensing - Mixed Masses",
    };

    private static MicrolensingCalculator Render(FractalParameters p, int w, int h, double cx = 0, double cy = 0,
                                                 double zoom = MicrolensingCalculator.DefaultZoom, MicrolensingCalculator? reuse = null)
    {
        var c = reuse ?? new MicrolensingCalculator(w, h) { ColorMap = new HsvPalette() };
        c.FractalParameters = p;
        c.CenterX = cx; c.CenterY = cy; c.Zoom = zoom;
        c.Calculate(TestContext.Current.CancellationToken);
        return c;
    }

    private static double Paczynski(double u) => (u * u + 2) / (u * Math.Sqrt(u * u + 4));

    [Fact]
    public void A_Single_Lens_Follows_Paczynskis_Curve()
    {
        const int W = 200;
        var p = new FractalParameters { MicroLayout = MicrolensLayout.Single, MicroShear = 0, MicroKappaSmooth = 0, MicroRaysPerPixel = 64, MicroPad = 2 };
        var c = Render(p, W, W, zoom: 3.5 / 4.0);   // 4 Einstein radii across
        double pitch = FracturingFog.ViewState.ViewCamera.PixelPitch(W, W, 3.5 / 4.0);
        var sum = new double[8]; var n = new int[8]; var uSum = new double[8];
        for (int y = 0; y < W; y++)
            for (int x = 0; x < W; x++)
            {
                double a = (x + 0.5 - W / 2.0) * pitch, b = (y + 0.5 - W / 2.0) * pitch;
                double u = Math.Sqrt(a * a + b * b);
                int bin = (int)((u - 0.3) / 0.2);
                if (u < 0.3 || bin >= 8) continue;
                sum[bin] += c.Magnification[y * W + x]; n[bin]++;
                uSum[bin] += Paczynski(u);
            }
        for (int k = 0; k < 8; k++)
        {
            double got = sum[k] / n[k], want = uSum[k] / n[k];
            Assert.True(Math.Abs(got / want - 1) < 0.03, $"ring {k}: {got} vs A(u) {want}");
        }
    }

    [Fact]
    public void The_Mean_Magnification_Matches_Theory()
    {
        double total = 0;
        double theory = 0;
        int[] seeds = { 1, 2, 3, 4 };
        foreach (int seed in seeds)
        {
            // A large window (20 Einstein radii) inside a larger field, pad 8: rays scattered in from beyond
            // the pad are lost (about 13% at pad 2 on a 6-radius window), and a small window's mean swings
            // with the few caustics it holds.
            var p = new FractalParameters
            {
                MicroKappaStars = 0.3, MicroKappaSmooth = 0.1, MicroShear = 0.1, MicroFieldRadius = 60, MicroSeed = seed,
                MicroRaysPerPixel = 1, MicroPad = 8,
            };
            var c = Render(p, 160, 160, zoom: 3.5 / 20.0);
            total += c.Magnification.Average();
            theory = c.LastField!.MeanMagnification;
        }
        Assert.Equal(1.0 / ((1 - 0.4) * (1 - 0.4) - 0.01), theory, 12);
        double mean = total / seeds.Length;
        Assert.True(Math.Abs(mean / theory - 1) < 0.06, $"mean {mean} vs theory {theory}");
        // With smooth matter alone the map is exact (no scattering).
        var smooth = Render(new FractalParameters { MicroKappaStars = 0, MicroKappaSmooth = 0.4, MicroShear = 0.1, MicroRaysPerPixel = 3 }, 160, 160, zoom: 3.5 / 6.0);
        Assert.Equal(theory, smooth.Magnification.Average(), 2);
    }

    [Fact]
    public void Rays_Are_Conserved()
    {
        var p = new FractalParameters { MicroKappaStars = 0.4, MicroShear = 0.3, MicroRaysPerPixel = 2, MicroPad = 0.5 };
        const int W = 24, H = 16;
        var c = Render(p, W, H);
        var shot = c.LastShot!;
        Assert.Equal(shot.RaysLanded, shot.Counts.Sum(x => (long)x));
        // Independent recount: rebuild the same ray grid and map every ray.
        var f = c.LastField!;
        double pitch = shot.Pitch, spacing = shot.Spacing;
        double a0 = -0.5 * W * pitch, a1 = 0.5 * W * pitch, b0 = -0.5 * H * pitch, b1 = 0.5 * H * pitch;
        double lx = 1 - f.Kappa - f.Shear, ly = 1 - f.Kappa + f.Shear;
        double xs0 = Math.Min(a0 / lx, a1 / lx) - 0.5, xs1 = Math.Max(a0 / lx, a1 / lx) + 0.5;
        double ys0 = Math.Min(b0 / ly, b1 / ly) - 0.5, ys1 = Math.Max(b0 / ly, b1 / ly) + 0.5;
        int nx = (int)Math.Ceiling((xs1 - xs0) / spacing), ny = (int)Math.Ceiling((ys1 - ys0) / spacing);
        Assert.Equal(shot.RaysShot, (long)nx * ny);
        long inside = 0;
        for (int j = 0; j < ny; j++)
            for (int i = 0; i < nx; i++)
            {
                var (jx, jy) = MicrolensShooter.Jitter(i, j, p.MicroSeed);
                var (y1, y2) = f.Map(xs0 + (i + jx) * spacing, ys0 + (j + jy) * spacing);
                if (y1 >= a0 && y1 < a1 && y2 > -b1 && y2 <= b1 && (-y2 / pitch + 0.5 * H) < H && (-y2 / pitch + 0.5 * H) >= 0) inside++;
            }
        Assert.Equal(inside, shot.RaysLanded);
    }

    [Fact]
    public void The_Lens_Map_Is_The_Lens_Equation()
    {
        var empty = new MicrolensField(Array.Empty<double>(), Array.Empty<double>(), Array.Empty<double>(), 0, 0.2, 0.15, 0);
        var (e1, e2) = empty.Map(1.3, -0.7);
        Assert.Equal(0.65 * 1.3, e1, 12);
        Assert.Equal(0.95 * -0.7, e2, 12);
        var one = new MicrolensField(new[] { 0.5 }, new[] { -0.2 }, new[] { 2.0 }, 0, 0, 0, 0);
        double d1 = 1.5 - 0.5, d2 = 0.3 + 0.2, r2 = d1 * d1 + d2 * d2;
        var (y1, y2) = one.Map(1.5, 0.3);
        Assert.Equal(1.5 - 2 * d1 / r2, y1, 12);
        Assert.Equal(0.3 - 2 * d2 / r2, y2, 12);
    }

    [Theory]
    [InlineData(MicrolensMassFunction.Equal)]
    [InlineData(MicrolensMassFunction.UniformRange)]
    public void The_Star_Field_Carries_Kappa_R_Squared(MicrolensMassFunction mf)
    {
        var f = MicrolensField.Build(new FractalParameters { MicroKappaStars = 0.55, MicroFieldRadius = 9, MicroMassFunction = mf, MicroSeed = 7 });
        Assert.Equal(0.55 * 81, f.M.Sum(), 9);
        Assert.All(Enumerable.Range(0, f.Count), i => Assert.True(f.X[i] * f.X[i] + f.Y[i] * f.Y[i] <= 81 + 1e-9));
    }

    [Fact]
    public void Renders_Are_Deterministic_By_Seed()
    {
        var p = new FractalParameters { MicroRaysPerPixel = 2 };
        var a = Render(p, 40, 24).ColorBuffer.ToArray();
        var b = Render(p, 40, 24).ColorBuffer.ToArray();
        Assert.Equal(a, b);
        p.MicroSeed = 2;
        Assert.NotEqual(a, Render(p, 40, 24).ColorBuffer);
    }

    // ── Batch, presets, animation, help ──

    private static (FractalRegion Region, FractalParameters P) Preset(string name)
    {
        var region = FractalRegionLibrary.Instance.BuiltIns.Single(r => r.Name == name);
        Assert.Equal(FractalType.MicrolensingMap, region.FractalType);
        var p = new FractalParameters();
        region.Params!.ApplyTo(p);
        return (region, p);
    }

    private static FractalParameters ViaCommandLine(FractalParameters p)
    {
        var snap = RegionFractalParams.Snapshot(FractalType.MicrolensingMap, p)!;
        var report = BatchCommandBuilder.BuildWithReport(new BatchCommandSnapshot
        {
            Fractal = FractalType.MicrolensingMap, Parameters = p, FamilyParams = snap.ToKeyValues(),
        });
        Assert.DoesNotContain(report.Gaps, g => g.Contains("Micro", StringComparison.OrdinalIgnoreCase));
        Assert.True(BatchOptions.TryParse(report.Args.ToArray(), 0, out var o, out var err), err);
        var q = new FractalParameters();
        RegionFractalParams.FromKeyValues(o.Params, FractalType.MicrolensingMap, out var perr)!.ApplyTo(q);
        Assert.Null(perr);
        return q;
    }

    [Fact]
    public void Every_Setting_Survives_The_Region_Snapshot_And_The_Command_Line()
    {
        var p = new FractalParameters
        {
            MicroLayout = MicrolensLayout.Single, MicroKappaStars = 0.37, MicroKappaSmooth = -0.1, MicroShear = 0.21, MicroFieldRadius = 17,
            MicroSeed = 42, MicroMassFunction = MicrolensMassFunction.UniformRange, MicroMassMin = 0.2, MicroRaysPerPixel = 5.5,
            MicroPad = 1.5, MicroColourScale = 2.5,
        };
        var q = ViaCommandLine(p);
        foreach (var prop in typeof(FractalParameters).GetProperties().Where(x => x.Name.StartsWith("Micro", StringComparison.Ordinal)))
            Assert.Equal(prop.GetValue(p), prop.GetValue(q));
    }

    [Fact]
    public void Every_Preset_Renders_The_Same_Live_From_The_Command_Line_And_As_A_Poster()
    {
        foreach (var name in Presets)
        {
            var (region, p) = Preset(name);
            var live = Render(p, 40, 24, region.CenterX, region.CenterY, region.Zoom);
            var cli = Render(ViaCommandLine(p), 40, 24, region.CenterX, region.CenterY, region.Zoom);
            Assert.True(live.ColorBuffer.SequenceEqual(cli.ColorBuffer), $"{name}: --param render differs from live");
            var req = new PosterRequest
            {
                FractalType = FractalType.MicrolensingMap, Width = 40, Height = 24,
                CenterX = region.CenterX, CenterY = region.CenterY, Zoom = region.Zoom, MaxIterations = live.MaxIterations,
                Quality = QualityPreset.Standard, FractalParameters = p, ColorMap = new HsvPalette(),
            };
            var poster = PosterRenderer.RenderToPixels(req, TestContext.Current.CancellationToken, out _, out _);
            Assert.True(live.ColorBuffer.SequenceEqual(poster), $"{name}: poster differs from live");
            Assert.True(live.ColorBuffer.Distinct().Count() > 5, $"{name}: a flat frame");
        }
    }

    [Fact]
    public void Animated_Frames_Equal_The_Stills()
    {
        var lib = AnimationLibrary.Instance;
        lib.Load();
        var (region, p0) = Preset("Microlensing - Caustic Network");
        var anim = lib.GetByName(region.AnimationName!)!;
        var animatable = FractalAnimatableParamsMap.For(FractalType.MicrolensingMap).Select(d => d.ParamName).ToHashSet();
        Assert.All(anim.Tracks, t => Assert.Contains(t.ParamName, animatable));
        var video = p0.Clone();
        MicrolensingCalculator? calc = null;
        var seen = new List<double>();
        foreach (int frame in new[] { 0, 9, 17, 26 })
        {
            anim.ApplyAt(video, frame / 2.0);
            seen.Add(video.MicroShear);
            calc = Render(video, 32, 20, region.CenterX, region.CenterY, region.Zoom, calc);
            var frameColours = (uint[])calc.ColorBuffer.Clone();
            var still = p0.Clone();
            still.MicroShear = video.MicroShear;
            var stillCalc = Render(ViaCommandLine(still), 32, 20, region.CenterX, region.CenterY, region.Zoom);
            Assert.True(frameColours.SequenceEqual(stillCalc.ColorBuffer), $"shear {video.MicroShear}: frame {frame} differs");
        }
        Assert.True(seen.Distinct().Count() >= 3);
    }

    [Fact]
    public void Help_Says_It_Is_Not_A_Fractal_And_Names_Presets_And_Sources()
    {
        string help = HelpTextBundle.MathMicrolensingText;
        Assert.Contains("NOT a fractal", help);
        Assert.All(Presets, n => Assert.Contains(n, help));
        Assert.Contains("Microlensing: shear", help);
        foreach (var s in new[] { "Kayser, Refsdal & Stabell", "Schneider, Ehlers & Falco", "Paczynski" })
            Assert.Contains(s, help.Replace("\r\n", " ").Replace("\n", " ").Replace("  ", " "));
    }
}
