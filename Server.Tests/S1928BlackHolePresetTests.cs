// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

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
using Xunit;

namespace FracturingFog.Server.Tests;

// #1928 (black hole shadows S6, epic #1922) — presets, bound animations, help. Checks that do
// not trust the presets' own numbers:
//   • the Equal-Mass Pair preset is Shipley & Dolan's side-on setup (unit masses, a = 2,
//     r_max = 50 M, θ = 90°), and its frame is mirror symmetric with the two holes swapped
//     (equal masses: reflecting z swaps them);
//   • every preset shows what its description promises (both / all three holes, escaped light,
//     nested eyebrow edges, a sky) and renders the same live, through the poster path and
//     from the command line (Command builder → --param parse);
//   • each animated video frame (the batch's per-frame ApplyAt) equals the still rendered with
//     that frame's value set through --param, with one calculator reused across frames as the
//     video loop does;
//   • the help page names every preset and animation.
public sealed class S1928BlackHolePresetTests
{
    private const string Pair = "Black Hole Shadow - Equal-Mass Pair";
    private const string Eyebrows = "Black Hole Shadow - Eyebrow Hierarchy";
    private const string Unequal = "Black Hole Shadow - Unequal Masses";
    private const string Triangle = "Black Hole Shadow - Triangle of Three";
    private const string Inclined = "Black Hole Shadow - Inclined Camera";
    private const string Starfield = "Black Hole Shadow - Lensed Starfield";
    private static readonly string[] All = { Pair, Eyebrows, Unequal, Triangle, Inclined, Starfield };
    private static readonly string[] Animations = { "Black hole: closing pair", "Black hole: camera swing", "Black hole: turning sky" };

    private const int W = 64, H = 36;

    private static (FractalRegion Region, FractalParameters P) Preset(string name)
    {
        var region = FractalRegionLibrary.Instance.BuiltIns.Single(r => r.Name == name);
        Assert.Equal(RegionType.BuiltIn, region.RegionType);
        Assert.Equal(FractalType.BlackHoleShadow, region.FractalType);
        var p = new FractalParameters();
        region.Params!.ApplyTo(p);
        return (region, p);
    }

    private static BlackHoleShadowCalculator Render(FractalRegion region, FractalParameters p, BlackHoleShadowCalculator? reuse = null)
    {
        var c = reuse ?? new BlackHoleShadowCalculator(W, H) { ColorMap = new HsvPalette() };
        c.FractalParameters = p;
        c.CenterX = region.CenterX; c.CenterY = region.CenterY; c.Zoom = region.Zoom;
        c.Calculate(TestContext.Current.CancellationToken);
        return c;
    }

    private static FractalParameters ViaCommandLine(FractalParameters p)
    {
        var snap = RegionFractalParams.Snapshot(FractalType.BlackHoleShadow, p)!;
        var report = BatchCommandBuilder.BuildWithReport(new BatchCommandSnapshot
        {
            Fractal = FractalType.BlackHoleShadow, Parameters = p, FamilyParams = snap.ToKeyValues(),
        });
        Assert.DoesNotContain(report.Gaps, g => g.Contains("BlackHole", StringComparison.OrdinalIgnoreCase));
        Assert.True(BatchOptions.TryParse(report.Args.ToArray(), 0, out var o, out var err), err);
        var q = new FractalParameters();
        RegionFractalParams.FromKeyValues(o.Params, FractalType.BlackHoleShadow, out var perr)!.ApplyTo(q);
        Assert.Null(perr);
        return q;
    }

    [Fact]
    public void Equal_Mass_Pair_Is_The_Shipley_Dolan_Setup_And_Mirror_Symmetric()
    {
        var (region, p) = Preset(Pair);
        Assert.Equal(BlackHoleLayout.Binary, p.BlackHoleLayout);
        Assert.Equal(1.0, p.BlackHoleMass1);
        Assert.Equal(1.0, p.BlackHoleMass2);
        Assert.Equal(2.0, p.BlackHoleSeparation);
        Assert.Equal(50.0, p.BlackHoleCameraDistance);
        Assert.Equal(90.0, p.BlackHoleInclination);
        Assert.Equal(BlackHoleProjection.Parallel, p.BlackHoleProjection);
        Assert.Equal(0.0, region.CenterX);
        Assert.Equal(0.0, region.CenterY);

        // Pixel x samples u = (x + 0.5 - W/2)·pitch, so x and W - 1 - x mirror u; the u axis is the holes'
        // axis, and with equal masses the mirror swaps hole 0 and hole 1 and keeps escapes.
        var c = Render(region, p);
        var f = c.FateBuffer;
        int compared = 0, mismatched = 0;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                sbyte a = f[y * W + x], b = f[y * W + (W - 1 - x)];
                sbyte bMirrored = b switch { 0 => 1, 1 => 0, _ => b };
                compared++;
                if (a != bMirrored) mismatched++;
            }
        TestContext.Current.TestOutputHelper?.WriteLine($"mirror: {mismatched} of {compared} pixels differ");
        Assert.True(mismatched <= compared / 200, $"{mismatched} of {compared} pixels break the mirror symmetry");
        Assert.Contains(f, v => v == 0);
        Assert.Contains(f, v => v == 1);
        Assert.Contains(f, v => v == 2);
    }

    [Fact]
    public void Every_Preset_Shows_What_It_Promises()
    {
        foreach (var name in All)
        {
            var (region, p) = Preset(name);
            var c = Render(region, p);
            var fates = c.FateBuffer.Distinct().ToHashSet();
            int holes = name == Triangle ? 3 : 2;
            for (sbyte k = 0; k < holes; k++)
                Assert.True(fates.Contains(k), $"{name}: hole {k} never casts a shadow in view");
            Assert.True(fates.Contains((sbyte)holes), $"{name}: no escaped light in view");
            Assert.True(c.FateBuffer.Count(v => v < 0) <= W * H / 100, $"{name}: too many rays hit the step cap");
        }
    }

    [Fact]
    public void Eyebrow_Hierarchy_Nests_Edges_Of_Both_Holes()
    {
        var (region, p) = Preset(Eyebrows);
        Assert.Equal(1.0, p.BlackHoleSeparation);
        var c = Render(region, p);
        // Down the edge columns the eyebrows alternate between the holes: count hole-to-hole
        // changes (an escaped stretch between them counts once).
        int best = 0;
        for (int x = 0; x < W; x++)
        {
            int changes = 0; sbyte last = -2;
            for (int y = 0; y < H; y++)
            {
                sbyte v = c.FateBuffer[y * W + x];
                if (v is 0 or 1)
                {
                    if (last is 0 or 1 && v != last) changes++;
                    last = v;
                }
            }
            best = Math.Max(best, changes);
        }
        Assert.True(best >= 2, $"only {best} hole-to-hole changes in any column");
    }

    [Fact]
    public void Lensed_Starfield_Has_A_Turning_Sky_Behind_Fixed_Shadows()
    {
        var (region, p) = Preset(Starfield);
        Assert.Equal(BlackHoleProjection.Pinhole, p.BlackHoleProjection);
        Assert.Equal(BlackHoleSky.Stars, p.BlackHoleSky);
        var a = Render(region, p);
        int escaped = a.FateBuffer.Count(v => v == 2);
        var skyColours = Enumerable.Range(0, W * H).Where(i => a.FateBuffer[i] == 2).Select(i => a.ColorBuffer[i]).Distinct().Count();
        Assert.True(skyColours > 1, "the sky behind the holes is a single colour");

        var q = p.Clone();
        q.BlackHoleSkyRotation = 90.0;
        var b = Render(region, q);
        Assert.Equal(a.FateBuffer, b.FateBuffer);            // turning the sky moves no shadow
        int moved = Enumerable.Range(0, W * H).Count(i => a.FateBuffer[i] == 2 && a.ColorBuffer[i] != b.ColorBuffer[i]);
        Assert.True(moved > 0, $"turning the sky changed none of {escaped} sky pixels");
    }

    [Fact]
    public void Every_Preset_Renders_The_Same_Live_From_The_Command_Line_And_As_A_Poster()
    {
        foreach (var name in All)
        {
            var (region, p) = Preset(name);
            var live = Render(region, p);
            var cli = Render(region, ViaCommandLine(p));
            Assert.True(live.ColorBuffer.SequenceEqual(cli.ColorBuffer), $"{name}: --param render differs from live");

            var req = new PosterRequest
            {
                FractalType = FractalType.BlackHoleShadow, Width = W, Height = H,
                CenterX = region.CenterX, CenterY = region.CenterY, Zoom = region.Zoom, MaxIterations = live.MaxIterations,
                Quality = QualityPreset.Standard, FractalParameters = p, ColorMap = new HsvPalette(),
            };
            var poster = PosterRenderer.RenderToPixels(req, TestContext.Current.CancellationToken, out _, out _);
            Assert.True(live.ColorBuffer.SequenceEqual(poster), $"{name}: poster differs from live");
        }
    }

    [Fact]
    public void Bound_Animations_Exist_And_Drive_Animatable_Settings()
    {
        var lib = AnimationLibrary.Instance;
        lib.Load();
        var animatable = FractalAnimatableParamsMap.For(FractalType.BlackHoleShadow).Select(d => d.ParamName).ToHashSet();
        Assert.Contains("BlackHoleCameraDistance", animatable);
        foreach (var name in All)
        {
            var region = FractalRegionLibrary.Instance.BuiltIns.Single(r => r.Name == name);
            if (region.AnimationName is null) continue;
            var anim = lib.GetByName(region.AnimationName);
            Assert.NotNull(anim);
            Assert.Contains(FractalType.BlackHoleShadow, anim!.TargetFractalTypes);
            Assert.All(anim.Tracks, t => Assert.Contains(t.ParamName, animatable));
            Assert.NotEmpty(anim.ToAnimators(new FractalParameters()));
        }
        Assert.All(Animations, a => Assert.NotNull(lib.GetByName(a)));
        Assert.Equal(3, All.Count(n => FractalRegionLibrary.Instance.BuiltIns.Single(r => r.Name == n).AnimationName != null));
    }

    [Theory]
    [InlineData(Pair, "BlackHoleSeparation")]
    [InlineData(Inclined, "BlackHoleInclination")]
    [InlineData(Starfield, "BlackHoleSkyRotation")]
    public void Animated_Frames_Equal_The_Stills_At_Each_Frames_Value(string presetName, string param)
    {
        var lib = AnimationLibrary.Instance;
        lib.Load();
        var (region, p0) = Preset(presetName);
        var anim = lib.GetByName(region.AnimationName!)!;
        var prop = typeof(FractalParameters).GetProperty(param)!;

        // The batch video loop: one parameter object, ApplyAt(frame / fps) each frame, the same
        // calculator re-rendering. Frames chosen to land on distinct values.
        const int fps = 2;
        var video = p0.Clone();
        BlackHoleShadowCalculator? calc = null;
        var seen = new List<double>();
        foreach (int frame in new[] { 0, 9, 17, 26 })
        {
            anim.ApplyAt(video, frame / (double)fps);
            double value = (double)prop.GetValue(video)!;
            seen.Add(value);
            calc = Render(region, video, calc);
            var frameColours = (uint[])calc.ColorBuffer.Clone();

            // The still: the preset with the frame's value given on the command line.
            var still = p0.Clone();
            prop.SetValue(still, value);
            var stillCalc = Render(region, ViaCommandLine(still));
            Assert.True(frameColours.SequenceEqual(stillCalc.ColorBuffer), $"{param} = {value}: frame {frame} differs from the still");
        }
        Assert.True(seen.Distinct().Count() >= 3, $"{param} barely moved: {string.Join(", ", seen)}");
    }

    [Fact]
    public void Help_Names_Every_Preset_And_Animation()
    {
        string help = HelpTextBundle.MathBlackHoleShadowText;
        Assert.All(All, n => Assert.Contains(n, help));
        Assert.All(Animations, a => Assert.Contains(a, help));
        foreach (var source in new[] { "Shipley & Dolan", "Majumdar", "Papapetrou", "Trettel", "Kluitenberg", "Alonso" })
            Assert.Contains(source, help);
    }
}
