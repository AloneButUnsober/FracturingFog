// Engine/Imaging/FfmpegVideoWriter.cs
//
// Phase X.2 / Slice 2.1 — IVideoWriter adapter that funnels BGRA frames
// through PngSequenceWriter into a temporary folder, then invokes
// FfmpegEncoder.EncodeAsync on Dispose to produce the final container.
//
// Picked over a stdin-pipe path for first cut because:
//   * PngSequenceWriter already exists, is cross-platform (SkiaSharp),
//     and is unit-tested via slideshow exports.
//   * The PNG sequence is independently recoverable if ffmpeg fails or
//     the user cancels — encoders can drop the .png/.ppm pipe approach
//     as a follow-up once disk I/O proves a bottleneck.
//
// Lives in the cross-platform Engine so FractalRenderHost.VideoWriterFactory
// can pick it on Linux/macOS without dragging Win-only APIs into the boot
// path. Windows hosts continue to default to the Mp4Writer (Media Foundation)
// the bootstrap wired in Phase X.0 / Slice 0.1c.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FracturingFog.Imaging
{
    public sealed class FfmpegVideoWriter : IVideoWriter
    {
        private readonly string _outputPath;
        private readonly int _fps;
        private readonly FfmpegEncoder.Preset _preset;
        private readonly PngSequenceWriter _pngWriter;
        private readonly string _tempFolder;
        private readonly bool _keepIntermediatePngs;
        private bool _disposed;

        public int SourceWidth   { get; }
        public int SourceHeight  { get; }
        public int EncodedWidth  => _pngWriter.Width;
        public int EncodedHeight => _pngWriter.Height;

        /// <summary>
        /// Construct a video writer that records BGRA frames into a temp folder
        /// and encodes them via ffmpeg on Dispose.
        /// </summary>
        /// <param name="outputPath">Final container path (e.g. /tmp/zoom.mp4).</param>
        /// <param name="sourceWidth">Pixel width of the BGRA frames pushed to WriteFrame.</param>
        /// <param name="sourceHeight">Pixel height of the BGRA frames pushed to WriteFrame.</param>
        /// <param name="fps">Encoded frame rate.</param>
        /// <param name="preset">FfmpegEncoder preset (codec + container).</param>
        /// <param name="tempFolder">Optional override for the intermediate PNG folder. When null, the OS temp directory plus a GUID is used and removed on Dispose.</param>
        /// <param name="keepIntermediatePngs">Skip the temp-folder cleanup on Dispose. Useful when callers want to re-encode at a different preset.</param>
        public FfmpegVideoWriter(string outputPath,
                                 int sourceWidth, int sourceHeight,
                                 int fps,
                                 FfmpegEncoder.Preset preset,
                                 string? tempFolder = null,
                                 bool keepIntermediatePngs = false)
        {
            if (sourceWidth < 2 || sourceHeight < 2)
                throw new ArgumentException("Frame dimensions too small.");
            if (fps <= 0)
                throw new ArgumentOutOfRangeException(nameof(fps));

            _outputPath = outputPath ?? throw new ArgumentNullException(nameof(outputPath));
            SourceWidth  = sourceWidth;
            SourceHeight = sourceHeight;
            _fps   = fps;
            _preset = preset;
            _keepIntermediatePngs = keepIntermediatePngs;

            _tempFolder = tempFolder
                ?? Path.Combine(Path.GetTempPath(), "FracturingFog-ffmpeg-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempFolder);

            _pngWriter = new PngSequenceWriter(_tempFolder, sourceWidth, sourceHeight);
        }

        /// <summary>
        /// Push one frame. Timestamp is ignored; the encoder runs at the fixed
        /// fps configured in the constructor. Per-frame variable-rate output is
        /// a follow-up — the PNG image2 demuxer ffmpeg uses does not accept
        /// arbitrary per-frame timestamps without a pts CSV sidecar.
        /// </summary>
        public void WriteFrame(uint[] bgra, long timestamp100ns)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(FfmpegVideoWriter));
            _pngWriter.WriteFrame(bgra);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // Drain the PNG writer first so every frame_NNNNNN.png is on disk
            // before ffmpeg's image2 demuxer opens the folder.
            _pngWriter.Dispose();

            int frameCount = _pngWriter.FrameCount;
            if (frameCount == 0)
            {
                // No frames recorded — nothing for ffmpeg to encode. Clean up
                // and bail silently rather than failing late inside ffmpeg.
                CleanupTempFolder();
                return;
            }

            try
            {
                // Synchronous wait inside Dispose. Callers that need a non-
                // blocking close can spin off a Task; the IVideoWriter
                // contract is synchronous Dispose.
                var task = FfmpegEncoder.EncodeAsync(_tempFolder, _outputPath, _preset, _fps);
                var (ok, log) = task.GetAwaiter().GetResult();
                if (!ok)
                    throw new InvalidOperationException("ffmpeg encode failed:\n" + log);
            }
            finally
            {
                CleanupTempFolder();
            }
        }

        private void CleanupTempFolder()
        {
            if (_keepIntermediatePngs) return;
            try { Directory.Delete(_tempFolder, recursive: true); }
            catch { /* best-effort cleanup */ }
        }
    }
}
