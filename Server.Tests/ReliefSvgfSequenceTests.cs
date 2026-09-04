// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Roadmap slice S4 (3D-Rendering-Roadmap.md, #389 / #402) — the SVGF SEQUENCE seam:
// PosterRequest.SvgfHistory threads a persistent SvgfHistory through PosterRenderer →
// ApplyReliefIfEnabled → ReliefDenoisePass.ApplySvgf, so the offline sequence
// renderers (which all build PosterRequests) run the united temporal + variance-guided
// denoise. Locks: a temporal denoise render through PosterRenderer populates the
// history (and carries its camera forward); the temporal toggle off leaves the history
// untouched (the plain single-frame denoise runs instead).

using FracturingFog;
using FracturingFog.Imaging;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class ReliefSvgfSequenceTests
{
    private static PosterRequest ReliefRequest(bool temporal, SvgfHistory? history,
        ReliefMotionVectorAlias previous = default)
    {
        var fp = new FractalParameters
        {
            Relief2DEnabled = true,
            Relief2DRaymarch = true,
            Relief2DHeightScale = 1.4,
            Relief2DCameraAzimuthDeg = 25,
            Relief2DCameraElevationDeg = 45,
            Relief2DCameraFovDeg = 55,
            Relief2DGroundPlane = false,
            Relief2DDenoiseIterations = 3,      // denoise on
            Relief2DDenoiseColorSigma = 0.08,
            Relief2DDenoiseTemporal = temporal,
        };
        return new PosterRequest
        {
            FractalType = FractalType.Mandelbrot,
            CenterX = -0.5, CenterY = 0, Zoom = 1.0,
            MaxIterations = 150,
            Width = 96, Height = 72,
            ColorMap = ColorPalette.BuiltIns[0],
            Quality = QualityPreset.Standard,
            FractalParameters = fp,
            Path = "unused.png",
            Format = ImageFileFormat.Png,
            SvgfHistory = history,
            PreviousCamera = previous.Cam,
        };
    }

    // Small wrapper so the default-parameter signature stays clean.
    public readonly struct ReliefMotionVectorAlias
    {
        public FracturingFog.Rendering.Lighting.ReliefMotionVector.CameraView? Cam { get; init; }
    }

    [Fact]
    public void Temporal_Denoise_Populates_The_History_Through_PosterRenderer()
    {
        var history = new SvgfHistory();
        Assert.False(history.Valid);

        PosterRenderer.RenderToPixels(ReliefRequest(temporal: true, history), default, out _, out _);

        Assert.True(history.Valid, "SVGF temporal denoise did not run through PosterRenderer");
        Assert.NotNull(history.Color);
        Assert.True(history.PrevCamera.HasValue, "the history did not capture the frame's camera");
    }

    [Fact]
    public void Second_Frame_Reuses_The_History_And_Stays_Valid()
    {
        var history = new SvgfHistory();
        PosterRenderer.RenderToPixels(ReliefRequest(temporal: true, history), default, out _, out _);
        var camAfterFirst = history.PrevCamera;

        // Feed the captured camera back as the previous camera (what a sequence
        // renderer does) → the second frame reprojects against the seeded history.
        var beauty = PosterRenderer.RenderToPixels(
            ReliefRequest(temporal: true, history,
                new ReliefMotionVectorAlias { Cam = camAfterFirst }),
            default, out int w, out int h);

        Assert.True(history.Valid);
        Assert.Equal(96 * 72, w * h);
        // A real 3D relief frame is not an all-black wash.
        int nonBlack = 0;
        foreach (var px in beauty) if ((px & 0x00FFFFFF) != 0) nonBlack++;
        Assert.True(nonBlack > 0, "second SVGF frame is all black");
    }

    [Fact]
    public void Temporal_Off_Leaves_The_History_Untouched()
    {
        var history = new SvgfHistory();
        PosterRenderer.RenderToPixels(ReliefRequest(temporal: false, history), default, out _, out _);
        Assert.False(history.Valid);       // plain denoise path → ApplySvgf never runs
        Assert.Null(history.Color);
    }
}
