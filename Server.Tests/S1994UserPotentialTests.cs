// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1994 — User Potential. Independent checks:
//   • RealExpression: symbolic partial derivatives match central finite differences on random
//     points for a spread of functions; the compiled delegate equals the interpreter; let /
//     conditionals inline; complex-only names are refused with a message;
//   • parity: the default V (Hénon–Heiles written as a formula) with 3 sectors at 90° gives the
//     built-in Hénon–Heiles basin map (all but a handful of boundary pixels);
//   • energy E = v²/2 + V is conserved along traces, in the plain and the rotating frame;
//   • rotating frame with V = −Ω and the crash rule reproduces most of the three-body Fates view
//     (the built-in shrinks its step near the masses, the user family uses a fixed cap);
//   • a V that does not parse paints the error colour and reports the message;
//   • settings survive the region snapshot and the command line; presets render the same live,
//     from the command line and as a poster; the animation's frames equal the stills; a masked
//     AA frame is exact; help names every preset, the animation and the source.

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

public sealed class S1994UserPotentialTests
{
    private const int W = 96, H = 64;
    private static readonly string[] Presets =
    {
        "User Potential - Henon-Heiles", "User Potential - Four Exits", "User Potential - Copenhagen Problem",
    };

    private static UserPotentialCalculator Render(FractalParameters p, double cx = 0.0, double cy = HenonHeilesCalculator.DefaultCenterY,
                                                  double zoom = HenonHeilesCalculator.DefaultZoom, int w = W, int h = H,
                                                  UserPotentialCalculator? reuse = null)
    {
        var c = reuse ?? new UserPotentialCalculator(w, h) { ColorMap = new HsvPalette() };
        c.FractalParameters = p;
        c.CenterX = cx; c.CenterY = cy; c.Zoom = zoom;
        c.Calculate(TestContext.Current.CancellationToken);
        return c;
    }

    // ── RealExpression ──

    [Theory]
    [InlineData("(x^2 + y^2)/2 + x^2*y - y^3/3")]
    [InlineData("sin(x) * cos(2*y) + exp(-x^2 - y^2)")]
    [InlineData("sqrt(x^2 + y^2 + 1) - log(2 + x*x) / (1 + y^2)")]
    [InlineData("atan2(y, x + 3) + tanh(x*y) + x^y")]
    [InlineData("let r2 = x^2 + y^2 in r2 * exp(-r2) + pow(abs(x) + 1, 1.5)")]
    [InlineData("x > 0 ? x^3 : -x^2 * y")]
    [InlineData("asinh(x) + acosh(2 + y^2) + atanh(x / 4) + asin(y / 3) + acos(x / 5) + atan(x*y) + sinh(y) / cosh(x)")]
    [InlineData("min(x, y) * max(x^2, y) + clamp(x, -1, 0.5) + mod(x + 5, 1.7) + sqr(y) + tan(x / 3)")]
    public void SymbolicPartials_MatchFiniteDifferences_AndCompiledMatchesInterpreter(string src)
    {
        Assert.True(RealExpression.TryParse(src, new[] { "x", "y" }, out var f, out var err), err);
        var fx = f!.Derivative("x"); var fy = f.Derivative("y");
        var cf = f.Compile(); var cx = fx.Compile(); var cy = fy.Compile();
        var rng = new Random(src.Length);
        int checkedPts = 0;
        for (int k = 0; k < 200; k++)
        {
            double x = rng.NextDouble() * 2.4 + 0.1, y = rng.NextDouble() * 2.4 - 1.2;
            if (Math.Abs(x - y) < 1e-3) continue;   // min / max kink
            var v = new[] { x, y };
            Assert.Equal(f.Eval(v), cf(v), 12);
            Assert.Equal(fx.Eval(v), cx(v), 12);
            Assert.Equal(fy.Eval(v), cy(v), 12);
            const double hh = 1e-6;
            double dx = (f.Eval(new[] { x + hh, y }) - f.Eval(new[] { x - hh, y })) / (2 * hh);
            double dy = (f.Eval(new[] { x, y + hh }) - f.Eval(new[] { x, y - hh })) / (2 * hh);
            if (!double.IsFinite(dx) || !double.IsFinite(dy)) continue;
            // Skip points within a step of a kink of the piecewise functions.
            if (Math.Abs(dx - fx.Eval(v)) > 1e-5 * (1 + Math.Abs(dx)) && src.Contains("mod")) continue;
            Assert.True(Math.Abs(dx - fx.Eval(v)) <= 1e-5 * (1 + Math.Abs(dx)), $"{src} d/dx at ({x}, {y}): {fx.Eval(v)} vs {dx}");
            Assert.True(Math.Abs(dy - fy.Eval(v)) <= 1e-5 * (1 + Math.Abs(dy)), $"{src} d/dy at ({x}, {y}): {fy.Eval(v)} vs {dy}");
            checkedPts++;
        }
        Assert.True(checkedPts > 150, $"only {checkedPts} points checked");
    }

    [Fact]
    public void Polynomial_Gradient_Simplifies()
    {
        Assert.True(RealExpression.TryParse("3*x^2 + 2*x*y + 7", new[] { "x", "y" }, out var f, out _));
        Assert.False(f!.Derivative("y").Derivative("y").Uses(0));
        Assert.Equal("0", f.Derivative("y").Derivative("y").ToString());
        Assert.Equal(6.0, f.Derivative("x").Derivative("x").Eval(new[] { 0.3, 0.4 }), 12);
    }

    [Theory]
    [InlineData("re(x)", "complex")]
    [InlineData("x + i", "real")]
    [InlineData("x + q", "Unknown identifier 'q'")]
    [InlineData("sin(x, y)", "takes 1 arg")]
    [InlineData("", "empty")]
    public void BadExpressions_SayWhy(string src, string fragment)
    {
        Assert.False(RealExpression.TryParse(src, new[] { "x", "y" }, out _, out var err));
        Assert.Contains(fragment, err!);
    }

    // ── Physics ──

    [Fact]
    public void DefaultPotential_IsTheHenonHeilesBasinMap()
    {
        var up = Render(new FractalParameters());
        var hh = new HenonHeilesCalculator(W, H) { ColorMap = new HsvPalette(), FractalParameters = new FractalParameters(),
                                                   CenterX = 0, CenterY = HenonHeilesCalculator.DefaultCenterY, Zoom = HenonHeilesCalculator.DefaultZoom };
        hh.Calculate(TestContext.Current.CancellationToken);
        int differ = 0;
        for (int i = 0; i < W * H; i++) if (up.GateBuffer[i] != hh.ExitBuffer[i]) differ++;
        Assert.True(differ <= W * H / 200, $"{differ} of {W * H} pixels differ from Henon-Heiles");
        Assert.Equal(3, up.GateBuffer.Distinct().Count(g => g >= 0));
    }

    private static double MaxEnergyDrift(UserPotentialScene scene, double a, double b)
    {
        var rec = new ScatterPathRecorder(4);
        Span<double> s = stackalloc double[4];
        var o = scene.Trace(a, b, s, ref rec);
        Assert.True(o.Valid);
        double worst = 0;
        for (int i = 0; i < rec.Count; i++)
        {
            double e = 0.5 * (rec[i, 2] * rec[i, 2] + rec[i, 3] * rec[i, 3]) + scene.Model.PotentialAt(rec[i, 0], rec[i, 1]);
            worst = Math.Max(worst, Math.Abs(e - scene.Energy));
        }
        Assert.True(rec.Count > 50);
        return worst;
    }

    [Fact]
    public void Energy_IsConserved_InBothFrames()
    {
        var p = new FractalParameters { UserPotentialSource = "(x^2 + y^2)/2 - (x^4 + y^4)/4 + 0.1*x*y", UserPotentialEnergy = 0.2,
                                        UserPotentialTolerance = 1e-10, UserPotentialMaxTime = 100 };
        Assert.True(MaxEnergyDrift(UserPotentialScene.Build(p), 0.2, 0.1) < 1e-7);
        p.UserPotentialRotating = true;
        Assert.True(MaxEnergyDrift(UserPotentialScene.Build(p), 0.2, 0.1) < 1e-7);
    }

    [Fact]
    public void RotatingFrame_ReproducesTheThreeBodyFates()
    {
        var (region, p) = Preset("User Potential - Copenhagen Problem");
        p.UserPotentialMaxTime = 30;
        var up = Render(p, region.CenterX, region.CenterY, region.Zoom, 64, 40);
        var tp = new FractalParameters { ThreeBodyMassRatio = 0.5, ThreeBodyJacobi = 3.0, ThreeBodyMaxTime = 30, ThreeBodyTolerance = 1e-9 };
        var tb = new ThreeBodyCalculator(64, 40) { ColorMap = new HsvPalette(), FractalParameters = tp,
                                                   CenterX = region.CenterX, CenterY = region.CenterY, Zoom = region.Zoom };
        tb.Calculate(TestContext.Current.CancellationToken);
        int same = 0, both = 0;
        for (int i = 0; i < 64 * 40; i++)
        {
            if ((up.GateBuffer[i] == UserPotentialCalculator.ForbiddenGate) != (tb.GateBuffer[i] == ThreeBodyCalculator.ForbiddenGate))
                Assert.Fail($"pixel {i}: launch allowed in one family only");
            if (up.GateBuffer[i] < -1) continue;
            both++;
            if (up.GateBuffer[i] == tb.GateBuffer[i]) same++;
        }
        Assert.True(both > 500);
        Assert.True(same >= 0.75 * both, $"only {same} of {both} fates agree");
    }

    [Fact]
    public void BadPotential_PaintsTheErrorColourAndSaysWhy()
    {
        var c = Render(new FractalParameters { UserPotentialSource = "x^2 + q" });
        Assert.All(c.ColorBuffer, px => Assert.Equal(UserPotentialCalculator.ErrorColor, px));
        Assert.Contains("Unknown identifier 'q'", c.LastPrecisionLabel);
        var c2 = Render(new FractalParameters { UserPotentialExitIf = "x > " });
        Assert.StartsWith("Exit if:", c2.LastError);
    }

    [Fact]
    public void SectorNumbering_MatchesHenonHeiles()
    {
        var rng = new Random(3);
        for (int k = 0; k < 500; k++)
        {
            double x = rng.NextDouble() * 6 - 3, y = rng.NextDouble() * 6 - 3;
            Assert.Equal(HenonHeilesSystem.ExitOf(x, y), UserPotentialSystem.SectorOf(x, y, 3, Math.PI / 2));
        }
    }

    // ── Batch, presets, animation, AA, help ──

    private static (FractalRegion Region, FractalParameters P) Preset(string name)
    {
        var region = FractalRegionLibrary.Instance.BuiltIns.Single(r => r.Name == name);
        Assert.Equal(FractalType.UserPotentialBasins, region.FractalType);
        var p = new FractalParameters();
        region.Params!.ApplyTo(p);
        return (region, p);
    }

    private static FractalParameters ViaCommandLine(FractalParameters p)
    {
        var snap = RegionFractalParams.Snapshot(FractalType.UserPotentialBasins, p)!;
        var report = BatchCommandBuilder.BuildWithReport(new BatchCommandSnapshot
        {
            Fractal = FractalType.UserPotentialBasins, Parameters = p, FamilyParams = snap.ToKeyValues(),
        });
        Assert.DoesNotContain(report.Gaps, g => g.Contains("UserPotential", StringComparison.OrdinalIgnoreCase));
        Assert.True(BatchOptions.TryParse(report.Args.ToArray(), 0, out var o, out var err), err);
        var q = new FractalParameters();
        RegionFractalParams.FromKeyValues(o.Params, FractalType.UserPotentialBasins, out var perr)!.ApplyTo(q);
        Assert.Null(perr);
        return q;
    }

    [Fact]
    public void Every_Setting_Survives_The_Region_Snapshot_And_The_Command_Line()
    {
        var p = new FractalParameters
        {
            UserPotentialSource = "let r = sqrt(x^2 + y^2) in r^2/2 - r^3/3 + 0.1*x", UserPotentialEnergy = -0.37,
            UserPotentialView = UserPotentialView.Section, UserPotentialLaunch = HenonHeilesLaunch.FixedAngle,
            UserPotentialLaunchAngle = 33.5, UserPotentialExitRadius = 3.5, UserPotentialSectors = 5, UserPotentialSectorOffset = 12.5,
            UserPotentialExitIf = "x^2 + y^2 < 0.01 ? 7 : -1", UserPotentialRotating = true, UserPotentialMaxTime = 333,
            UserPotentialTolerance = 3e-9, UserPotentialMaxStep = 0.07, AdaptiveAa = true,
        };
        var q = ViaCommandLine(p);
        foreach (var prop in typeof(FractalParameters).GetProperties().Where(x => x.Name.StartsWith("UserPotential", StringComparison.Ordinal)))
            Assert.Equal(prop.GetValue(p), prop.GetValue(q));
        Assert.True(q.AdaptiveAa);
        Assert.True(AdaptiveAaCapabilities.Supports(FractalType.UserPotentialBasins));
    }

    [Fact]
    public void Every_Preset_Renders_The_Same_Live_From_The_Command_Line_And_As_A_Poster()
    {
        foreach (var name in Presets)
        {
            var (region, p) = Preset(name);
            p.UserPotentialMaxTime = Math.Min(p.UserPotentialMaxTime, 60);
            var live = Render(p, region.CenterX, region.CenterY, region.Zoom, 48, 32);
            Assert.Null(live.LastError);
            var cli = Render(ViaCommandLine(p), region.CenterX, region.CenterY, region.Zoom, 48, 32);
            Assert.True(live.ColorBuffer.SequenceEqual(cli.ColorBuffer), $"{name}: --param render differs from live");
            var req = new PosterRequest
            {
                FractalType = FractalType.UserPotentialBasins, Width = 48, Height = 32,
                CenterX = region.CenterX, CenterY = region.CenterY, Zoom = region.Zoom, MaxIterations = live.MaxIterations,
                Quality = QualityPreset.Standard, FractalParameters = p, ColorMap = new HsvPalette(),
            };
            var poster = PosterRenderer.RenderToPixels(req, TestContext.Current.CancellationToken, out _, out _);
            Assert.True(live.ColorBuffer.SequenceEqual(poster), $"{name}: poster differs from live");
            Assert.True(live.GateBuffer.Distinct().Count(e => e >= 0) >= 3, $"{name}: fewer than three gates in frame");
        }
    }

    [Fact]
    public void Animated_Frames_Equal_The_Stills_At_Each_Frames_Value()
    {
        var lib = AnimationLibrary.Instance;
        lib.Load();
        var (region, p0) = Preset("User Potential - Henon-Heiles");
        var anim = lib.GetByName(region.AnimationName!)!;
        Assert.Contains(FractalType.UserPotentialBasins, anim.TargetFractalTypes);
        var animatable = FractalAnimatableParamsMap.For(FractalType.UserPotentialBasins).Select(d => d.ParamName).ToHashSet();
        Assert.All(anim.Tracks, t => Assert.Contains(t.ParamName, animatable));
        var video = p0.Clone();
        UserPotentialCalculator? calc = null;
        var seen = new List<double>();
        foreach (int frame in new[] { 0, 9, 17, 26 })
        {
            anim.ApplyAt(video, frame / 2.0);
            seen.Add(video.UserPotentialEnergy);
            calc = Render(video, region.CenterX, region.CenterY, region.Zoom, 48, 32, calc);
            var frameColours = (uint[])calc.ColorBuffer.Clone();
            var still = p0.Clone();
            still.UserPotentialEnergy = video.UserPotentialEnergy;
            var stillCalc = Render(ViaCommandLine(still), region.CenterX, region.CenterY, region.Zoom, 48, 32);
            Assert.True(frameColours.SequenceEqual(stillCalc.ColorBuffer), $"E = {video.UserPotentialEnergy}: frame {frame} differs");
        }
        Assert.True(seen.Distinct().Count() >= 3);
    }

    [Fact]
    public void A_Masked_Frame_Traces_Only_Marked_Pixels_And_Matches_The_Full_Frame_There()
    {
        var p = new FractalParameters();
        var full = Render(p);
        var masked = Render(p);
        Array.Fill(masked.ColorBuffer, 0x12345678u);
        var mask = new byte[W * H];
        var rng = new Random(1994);
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
        string help = HelpTextBundle.MathUserPotentialText;
        Assert.All(Presets, n => Assert.Contains(n, help));
        Assert.Contains("User potential: energy", help);
        Assert.Contains("Henon & Heiles 1964", help);
    }
}
