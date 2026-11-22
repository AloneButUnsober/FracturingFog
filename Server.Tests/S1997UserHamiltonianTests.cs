// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1997 — General User Hamiltonian. Independent checks:
//   • parity: the default H (Hénon–Heiles written out) with the section launch (x = 0, pixel
//     (y, py), px solved from H = h0) gives the built-in Hénon–Heiles Section map (all but a few
//     boundary pixels);
//   • Hamilton's equations from the symbolic partials match central finite differences of H;
//   • the solved launch value is the closed form px = √(2(h0 − V) − py²);
//   • H is conserved along traces; with projection on it is held to h0 at every segment end;
//   • launch / exit spec errors are reported; a bad text paints the error colour;
//   • presets render (three gates or more), live = command line = poster; the animation's frames
//     equal the stills; masked AA is exact; batch round trip; help names everything.

using System;
using System.Collections.Generic;
using System.Linq;
using FracturingFog;
using FracturingFog.Abstractions.Animation;
using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.Help;
using FracturingFog.Imaging;
using FracturingFog.Models;
using FracturingFog.Rendering;
using FracturingFog.Scattering;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1997UserHamiltonianTests
{
    private const int W = 64, H = 40;
    private static readonly string[] Presets =
    {
        "User Hamiltonian - Henon-Heiles Section", "User Hamiltonian - Three Degrees of Freedom", "User Hamiltonian - Copenhagen Canonical",
    };

    private static UserHamiltonianCalculator Render(FractalParameters p, double cx = 0.2, double cy = 0.0, double zoom = 1.6,
                                                    int w = W, int h = H, UserHamiltonianCalculator? reuse = null)
    {
        var c = reuse ?? new UserHamiltonianCalculator(w, h) { ColorMap = new HsvPalette() };
        c.FractalParameters = p;
        c.CenterX = cx; c.CenterY = cy; c.Zoom = zoom;
        c.Calculate(TestContext.Current.CancellationToken);
        return c;
    }

    [Fact]
    public void Default_H_Is_The_Henon_Heiles_Section_View()
    {
        var up = Render(new FractalParameters());
        Assert.Null(up.LastError);
        var hp = new FractalParameters { HenonHeilesView = HenonHeilesView.Section };
        var hh = new HenonHeilesCalculator(W, H) { ColorMap = new HsvPalette(), FractalParameters = hp, CenterX = 0.2, CenterY = 0.0, Zoom = 1.6 };
        hh.Calculate(TestContext.Current.CancellationToken);
        int differ = 0;
        for (int i = 0; i < W * H; i++) if (up.GateBuffer[i] != hh.ExitBuffer[i]) differ++;
        Assert.True(differ <= W * H / 200, $"{differ} of {W * H} pixels differ from the Henon-Heiles section");
        Assert.Equal(3, up.GateBuffer.Distinct().Count(g => g >= 0));
    }

    [Fact]
    public void Hamiltons_Equations_Match_Finite_Differences_Of_H()
    {
        var p = new FractalParameters
        {
            UserHamDimension = 3, UserHamSource = "(px^2 + py^2 + pz^2)/2 + sin(x)*cos(y) + x*y*z^2 + 0.3*px*z + exp(-z^2)",
            UserHamAxisA = "x", UserHamAxisB = "y", UserHamFixed = "", UserHamSolveFor = "",
        };
        var scene = UserHamiltonianScene.Build(p);
        Assert.Null(scene.Model.Error);
        var sys = new UserHamiltonianSystem { Model = scene.Model, Scratch = new double[16], ExitRadius = 10, Sectors = 1, TimeCap = 1, StepCap = 1 };
        var rng = new Random(1997);
        Span<double> s = stackalloc double[6];
        Span<double> ds = stackalloc double[6];
        for (int k = 0; k < 50; k++)
        {
            for (int i = 0; i < 6; i++) s[i] = rng.NextDouble() * 2 - 1;
            sys.Derivative(s, ds);
            for (int i = 0; i < 6; i++)
            {
                const double h = 1e-6;
                var sp = s.ToArray(); sp[i] += h;
                var sm = s.ToArray(); sm[i] -= h;
                double dH = (scene.HamiltonianAt(sp) - scene.HamiltonianAt(sm)) / (2 * h);
                // State i is q (i < 3): dp/dt = -dH/dq; i is p: dq/dt = dH/dp.
                double flow = i < 3 ? -ds[i + 3] : ds[i - 3];
                Assert.Equal(dH, flow, 6);
            }
        }
    }

    [Fact]
    public void The_Solved_Launch_Value_Is_The_Closed_Form()
    {
        var scene = UserHamiltonianScene.Build(new FractalParameters());
        Span<double> s = stackalloc double[4];
        Assert.True(scene.LaunchState(0.1, 0.2, s));   // (y, py)
        double V = 0.5 * 0.01 - 0.001 / 3.0;
        Assert.Equal(Math.Sqrt(2 * (0.25 - V) - 0.04), s[2], 12);
        Assert.Equal(0.0, s[0]);
        Assert.False(scene.LaunchState(0.1, 2.0, s));   // py too large: no root
    }

    [Fact]
    public void H_Is_Conserved_And_Projection_Holds_It()
    {
        foreach (int every in new[] { 0, 16 })
        {
            var p = new FractalParameters { UserHamProjectEvery = every, UserHamTolerance = 1e-10, UserHamEnergy = 0.15 };   // bound below 1/6
            var scene = UserHamiltonianScene.Build(p);
            var rec = new ScatterPathRecorder(4);
            Span<double> s = stackalloc double[4];
            var o = scene.Trace(0.1, 0.05, s, ref rec);
            Assert.True(o.Valid);
            Assert.True(rec.Count > 200);
            double worst = 0;
            for (int i = 0; i < rec.Count; i++)
                worst = Math.Max(worst, Math.Abs(scene.HamiltonianAt(new[] { rec[i, 0], rec[i, 1], rec[i, 2], rec[i, 3] }) - 0.15));
            Assert.True(worst < 1e-7, $"every {every}: drift {worst}");
            Assert.True(Math.Abs(scene.HamiltonianAt(s.ToArray()) - 0.15) < (every > 0 ? 1e-9 : 1e-7));
        }
    }

    [Theory]
    [InlineData("q", "y", "x=0", "px", "Axis A")]
    [InlineData("y", "y", "x=0", "px", "different")]
    [InlineData("y", "py", "x=", "px", "Fixed x")]
    [InlineData("y", "py", "x=0; x=1", "px", "set twice")]
    [InlineData("y", "py", "x=0", "py", "cannot be a pixel axis")]
    [InlineData("y", "py", "x=0", "pq", "Solve for")]
    public void Bad_Launch_Specs_Say_Why(string a, string b, string fixedVals, string solve, string fragment)
    {
        var c = Render(new FractalParameters { UserHamAxisA = a, UserHamAxisB = b, UserHamFixed = fixedVals, UserHamSolveFor = solve }, w: 8, h: 6);
        Assert.Contains(fragment, c.LastError);
        Assert.All(c.ColorBuffer, px => Assert.Equal(UserHamiltonianCalculator.ErrorColor, px));
    }

    // ── Batch, presets, animation, AA, help ──

    private static (FractalRegion Region, FractalParameters P) Preset(string name)
    {
        var region = FractalRegionLibrary.Instance.BuiltIns.Single(r => r.Name == name);
        Assert.Equal(FractalType.UserHamiltonianBasins, region.FractalType);
        var p = new FractalParameters();
        region.Params!.ApplyTo(p);
        return (region, p);
    }

    private static FractalParameters ViaCommandLine(FractalParameters p)
    {
        var snap = RegionFractalParams.Snapshot(FractalType.UserHamiltonianBasins, p)!;
        var report = BatchCommandBuilder.BuildWithReport(new BatchCommandSnapshot
        {
            Fractal = FractalType.UserHamiltonianBasins, Parameters = p, FamilyParams = snap.ToKeyValues(),
        });
        Assert.DoesNotContain(report.Gaps, g => g.Contains("UserHam", StringComparison.OrdinalIgnoreCase));
        Assert.True(BatchOptions.TryParse(report.Args.ToArray(), 0, out var o, out var err), err);
        var q = new FractalParameters();
        RegionFractalParams.FromKeyValues(o.Params, FractalType.UserHamiltonianBasins, out var perr)!.ApplyTo(q);
        Assert.Null(perr);
        return q;
    }

    [Fact]
    public void Every_Setting_Survives_The_Region_Snapshot_And_The_Command_Line()
    {
        var p = new FractalParameters
        {
            UserHamSource = "let k = (px^2 + py^2 + pz^2)/2 in k + z^2*x", UserHamDimension = 3, UserHamAxisA = "z", UserHamAxisB = "pz",
            UserHamFixed = "x=0.1; y=-z", UserHamSolveFor = "py", UserHamSolveSign = -1, UserHamEnergy = 0.7, UserHamExitRadius = 4,
            UserHamSectors = 5, UserHamSectorOffset = 12.5, UserHamExitIf = "x^2 < 0.001 ? 4 : -1", UserHamMaxTime = 77,
            UserHamTolerance = 3e-9, UserHamMaxStep = 0.07, UserHamProjectEvery = 24, AdaptiveAa = true,
        };
        var q = ViaCommandLine(p);
        foreach (var prop in typeof(FractalParameters).GetProperties().Where(x => x.Name.StartsWith("UserHam", StringComparison.Ordinal)))
            Assert.Equal(prop.GetValue(p), prop.GetValue(q));
        Assert.True(AdaptiveAaCapabilities.Supports(FractalType.UserHamiltonianBasins));
    }

    [Fact]
    public void Every_Preset_Renders_The_Same_Live_From_The_Command_Line_And_As_A_Poster()
    {
        foreach (var name in Presets)
        {
            var (region, p) = Preset(name);
            p.UserHamMaxTime = Math.Min(p.UserHamMaxTime, 60);
            var live = Render(p, region.CenterX, region.CenterY, region.Zoom, 48, 32);
            Assert.Null(live.LastError);
            var cli = Render(ViaCommandLine(p), region.CenterX, region.CenterY, region.Zoom, 48, 32);
            Assert.True(live.ColorBuffer.SequenceEqual(cli.ColorBuffer), $"{name}: --param render differs from live");
            var req = new PosterRequest
            {
                FractalType = FractalType.UserHamiltonianBasins, Width = 48, Height = 32,
                CenterX = region.CenterX, CenterY = region.CenterY, Zoom = region.Zoom, MaxIterations = live.MaxIterations,
                Quality = QualityPreset.Standard, FractalParameters = p, ColorMap = new HsvPalette(),
            };
            var poster = PosterRenderer.RenderToPixels(req, TestContext.Current.CancellationToken, out _, out _);
            Assert.True(live.ColorBuffer.SequenceEqual(poster), $"{name}: poster differs from live");
            Assert.True(live.GateBuffer.Distinct().Count(e => e >= 0) >= 3, $"{name}: fewer than three gates in frame");
        }
    }

    [Fact]
    public void Animated_Frames_Equal_The_Stills()
    {
        var lib = AnimationLibrary.Instance;
        lib.Load();
        var (region, p0) = Preset("User Hamiltonian - Henon-Heiles Section");
        var anim = lib.GetByName(region.AnimationName!)!;
        var animatable = FractalAnimatableParamsMap.For(FractalType.UserHamiltonianBasins).Select(d => d.ParamName).ToHashSet();
        Assert.All(anim.Tracks, t => Assert.Contains(t.ParamName, animatable));
        var video = p0.Clone();
        UserHamiltonianCalculator? calc = null;
        var seen = new List<double>();
        foreach (int frame in new[] { 0, 9, 17, 26 })
        {
            anim.ApplyAt(video, frame / 2.0);
            seen.Add(video.UserHamEnergy);
            calc = Render(video, region.CenterX, region.CenterY, region.Zoom, 40, 24, calc);
            var frameColours = (uint[])calc.ColorBuffer.Clone();
            var still = p0.Clone();
            still.UserHamEnergy = video.UserHamEnergy;
            var stillCalc = Render(ViaCommandLine(still), region.CenterX, region.CenterY, region.Zoom, 40, 24);
            Assert.True(frameColours.SequenceEqual(stillCalc.ColorBuffer), $"h0 = {video.UserHamEnergy}: frame {frame} differs");
        }
        Assert.True(seen.Distinct().Count() >= 3);
    }

    [Fact]
    public void A_Masked_Frame_Matches_The_Full_Frame_There()
    {
        var p = new FractalParameters();
        var full = Render(p);
        var masked = Render(p);
        Array.Fill(masked.ColorBuffer, 0x12345678u);
        var mask = new byte[W * H];
        var rng = new Random(1997);
        for (int i = 0; i < mask.Length; i++) mask[i] = (byte)(rng.Next(3) == 0 ? 1 : 0);
        masked.PixelMask = mask;
        masked.Calculate(TestContext.Current.CancellationToken);
        masked.PixelMask = null;
        for (int i = 0; i < mask.Length; i++)
            Assert.Equal(mask[i] != 0 ? full.ColorBuffer[i] : 0x12345678u, masked.ColorBuffer[i]);
    }

    [Fact]
    public void Help_Names_Every_Preset_Animation_And_Source()
    {
        string help = HelpTextBundle.MathUserHamiltonianText;
        Assert.All(Presets, n => Assert.Contains(n, help));
        Assert.Contains("User Hamiltonian: energy", help);
        Assert.Contains("Henon & Heiles 1964", help);
    }
}
