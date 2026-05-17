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
        private readonly int _w, _h;
        private readonly SemaphoreSlim _writeGate;
        private readonly object _pendingLock = new();
        private int _pending;
        private readonly ManualResetEventSlim _drained = new(true);
        private int _frameIdx;
        private bool _disposed;

        public string Folder => _folder;
        public int FrameCount => _frameIdx;

        public PngSequenceWriter(string folder, int width, int height, int maxConcurrent = 4)
        {
            if (width < 2 || height < 2)
                throw new ArgumentException("Frame dimensions too small.");
            Directory.CreateDirectory(folder);
            _folder = folder;
            _w = width;
            _h = height;
            _writeGate = new SemaphoreSlim(maxConcurrent, maxConcurrent);
        }

        public void WriteFrame(uint[] bgra)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(PngSequenceWriter));
            if (bgra.Length < _w * _h)
                throw new ArgumentException("Frame buffer too small.");

            int idx = Interlocked.Increment(ref _frameIdx);
            // Snapshot the buffer — caller may overwrite it for the next frame.
            var copy = new uint[_w * _h];
            Array.Copy(bgra, copy, _w * _h);

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
                    SavePng(copy, _w, _h, path);
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
