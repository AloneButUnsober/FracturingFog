// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Imaging/GifSequenceWriter.cs
//
// Streaming animated-GIF writer (#784). Accepts BGRA frames one at a time
// (same push model as PngSequenceWriter) and packs them into one looping
// GIF89a. Each frame is quantized independently with its own local colour
// table (a zoom's palette drifts frame to frame, so a shared global table
// would band badly), LZW-compressed, and appended in order.
//
// Threading: WriteFrame snapshots the buffer and hands it to a single ordered
// background encoder thread through a bounded queue, so the caller's render /
// zoom loop only pays a memcpy (mirrors PngSequenceWriter's offload). The GIF
// file must be written strictly in frame order, so exactly one encoder thread
// consumes the queue; the bound caps in-flight memory by blocking the producer
// when the encoder falls behind.
//
// Frame delay: derived from the 100-ns timestamps the caller passes (real
// capture cadence), converted to GIF centiseconds and clamped to a sane range.
// A frame's delay is only known once the NEXT frame's timestamp arrives, so
// the encoder holds one frame back and flushes it with the measured delay;
// Dispose flushes the final held frame with the default delay.

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace FracturingFog.Imaging
{
    /// <summary>Push-model animated GIF89a encoder. See file header.</summary>
    public sealed class GifSequenceWriter : IDisposable
    {
        private readonly string _path;
        private readonly int _w, _h;
        private readonly int _defaultDelayCs;
        private readonly BlockingCollection<(uint[] pixels, long ts)> _queue;
        private readonly Thread _worker;
        private volatile bool _faulted;
        private Exception? _fault;
        private bool _disposed;

        public string Path => _path;
        public int Width => _w;
        public int Height => _h;
        public int FrameCount { get; private set; }

        /// <param name="defaultDelayCs">Delay (centiseconds) for the final frame
        /// and any frame whose measured delta is unusable. 5 cs = 20 fps.</param>
        /// <param name="queueBound">Max frames buffered before WriteFrame blocks.</param>
        public GifSequenceWriter(string path, int width, int height,
            int defaultDelayCs = 5, int queueBound = 8)
        {
            if (width < 1 || height < 1) throw new ArgumentException("Frame dimensions too small.");
            _path = path;
            _w = width;
            _h = height;
            _defaultDelayCs = Math.Clamp(defaultDelayCs, MinDelayCs, MaxDelayCs);
            _queue = new BlockingCollection<(uint[], long)>(Math.Max(1, queueBound));
            _worker = new Thread(EncodeLoop) { IsBackground = true, Name = "GifSequenceWriter" };
            _worker.Start();
        }

        // GIF delay is a 16-bit centisecond field; browsers treat <2 cs oddly.
        private const int MinDelayCs = 2;
        private const int MaxDelayCs = 65535;

        /// <summary>Push one straight-alpha BGRA frame. <paramref name="timestamp100ns"/>
        /// is 100-ns ticks from recording start (pass <c>stopwatch.Elapsed.Ticks</c>).</summary>
        public void WriteFrame(uint[] bgra, long timestamp100ns)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(GifSequenceWriter));
            if (_faulted) return; // encoder died — drop silently, surfaced on Dispose
            if (bgra.Length < _w * _h)
                throw new ArgumentException("Frame buffer too small.");

            // Snapshot the top _h rows of _w pixels — the caller reuses its buffer.
            var copy = new uint[_w * _h];
            Array.Copy(bgra, copy, _w * _h);
            try { _queue.Add((copy, timestamp100ns)); }
            catch (InvalidOperationException) { /* adding completed after fault */ }
        }

        private void EncodeLoop()
        {
            try
            {
                using var fs = File.Create(_path);
                using var bw = new BinaryWriter(fs);

                bool headerWritten = false;
                PendingFrame? pending = null;
                long pendingTs = 0;

                foreach (var (pixels, ts) in _queue.GetConsumingEnumerable())
                {
                    if (!headerWritten) { WriteHeader(bw); headerWritten = true; }

                    PendingFrame enc = EncodeFrame(pixels);
                    if (pending is { } prev)
                    {
                        int delay = DelayCs(ts - pendingTs);
                        FlushFrame(bw, prev, delay);
                    }
                    pending = enc;
                    pendingTs = ts;
                    FrameCount++;
                }

                if (!headerWritten) WriteHeader(bw); // zero-frame file stays valid
                if (pending is { } last) FlushFrame(bw, last, _defaultDelayCs);
                bw.Write((byte)0x3B); // trailer
            }
            catch (Exception ex)
            {
                _fault = ex;
                _faulted = true;
            }
        }

        private void WriteHeader(BinaryWriter bw)
        {
            GifEncoder.WriteSignature(bw);
            GifEncoder.WriteU16(bw, _w);
            GifEncoder.WriteU16(bw, _h);
            bw.Write((byte)0x00); // no global colour table (each frame carries its own)
            bw.Write((byte)0);    // background colour index
            bw.Write((byte)0);    // pixel aspect ratio

            // NETSCAPE2.0 application extension → loop forever.
            bw.Write((byte)0x21); // extension introducer
            bw.Write((byte)0xFF); // application label
            bw.Write((byte)0x0B); // block size (11)
            bw.Write(new[] { (byte)'N', (byte)'E', (byte)'T', (byte)'S', (byte)'C', (byte)'A',
                             (byte)'P', (byte)'E', (byte)'2', (byte)'.', (byte)'0' });
            bw.Write((byte)0x03); // sub-block size
            bw.Write((byte)0x01); // sub-block id
            GifEncoder.WriteU16(bw, 0); // loop count 0 = infinite
            bw.Write((byte)0x00); // block terminator
        }

        private readonly struct PendingFrame
        {
            public readonly GifEncoder.QuantizedFrame Frame;
            public readonly int TableSize, SizeField, MinCodeSize;
            public readonly byte[] Lzw;

            public PendingFrame(GifEncoder.QuantizedFrame frame, int tableSize, int sizeField,
                int minCodeSize, byte[] lzw)
            {
                Frame = frame; TableSize = tableSize; SizeField = sizeField;
                MinCodeSize = minCodeSize; Lzw = lzw;
            }
        }

        private PendingFrame EncodeFrame(uint[] pixels)
        {
            var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(pixels.AsSpan());
            GifEncoder.QuantizedFrame frame = GifEncoder.Quantize(bytes, _w, _h);
            bool hasTransparent = frame.TransparentIndex >= 0;
            int used = frame.ColorCount + (hasTransparent ? 1 : 0);
            GifEncoder.ComputeTableGeometry(used, out int tableSize, out int sizeField, out int minCodeSize);
            byte[] lzw = GifEncoder.LzwCompress(frame.Indices, minCodeSize);
            return new PendingFrame(frame, tableSize, sizeField, minCodeSize, lzw);
        }

        private void FlushFrame(BinaryWriter bw, in PendingFrame pf, int delayCs)
        {
            // Per-frame Graphic Control Extension carries the delay (+ transparency).
            GifEncoder.WriteGraphicControl(bw, delayCs, pf.Frame.TransparentIndex);

            // Image descriptor with a LOCAL colour table.
            bw.Write((byte)0x2C);
            GifEncoder.WriteU16(bw, 0); GifEncoder.WriteU16(bw, 0); // left, top
            GifEncoder.WriteU16(bw, _w); GifEncoder.WriteU16(bw, _h);
            bw.Write((byte)(0x80 | pf.SizeField)); // local colour table present + size
            GifEncoder.WriteColorTable(bw, pf.Frame, pf.TableSize);

            GifEncoder.WriteImageData(bw, pf.MinCodeSize, pf.Lzw);
        }

        private static int DelayCs(long deltaTicks)
        {
            // 1 centisecond = 10 ms = 100_000 ticks of 100 ns.
            double cs = deltaTicks / 100_000.0;
            if (!(cs >= MinDelayCs)) return MinDelayCs; // also catches <=0 / NaN
            return (int)Math.Min(MaxDelayCs, Math.Round(cs));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _queue.CompleteAdding();
            _worker.Join();
            _queue.Dispose();
            if (_faulted && _fault != null)
                throw new IOException("Animated GIF encoding failed.", _fault);
        }
    }
}
