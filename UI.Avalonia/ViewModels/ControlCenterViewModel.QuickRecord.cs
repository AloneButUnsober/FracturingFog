// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// ControlCenterViewModel.QuickRecord.cs
//
// Capture ▸ Quick Record group (#946): start/stop instant recording plus its
// persisted preferences — capture rate, default export format / quality /
// fps / size, and "ask on stop" vs "save straight to a folder". Every edit
// writes through LiveRecordingPrefsStore; a save from the Save Recording
// prompt (which remembers the last choices) raises Changed and refreshes the
// bound controls here.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;

using Avalonia.Threading;

using ReactiveUI;

using FracturingFog.Models;
using FracturingFog.Render;

namespace FracturingFog.UI.Avalonia.ViewModels;

public sealed partial class ControlCenterViewModel
{
    private static readonly (LiveRecordingFormat F, string Label)[] QuickFormats =
    {
        (LiveRecordingFormat.Mp4H264, "MP4 — H.264"),
        (LiveRecordingFormat.Mp4H265, "MP4 — H.265 (ffmpeg)"),
        (LiveRecordingFormat.WebmVp9, "WebM — VP9 (ffmpeg)"),
        (LiveRecordingFormat.MkvFfv1, "MKV — FFV1 lossless (ffmpeg)"),
        (LiveRecordingFormat.Gif, "Animated GIF"),
        (LiveRecordingFormat.PngSequence, "PNG sequence"),
    };

    private static readonly (LiveRecordingQuality Q, string Label)[] QuickQualities =
    {
        (LiveRecordingQuality.Lossless, "Lossless"),
        (LiveRecordingQuality.High, "High"),
        (LiveRecordingQuality.Medium, "Medium"),
        (LiveRecordingQuality.Low, "Low"),
    };

    public IReadOnlyList<string> QuickRecordFormatChoices { get; } = QuickFormats.Select(x => x.Label).ToList();
    public IReadOnlyList<string> QuickRecordQualityChoices { get; } = QuickQualities.Select(x => x.Label).ToList();
    public IReadOnlyList<int> QuickRecordFpsChoices { get; } = new[] { 15, 24, 25, 30, 50, 60 };
    public IReadOnlyList<int> QuickRecordScaleChoices { get; } = new[] { 100, 75, 50, 25 };

    /// <summary>Host-supplied folder picker (UI.Avalonia can't reach AvaloniaDialogs).</summary>
    public Func<Task<string?>>? QuickRecordFolderPickRequested;

    public ReactiveCommand<Unit, Unit> BrowseQuickRecordFolderCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ClearQuickRecordFolderCommand { get; private set; } = null!;

    private static LiveRecordingPrefs Prefs => LiveRecordingPrefsStore.Current;

    private void InitQuickRecord()
    {
        BrowseQuickRecordFolderCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            var pick = QuickRecordFolderPickRequested;
            if (pick == null) return;
            string? folder = await pick();
            if (!string.IsNullOrWhiteSpace(folder)) QuickRecordFolder = folder;
        });
        ClearQuickRecordFolderCommand = ReactiveCommand.Create(() => { QuickRecordFolder = ""; });

        // App-lifetime VM (see UiScaleService note in the ctor) — no leak.
        LiveRecordingPrefsStore.Changed += () =>
            Dispatcher.UIThread.Post(RaiseQuickRecordChanged);
    }

    private void RaiseQuickRecordChanged()
    {
        foreach (var n in new[]
        {
            nameof(QuickRecordCaptureFps), nameof(QuickRecordFormatIndex), nameof(QuickRecordQualityIndex),
            nameof(QuickRecordOutputFps), nameof(QuickRecordScale), nameof(QuickRecordAskOnStop),
            nameof(QuickRecordFolder), nameof(QuickRecordFolderDisplay), nameof(IsQuickRecordQualityApplicable),
        })
            this.RaisePropertyChanged(n);
    }

    // Mutate + persist. Save raises Changed, which refreshes every binding.
    private static void Update(Action<LiveRecordingPrefs> edit)
    {
        edit(Prefs);
        LiveRecordingPrefsStore.Save();
    }

    public int QuickRecordCaptureFps
    {
        get => Prefs.CaptureFps;
        set { if (value > 0 && value != Prefs.CaptureFps) Update(p => p.CaptureFps = value); }
    }

    public int QuickRecordFormatIndex
    {
        get => Math.Max(0, Array.FindIndex(QuickFormats, x => x.F == Prefs.Format));
        set
        {
            if (value < 0 || value >= QuickFormats.Length || QuickFormats[value].F == Prefs.Format) return;
            Update(p => p.Format = QuickFormats[value].F);
        }
    }

    public int QuickRecordQualityIndex
    {
        get => Math.Max(0, Array.FindIndex(QuickQualities, x => x.Q == Prefs.Quality));
        set
        {
            if (value < 0 || value >= QuickQualities.Length || QuickQualities[value].Q == Prefs.Quality) return;
            Update(p => p.Quality = QuickQualities[value].Q);
        }
    }

    /// <summary>Quality is ignored by the inherently lossless formats.</summary>
    public bool IsQuickRecordQualityApplicable =>
        Prefs.Format is not (LiveRecordingFormat.MkvFfv1 or LiveRecordingFormat.PngSequence);

    public int QuickRecordOutputFps
    {
        get => Prefs.OutputFps;
        set { if (value > 0 && value != Prefs.OutputFps) Update(p => p.OutputFps = value); }
    }

    public int QuickRecordScale
    {
        get => Prefs.ScalePercent;
        set { if (value > 0 && value != Prefs.ScalePercent) Update(p => p.ScalePercent = value); }
    }

    /// <summary>True → Save Recording prompt on stop; false → save straight to
    /// <see cref="QuickRecordFolder"/> with the defaults.</summary>
    public bool QuickRecordAskOnStop
    {
        get => Prefs.AskOnStop;
        set { if (value != Prefs.AskOnStop) Update(p => p.AskOnStop = value); }
    }

    public string QuickRecordFolder
    {
        get => Prefs.OutputFolder;
        set { if ((value ?? "") != Prefs.OutputFolder) Update(p => p.OutputFolder = value ?? ""); }
    }

    /// <summary>Folder shown in the UI — the resolved default when unset.</summary>
    public string QuickRecordFolderDisplay => string.IsNullOrWhiteSpace(Prefs.OutputFolder)
        ? $"{Prefs.ResolveOutputFolder()} (default)"
        : Prefs.OutputFolder;
}
