// Engine/Imaging/IVideoWriter.cs
//
// Interface boundary between the cross-platform engine's video pipeline
// (FractalRenderHost zoom recording) and the platform-specific encoder
// backend. Phase X.0 / Slice 0.1c extracted this so FractalRenderHost can
// live in the cross-platform Engine without referencing the Win-only
// Mp4Writer (Media Foundation P/Invoke, ships in FracturingFog.Rendering.D3D).
//
// Phase X.2 expands the implementation set to include FfmpegVideoWriter
// (process-based, cross-platform) — same interface, no FractalRenderHost
// changes needed.

using System;

namespace FracturingFog.Imaging
{
    /// <summary>
    /// Streaming BGRA frame sink. Implementations encode frames to disk as
    /// MP4/MKV/etc. Caller pushes one frame at a time via
    /// <see cref="WriteFrame"/>; <c>Dispose</c> flushes the encoder.
    /// </summary>
    public interface IVideoWriter : IDisposable
    {
        /// <summary>Source-frame dimensions accepted by WriteFrame. May
        /// differ from the encoded dimensions when the implementation crops
        /// to even multiples for codec compatibility.</summary>
        int SourceWidth { get; }
        int SourceHeight { get; }

        /// <summary>Encoded dimensions on disk (always even). Useful for
        /// matching downstream player resolutions.</summary>
        int EncodedWidth { get; }
        int EncodedHeight { get; }

        /// <summary>Push one frame. <paramref name="timestamp100ns"/> is
        /// 100-ns ticks from the recording start; pass
        /// <c>stopwatch.Elapsed.Ticks</c>.</summary>
        void WriteFrame(uint[] bgra, long timestamp100ns);
    }
}
