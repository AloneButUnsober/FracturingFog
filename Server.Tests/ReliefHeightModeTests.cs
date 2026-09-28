// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1026 (S1 of #1025) — Relief 3D height normalisation modes. Peak (default) is
// byte-identical; "Lock current height" (measure → Fixed) reproduces the frame
// exactly; Fixed keeps the terrain still when a tall needle enters the view, where
// Peak rescales the whole terrain; Robust ignores a lone needle. Plus the batch
// grammar, the Command builder round-trip, region save/load and the dialog's lock.

using System;
using System.Linq;
using System.Text.Json;
using FracturingFog;
using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using FracturingFog.UI.Avalonia.ViewModels;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class ReliefHeightModeTests
{
    private const int W = 160, H = 120;          // output
    private const int FW = 1280, FH = 960;       // field (≥ 920 px: the small-window filters stay off)

    // A smooth mound with gentle ripples; `needle` adds a tall narrow bump near one
    // corner (a few hundred cells — above the Robust 99.5th percentile of the raised
    // cells).
    private static float[] Field(bool needle)
    {
        var f = new float[FW * FH];
        for (int y = 0; y < FH; y++)
            for (int x = 0; x < FW; x++)
            {
                double u = x / (FW - 1.0) * 2 - 1, v = y / (FH - 1.0) * 2 - 1;
                double mound = Math.Max(0.0, 1.0 - (u * u + v * v)) * 20.0;
                double ripple = 3.0 * (1 + Math.Sin(x * 0.045) * Math.Cos(y * 0.04));
                f[y * FW + x] = (float)(mound + ripple + 1.0);
            }
        if (needle)
            for (int y = 40; y < 200; y++)
                for (int x = 40; x < 200; x++)
                {
                    double r2 = ((x - 120) * (x - 120) + (y - 120) * (y - 120)) / (15.0 * 15.0);
                    f[y * FW + x] += (float)(60.0 * Math.Exp(-r2));
                }
        return f;
    }

    private static uint[] Albedo()
    {
        var a = new uint[W * H];
        for (int i = 0; i < a.Length; i++) a[i] = 0xFF6688AAu;
        return a;
    }

    private static FractalParameters P(ReliefHeightMode mode = ReliefHeightMode.Peak) => new()
    {
        Relief2DEnabled = true,
        Relief2DRaymarch = true,
        Relief2DGpuRaymarch = false,
        Relief2DHeightScale = 1.4,
        Relief2DCameraAzimuthDeg = 25,
        Relief2DCameraElevationDeg = 45,
        Relief2DCameraFovDeg = 55,
        Relief2DGroundPlane = false,
        Relief2DSupersample = 1,
        Relief2DAutoShade = false,   // no cast shadows: judge the terrain, not the needle's shadow
        Relief2DHeightMode = mode,
    };

    private static uint[] Render(float[] field, FractalParameters p)
    {
        var dst = new uint[W * H];
        HeightfieldRaymarch2D.Render(Albedo(), field, W, H, FW, FH, p, dst, out _);
        return dst;
    }

    // Camera-to-surface distance per output pixel (sky = NaN).
    private static float[] Depth(float[] field, FractalParameters p)
    {
        var aov = new HeightfieldRaymarch2D.ReliefAovBuffers(W, H);
        HeightfieldRaymarch2D.Render(Albedo(), field, W, H, FW, FH, p, new uint[W * H], out _, null, aov);
        return aov.Depth.Select(d => d > 0f && d < 9.9e5f ? d : float.NaN).ToArray();
    }

    // Fraction of the pixels that hit terrain in both frames whose surface moved
    // AWAY from the camera by more than the march tolerance — i.e. the terrain sank.
    // (A new tall feature can only bring pixels closer by covering them.)
    private static double SankFraction(float[] before, float[] after)
    {
        int both = 0, sank = 0;
        for (int i = 0; i < before.Length; i++)
        {
            if (!float.IsFinite(before[i]) || !float.IsFinite(after[i])) continue;
            both++;
            if (after[i] - before[i] > 0.02f) sank++;
        }
        Assert.True(both > before.Length / 4, "too few terrain hits to compare");
        return sank / (double)both;
    }

    // ── Render ──────────────────────────────────────────────────────────────

    [Fact]
    public void Peak_IsByteIdentical_AndIgnoresTheFixedValues()
    {
        var field = Field(needle: false);
        var plain = Render(field, P());
        var withValues = P();
        withValues.Relief2DHeightRef = 0.5;          // only read in Fixed mode
        withValues.Relief2DHeightBaseline = 2.0;
        Assert.Equal(plain, Render(field, withValues));
    }

    [Theory]
    [InlineData(ReliefHeightMode.Peak)]
    [InlineData(ReliefHeightMode.Robust)]
    public void LockingTheMeasuredHeight_ReproducesTheFrame(ReliefHeightMode mode)
    {
        var field = Field(needle: true);
        var live = P(mode);
        var before = Render(field, live);

        var m = HeightfieldRaymarch2D.MeasureHeightNormalization(field, FW, FH, live);
        Assert.NotNull(m);
        var locked = P(ReliefHeightMode.Fixed);
        locked.Relief2DHeightRef = m!.Value.Reference;
        locked.Relief2DHeightBaseline = m.Value.Baseline;
        Assert.Equal(before, Render(field, locked));
    }

    // Independent invariant: a tall bump appearing in one corner must not move the
    // rest of the terrain. Peak re-normalises to the bump, so the whole terrain sinks
    // away from the camera; Fixed leaves it in place (the bump can only cover some
    // of it).
    [Fact]
    public void Fixed_KeepsTheTerrain_WhenANeedleEntersTheView()
    {
        var calm = Field(needle: false);
        var spiky = Field(needle: true);

        double peakSank = SankFraction(Depth(calm, P()), Depth(spiky, P()));

        var m = HeightfieldRaymarch2D.MeasureHeightNormalization(calm, FW, FH, P())!.Value;
        var fixedP = P(ReliefHeightMode.Fixed);
        fixedP.Relief2DHeightRef = m.Reference;
        fixedP.Relief2DHeightBaseline = m.Baseline;
        double fixedSank = SankFraction(Depth(calm, fixedP), Depth(spiky, fixedP));

        // Only the raised part can sink (the flat base below the baseline stays at 0).
        Assert.True(peakSank > 0.08, $"Peak should sink the terrain ({peakSank:P1} of hits moved away)");
        Assert.True(fixedSank < peakSank * 0.1, $"Fixed sank {fixedSank:P1} of hits (Peak {peakSank:P1})");
    }

    [Fact]
    public void Robust_Reference_IgnoresALoneNeedle_WherePeakJumps()
    {
        var peakCalm   = HeightfieldRaymarch2D.MeasureHeightNormalization(Field(false), FW, FH, P())!.Value;
        var peakSpiky  = HeightfieldRaymarch2D.MeasureHeightNormalization(Field(true),  FW, FH, P())!.Value;
        var robCalm    = HeightfieldRaymarch2D.MeasureHeightNormalization(Field(false), FW, FH, P(ReliefHeightMode.Robust))!.Value;
        var robSpiky   = HeightfieldRaymarch2D.MeasureHeightNormalization(Field(true),  FW, FH, P(ReliefHeightMode.Robust))!.Value;

        Assert.True(peakSpiky.Reference > peakCalm.Reference * 1.5,
            $"peak {peakCalm.Reference:0.###} -> {peakSpiky.Reference:0.###}");
        Assert.True(Math.Abs(robSpiky.Reference - robCalm.Reference) < 0.1 * robCalm.Reference,
            $"robust {robCalm.Reference:0.###} -> {robSpiky.Reference:0.###}");
        Assert.True(robSpiky.Reference < peakSpiky.Reference);
    }

    [Fact]
    public void Fixed_UnsetValues_FallBackToTheMeasuredOnes()
    {
        var field = Field(needle: true);
        var fixedAuto = P(ReliefHeightMode.Fixed);   // ref 0, baseline -1 = measure per frame
        Assert.Equal(Render(field, P()), Render(field, fixedAuto));
    }

    [Fact]
    public void Measure_DeadFlatField_IsNull()
        => Assert.Null(HeightfieldRaymarch2D.MeasureHeightNormalization(new float[FW * FH], FW, FH, P()));

    // ── Batch grammar ───────────────────────────────────────────────────────

    private static string[] Argv(params string[] extra)
        => new[] { "FracturingFog", "--batch", "--fractal", "Mandelbrot", "--x", "-0.5", "--y", "0", "--zoom", "1",
                   "--relief-raymarch" }
           .Concat(extra).Concat(new[] { "--out", "out.png" }).ToArray();

    [Fact]
    public void Batch_ParsesTheModes_AndApplies()
    {
        Assert.True(BatchOptions.TryParse(Argv("--relief-height-mode", "robust"), 2, out var o, out var e), e);
        Assert.Equal(ReliefHeightMode.Robust, o.ReliefHeightMode);

        Assert.True(BatchOptions.TryParse(Argv("--relief-height-mode", "fixed", "--relief-height-ref", "4.25",
            "--relief-height-baseline", "1.5"), 2, out var f, out e), e);
        Assert.Equal(ReliefHeightMode.Fixed, f.ReliefHeightMode);
        Assert.Equal(4.25, f.ReliefHeightRef);
        Assert.Equal(1.5, f.ReliefHeightBaseline);
        Assert.True(f.Relief);
    }

    [Fact]
    public void Batch_ReferenceAlone_SelectsFixed()
    {
        Assert.True(BatchOptions.TryParse(Argv("--relief-height-ref", "3"), 2, out var o, out var e), e);
        Assert.Equal(ReliefHeightMode.Fixed, o.ReliefHeightMode);
    }

    [Theory]
    [InlineData("--relief-height-mode", "tallest")]
    [InlineData("--relief-height-ref", "0")]
    [InlineData("--relief-height-baseline", "-2")]
    public void Batch_BadValues_Rejected(string flag, string value)
    {
        Assert.False(BatchOptions.TryParse(Argv(flag, value), 2, out _, out var err));
        Assert.Contains(flag, err);
    }

    [Fact]
    public void Batch_FixedValues_WithAnotherMode_Rejected()
    {
        Assert.False(BatchOptions.TryParse(Argv("--relief-height-mode", "peak", "--relief-height-ref", "3"), 2, out _, out var err));
        Assert.Contains("--relief-height-mode fixed", err);
    }

    // ── Command builder round-trip ──────────────────────────────────────────

    [Fact]
    public void Builder_RoundTripsFixedMode()
    {
        var snap = new BatchCommandSnapshot
        {
            Fractal = FractalType.Mandelbrot, CenterX = -0.5, CenterY = 0, Zoom = 1,
            ReliefEnabled = true, ReliefRaymarch = true,
            ReliefHeightMode = ReliefHeightMode.Fixed, ReliefHeightRef = 4.123456789, ReliefHeightBaseline = 0.75,
        };
        var report = BatchCommandBuilder.BuildWithReport(snap);
        var argv = new[] { "FracturingFog", "--batch" }
            .Concat(report.Args.Select(a => a == "<OUTPUT.png>" ? "out.png" : a)).ToArray();
        Assert.True(BatchOptions.TryParse(argv, 2, out var o, out var e), e);
        Assert.Equal(ReliefHeightMode.Fixed, o.ReliefHeightMode);
        Assert.Equal(4.123456789, o.ReliefHeightRef);
        Assert.Equal(0.75, o.ReliefHeightBaseline);
    }

    [Fact]
    public void Builder_OmitsPeak_AndUnsetFixedValues()
    {
        var peak = BatchCommandBuilder.Build(new BatchCommandSnapshot
        {
            Fractal = FractalType.Mandelbrot, ReliefEnabled = true, ReliefRaymarch = true,
            ReliefHeightRef = 3.0, ReliefHeightBaseline = 1.0,   // ignored outside Fixed
        });
        Assert.DoesNotContain("--relief-height-mode", peak);
        Assert.DoesNotContain("--relief-height-ref", peak);
        Assert.DoesNotContain("--relief-height-baseline", peak);

        var fixedAuto = BatchCommandBuilder.Build(new BatchCommandSnapshot
        {
            Fractal = FractalType.Mandelbrot, ReliefEnabled = true, ReliefRaymarch = true,
            ReliefHeightMode = ReliefHeightMode.Fixed,
        });
        Assert.Contains("--relief-height-mode fixed", fixedAuto);
        Assert.DoesNotContain("--relief-height-ref", fixedAuto);
        Assert.DoesNotContain("--relief-height-baseline", fixedAuto);
    }

    // ── Region save / load ──────────────────────────────────────────────────

    [Fact]
    public void Region_RoundTripsTheHeightMode()
    {
        var src = new FractalParameters
        {
            Relief2DEnabled = true, Relief2DRaymarch = true,
            Relief2DHeightMode = ReliefHeightMode.Fixed, Relief2DHeightRef = 5.5, Relief2DHeightBaseline = 1.25,
        };
        string json = JsonSerializer.Serialize(Relief3DSettings.Snapshot(src));
        Assert.Contains("\"Fixed\"", json);
        var dst = new FractalParameters();
        JsonSerializer.Deserialize<Relief3DSettings>(json)!.ApplyTo(dst);
        Assert.Equal(ReliefHeightMode.Fixed, dst.Relief2DHeightMode);
        Assert.Equal(5.5, dst.Relief2DHeightRef);
        Assert.Equal(1.25, dst.Relief2DHeightBaseline);

        // A region saved before #1026 recalls as Peak with measured values.
        var legacy = new FractalParameters { Relief2DHeightMode = ReliefHeightMode.Robust, Relief2DHeightRef = 9 };
        JsonSerializer.Deserialize<Relief3DSettings>("{\"Enabled\":true}")!.ApplyTo(legacy);
        Assert.Equal(ReliefHeightMode.Peak, legacy.Relief2DHeightMode);
        Assert.Equal(0.0, legacy.Relief2DHeightRef);
        Assert.Equal(-1.0, legacy.Relief2DHeightBaseline);
    }

    [Fact]
    public void Clone_CarriesTheHeightMode()
    {
        var p = new FractalParameters
        {
            Relief2DHeightMode = ReliefHeightMode.Robust, Relief2DHeightRef = 2, Relief2DHeightBaseline = 0.5,
        };
        var c = p.Clone();
        Assert.Equal(ReliefHeightMode.Robust, c.Relief2DHeightMode);
        Assert.Equal(2.0, c.Relief2DHeightRef);
        Assert.Equal(0.5, c.Relief2DHeightBaseline);
    }

    // ── Dialog lock ─────────────────────────────────────────────────────────

    [Fact]
    public void LockCommand_StoresTheMeasurement_AndSwitchesToFixed()
    {
        var p = new FractalParameters { Relief2DEnabled = true, Relief2DRaymarch = true };
        var vm = new FractalParamsViewModel(FractalType.Mandelbrot, p) { ReliefHeightMeasurer = () => (1.5, 6.25) };
        vm.LockReliefHeight();
        Assert.Equal(ReliefHeightMode.Fixed, p.Relief2DHeightMode);
        Assert.Equal(6.25, p.Relief2DHeightRef);
        Assert.Equal(1.5, p.Relief2DHeightBaseline);
        Assert.True(vm.Relief2DHeightModeIsFixed);
        Assert.False(vm.ReliefHeightLockFailed);

        var none = new FractalParamsViewModel(FractalType.Mandelbrot, new FractalParameters()) { ReliefHeightMeasurer = () => null };
        none.LockReliefHeight();
        Assert.True(none.ReliefHeightLockFailed);
        Assert.Equal(ReliefHeightMode.Peak, none.Relief2DHeightMode);
    }
}
