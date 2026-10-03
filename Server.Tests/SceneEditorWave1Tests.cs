// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Epic #943 Wave 1 — Scene Editor: Animation "Edit…" (#1055), per-shot combo
// right-click menus + sorting (#1054), and the GIF export encode (#1053).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using FracturingFog;
using FracturingFog.Abstractions.Assets;
using FracturingFog.Batch;
using FracturingFog.Imaging;
using FracturingFog.Models;
using FracturingFog.UI.Avalonia.ViewModels;
using Xunit;

using LibraryService = FracturingFog.Server.Tests.SceneEditorPickerRefreshTests.LibraryService;

namespace FracturingFog.Server.Tests;

public sealed class SceneEditorWave1Tests
{
    // ── #1055 Animation "Edit…" ────────────────────────────────────────────

    [Fact]
    public void EditAnimationButton_RaisesEditAsset_WithShotAnimation()
    {
        var (svc, state) = LibraryService.Create();
        state.Animations = new() { "Spin" };
        var vm = new SceneEditorViewModel(svc);
        var raised = new List<SceneEditAssetEventArgs>();
        vm.EditAssetRequested += (_, e) => raised.Add(e);
        var row = vm.Shots.Single();

        row.EditAnimationCommand.Execute().Subscribe();       // "(none)" → open without a target
        row.SelectedAnimation = "Spin";
        row.EditAnimationCommand.Execute().Subscribe();

        Assert.Equal(new[]
        {
            new SceneEditAssetEventArgs(AssetKind.Animation, null),
            new SceneEditAssetEventArgs(AssetKind.Animation, "Spin"),
        }, raised);
    }

    // ── #1054 right-click menus ───────────────────────────────────────────

    [Fact]
    public void RegionMenu_OffersEditOnlyForARealPick_AndRaisesEditAsset()
    {
        var (svc, state) = LibraryService.Create();
        state.Regions = new() { "Seahorse" };
        var vm = new SceneEditorViewModel(svc);
        SceneEditAssetEventArgs? raised = null;
        vm.EditAssetRequested += (_, e) => raised = e;
        var row = vm.Shots.Single();

        Assert.DoesNotContain(row.RegionMenu(), i => i.Header == "Edit region…");

        row.SelectedRegion = "Seahorse";
        row.RegionMenu().Single(i => i.Header == "Edit region…").Invoke!();

        Assert.Equal(new SceneEditAssetEventArgs(AssetKind.Region, "Seahorse"), raised);
    }

    [Fact]
    public void ThemeAndLightingMenus_EditTheirOwnPick()
    {
        var (svc, state) = LibraryService.Create();
        state.Regions = new() { "A", "B" };
        state.Themes = new() { "Fire" };
        var vm = new SceneEditorViewModel(svc);
        var raised = new List<SceneEditAssetEventArgs>();
        vm.EditAssetRequested += (_, e) => raised.Add(e);
        var row = vm.Shots.Single();
        row.SelectedRegion = "A";
        row.SelectedLightingRegion = "B";
        row.SelectedTheme = "Fire";

        row.LightingRegionMenu().Single(i => i.Header == "Edit region…").Invoke!();
        row.ThemeMenu().Single(i => i.Header == "Edit theme…").Invoke!();

        Assert.Equal(new[]
        {
            new SceneEditAssetEventArgs(AssetKind.Region, "B"),
            new SceneEditAssetEventArgs(AssetKind.ColorTheme, "Fire"),
        }, raised);
    }

    [Fact]
    public void RegionSortByType_FiltersList_ButKeepsTheCurrentPick()
    {
        var (svc, state) = LibraryService.Create();
        state.Regions = new() { "Bulb1", "Box1", "Seahorse" };
        state.RegionTypes["Bulb1"] = FractalType.Mandelbulb;
        state.RegionTypes["Box1"] = FractalType.Mandelbox;
        state.RegionTypes["Seahorse"] = FractalType.Mandelbrot;
        var vm = new SceneEditorViewModel(svc);
        var row = vm.Shots.Single();
        row.SelectedRegion = "Seahorse";

        row.RegionMenu().Single(i => i.Header == nameof(FractalType.Mandelbulb)).Invoke!();

        // Filtered to Mandelbulb, but the Mandelbrot pick stays listed + selected
        // (filtered, not deleted), and the service placeholder header is stripped.
        Assert.Equal(new[] { SceneShotRowViewModel.RegionNone, "Bulb1", "Seahorse" }, row.RegionNames);
        Assert.Equal("Seahorse", row.SelectedRegion);
        Assert.Equal("Seahorse", row.ToShot().RegionName);
        // The lighting combo has its own, independent sort.
        Assert.Contains("Box1", row.LightingRegionNames);
    }

    [Fact]
    public void FractalFilter3D_ListsOnly3DTypes_PlusTheCurrentType()
    {
        var (svc, _) = LibraryService.Create();
        var vm = new SceneEditorViewModel(svc);
        var row = vm.Shots.Single();
        row.FractalType = FractalType.Julia;

        row.FractalTypeMenu().Single(i => i.Header == "3D").Invoke!();

        Assert.Contains(FractalType.Mandelbulb, row.FractalTypes);
        Assert.Contains(FractalType.Julia, row.FractalTypes);     // current pick kept
        Assert.DoesNotContain(FractalType.Mandelbrot, row.FractalTypes);
        Assert.Equal(FractalType.Julia, row.FractalType);
    }

    [Fact]
    public void ThemeCompatMode_TracksTheShotsFractalType()
    {
        var (svc, state) = LibraryService.Create();
        var vm = new SceneEditorViewModel(svc);
        var row = vm.Shots.Single();
        row.ThemeMenu().Single(i => i.Header.StartsWith("Compatible", StringComparison.Ordinal)).Invoke!();

        row.FractalType = FractalType.Kifs;

        Assert.Equal((ThemeSortMode.ByFractalCompat, (string?)null, (FractalType?)FractalType.Kifs), state.LastThemeQuery);
        Assert.Contains("Compatible with Kifs", row.ThemeMenu().Select(i => i.Header));
    }

    [Fact]
    public void DefaultSortedLists_AreEnumeratedOncePerRefresh_NotPerRow()
    {
        var (svc, state) = LibraryService.Create();
        state.Regions = new() { "A" };
        var vm = new SceneEditorViewModel(svc);
        for (int i = 0; i < 4; i++) vm.AddShotCommand.Execute().Subscribe();
        int afterBuild = state.SortedRegionCalls;

        vm.RefreshNameLists();

        Assert.Equal(1, afterBuild);
        Assert.Equal(2, state.SortedRegionCalls);
    }

    [Fact]
    public void SceneNames_AreSortedAToZ()
    {
        var (svc, _) = SceneNameService.Create(new[] { "zeta", "Alpha", "mid" });
        var vm = new SceneEditorViewModel(svc);
        Assert.Equal(new[] { "Alpha", "mid", "zeta" }, vm.SceneNames);
    }

    // ── #1053 GIF encode ──────────────────────────────────────────────────

    [Theory]
    [InlineData(30, 30, 100)]   // 3.33 cs/frame → 3,3,4… sums to exactly 1 s
    [InlineData(24, 48, 200)]
    [InlineData(10, 7, 70)]
    public void FrameTimestamps_SitOnTheCentisecondGrid_WithoutDrift(int fps, int frames, int totalCs)
    {
        long end = GifFolderEncoder.FrameTimestamp(frames, fps);
        Assert.Equal(totalCs * 100_000L, end);
        for (int i = 0; i < frames; i++)
        {
            long d = GifFolderEncoder.FrameTimestamp(i + 1, fps) - GifFolderEncoder.FrameTimestamp(i, fps);
            Assert.Equal(0, d % 100_000L);                       // whole centiseconds
            Assert.InRange(d / 100_000L, (long)Math.Floor(100.0 / fps), (long)Math.Ceiling(100.0 / fps));
        }
    }

    [Fact]
    public void Encode_PngFolder_WritesLoopingGif_WithExactTotalDuration()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ff-gif-test-" + Guid.NewGuid().ToString("N"));
        string gifPath = Path.Combine(dir, "out.gif");
        Directory.CreateDirectory(dir);
        try
        {
            const int w = 8, h = 6, frames = 12, fps = 30;
            using (var png = new PngSequenceWriter(dir, w, h))
                for (int f = 0; f < frames; f++)
                {
                    var px = new uint[w * h];
                    for (int i = 0; i < px.Length; i++) px[i] = 0xFF000000u | (uint)(f * 20) << 16 | (uint)(i * 5);
                    png.WriteFrame(px);
                }

            var (ok, log) = GifFolderEncoder.Encode(dir, gifPath, fps);

            Assert.True(ok, log);
            var bytes = File.ReadAllBytes(gifPath);
            Assert.Equal("GIF89a", System.Text.Encoding.ASCII.GetString(bytes, 0, 6));
            var delays = GraphicControlDelays(bytes);
            Assert.Equal(frames, delays.Count);
            // 12 frames @ 30 fps = 0.4 s; last frame takes the default delay
            // (round(100/30) = 3 cs) so the total is 11 grid steps + 3.
            long grid = GifFolderEncoder.FrameTimestamp(frames - 1, fps) / 100_000L;
            Assert.Equal(grid + 3, delays.Sum());
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Encode_EmptyFolder_FailsWithoutWritingAFile()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ff-gif-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var (ok, log) = GifFolderEncoder.Encode(dir, Path.Combine(dir, "x.gif"), 30);
            Assert.False(ok);
            Assert.Contains("no frame_", log);
            Assert.False(File.Exists(Path.Combine(dir, "x.gif")));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Theory]
    [InlineData("scene")]
    [InlineData("slideshow")]
    public void EncodeGif_ParsesInSceneAndSlideshowModes(string mode)
    {
        var args = mode == "scene"
            ? new[] { "--scene", "X", "--encode", "gif", "--out", "o.gif" }
            : new[] { "--slideshow", "s", "--encode", "gif", "--out", "o.gif" };
        Assert.True(BatchOptions.TryParse(args, 0, out var o, out var err), err);
        Assert.Equal(BatchLossless.Gif, o.SlideshowEncode);
    }

    // GIF89a Graphic Control Extension (21 F9 04 …) delay fields, in centiseconds.
    // Walks the block structure rather than byte-scanning so LZW data can't alias.
    private static List<int> GraphicControlDelays(byte[] b)
    {
        var delays = new List<int>();
        int p = 13;
        if ((b[10] & 0x80) != 0) p += 3 * (1 << ((b[10] & 7) + 1)); // global colour table
        while (p < b.Length)
        {
            byte block = b[p];
            if (block == 0x3B) break;                                  // trailer
            if (block == 0x21)
            {
                byte label = b[p + 1];
                if (label == 0xF9) delays.Add(b[p + 4] | (b[p + 5] << 8));
                p += 2;
                while (b[p] != 0) p += b[p] + 1;                        // sub-blocks
                p++;
            }
            else if (block == 0x2C)
            {
                byte packed = b[p + 9];
                p += 10;
                if ((packed & 0x80) != 0) p += 3 * (1 << ((packed & 7) + 1)); // local table
                p++;                                                    // LZW min code size
                while (b[p] != 0) p += b[p] + 1;
                p++;
            }
            else throw new InvalidDataException($"Unexpected GIF block 0x{block:X2} at {p}.");
        }
        return delays;
    }

    // Minimal service that only lists scenes (everything else default / empty).
    private class SceneNameService : System.Reflection.DispatchProxy
    {
        private string[] _scenes = Array.Empty<string>();

        public static (IColorThemeService, string[]) Create(string[] scenes)
        {
            var svc = Create<IColorThemeService, SceneNameService>();
            ((SceneNameService)(object)svc)._scenes = scenes;
            return (svc, scenes);
        }

        protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(IColorThemeService.EnumerateSceneNames)) return _scenes;
            var rt = method.ReturnType;
            if (rt == typeof(void)) return null;
            if (rt != typeof(string) && rt.IsAssignableFrom(typeof(string[]))) return Array.Empty<string>();
            return rt.IsValueType ? Activator.CreateInstance(rt) : null;
        }
    }
}
