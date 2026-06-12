using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FracturingFog
{
    /// <summary>
    /// Captures BGRA32 frames to a folder as zero-padded PNGs (frame_00001.png …).
    /// Truly lossless, no codec dependencies. The folder can later be encoded by
    /// ffmpeg or kept as-is.
    ///
    /// PNG encoding is offloaded to background tasks bounded by a semaphore so
    /// per-frame WriteFrame() returns quickly and does not stall the zoom loop.
    /// Dispose() drains the queue before returning.
    /// </summary>
    public sealed class PngSequenceWriter : IDisposable
    {
        private readonly string _folder;
        private readonly int _srcW, _srcH;          // source frame dimensions
        private readonly int _encW, _encH;          // PNG dimensions (always even)
        private readonly SemaphoreSlim _writeGate;
        private readonly object _pendingLock = new();
        private int _pending;
        private readonly ManualResetEventSlim _drained = new(true);
        private int _frameIdx;
        private bool _disposed;

        public string Folder => _folder;
        public int FrameCount => _frameIdx;
        public int Width => _encW;
        public int Height => _encH;

        // PNG dimensions are forced even (width & ~1, height & ~1) so the
        // sequence can be fed directly to libx264 with yuv420p, which rejects
        // odd dimensions. An odd source has its right/bottom edge cropped by
        // one pixel — same strategy Mp4Writer uses.
        public PngSequenceWriter(string folder, int sourceWidth, int sourceHeight, int maxConcurrent = 4)
        {
            if (sourceWidth < 2 || sourceHeight < 2)
                throw new ArgumentException("Frame dimensions too small.");
            Directory.CreateDirectory(folder);
            _folder = folder;
            _srcW = sourceWidth;
            _srcH = sourceHeight;
            _encW = sourceWidth & ~1;
            _encH = sourceHeight & ~1;
            _writeGate = new SemaphoreSlim(maxConcurrent, maxConcurrent);
        }

        public void WriteFrame(uint[] bgra)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(PngSequenceWriter));
            if (bgra.Length < _srcW * _srcH)
                throw new ArgumentException("Frame buffer too small.");

            int idx = Interlocked.Increment(ref _frameIdx);
            // Snapshot the buffer — caller may overwrite it for the next frame.
            // Copy only the encoded subregion (left _encW columns of top _encH rows),
            // contiguously, so SavePng can treat it as a tightly packed _encW × _encH image.
            var copy = new uint[_encW * _encH];
            for (int row = 0; row < _encH; row++)
                Array.Copy(bgra, row * _srcW, copy, row * _encW, _encW);

            lock (_pendingLock)
            {
                _pending++;
                _drained.Reset();
            }

            _writeGate.Wait();
            Task.Run(() =>
            {
                try
                {
                    string path = Path.Combine(_folder, $"frame_{idx:D6}.png");
                    SavePng(copy, _encW, _encH, path);
                }
                finally
                {
                    _writeGate.Release();
                    lock (_pendingLock)
                    {
                        _pending--;
                        if (_pending == 0) _drained.Set();
                    }
                }
            });
        }

        private static unsafe void SavePng(uint[] pixels, int w, int h, string path)
        {
            using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            var data = bmp.LockBits(new Rectangle(0, 0, w, h),
                                    ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                fixed (uint* src = pixels)
                {
                    if (data.Stride == w * 4)
                        Buffer.MemoryCopy(src, (void*)data.Scan0, (long)w * h * 4, (long)w * h * 4);
                    else
                    {
                        byte* dst = (byte*)data.Scan0;
                        for (int row = 0; row < h; row++)
                            Buffer.MemoryCopy((byte*)src + (long)row * w * 4,
                                              dst + (long)row * data.Stride,
                                              (long)w * 4, (long)w * 4);
                    }
                }
            }
            finally { bmp.UnlockBits(data); }
            bmp.Save(path, ImageFormat.Png);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            // Wait for queued frame writes to finish so the folder is complete.
            _drained.Wait();
            _writeGate.Dispose();
            _drained.Dispose();
        }
    }
}
