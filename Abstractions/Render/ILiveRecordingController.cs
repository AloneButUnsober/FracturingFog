// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Render/ILiveRecordingController.cs
//
// Instant record (#936 / #944): one-touch capture of whatever the render
// window is showing — colour themes, animations, slideshow legs, lighting and
// FX, watermark, HUD — independent of the Video Zoom / slideshow recorders.
//
// The engine samples the presented frame at a fixed capture rate into a
// lossless temp intermediate (unique-frame PNGs + a per-frame hold manifest),
// so the output format / quality is chosen AFTER the user stops recording.
// The shell drives Start/Stop, shows the export prompt, and hands the
// resulting LiveRecordingExportOptions to the engine-side encoder.

using System;
using System.Collections.Generic;

namespace FracturingFog.Render
{
    /// <summary>Container / codec for an exported live recording.</summary>
    public enum LiveRecordingFormat
    {
        /// <summary>H.264 in MP4 — plays everywhere. Native writer fallback when ffmpeg is absent.</summary>
        Mp4H264,
        /// <summary>H.265 / HEVC in MP4 — smaller files, needs ffmpeg.</summary>
        Mp4H265,
        /// <summary>VP9 in WebM — web-friendly, needs ffmpeg.</summary>
        WebmVp9,
        /// <summary>FFV1 in Matroska — true lossless intermediate, needs ffmpeg.</summary>
        MkvFfv1,
        /// <summary>Animated GIF (ffmpeg palettegen, or the built-in encoder).</summary>
        Gif,
        /// <summary>Constant-rate PNG frame sequence in a folder (no encoder needed).</summary>
        PngSequence,
    }

    /// <summary>Encode quality tier. Ignored by the inherently lossless formats
    /// (FFV1, PNG sequence).</summary>
    public enum LiveRecordingQuality
    {
        Lossless,
        High,
        Medium,
        Low,
    }

    /// <summary>What the user picked in the post-stop export prompt.</summary>
    public sealed class LiveRecordingExportOptions
    {
        public LiveRecordingFormat Format { get; set; } = LiveRecordingFormat.Mp4H264;
        public LiveRecordingQuality Quality { get; set; } = LiveRecordingQuality.High;

        /// <summary>Output frame rate. Frames are resampled from the capture
        /// timeline, so this may differ from the capture rate.</summary>
        public int Fps { get; set; } = 30;

        /// <summary>Output size as a percentage of the captured size (10..100).</summary>
        public int ScalePercent { get; set; } = 100;
    }

    /// <summary>One unique captured frame and how long it stayed on screen.</summary>
    public sealed class LiveRecordingFrame
    {
        /// <summary>PNG file name relative to <see cref="LiveRecordingResult.Folder"/>.</summary>
        public string FileName { get; init; } = "";

        /// <summary>Seconds this frame was displayed before the next one.</summary>
        public double Seconds { get; init; }
    }

    /// <summary>The finalised temp intermediate handed to the shell on stop.</summary>
    public sealed class LiveRecordingResult
    {
        /// <summary>Temp folder holding the unique-frame PNGs + manifest. The
        /// shell owns it after Stop and must delete it when done.</summary>
        public string Folder { get; init; } = "";

        /// <summary>ffmpeg concat-demuxer manifest (per-frame durations).</summary>
        public string ManifestPath { get; init; } = "";

        public IReadOnlyList<LiveRecordingFrame> Frames { get; init; } = Array.Empty<LiveRecordingFrame>();

        public int Width { get; init; }
        public int Height { get; init; }

        /// <summary>Rate the display was sampled at while recording.</summary>
        public int CaptureFps { get; init; }

        /// <summary>Wall-clock recording length in seconds.</summary>
        public double DurationSeconds { get; init; }
    }

    /// <summary>Start/stop capture of the live render window.</summary>
    public interface ILiveRecordingController
    {
        /// <summary>True between a successful <see cref="StartLiveRecording"/> and
        /// <see cref="StopLiveRecording"/>.</summary>
        bool IsLiveRecording { get; }

        /// <summary>Time since recording started (zero when idle).</summary>
        TimeSpan LiveRecordingElapsed { get; }

        /// <summary>Unique frames captured so far.</summary>
        int LiveRecordingFrameCount { get; }

        /// <summary>Begin sampling the presented frame at <paramref name="captureFps"/>
        /// (clamped 1..60). Returns false (with a status message) when recording
        /// cannot start — already running, or nothing presented yet.</summary>
        bool StartLiveRecording(int captureFps);

        /// <summary>Stop and finalise the intermediate. Returns null when nothing
        /// was captured (the temp folder is already cleaned up).</summary>
        LiveRecordingResult? StopLiveRecording();

        /// <summary>Raised (on an arbitrary thread) when recording starts or stops.</summary>
        event EventHandler? LiveRecordingStateChanged;
    }
}
