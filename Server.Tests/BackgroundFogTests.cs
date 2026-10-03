// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1061 (quick fix) — "Fog the background": ray-miss pixels around a 3D fractal
// get the same classic fog as the surface instead of a black void.
//
// The render lock is an independent invariant: on a real Mandelbulb frame,
// background pixels must change (no longer the in-set black) while every hit
// pixel stays byte-identical (the surface path is untouched), and with the
// option off the frame is byte-identical to before.

using System;
using System.Linq;

using FracturingFog.Batch;
using FracturingFog.Calculators.Gpu;
using FracturingFog.Cli;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class BackgroundFogTests
{
    private static LightingFxData Fx(bool on, double density = 0.08, double dist = 12)
    {
        var fx = LightingFxData.CreateDefault();
        fx.FogDensity = density;
        fx.FogBackground = on;
        fx.FogBackgroundDistance = dist;
        fx.BgTopColor = 0xFF8090A0u;
        fx.BgBottomColor = 0xFF405060u;
        return fx;
    }

    [Fact]
    public void Fraction_IsBeerLambertOverTheDepth_AndOffWhenDisabledOrNoFog()
    {
        Assert.Equal(1 - Math.Exp(-12 * 0.08), ShadingPipeline.BackgroundFogFraction(Fx(true)), 12);
        Assert.Equal(1 - Math.Exp(-12 * 0.08), ShadingPipeline.BackgroundFogFraction(Fx(true, dist: 0)), 12); // ≤0 → default 12
        Assert.Equal(0.0, ShadingPipeline.BackgroundFogFraction(Fx(false)));
        Assert.Equal(0.0, ShadingPipeline.BackgroundFogFraction(Fx(true, density: 0)));
    }

    [Fact]
    public void MissColor_FogsTheFlatBackground_TowardTheSky()
    {
        var fx = Fx(true);
        uint black = 0xFF000000u;
        uint off = ShadingPipeline.MissColor(0, 0, 1, Fx(false), black);
        uint on = ShadingPipeline.MissColor(0, 0, 1, fx, black);
        uint sky = ShadingPipeline.SkyColor(0, fx.BgBottomColor, fx.BgTopColor);
        double f = ShadingPipeline.BackgroundFogFraction(fx);

        Assert.Equal(black, off);
        Assert.Equal((int)Math.Round(((sky >> 16) & 0xFF) * f), (int)((on >> 16) & 0xFF));
        Assert.Equal((int)Math.Round((sky & 0xFF) * f), (int)(on & 0xFF));
    }

    // GpuKernelUtils is internal to the Engine; reach its MissColor by reflection.
    private static readonly System.Reflection.MethodInfo GpuMissColor =
        typeof(GpuShadingParams).Assembly.GetType("FracturingFog.Calculators.Gpu.GpuKernelUtils", throwOnError: true)!
            .GetMethod("MissColor", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!;

    [Fact]
    public void GpuMissColor_MatchesCpu()
    {
        foreach (var on in new[] { false, true })
        foreach (var backdrop in new[] { false, true })
        foreach (double rdy in new[] { -0.7, 0.0, 0.4 })
        {
            var fx = Fx(on);
            fx.ShowSkyBackdrop = backdrop;
            fx.FogColor = 0xFFFFD0A0u;
            var sp = GpuShadingParams.Build(fx);
            var r = new GpuRaymarchParams { InSetColor = 0xFF101010u };
            uint gpu = (uint)GpuMissColor.Invoke(null, new object[] { rdy, r, sp })!;
            // CPU with the gradient sky (no HDRI loaded), same in-set colour.
            uint cpu = ShadingPipeline.MissColor(0, rdy, 0, fx, 0xFF101010u);
            Assert.True(Math.Abs((int)((gpu >> 16) & 0xFF) - (int)((cpu >> 16) & 0xFF)) <= 1
                     && Math.Abs((int)((gpu >> 8) & 0xFF) - (int)((cpu >> 8) & 0xFF)) <= 1
                     && Math.Abs((int)(gpu & 0xFF) - (int)(cpu & 0xFF)) <= 1,
                $"on={on} backdrop={backdrop} rdy={rdy}: gpu {gpu:X8} cpu {cpu:X8}");
        }
    }

    [Fact]
    public void MandelbulbFrame_BackgroundFogs_SurfaceUntouched_OffIsIdentical()
    {
        const int W = 48, H = 36;
        // Misses are the pixels the plain frame paints with the colour map's
        // in-set colour (backdrop off); the depth AOV isn't published without a
        // consumer, so identify them by colour.
        uint inSet = ColorPalette.BuiltIns[0].InSetColor;
        uint[] Render(LightingFxData fx)
        {
            var calc = new MandelbulbCalculator(W, H) { ColorMap = ColorPalette.BuiltIns[0], Zoom = 1.0 };
            fx.SsaoSamples = 0;
            fx.EdgeStrength = 0;
            calc.FractalParameters = new FractalParameters { BulbCameraDistance = 2.6, Lighting = fx };
            calc.Calculate(default);
            return (uint[])calc.ColorBuffer.Clone();
        }

        var plain = Render(Fx(false));
        var plain2 = Render(Fx(false));
        var fogged = Render(Fx(true));

        Assert.Equal(plain, plain2);                                   // off: deterministic, unchanged
        int miss = 0;
        for (int i = 0; i < W * H; i++)
        {
            if (plain[i] == inSet)
            {
                miss++;
                Assert.NotEqual(plain[i], fogged[i]);                  // background now fogged
            }
            else Assert.Equal(plain[i], fogged[i]);                    // surface byte-identical
        }
        Assert.InRange(miss, 1, W * H - 1);                            // frame has both
    }

    // ── Batch parity ──────────────────────────────────────────────────────

    [Fact]
    public void Flags_Parse_DistanceImpliesTheSwitch_AndValidate()
    {
        Assert.True(BatchOptions.TryParse(new[] { "--x", "0", "--y", "0", "--zoom", "1", "--out", "o.png", "--fog-background" }, 0, out var a, out var e1), e1);
        Assert.True(a.FogBackground);
        Assert.Null(a.FogBackgroundDistance);

        Assert.True(BatchOptions.TryParse(new[] { "--x", "0", "--y", "0", "--zoom", "1", "--out", "o.png", "--fog-background-distance", "20" }, 0, out var b, out var e2), e2);
        Assert.True(b.FogBackground);
        Assert.Equal(20, b.FogBackgroundDistance);

        Assert.False(BatchOptions.TryParse(new[] { "--x", "0", "--y", "0", "--zoom", "1", "--out", "o.png", "--fog-background-distance", "0.1" }, 0, out _, out _));
    }

    [Fact]
    public void CommandBuilder_EmitsTheFlags_AndTheyParseBack()
    {
        string onDefault = BatchCommandBuilder.Build(new BatchCommandSnapshot { FogDensity = 0.1, FogBackground = true });
        Assert.Contains("--fog-background", onDefault);
        Assert.DoesNotContain("--fog-background-distance", onDefault);

        string custom = BatchCommandBuilder.Build(new BatchCommandSnapshot
            { FogDensity = 0.1, FogBackground = true, FogBackgroundDistance = 25 });
        Assert.Contains("--fog-background-distance 25", custom);

        string off = BatchCommandBuilder.Build(new BatchCommandSnapshot { FogDensity = 0.1 });
        Assert.DoesNotContain("--fog-background", off);
    }

    [Fact]
    public void Fidelity_CoversTheNewFields()
    {
        var fx = LightingFxData.CreateDefault();
        fx.FogBackground = true;
        fx.FogBackgroundDistance = 30;
        Assert.DoesNotContain("FogBackground", LightingFidelity.UnexpressedFields(fx));
        Assert.DoesNotContain("FogBackgroundDistance", LightingFidelity.UnexpressedFields(fx));
    }

    [Fact]
    public void PresetDto_RoundTripsTheNewFields()
    {
        var fx = Fx(true, dist: 7.5);
        var back = LightingFxPresetData.FromFx(fx).ToFx();
        Assert.True(back.FogBackground);
        Assert.Equal(7.5, back.FogBackgroundDistance);
    }
}
