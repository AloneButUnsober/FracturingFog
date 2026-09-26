// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.IO;

using FracturingFog.Abstractions;
using FracturingFog.Models;
using FracturingFog.Render;
using Xunit;

namespace FracturingFog.Server.Tests;

/// <summary>
/// #946 — Quick Record preferences persist through LiveRecordingPrefsStore
/// (data root is redirected per test process by TestDataRootIsolation).
/// </summary>
public sealed class LiveRecordingPrefsTests
{
    private static string File_ => Path.Combine(AppDataPaths.Root, "live-recording.json");

    [Fact]
    public void Save_then_load_round_trips_every_field_and_raises_changed()
    {
        LiveRecordingPrefsStore.ResetForTests();
        int changed = 0;
        Action h = () => changed++;
        LiveRecordingPrefsStore.Changed += h;
        try
        {
            var p = LiveRecordingPrefsStore.Current;
            p.CaptureFps = 60;
            p.SetExportOptions(new LiveRecordingExportOptions
            {
                Format = LiveRecordingFormat.WebmVp9, Quality = LiveRecordingQuality.Low, Fps = 24, ScalePercent = 50,
            });
            p.AskOnStop = false;
            p.OutputFolder = @"C:\clips";
            LiveRecordingPrefsStore.Save();
            Assert.Equal(1, changed);

            var back = LiveRecordingPrefsStore.Load();
            Assert.Equal(60, back.CaptureFps);
            Assert.Equal(LiveRecordingFormat.WebmVp9, back.Format);
            Assert.Equal(LiveRecordingQuality.Low, back.Quality);
            Assert.Equal(24, back.OutputFps);
            Assert.Equal(50, back.ScalePercent);
            Assert.False(back.AskOnStop);
            Assert.Equal(@"C:\clips", back.OutputFolder);
            Assert.Contains("\"webmVp9\"", File.ReadAllText(File_), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            LiveRecordingPrefsStore.Changed -= h;
            try { File.Delete(File_); } catch { }
            LiveRecordingPrefsStore.ResetForTests();
        }
    }

    [Fact]
    public void Out_of_range_values_are_clamped_on_load()
    {
        Directory.CreateDirectory(AppDataPaths.Root);
        File.WriteAllText(File_, "{\"captureFps\": 500, \"outputFps\": 0, \"scalePercent\": 3, \"format\": \"Nope\"}");
        try
        {
            var p = LiveRecordingPrefsStore.Load();
            Assert.Equal(new LiveRecordingPrefs().Format, p.Format); // bad enum → whole file rejected → defaults
            File.WriteAllText(File_, "{\"captureFps\": 500, \"outputFps\": 0, \"scalePercent\": 3}");
            p = LiveRecordingPrefsStore.Load();
            Assert.Equal(60, p.CaptureFps);
            Assert.Equal(1, p.OutputFps);
            Assert.Equal(10, p.ScalePercent);
        }
        finally { try { File.Delete(File_); } catch { } }
    }

    [Fact]
    public void Empty_folder_resolves_to_a_default_location()
    {
        var p = new LiveRecordingPrefs { OutputFolder = "" };
        Assert.EndsWith("FracturingFog", p.ResolveOutputFolder());
        p.OutputFolder = @"D:\x";
        Assert.Equal(@"D:\x", p.ResolveOutputFolder());
    }
}
