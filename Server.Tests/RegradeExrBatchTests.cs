// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Roadmap slice S2 (#396) — the EXR read-back BATCH surface. `--regrade-exr IN.exr`
// selects Regrade mode: read a scene-linear EXR, apply --view-transform + --exposure,
// write to --out — no fractal render, so no region/coord is required. These lock the
// CLI grammar; the actual read→tonemap→write (BatchRenderer.RenderRegrade, WinExe-only)
// is build-verified, and the underlying ExrRegrade is covered by ExrReadBackTests.

using FracturingFog.Batch;
using FracturingFog.Imaging;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class RegradeExrBatchTests
{
    private static string[] Args(params string[] tail)
    {
        var head = new[] { "app.exe", "--batch" };
        var all = new string[head.Length + tail.Length];
        head.CopyTo(all, 0);
        tail.CopyTo(all, head.Length);
        return all;
    }

    [Fact]
    public void RegradeExr_SelectsRegradeMode_NoRegionRequired()
    {
        // Note: NO --region / --x-y-zoom — regrade must not demand a fractal source.
        Assert.True(BatchOptions.TryParse(
            Args("--regrade-exr", "in.exr", "--out", "out.png",
                 "--view-transform", "agx", "--exposure", "1.5"),
            startIndex: 2, out var opts, out var err), err);

        Assert.Equal(BatchMode.Regrade, opts.Mode);
        Assert.Equal("in.exr", opts.RegradeExrInput);
        Assert.Equal("out.png", opts.OutputPath);
        Assert.Equal(ViewTransform.AgX, opts.ViewTransform);
        Assert.Equal(1.5, opts.ViewExposureEv);
    }

    [Fact]
    public void RegradeExr_RequiresOutput()
    {
        Assert.False(BatchOptions.TryParse(
            Args("--regrade-exr", "in.exr"), startIndex: 2, out _, out var err));
        Assert.Contains("--out", err);
    }

    [Fact]
    public void RegradeExr_MissingInputValue_Errors()
    {
        // Flag present but no following value.
        Assert.False(BatchOptions.TryParse(
            Args("--out", "out.png", "--regrade-exr"), startIndex: 2, out _, out _));
    }

    [Fact]
    public void RegradeExr_ExposureRangeChecked()
    {
        Assert.False(BatchOptions.TryParse(
            Args("--regrade-exr", "in.exr", "--out", "out.png", "--exposure", "99"),
            startIndex: 2, out _, out var err));
        Assert.Contains("exposure", err);
    }

    [Fact]
    public void RegradeExr_DefaultsToNoneTransform()
    {
        Assert.True(BatchOptions.TryParse(
            Args("--regrade-exr", "in.exr", "--out", "out.png"),
            startIndex: 2, out var opts, out var err), err);
        Assert.Equal(BatchMode.Regrade, opts.Mode);
        Assert.Null(opts.ViewTransform);          // None (identity) by default
        Assert.Null(opts.ViewExposureEv);
    }
}
