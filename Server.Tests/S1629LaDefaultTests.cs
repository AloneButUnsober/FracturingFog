// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1629 (epic #1623, S6) — the LA stage evaluator on by default (DeepLaMode.Auto) and its
// way back (FractalParameters.DeepLa → region snapshot → --param → Command builder).
// Method from Imagina's HInfLAEvaluator (GitHub user 5E-324, AGPL-3.0; see the
// bibliography #imagina); the selection rule is FF's, measured on its built-in regions
// and the bench-kit scenes (fractadyne bench-kit, WindySnowOwl,
// https://github.com/WindySnowOwl/fractadyne/tree/main/bench-kit, from rith's FF 0.7.1
// benchmark report) — design doc §4.5.
//   • Auto takes LA at zoom ≥ 1e30 (a periodic reference below it too was measured
//     neutral, so it does not count), BLA below;
//     On forces LA; Off is the BLA rows. The precision label says which ran.
//   • The option survives Clone, the region snapshot, --param and the Command builder,
//     resets with the family, and the poster renders it as the live calculator does.
// LA's accuracy against exact iteration is S1626 / S1627's.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1629LaDefaultTests(ITestOutputHelper output)
{
    private const int W = 48, H = 27;

    private static readonly double[] P448X = [-0.7145519151951202, -2.0790192891565988e-17, 1.1753166641509792e-33, -8.102457136962753e-50, -1.0894092006534645e-66, -5.481175082318642e-83, -1.5340609125118276e-99, 1.4285946495395532e-116];
    private static readonly double[] P448Y = [-0.35402073332318235, 2.5477179573089096e-17, -6.072997541261718e-34, -1.8911088656798612e-50, -4.820603700098655e-67, -3.1189999875481587e-83, 1.2339668272603997e-99, -9.480108552256602e-117];
    private static readonly double[] P953X = [-0.028041054305504548, 1.3292199503012176e-18, -5.275751227722853e-35, -4.203746728154813e-51, -4.269596053996866e-68, 1.9389949392385534e-84, 6.25819023760954e-101, -2.6646795788290983e-117];
    private static readonly double[] P953Y = [-0.6948927538996524, 5.189489838305484e-17, 5.567887477024055e-34, 1.3771597857226995e-50, -5.482812918750436e-67, 4.692321383846679e-84, -2.313052339214723e-100, 2.1139231852108862e-116];
    // FF's built-in "Deep Lightning in Space" (Installer/Stage/Resources/regions.json).
    private static readonly double[] LightX = [-1.4181949444785762, -7.4415882477902279E-17, 0, 0, 0, 0, 0, 0];
    private static readonly double[] LightY = [-0.12700786443815276, -2.3429499355532375E-18, 0, 0, 0, 0, 0, 0];

    // The period-3 nucleus on the real axis, to double-double (Newton in 60 digits).
    private static readonly double[] N3X = [-1.7548776662466927, -3.552187509647825e-17, 0, 0, 0, 0, 0, 0];
    private static readonly double[] N3Y = [0, 0, 0, 0, 0, 0, 0, 0];

    private static MandelbrotCalculator Calc(double[] X, double[] Y, double zoom, int iter, DeepLaMode? mode = null, QualityPreset? quality = null)
    {
        var c = new MandelbrotCalculator(W, H)
        {
            Quality = quality ?? QualityPreset.Extreme,
            CenterX = X[0], CenterXLo = X[1], CenterX2 = X[2], CenterX3 = X[3], CenterX4 = X[4], CenterX5 = X[5], CenterX6 = X[6], CenterX7 = X[7],
            CenterY = Y[0], CenterYLo = Y[1], CenterY2 = Y[2], CenterY3 = Y[3], CenterY4 = Y[4], CenterY5 = Y[5], CenterY6 = Y[6], CenterY7 = Y[7],
            Zoom = zoom, MaxIterations = iter, ColorMap = new HsvPalette(),
        };
        if (mode is { } m) c.LaMode = m;
        return c;
    }

    private static MandelbrotCalculator Run(MandelbrotCalculator c) { c.Calculate(); return c; }

    [Fact]
    public void Auto_Is_The_Default_And_Follows_The_Measured_Rule()
    {
        Assert.Equal(DeepLaMode.Auto, new FractalParameters().DeepLa);
        Assert.Equal(DeepLaMode.Auto, new MandelbrotCalculator(4, 4).LaMode);
        if (MandelbrotCalculator.LaModeOverride != null) { output.WriteLine("FF_LA set: rule not checked"); return; }

        // Zoom >= 1e30, non-periodic reference (p448 at 5.2e33 as the kit's view): LA.
        var deep = Run(Calc(P448X, P448Y, 5.176068e33 * 3.5 * H / (4.0 * W), 13440));
        Assert.Equal(0, deep.ReferencePeriod);   // precondition
        Assert.True(deep.LastFrameUsedLa);
        Assert.EndsWith(" LA", deep.LastPrecisionLabel);

        // Below 1e30, non-periodic ("Deep Lightning in Space", 7.6e26): the BLA rows.
        var light = Run(Calc(LightX, LightY, 7.58348E+26, 14786));
        Assert.Equal(0, light.ReferencePeriod);  // precondition
        Assert.False(light.LastFrameUsedLa);
        Assert.DoesNotContain("LA", light.LastPrecisionLabel);

        // Below 1e30 with a periodic reference (the period-3 nucleus at 1e14): still BLA.
        var per = Run(Calc(N3X, N3Y, 1e14, 5000));
        output.WriteLine($"period-3 nucleus at 1e14: period {per.ReferencePeriod}, LA {per.LastFrameUsedLa}, label {per.LastPrecisionLabel}");
        Assert.True(per.ReferencePeriod > 0, "precondition: the nucleus gives a periodic reference");
        Assert.False(per.LastFrameUsedLa);
        // The threshold itself: just above 1e30 LA, just below BLA (p448's centre).
        Assert.True(Run(Calc(P448X, P448Y, 1.0001e30, 13440)).LastFrameUsedLa);
        Assert.False(Run(Calc(P448X, P448Y, 0.9999e30, 13440)).LastFrameUsedLa);

        // On forces LA below the threshold; Off keeps the BLA rows above it.
        Assert.True(Run(Calc(LightX, LightY, 7.58348E+26, 14786, DeepLaMode.On)).LastFrameUsedLa);
        var off = Run(Calc(P448X, P448Y, 5.176068e33 * 3.5 * H / (4.0 * W), 13440, DeepLaMode.Off));
        Assert.False(off.LastFrameUsedLa);
        Assert.DoesNotContain("LA", off.LastPrecisionLabel);
        // The legacy switch maps onto the mode.
        var legacy = new MandelbrotCalculator(4, 4) { UseLaEvaluator = false };
        Assert.Equal(DeepLaMode.Off, legacy.LaMode);
        legacy.UseLaEvaluator = true;
        Assert.Equal(DeepLaMode.On, legacy.LaMode);
    }

    [Theory]
    [InlineData(DeepLaMode.Off)]
    [InlineData(DeepLaMode.On)]
    public void The_Snapshot_Param_And_Command_Builder_Carry_The_Option(DeepLaMode mode)
    {
        Assert.Null(RegionFractalParams.Snapshot(FractalType.Mandelbrot, new FractalParameters()));   // Auto = omitted
        var p = new FractalParameters { DeepLa = mode };
        Assert.Equal(mode, p.Clone().DeepLa);
        var snap = RegionFractalParams.Snapshot(FractalType.Mandelbrot, p)!;
        var back = System.Text.Json.JsonSerializer.Deserialize<RegionFractalParams>(System.Text.Json.JsonSerializer.Serialize(snap))!;
        var q = new FractalParameters();
        back.ApplyTo(q);
        Assert.Equal(mode, q.DeepLa);
        RegionFractalParams.ResetFamilyToDefaults(FractalType.Mandelbrot, q);
        Assert.Equal(DeepLaMode.Auto, q.DeepLa);

        var cmdSnap = new FracturingFog.Cli.BatchCommandSnapshot
        {
            Fractal = FractalType.Mandelbrot, Parameters = p,
            FamilyParams = snap.ToKeyValues(),
        };
        string cmd = FracturingFog.Cli.BatchCommandBuilder.Build(cmdSnap);
        Assert.Contains($"DeepLa={(int)mode}", cmd);
        var args = new List<string> { "--x", "0", "--y", "0", "--zoom", "1", "--out", "o.png" };
        foreach (var kv in cmdSnap.FamilyParams) { args.Add(FracturingFog.Batch.BatchFlags.Param); args.Add(kv.Key + "=" + kv.Value); }
        Assert.True(FracturingFog.Batch.BatchOptions.TryParse(args.ToArray(), 0, out var opts, out var err), err);
        var overlay = RegionFractalParams.FromKeyValues(opts.Params, FractalType.Mandelbrot, out var error);
        Assert.True(overlay != null, error);
        var fresh = new FractalParameters();
        overlay!.ApplyTo(fresh);
        Assert.Equal(mode, fresh.DeepLa);
    }

    [Fact]
    public void An_Out_Of_Range_Param_Value_Is_Ignored()
    {
        var overlay = RegionFractalParams.FromKeyValues(new[] { new KeyValuePair<string, string>("DeepLa", "7") }, FractalType.Mandelbrot, out var error);
        Assert.True(overlay != null, error);
        var fp = new FractalParameters();
        overlay!.ApplyTo(fp);
        Assert.Equal(DeepLaMode.Auto, fp.DeepLa);
    }

    [Fact]
    public void The_Poster_Honours_It_Like_The_Live_Calculator()
    {
        if (MandelbrotCalculator.LaModeOverride != null) return;
        double zoom = 5.176068e33 * 3.5 * H / (4.0 * W);
        FracturingFog.Imaging.PosterRequest Req(DeepLaMode m) => new()
        {
            FractalType = FractalType.Mandelbrot, Width = W, Height = H,
            CenterX = P448X[0], CenterXLo = P448X[1], CenterX2 = P448X[2], CenterX3 = P448X[3],
            CenterX4 = P448X[4], CenterX5 = P448X[5], CenterX6 = P448X[6], CenterX7 = P448X[7],
            CenterY = P448Y[0], CenterYLo = P448Y[1], CenterY2 = P448Y[2], CenterY3 = P448Y[3],
            CenterY4 = P448Y[4], CenterY5 = P448Y[5], CenterY6 = P448Y[6], CenterY7 = P448Y[7],
            Zoom = zoom, MaxIterations = 13440, Quality = QualityPreset.Standard,   // Standard: no offline TAA (#1380)
            ColorMap = new HsvPalette(), FractalParameters = new FractalParameters { DeepLa = m }, Path = string.Empty,
        };
        var liveAuto = Run(Calc(P448X, P448Y, zoom, 13440, quality: QualityPreset.Standard));
        var liveOff = Run(Calc(P448X, P448Y, zoom, 13440, DeepLaMode.Off, QualityPreset.Standard));
        Assert.True(liveAuto.LastFrameUsedLa && !liveOff.LastFrameUsedLa);   // precondition
        Assert.NotEqual(liveOff.ColorBuffer, liveAuto.ColorBuffer);         // precondition: they differ somewhere
        Assert.Equal(liveAuto.ColorBuffer, FracturingFog.Imaging.PosterRenderer.RenderToPixels(Req(DeepLaMode.Auto), CancellationToken.None, out _, out _));
        Assert.Equal(liveOff.ColorBuffer, FracturingFog.Imaging.PosterRenderer.RenderToPixels(Req(DeepLaMode.Off), CancellationToken.None, out _, out _));
    }

    [Fact]
    public void The_View_Model_Forwards_The_Option()
    {
        var fp = new FractalParameters();
        var vm = new FracturingFog.UI.Avalonia.ViewModels.FractalParamsViewModel(FractalType.Mandelbrot, fp);
        Assert.Equal("Auto", vm.DeepLaChoice);
        Assert.Equal(3, vm.DeepLaModes.Count);
        vm.DeepLaChoice = vm.DeepLaModes[0];
        Assert.Equal(DeepLaMode.Off, fp.DeepLa);
        vm.DeepLaChoice = vm.DeepLaModes[2];
        Assert.Equal(DeepLaMode.On, fp.DeepLa);
    }
}
