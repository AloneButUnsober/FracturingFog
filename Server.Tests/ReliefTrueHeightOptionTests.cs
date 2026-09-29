// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1027 follow-up — the real-surface trace is opt-in (Relief2DTrueHeight); the
// default keeps the established look. Batch / builder / region / dialog parity, plus
// the read-only Fixed-lock summary that replaced the raw reference/baseline editors.

using System.Linq;
using System.Text.Json;
using FracturingFog;
using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.Models;
using FracturingFog.UI.Avalonia.ViewModels;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class ReliefTrueHeightOptionTests
{
    [Fact]
    public void Batch_Switch_Parses_And_Builder_RoundTrips()
    {
        var snap = new BatchCommandSnapshot
        {
            Fractal = FractalType.Mandelbrot, CenterX = -0.5, CenterY = 0, Zoom = 1,
            ReliefEnabled = true, ReliefRaymarch = true, ReliefTrueHeight = true,
        };
        var report = BatchCommandBuilder.BuildWithReport(snap);
        Assert.Contains("--relief-true-height", report.Args);
        var argv = new[] { "FracturingFog", "--batch" }
            .Concat(report.Args.Select(a => a == "<OUTPUT.png>" ? "out.png" : a)).ToArray();
        Assert.True(BatchOptions.TryParse(argv, 2, out var o, out var e), e);
        Assert.True(o.ReliefTrueHeight);
        Assert.True(o.Relief);

        Assert.DoesNotContain("--relief-true-height", BatchCommandBuilder.Build(new BatchCommandSnapshot
        {
            Fractal = FractalType.Mandelbrot, ReliefEnabled = true, ReliefRaymarch = true,
        }));
    }

    [Fact]
    public void Region_And_Clone_CarryTheSwitch()
    {
        var src = new FractalParameters { Relief2DEnabled = true, Relief2DRaymarch = true, Relief2DTrueHeight = true };
        Assert.True(src.Clone().Relief2DTrueHeight);
        var dst = new FractalParameters();
        JsonSerializer.Deserialize<Relief3DSettings>(JsonSerializer.Serialize(Relief3DSettings.Snapshot(src)))!.ApplyTo(dst);
        Assert.True(dst.Relief2DTrueHeight);

        var legacy = new FractalParameters { Relief2DTrueHeight = true };
        JsonSerializer.Deserialize<Relief3DSettings>("{\"Enabled\":true}")!.ApplyTo(legacy);
        Assert.False(legacy.Relief2DTrueHeight);
    }

    [Fact]
    public void Dialog_WritesTheSwitch()
    {
        var p = new FractalParameters { Relief2DEnabled = true, Relief2DRaymarch = true };
        var vm = new FractalParamsViewModel(FractalType.Mandelbrot, p) { Relief2DTrueHeight = true };
        Assert.True(p.Relief2DTrueHeight);
    }

    [Fact]
    public void FixedLock_IsShownReadOnly_WithGuidance()
    {
        var p = new FractalParameters { Relief2DEnabled = true, Relief2DRaymarch = true };
        var vm = new FractalParamsViewModel(FractalType.Mandelbrot, p);
        Assert.Equal("", vm.ReliefHeightLockSummary);                       // Peak: nothing shown

        vm.Relief2DHeightMode = ReliefHeightMode.Fixed;                      // nothing locked yet
        Assert.Contains("Lock current height", vm.ReliefHeightLockSummary);

        vm.ReliefHeightMeasurer = () => (1.25, 4.5);
        vm.LockReliefHeight();
        Assert.Contains("4.5", vm.ReliefHeightLockSummary);
        Assert.Contains("Height scale", vm.ReliefHeightLockSummary);
    }
}
