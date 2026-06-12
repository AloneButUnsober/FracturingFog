using System;

namespace FracturingFog.UI.Avalonia.Slideshow;

/// <summary>Sink for slideshow frames captured during a recorded run.
/// Concrete implementation lives in the host (WinExe) so the UI.Avalonia
/// project does not depend on System.Drawing-backed encoders.</summary>
public interface ISlideshowFrameRecorder : IDisposable
{
    /// <summary>Absolute path of the directory frames are being written to
    /// (or any sink-specific identifier the host shows in dialogs).</summary>
    string Sink { get; }

    /// <summary>Encoded frame width — matches the BGRA buffers the engine
    /// pushes via <see cref="WriteFrame"/>.</summary>
    int Width { get; }
    int Height { get; }

    /// <summary>Number of frames the sink has successfully accepted.</summary>
    int FrameCount { get; }

    /// <summary>Append one BGRA32 frame. Sink copies the buffer before
    /// returning (engine reuses its blend array).</summary>
    void WriteFrame(uint[] bgra);
}
