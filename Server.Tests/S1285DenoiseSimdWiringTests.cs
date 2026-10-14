// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1285 — the À-Trous denoiser's SIMD path (AtrousParams.UseSimd) was reachable
// only from SimdAtrousTests. It is now a FractalParameters knob
// (Relief2DDenoiseSimd) carried by the render path, the region's relief
// snapshot, --denoise-simd and the Command builder. Default off = the scalar
// reference path, byte-identical.

using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.Imaging;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1285DenoiseSimdWiringTests
{
    [Fact]
    public void Default_Is_Off_And_Maps_To_The_Scalar_Path()
    {
        var p = new FractalParameters { Relief2DDenoiseIterations = 2 };
        Assert.False(p.Relief2DDenoiseSimd);
        Assert.False(ReliefDenoisePass.BuildParams(p).UseSimd);
    }

    [Fact]
    public void Knob_Reaches_The_Denoiser_Params()
    {
        var p = new FractalParameters { Relief2DDenoiseIterations = 2, Relief2DDenoiseSimd = true };
        Assert.True(ReliefDenoisePass.BuildParams(p).UseSimd);
        Assert.True(p.Clone().Relief2DDenoiseSimd);
    }

    [Fact]
    public void Region_Relief_Snapshot_Round_Trips_It()
    {
        var src = new FractalParameters
        {
            Relief2DEnabled = true, Relief2DRaymarch = true,
            Relief2DDenoiseIterations = 3, Relief2DDenoiseSimd = true,
        };
        var snap = Relief3DSettings.Snapshot(src)!;
        Assert.True(snap.DenoiseSimd);

        var dst = new FractalParameters();
        snap.ApplyTo(dst);
        Assert.True(dst.Relief2DDenoiseSimd);
    }

    [Fact]
    public void Batch_Flag_Parses()
    {
        Assert.True(BatchOptions.TryParse(new[]
        {
            "--x", "0", "--y", "0", "--zoom", "1", "--out", "o.png", "--denoise", "2", "--denoise-simd",
        }, 0, out var opts, out var err), err);
        Assert.True(opts.ReliefDenoiseSimd);
        Assert.True(opts.Relief && opts.ReliefRaymarch);

        Assert.True(BatchOptions.TryParse(new[] { "--x", "0", "--y", "0", "--zoom", "1", "--out", "o.png", "--denoise", "2" },
            0, out var off, out var err2), err2);
        Assert.Null(off.ReliefDenoiseSimd);
    }

    [Fact]
    public void Command_Builder_Emits_It_And_It_Parses_Back()
    {
        var snap = new BatchCommandSnapshot
        {
            ReliefEnabled = true, ReliefRaymarch = true,
            ReliefDenoiseIterations = 2, ReliefDenoiseSimd = true,
        };
        string cmd = BatchCommandBuilder.Build(snap);
        Assert.Contains("--denoise-simd", cmd);
        // Builder output: "<exe> --batch ... --out <OUTPUT.png> ..." (no quoted args here).
        string[] args = cmd.Replace("<OUTPUT.png>", "out.png").Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        int start = System.Array.IndexOf(args, "--batch") + 1;
        Assert.True(start > 0, cmd);
        Assert.True(BatchOptions.TryParse(args, start, out var parsed, out var err), err);
        Assert.True(parsed.ReliefDenoiseSimd);

        string off = BatchCommandBuilder.Build(new BatchCommandSnapshot
        {
            ReliefEnabled = true, ReliefRaymarch = true, ReliefDenoiseIterations = 2,
        });
        Assert.DoesNotContain("--denoise-simd", off);
    }
}
