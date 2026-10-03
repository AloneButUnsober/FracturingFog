// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1065 — opt-in scene debug overlay burned into exported frames, plus its batch
// switch. Checked against independent facts rather than the overlay's own output:
//   - with a static camera every underlying frame is the same picture, so with the
//     overlay on frames may differ ONLY inside the bottom-left panel (the clock
//     ticks), and with it off they are identical;
//   - the overlay text carries the shot and the camera pose the keys put there.
//
// Runs under the test data-root redirect (FractalRegionLibraryCollection).

using System;
using System.IO;
using System.Linq;
using System.Threading;

using FracturingFog.Abstractions.Animation;
using FracturingFog.Batch;
using FracturingFog.Export;
using FracturingFog.Models;
using FracturingFog.Render;
using SkiaSharp;
using Xunit;

namespace FracturingFog.Server.Tests;

[Collection(FractalRegionLibraryCollection.Name)]
public sealed class SceneDebugOverlayExportTests
{
    private const int W = 200, H = 120;
    private const uint Ink = 0xFFDDDDDDu;

    private static SceneData StaticBulbScene()
    {
        var track = new CameraTrack { Interpolation = CameraInterpolation.Linear };
        track.Add(new CameraKey(0, new CameraState(3.0, 0.5, 1.2)));
        track.Add(new CameraKey(1, new CameraState(3.0, 0.5, 1.2)));
        return new SceneData
        {
            Name = "FF-1065",
            Shots =
            {
                new SceneShot
                {
                    Name = "Bulb", FractalType = FractalType.Mandelbulb,
                    DurationSeconds = 1.0, Transition = SceneTransitionKind.Cut, Camera = track,
                },
            },
        };
    }

    private static System.Collections.Generic.List<uint[]> Export(bool overlay)
    {
        string outDir = Path.Combine(Path.GetTempPath(), "FracturingFog", "1065-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outDir);
        try
        {
            var result = SceneVideoRenderer.Render(StaticBulbScene(), new SceneVideoOptions
            {
                Width = W, Height = H, OutputPath = outDir, KeepFrames = true, EncodeGif = true,
                DebugOverlay = overlay,
                Settings = new SceneRenderSettings { Fps = 3, MotionBlurSubframes = 1 },
            }, null, CancellationToken.None);
            Assert.True(result.FramesWritten >= 3, result.Message);

            var frames = new System.Collections.Generic.List<uint[]>();
            for (int f = 1; f <= result.FramesWritten; f++)
            {
                using var bmp = SKBitmap.Decode(Path.Combine(result.FrameFolder!, $"frame_{f:000000}.png"));
                frames.Add(bmp.Pixels.Select(c => (uint)c).ToArray());
            }
            try { Directory.Delete(result.FrameFolder!, true); } catch { }
            return frames;
        }
        finally { try { Directory.Delete(outDir, true); } catch { } }
    }

    [Fact]
    public void Export_OverlayOnlyTouchesTheBottomLeftPanel_AndTracksTheClock()
    {
        var off = Export(false);
        var on = Export(true);
        Assert.Equal(off.Count, on.Count);

        // Static camera → the picture never changes; without the overlay the frames match.
        Assert.All(off.Skip(1), f => Assert.Equal(off[0], f));

        // Panel: 8 px margins, 4 px padding, 9 px per text line (7 px glyphs + 2 lead).
        int lines = SceneDebugInfo.Format(StaticBulbScene(), SceneTimeline.Build(StaticBulbScene()).Sample(0), 0, 1,
            new CameraState(3, 0.5, 1.2)).Split('\n').Length;
        int panelTop = H - 8 - (8 + 9 * lines - 2);

        for (int k = 0; k < on.Count; k++)
        {
            int inkTop = H;
            for (int i = 0; i < W * H; i++)
            {
                int x = i % W, y = i / W;
                if (on[k][i] == Ink) inkTop = Math.Min(inkTop, y);
                if (on[k][i] == off[k][i]) continue;
                // Every changed pixel lies in the panel (dark-over-black edges don't
                // change, so the panel is bounded, not matched exactly).
                Assert.True(x >= 8 && y >= panelTop && y < H - 8, $"frame {k}: overlay pixel at ({x},{y}) outside the panel");
            }
            Assert.Equal(panelTop + 4, inkTop);    // first text line sits under the 4 px padding
        }

        // The clock advances, so the burned text differs frame to frame.
        Assert.NotEqual(on[0], on[1]);
    }

    [Fact]
    public void OverlayText_NamesTheShot_AndThePoseTheKeysSet()
    {
        var scene = StaticBulbScene();
        var tl = SceneTimeline.Build(scene);
        var p = new FractalParameters();
        CameraParamBinding.ApplyFor(p, FractalType.Mandelbulb, scene.Shots[0].Camera!.Evaluate(0.5));
        string text = SceneVideoRenderer.DebugOverlayText(scene, tl.Sample(0.5), 0.5, tl.TotalDuration, p, FractalType.Mandelbulb);

        Assert.Contains("Shot 1/1", text);
        Assert.Contains("\"Bulb\"", text);
        Assert.Contains("t 0.50 / 1.00 s", text);
        Assert.Contains("Pose dist 3.000", text);
        Assert.Contains("el 68.8°", text);                                 // φ = 1.2 rad
    }

    [Fact]
    public void Glyphs_TheOverlayPunctuationIsDrawn()
    {
        foreach (var s in new[] { "\"", ">", ",", "°" })
        {
            var buf = new uint[20 * 12];
            FracturingFog.Rendering.Lighting.ScreenSpacePost.DrawText(buf, 20, 12, 1, 1, s, Ink);
            Assert.Contains(Ink, buf);
        }
    }

    // ── Scene Editor ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Editor_Checkbox_ReachesTheExportSettings(bool burn)
    {
        var (svc, _) = SceneEditorPickerRefreshTests.LibraryService.Create();
        var vm = new FracturingFog.UI.Avalonia.ViewModels.SceneEditorViewModel(svc) { ExportDebugOverlay = burn };
        FracturingFog.UI.Avalonia.ViewModels.SceneExportSettings? got = null;
        vm.ExportSceneRequested += (_, a) => { got = a.Settings; a.Completion.TrySetResult(true); };
        vm.ExportCommand.Execute().Subscribe();
        Assert.NotNull(got);
        Assert.Equal(burn, got!.BurnDebugOverlay);
    }

    // ── Batch parity ──────────────────────────────────────────────────────

    [Fact]
    public void BatchFlag_ParsesForSceneMode_DefaultOff()
    {
        Assert.True(BatchOptions.TryParse(new[] { "--scene", "X", "--out", "o.mp4", "--scene-debug-overlay" }, 0, out var on, out var e1), e1);
        Assert.True(on.SceneDebugOverlay);
        Assert.True(BatchOptions.TryParse(new[] { "--scene", "X", "--out", "o.mp4" }, 0, out var off, out var e2), e2);
        Assert.False(off.SceneDebugOverlay);
        Assert.Contains(BatchFlagCatalog.All, f => f.Name == BatchFlags.SceneDebugOverlay);
    }
}
