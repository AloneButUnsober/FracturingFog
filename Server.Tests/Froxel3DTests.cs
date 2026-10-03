// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1068 (froxel F2) — froxel volumetrics on the 3D raymarchers.

using System;
using System.Linq;

using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class Froxel3DTests
{
    private const int W = 48, H = 36;

    private static LightingFxData Fx(bool froxel, double density)
    {
        var fx = LightingFxData.CreateDefault();
        fx.FogDensity = density;
        fx.Froxel3D = froxel;
        fx.SsaoSamples = 0;
        fx.EdgeStrength = 0;
        return fx;
    }

    private static uint[] Render(LightingFxData fx)
    {
        var calc = new MandelbulbCalculator(W, H) { ColorMap = ColorPalette.BuiltIns[0], Zoom = 1.0 };
        calc.FractalParameters = new FractalParameters { BulbCameraDistance = 2.6, Lighting = fx };
        calc.Calculate(default);
        return (uint[])calc.ColorBuffer.Clone();
    }

    private static double Luma(uint p) => ((p >> 16) & 0xFF) * 0.299 + ((p >> 8) & 0xFF) * 0.587 + (p & 0xFF) * 0.114;

    // ── Arm / strip semantics ─────────────────────────────────────────────

    [Fact]
    public void FogFree_StripsSurfaceFog_AndKeepsTheArmFlag_OnlyWhenActive()
    {
        var on = Fx(true, 0.2);
        on.VolumeSteps = 24;
        on.FogBackground = true;
        var s = ScreenSpacePost.FogFreeForFroxel3D(on);
        Assert.Equal((0.0, 0, false, true), (s.FogDensity, s.VolumeSteps, s.FogBackground, s.Froxel3D));
        Assert.True(ScreenSpacePost.WantsGBuffer(s));
        Assert.True(ScreenSpacePost.ForcesCpuTrace(s));

        // On but no fog → inactive: nothing stripped, nothing armed.
        var noFog = ScreenSpacePost.FogFreeForFroxel3D(Fx(true, 0.0));
        Assert.False(noFog.Froxel3D);
        Assert.False(ScreenSpacePost.WantsGBuffer(noFog));

        // Off → unchanged.
        var off = Fx(false, 0.2);
        Assert.Equal(off, ScreenSpacePost.FogFreeForFroxel3D(off));
    }

    // ── Real Mandelbulb frame ─────────────────────────────────────────────

    [Fact]
    public void Off_OrWithoutFog_IsByteIdentical()
    {
        Assert.Equal(Render(Fx(false, 0.0)), Render(Fx(true, 0.0)));   // armed but no fog → inactive
    }

    [Fact]
    public void On_FillsTheBackgroundWithHaze_ThatGrowsWithDensity()
    {
        uint inSet = ColorPalette.BuiltIns[0].InSetColor;
        var clear = Render(Fx(false, 0.0));
        var thin = Render(Fx(true, 0.05));
        var thick = Render(Fx(true, 0.25));

        var miss = Enumerable.Range(0, W * H).Where(i => clear[i] == inSet).ToArray();
        Assert.InRange(miss.Length, W * H / 10, W * H - 1);

        // Background is no longer the flat in-set colour: lit haze.
        Assert.True(miss.Count(i => thin[i] != inSet) > miss.Length * 0.9);
        // More fog → brighter haze (in-scatter grows with density along the full column).
        double lumThin = miss.Average(i => Luma(thin[i]));
        double lumThick = miss.Average(i => Luma(thick[i]));
        Assert.True(lumThick > lumThin, $"thick {lumThick:0.0} <= thin {lumThin:0.0}");

        // The object is still there: hit pixels differ from the haze around them.
        var hit = Enumerable.Range(0, W * H).Where(i => clear[i] != inSet).ToArray();
        Assert.True(hit.Average(i => Math.Abs(Luma(thin[i]) - Luma(clear[i]))) > 0.5, "fog had no effect on the surface");
    }

    [Fact]
    public void On_ReplacesTheClassicSurfaceFog_RatherThanStackingOnIt()
    {
        // With the froxel pass on, the calculator shades fog-free: rendering with
        // froxel on equals compositing the volume over the fog-free frame — i.e. the
        // classic per-surface fog is NOT also applied. Proxy: the froxel frame
        // differs from the classic-fog frame.
        Assert.NotEqual(Render(Fx(false, 0.1)), Render(Fx(true, 0.1)));
    }

    // ── Batch parity ──────────────────────────────────────────────────────

    private static readonly string[] Base = { "--x", "0", "--y", "0", "--zoom", "1", "--out", "o.png" };

    [Fact]
    public void Flags_Parse_QualityImpliesTheSwitch()
    {
        Assert.True(BatchOptions.TryParse(Base.Append("--froxel-3d").ToArray(), 0, out var a, out var e1), e1);
        Assert.True(a.Froxel3D);
        Assert.Null(a.Froxel3DQuality);

        Assert.True(BatchOptions.TryParse(Base.Concat(new[] { "--froxel-3d-quality", "high" }).ToArray(), 0, out var b, out var e2), e2);
        Assert.True(b.Froxel3D);
        Assert.Equal(FroxelQuality.High, b.Froxel3DQuality);

        Assert.False(BatchOptions.TryParse(Base.Concat(new[] { "--froxel-3d-quality", "ultra" }).ToArray(), 0, out _, out _));
    }

    [Fact]
    public void CommandBuilder_EmitsTheFlags()
    {
        Assert.Contains("--froxel-3d", BatchCommandBuilder.Build(new BatchCommandSnapshot { FogDensity = 0.1, Froxel3D = true }));
        Assert.Contains("--froxel-3d-quality Low",
            BatchCommandBuilder.Build(new BatchCommandSnapshot { FogDensity = 0.1, Froxel3D = true, Froxel3DQuality = FroxelQuality.Low }));
        Assert.DoesNotContain("--froxel-3d", BatchCommandBuilder.Build(new BatchCommandSnapshot { FogDensity = 0.1 }));
    }

    [Fact]
    public void FidelityAndPreset_CoverTheNewFields()
    {
        var fx = LightingFxData.CreateDefault();
        fx.Froxel3D = true;
        fx.Froxel3DQuality = FroxelQuality.High;
        Assert.DoesNotContain("Froxel3D", LightingFidelity.UnexpressedFields(fx));
        Assert.DoesNotContain("Froxel3DQuality", LightingFidelity.UnexpressedFields(fx));
        var back = LightingFxPresetData.FromFx(fx).ToFx();
        Assert.Equal((true, FroxelQuality.High), (back.Froxel3D, back.Froxel3DQuality));
    }
}
