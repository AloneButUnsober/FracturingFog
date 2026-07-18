// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;

namespace FracturingFog.UI.Avalonia.ViewModels;

/// <summary>Payload for <see cref="ShellViewModel.SlideshowRecordingReady"/>.
/// Surfaces the PNG-sequence directory the engine just finished writing into
/// plus the encode preset the user picked in the settings dialog so the host
/// can pop Convert / Save / Cancel without re-reading the SlideshowConfig.</summary>
public sealed class SlideshowRecordingReadyEventArgs : EventArgs
{
    public SlideshowRecordingReadyEventArgs(string folderPath, int frameCount, string encodePreset, int width, int height)
    {
        FolderPath = folderPath;
        FrameCount = frameCount;
        EncodePreset = encodePreset;
        Width = width;
        Height = height;
    }

    public string FolderPath { get; }
    public int FrameCount { get; }
    public string EncodePreset { get; }
    public int Width { get; }
    public int Height { get; }
}
