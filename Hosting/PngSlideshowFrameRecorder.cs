// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Hosting/PngSlideshowFrameRecorder.cs
//
// WinExe-side adapter: wraps the System.Drawing-backed PngSequenceWriter
// behind the shell-neutral ISlideshowFrameRecorder contract so UI.Avalonia
// can drive the recording lifecycle without referencing System.Drawing or
// the WinExe.

using FracturingFog.UI.Avalonia.Slideshow;

namespace FracturingFog.Hosting;

public sealed class PngSlideshowFrameRecorder : ISlideshowFrameRecorder
{
    private readonly PngSequenceWriter _inner;
    private readonly string _folder;

    public PngSlideshowFrameRecorder(string folder, int width, int height)
    {
        _folder = folder;
        _inner = new PngSequenceWriter(folder, width, height);
    }

    public string Sink => _folder;
    public int Width => _inner.Width;
    public int Height => _inner.Height;
    public int FrameCount => _inner.FrameCount;

    public void WriteFrame(uint[] bgra) => _inner.WriteFrame(bgra);

    public void Dispose() => _inner.Dispose();
}
