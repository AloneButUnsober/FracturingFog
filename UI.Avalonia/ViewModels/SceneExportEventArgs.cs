// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// UI.Avalonia/ViewModels/SceneExportEventArgs.cs
//
// Scene Engine Roadmap — Phase S8 polish: the shell seam for the "Export
// Scene…" command. The Scene Editor can't touch the Engine's SceneVideoRenderer
// directly (UI.Avalonia stays Engine-free), so it raises this event; the host
// (AvaloniaShellBootstrap) picks an output path, runs the offline render on a
// background thread, and signals Completion. Mirrors the SaveFileRequested /
// MessageRequested host-fulfilled event pattern.

using System;
using System.Threading.Tasks;

using FracturingFog.Abstractions.Animation;

namespace FracturingFog.UI.Avalonia.ViewModels;

/// <summary>Encoder choice for a scene export, mapped to the Engine's ffmpeg
/// preset host-side (UI.Avalonia can't name <c>FfmpegEncoder.Preset</c>).</summary>
public enum SceneExportEncode
{
    /// <summary>libx264 CRF 18 — visually lossless MP4 (default, broad compat).</summary>
    HighQualityH264,
    /// <summary>libx264 -qp 0 — mathematically lossless MP4.</summary>
    LosslessH264,
    /// <summary>FFV1 v3 — lossless MKV intermediate.</summary>
    Ffv1,
}

/// <summary>The render knobs for one scene export. Plain DTO — the host maps it
/// onto the Engine's <c>SceneVideoOptions</c> / <see cref="SceneRenderSettings"/>.</summary>
public sealed class SceneExportSettings
{
    public int Width { get; init; } = 1920;
    public int Height { get; init; } = 1080;
    public int Fps { get; init; } = 30;
    public int MotionBlurSubframes { get; init; } = 1;
    public double ShutterFraction { get; init; } = 0.5;
    public SceneExportEncode Encode { get; init; } = SceneExportEncode.HighQualityH264;
}

/// <summary>Raised by the Scene Editor to ask the host to render + encode a
/// scene offline. The host awaits nothing from the VM; it picks the output
/// path, runs the render, reports the outcome, then signals
/// <see cref="Completion"/>.</summary>
public sealed class SceneExportEventArgs : EventArgs
{
    public SceneExportEventArgs(SceneData scene, SceneExportSettings settings)
    {
        Scene = scene;
        Settings = settings;
    }

    /// <summary>The scene to render (a fresh <see cref="SceneData"/> built from
    /// the editor's current state).</summary>
    public SceneData Scene { get; }

    /// <summary>Resolution / fps / motion-blur / encode knobs.</summary>
    public SceneExportSettings Settings { get; }

    /// <summary>Signalled by the host when the export finishes (success or
    /// cancel). The editor's command awaits this to re-enable its UI.</summary>
    public TaskCompletionSource<bool> Completion { get; } = new();
}
