// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Regression for #656 — Relief 3D poster/wallpaper rendered "out of sync" (colour
// vs relief) when the export aspect differed from the on-screen window aspect. The
// calculator maps the complex plane by pixel aspect (scale = 3.5/max(W,H)/Zoom), so
// an export at a different aspect covers a different complex rectangle than the
// screen. #508 shipped the on-screen relief field snapshot and HeightDe stretched it
// to the OUTPUT aspect, desyncing it from the (recomputed, output-aspect) albedo.
//
// The fix (PosterRenderer.ResolveReliefField) uses the caller snapshot ONLY when its
// aspect matches the output; otherwise it recomputes an aspect-correct field —
// falling back to the albedo calc's output-dims SmoothBuffer at/above the field floor
// (null return), and upsampling a dedicated field at the output aspect below it.
// These assert that decision so a poster/wallpaper and its albedo always share the
// same complex view.

using System;
using FracturingFog;
using FracturingFog.Imaging;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class PosterReliefAspectTests
{
    private static FractalParameters ReliefParams() => new()
    {
        Relief2DEnabled = true,
        Relief2DRaymarch = true,
        Relief2DHeightScale = 1.4,
        Relief2DFieldFloor = 1080,     // default floor
        Relief2DGpuRaymarch = false,   // CPU trace — deterministic, no device
    };

    private static PosterRequest MandelReq(FractalParameters fp, int w, int h,
        float[]? reliefField, int rfW, int rfH) => new()
    {
        FractalType = FractalType.Mandelbrot,
        CenterX = -0.5, CenterY = 0,
        Zoom = 1.0,
        MaxIterations = 200,
        Width = w, Height = h,
        ColorMap = ColorPalette.BuiltIns[0],
        Quality = QualityPreset.Standard,
        FractalParameters = fp,
        ReliefField = reliefField,
        ReliefFieldW = rfW,
        ReliefFieldH = rfH,
        Format = ImageFileFormat.Png,
    };

    private static float[] Field(int fw, int fh) => new float[fw * fh];

    // Snapshot whose aspect matches the output is honoured verbatim (WYSIWYG poster
    // at the on-screen aspect — e.g. the same-dims preview). This is the #508 path,
    // preserved.
    [Fact]
    public void Snapshot_Used_When_Aspect_Matches()
    {
        var fp = ReliefParams();
        // Field aspect 480/360 = 1.333; output 96x72 = 1.333 → match.
        var snap = Field(480, 360);
        var req = MandelReq(fp, 96, 72, snap, 480, 360);

        var got = PosterRenderer.ResolveReliefField(req, 96, 72, default, out int fw, out int fh);

        Assert.Same(snap, got);
        Assert.Equal(480, fw);
        Assert.Equal(360, fh);
    }

    // #656 core — a snapshot at a DIFFERENT aspect than the output must NOT be reused
    // (it would stretch vs the albedo). At/above the field floor the output-dims
    // SmoothBuffer is aspect-correct, so ResolveReliefField returns null (use the
    // height source) rather than the mismatched snapshot.
    [Fact]
    public void Mismatched_Aspect_At_Floor_Falls_Back_To_SmoothBuffer()
    {
        var fp = ReliefParams();
        // On-screen field aspect 442/408 ≈ 1.083; export 1920x1080 ≈ 1.778 (short
        // axis 1080 == floor) → snapshot rejected, null (SmoothBuffer path).
        var snap = Field(442, 408);
        var req = MandelReq(fp, 1920, 1080, snap, 442, 408);

        var got = PosterRenderer.ResolveReliefField(req, 1920, 1080, default, out int fw, out int fh);

        Assert.Null(got);
        Assert.Equal(0, fw);
        Assert.Equal(0, fh);
    }

    // #656 — below the floor a mismatched-aspect export recomputes a DEDICATED field
    // at the OUTPUT aspect (upsampled to the floor), so a small cross-aspect poster
    // keeps hi-res quality AND matches the albedo view. The returned field's aspect
    // tracks the output aspect, not the snapshot's.
    [Fact]
    public void Mismatched_Aspect_Below_Floor_Recomputes_At_Output_Aspect()
    {
        var fp = ReliefParams();
        // On-screen field aspect ≈ 1.083; export 1024x768 = 1.333 (short axis 768 <
        // floor 1080) → recompute a dedicated field at the OUTPUT aspect.
        var snap = Field(442, 408);
        var req = MandelReq(fp, 1024, 768, snap, 442, 408);

        var got = PosterRenderer.ResolveReliefField(req, 1024, 768, default, out int fw, out int fh);

        Assert.NotNull(got);
        Assert.NotSame(snap, got);
        Assert.Equal(fw * fh, got!.Length);
        Assert.True(Math.Min(fw, fh) >= 1080, "field short axis should hit the floor");
        // Field aspect matches the OUTPUT aspect (1024/768), NOT the snapshot's.
        double outAspect = 1024.0 / 768.0;
        Assert.InRange((double)fw / fh, outAspect - 0.02, outAspect + 0.02);
    }
}
