// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Linq;
using System.Text.Json;
using FracturingFog;
using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.Models;
using FracturingFog.UI.Avalonia.ViewModels;
using Xunit;

namespace FracturingFog.Server.Tests;

// #1970 — black hole shadow CPU speed-ups must not change the picture. Each check compares
// with an independent tight reference (tolerance 1e-11 at every zoom, the old 0.02 capture
// radius, channels tracked) rather than with the code's own defaults:
//   • the new defaults (tolerance 1e-7 tied to the zoom, capture 0.25 m, no channel bookkeeping
//     in Fate mode) give the reference's fate map at the default view and at eyebrow zooms;
//   • solid blocks change no pixel against a full trace there, and actually fill pixels;
//   • the tolerance tie leaves the default zoom alone and only tightens deeper.
public class S1970BlackHolePerfTests
{
    private const int W = 160, H = 90;

    private static BlackHoleShadowCalculator Render(FractalParameters p, double cx, double cy, double zoom)
    {
        var c = new BlackHoleShadowCalculator(W, H) { FractalParameters = p, ColorMap = new HsvPalette(), CenterX = cx, CenterY = cy, Zoom = zoom };
        c.Calculate(TestContext.Current.CancellationToken);
        return c;
    }

    private static FractalParameters Reference(double zoom)
    {
        // Undo the zoom tie so the reference really runs at 1e-11.
        double tie = BlackHoleShadowCalculator.Scene.EffectiveTolerance(1e-6, zoom) / 1e-6;
        return new FractalParameters { BlackHoleTolerance = 1e-11 / tie, BlackHoleCaptureFraction = 0.02 };
    }

    public static TheoryData<double, double, double> Views => new()
    {
        { 0.0, 0.0, BlackHoleShadowCalculator.DefaultZoom },   // the whole binary
        { -7.72, 0.0, 6.0 },                                    // outer eyebrow edge
        { -7.72, 0.0, 60.0 },                                   // nested eyebrow bands
    };

    [Theory]
    [MemberData(nameof(Views))]
    public void New_Defaults_Keep_The_Reference_Fate_Map(double cx, double cy, double zoom)
    {
        var reference = Render(Reference(zoom), cx, cy, zoom);
        var fast = Render(new FractalParameters(), cx, cy, zoom);
        int mismatch = fast.FateBuffer.Zip(reference.FateBuffer).Count(z => z.First != z.Second);
        // Measured 0 at 480x270 (#1970); allow a few boundary pixels.
        Assert.True(mismatch <= W * H / 1000, $"{mismatch} fate pixels differ from the tight reference");
    }

    [Theory]
    [MemberData(nameof(Views))]
    public void Solid_Blocks_Change_No_Pixel(double cx, double cy, double zoom)
    {
        var full = Render(new FractalParameters(), cx, cy, zoom);
        var solid = Render(new FractalParameters { BlackHoleSolidBlocks = true }, cx, cy, zoom);
        Assert.Equal(full.FateBuffer, solid.FateBuffer);
        Assert.Equal(full.ColorBuffer, solid.ColorBuffer);
        Assert.True(solid.LastSolidFilled > 0, "no block was filled");
        Assert.Equal(0, full.LastSolidFilled);
    }

    [Fact]
    public void Solid_Blocks_Apply_Only_To_Fate_Colouring()
    {
        var p = new FractalParameters { BlackHoleSolidBlocks = true, BlackHoleChannel = BlackHoleChannel.Winding };
        var c = Render(p, 0, 0, BlackHoleShadowCalculator.DefaultZoom);
        Assert.Equal(0, c.LastSolidFilled);   // a channel varies inside the shadows: trace every pixel
        p.BlackHoleSolidBlocks = false;
        Assert.Equal(Render(p, 0, 0, BlackHoleShadowCalculator.DefaultZoom).ColorBuffer, c.ColorBuffer);
    }

    [Fact]
    public void Tolerance_Tie_Leaves_The_Default_Zoom_Alone_And_Tightens_Deeper()
    {
        double z0 = BlackHoleShadowCalculator.DefaultZoom;
        Assert.Equal(1e-7, BlackHoleShadowCalculator.Scene.EffectiveTolerance(1e-7, z0), 15);
        Assert.Equal(1e-7, BlackHoleShadowCalculator.Scene.EffectiveTolerance(1e-7, z0 / 10), 15);   // wider: unchanged
        double t6 = BlackHoleShadowCalculator.Scene.EffectiveTolerance(1e-7, 6.0);
        double t600 = BlackHoleShadowCalculator.Scene.EffectiveTolerance(1e-7, 600.0);
        Assert.True(t6 < 1e-7 && t600 < t6);
        Assert.Equal(1e-7 * Math.Cbrt(z0 / 600.0), t600, 15);
        Assert.True(BlackHoleShadowCalculator.Scene.EffectiveTolerance(1e-13, 1e30) == 1e-13);   // floor
    }

    [Fact]
    public void Solid_Blocks_Round_Trip_And_Panel()
    {
        var p = new FractalParameters { BlackHoleSolidBlocks = true };
        var snap = RegionFractalParams.Snapshot(FractalType.BlackHoleShadow, p)!;
        var viaJson = new FractalParameters();
        JsonSerializer.Deserialize<RegionFractalParams>(JsonSerializer.Serialize(snap))!.ApplyTo(viaJson);
        var report = BatchCommandBuilder.BuildWithReport(new BatchCommandSnapshot
        {
            Fractal = FractalType.BlackHoleShadow, Parameters = p, FamilyParams = snap.ToKeyValues(),
        });
        Assert.True(BatchOptions.TryParse(report.Args.ToArray(), 0, out var o, out var err), err);
        var viaCli = new FractalParameters();
        RegionFractalParams.FromKeyValues(o.Params, FractalType.BlackHoleShadow, out var perr)!.ApplyTo(viaCli);
        Assert.Null(perr);
        Assert.True(viaJson.BlackHoleSolidBlocks && viaCli.BlackHoleSolidBlocks && p.Clone().BlackHoleSolidBlocks);

        var vm = new FractalParamsViewModel(FractalType.BlackHoleShadow, new FractalParameters());
        Assert.False(vm.BlackHoleSolidBlocks);
        vm.BlackHoleSolidBlocks = true;
        Assert.True(vm.BlackHoleSolidBlocks);
    }
}
