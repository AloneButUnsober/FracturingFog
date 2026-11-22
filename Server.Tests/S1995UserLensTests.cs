// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1995 — User Lens: a user-written U(x, y, z) for the black hole shadow trace. Independent checks:
//   • a single hole written as U has its shadow edge at the extremal Reissner–Nordström value
//     (impact parameter 4m, corrected for the launch plane), the built-in test re-run through U;
//   • the built-in pair written as U gives the Equal-Mass Pair's fate map (all but a few pixels);
//   • rays stay null (H = 0) along the recorded path, and the symbolic ∇U matches finite
//     differences;
//   • the symbolic Laplacian tells harmonic U (a Majumdar–Papapetrou spacetime) from a smooth lens,
//     and the panel says so; a U threshold captures; a bad U paints the error colour;
//   • the lens runs on the CPU only; settings survive the snapshot and the command line, and only
//     the UserLens layout carries them; presets render the same live, from the command line and as
//     a poster; help names the presets and the honesty note.

using System;
using System.Linq;
using FracturingFog;
using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.Help;
using FracturingFog.Imaging;
using FracturingFog.Models;
using FracturingFog.Scattering;
using FracturingFog.UI.Avalonia.ViewModels;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1995UserLensTests
{
    private static readonly string[] Presets =
    {
        "Black Hole Shadow - User Lens Pair", "Black Hole Shadow - User Lens Ring", "Black Hole Shadow - Smooth Lens",
    };

    private static FractalParameters Lens(string u, string holes = "", double capture = 0)
        => new() { BlackHoleLayout = BlackHoleLayout.UserLens, BlackHoleLensSource = u, BlackHoleCustom = holes, BlackHoleLensCapture = capture };

    private static BlackHoleShadowCalculator Render(FractalParameters p, double cx, double cy, double zoom, int w = 64, int h = 40)
    {
        var c = new BlackHoleShadowCalculator(w, h) { ColorMap = new HsvPalette(), FractalParameters = p, CenterX = cx, CenterY = cy, Zoom = zoom };
        c.Calculate(TestContext.Current.CancellationToken);
        return c;
    }

    [Fact]
    public void Single_Hole_Written_As_U_Has_Its_Shadow_Edge_At_4m()
    {
        const double D = 4000.0;
        var p = Lens("1 + 1/sqrt(x^2 + y^2 + z^2)", "0,0,0,1");
        p.BlackHoleCameraDistance = D;
        var scene = BlackHoleShadowCalculator.Scene.Build(p);
        Assert.NotNull(scene.Lens);
        bool Captured(double b) => scene.Trace(b, 0.0).Outcome.Fate == ScatterFate.Captured;
        double expected = 4.0 / Math.Pow(1.0 + 1.0 / D, 2);
        Assert.True(Captured(3.9));
        Assert.False(Captured(4.1));
        double lo = 3.9, hi = 4.1;
        for (int i = 0; i < 30; i++) { double mid = 0.5 * (lo + hi); if (Captured(mid)) lo = mid; else hi = mid; }
        Assert.InRange(0.5 * (lo + hi), expected - 2e-4, expected + 2e-4);
    }

    [Fact]
    public void The_Pair_Written_As_U_Is_The_Equal_Mass_Pair()
    {
        var builtIn = Render(new FractalParameters(), 0, 0, BlackHoleShadowCalculator.DefaultZoom);
        var lens = Render(Lens(FractalParameters.BlackHoleLensDefaultSource, "0,0,1,1;0,0,-1,1"), 0, 0, BlackHoleShadowCalculator.DefaultZoom);
        int differ = 0;
        for (int i = 0; i < builtIn.FateBuffer.Length; i++) if (builtIn.FateBuffer[i] != lens.FateBuffer[i]) differ++;
        Assert.True(differ <= builtIn.FateBuffer.Length / 100, $"{differ} pixels differ");
        Assert.Contains((sbyte)0, lens.FateBuffer);
        Assert.Contains((sbyte)1, lens.FateBuffer);
    }

    [Fact]
    public void Rays_Stay_Null_And_The_Gradient_Matches_Finite_Differences()
    {
        var p = Lens("1 + 0.7/sqrt((x - 0.3)^2 + y^2 + (z - 1)^2) + 1.2/sqrt(x^2 + (y + 0.5)^2 + (z + 1)^2)",
                     "0.3,0,1,0.7;0,-0.5,-1,1.2");
        var scene = BlackHoleShadowCalculator.Scene.Build(p);
        var rng = new Random(1995);
        for (int k = 0; k < 20; k++)
        {
            Span<double> s = stackalloc double[6];
            var (o, sys) = scene.Trace(rng.NextDouble() * 12 - 6, rng.NextDouble() * 12 - 6, s);
            Assert.NotEqual(ScatterFate.Undecided, o.Fate);
            double uu = sys.U(s[0], s[1], s[2]);
            Assert.True(Math.Abs(sys.Hamiltonian(s)) / (uu * uu) < 1e-6, $"H = {sys.Hamiltonian(s)}");
        }
        var model = UserLensModel.For(p.BlackHoleLensSource);
        for (int k = 0; k < 100; k++)
        {
            double x = rng.NextDouble() * 6 - 3, y = rng.NextDouble() * 6 - 3, z = rng.NextDouble() * 6 - 3;
            model.Eval(x, y, z, out double gx, out double gy, out double gz);
            const double h = 1e-6;
            double U(double a, double b, double c) => model.Eval(a, b, c, out _, out _, out _);
            Assert.Equal((U(x + h, y, z) - U(x - h, y, z)) / (2 * h), gx, 4);
            Assert.Equal((U(x, y + h, z) - U(x, y - h, z)) / (2 * h), gy, 4);
            Assert.Equal((U(x, y, z + h) - U(x, y, z - h)) / (2 * h), gz, 4);
        }
    }

    [Fact]
    public void The_Laplacian_Tells_A_Spacetime_From_A_Lens()
    {
        var harmonic = UserLensModel.For(FractalParameters.BlackHoleLensDefaultSource);
        var smooth = UserLensModel.For("1 + 1.5*exp(-(x^2 + y^2 + z^2)/2)");
        Assert.True(Math.Abs(harmonic.LaplacianAt(0.4, 0.7, 0.2)) < 1e-10);
        Assert.True(Math.Abs(smooth.LaplacianAt(0.4, 0.7, 0.2)) > 0.1);
        var vm = new FractalParamsViewModel(FractalType.BlackHoleShadow, new FractalParameters { BlackHoleLayout = BlackHoleLayout.UserLens });
        Assert.True(vm.BhIsUserLens && vm.BhIsCustom && !vm.BhUsesMass1);
        Assert.StartsWith("Harmonic", vm.BhLensStatus);
        vm.BlackHoleLensSource = "1 + 1.5*exp(-(x^2 + y^2 + z^2)/2)";
        Assert.StartsWith("Not harmonic", vm.BhLensStatus);
        vm.BlackHoleLensSource = "1 + w";
        Assert.StartsWith("U:", vm.BhLensStatus);
    }

    [Fact]
    public void A_U_Threshold_Captures_And_A_Smooth_Lens_Captures_Nothing()
    {
        var ring = Preset("Black Hole Shadow - User Lens Ring");
        var c = Render(ring.P, ring.Region.CenterX, ring.Region.CenterY, ring.Region.Zoom, 48, 30);
        Assert.Contains(c.FateBuffer, f => f == 0);   // captured (the marker hole's gate)
        var smooth = Preset("Black Hole Shadow - Smooth Lens");
        var s = Render(smooth.P, smooth.Region.CenterX, smooth.Region.CenterY, smooth.Region.Zoom, 48, 30);
        Assert.DoesNotContain(s.FateBuffer, f => f == 0);
    }

    [Fact]
    public void A_Bad_U_Paints_The_Error_Colour_And_Runs_On_The_Cpu()
    {
        var p = Lens("1 + q");
        var c = Render(p, 0, 0, BlackHoleShadowCalculator.DefaultZoom, 16, 10);
        Assert.All(c.ColorBuffer, px => Assert.Equal(BlackHoleShadowCalculator.LensErrorColor, px));
        Assert.Contains("Unknown identifier 'q'", c.LastPrecisionLabel);
        Assert.NotNull(BlackHoleShadowCalculator.CpuOnlyReason(Lens(FractalParameters.BlackHoleLensDefaultSource)));
        Assert.Null(BlackHoleShadowCalculator.CpuOnlyReason(new FractalParameters()));
    }

    // ── Batch, presets, help ──

    private static (FractalRegion Region, FractalParameters P) Preset(string name)
    {
        var region = FractalRegionLibrary.Instance.BuiltIns.Single(r => r.Name == name);
        var p = new FractalParameters();
        region.Params!.ApplyTo(p);
        return (region, p);
    }

    private static FractalParameters ViaCommandLine(FractalParameters p)
    {
        var snap = RegionFractalParams.Snapshot(FractalType.BlackHoleShadow, p)!;
        var report = BatchCommandBuilder.BuildWithReport(new BatchCommandSnapshot
        {
            Fractal = FractalType.BlackHoleShadow, Parameters = p, FamilyParams = snap.ToKeyValues(),
        });
        Assert.True(BatchOptions.TryParse(report.Args.ToArray(), 0, out var o, out var err), err);
        var q = new FractalParameters();
        RegionFractalParams.FromKeyValues(o.Params, FractalType.BlackHoleShadow, out var perr)!.ApplyTo(q);
        Assert.Null(perr);
        return q;
    }

    [Fact]
    public void Lens_Settings_Survive_The_Command_Line_And_Only_The_UserLens_Layout_Carries_Them()
    {
        var p = Lens("let r = sqrt(x^2 + y^2 + z^2) in 1 + 2/r", "0,0,0,2", 9.5);
        var q = ViaCommandLine(p);
        Assert.Equal(p.BlackHoleLensSource, q.BlackHoleLensSource);
        Assert.Equal(p.BlackHoleCustom, q.BlackHoleCustom);
        Assert.Equal(9.5, q.BlackHoleLensCapture);
        Assert.Equal(BlackHoleLayout.UserLens, q.BlackHoleLayout);
        var plain = RegionFractalParams.Snapshot(FractalType.BlackHoleShadow, new FractalParameters())!;
        Assert.Null(plain.BlackHoleLensSource);
        Assert.Null(plain.BlackHoleLensCapture);
    }

    [Fact]
    public void Every_Lens_Preset_Renders_The_Same_Live_From_The_Command_Line_And_As_A_Poster()
    {
        foreach (var name in Presets)
        {
            var (region, p) = Preset(name);
            var live = Render(p, region.CenterX, region.CenterY, region.Zoom, 40, 24);
            Assert.DoesNotContain(BlackHoleShadowCalculator.LensErrorColor, live.ColorBuffer);
            var cli = Render(ViaCommandLine(p), region.CenterX, region.CenterY, region.Zoom, 40, 24);
            Assert.True(live.ColorBuffer.SequenceEqual(cli.ColorBuffer), $"{name}: --param render differs from live");
            var req = new PosterRequest
            {
                FractalType = FractalType.BlackHoleShadow, Width = 40, Height = 24,
                CenterX = region.CenterX, CenterY = region.CenterY, Zoom = region.Zoom, MaxIterations = live.MaxIterations,
                Quality = QualityPreset.Standard, FractalParameters = p, ColorMap = new HsvPalette(),
            };
            var poster = PosterRenderer.RenderToPixels(req, TestContext.Current.CancellationToken, out _, out _);
            Assert.True(live.ColorBuffer.SequenceEqual(poster), $"{name}: poster differs from live");
        }
    }

    [Fact]
    public void Help_Names_The_Lens_Presets_And_The_Honesty_Note()
    {
        string help = HelpTextBundle.MathBlackHoleShadowText;
        Assert.All(Presets, n => Assert.Contains(n, help));
        Assert.Contains("HARMONIC", help);
        Assert.Contains("not a\nsolution of Einstein's equations", help.Replace("\r\n", "\n"));
    }
}
