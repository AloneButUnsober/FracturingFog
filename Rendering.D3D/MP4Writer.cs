using System;
using System.IO;
using System.Runtime.InteropServices;

namespace FracturingFog
{
    /// <summary>
    /// Minimal Windows Media Foundation H.264 MP4 writer. Built-in to Windows 8+;
    /// no third-party dependencies.
    ///
    /// Frames are submitted as <see cref="uint"/>[] in BGRA32 memory order (the
    /// same layout produced by MandelbrotCalculator.ColorBuffer / the post-
    /// processed buffer uploaded to the GPU). Timestamps are 100-ns ticks; the
    /// caller normally passes <c>Stopwatch.Elapsed.Ticks</c> from a stopwatch
    /// started at the first frame.
    ///
    /// The configured input dimensions must be even (H.264 constraint); odd
    /// source dimensions are silently rounded down by one pixel and the right /
    /// bottom edge is dropped during the per-frame copy.
    /// </summary>
    public sealed class Mp4Writer : FracturingFog.Imaging.IVideoWriter
    {
        // IVideoWriter surface (Phase X.0 / Slice 0.1c)
        public int SourceWidth  => _srcW;
        public int SourceHeight => _srcH;
        public int EncodedWidth  => _encW;
        public int EncodedHeight => _encH;

        // ── Win32 P/Invoke ────────────────────────────────────────────────
        [DllImport("mfplat.dll", ExactSpelling = true)]
        private static extern int MFStartup(uint version, uint flags);

        [DllImport("mfplat.dll", ExactSpelling = true)]
        private static extern int MFShutdown();

        [DllImport("mfreadwrite.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        private static extern int MFCreateSinkWriterFromURL(
            string pwszOutputURL,
            IntPtr pByteStream,
            IntPtr pAttributes,
            [MarshalAs(UnmanagedType.Interface)] out IMFSinkWriter ppSinkWriter);

        [DllImport("mfplat.dll", ExactSpelling = true)]
        private static extern int MFCreateMediaType(
            [MarshalAs(UnmanagedType.Interface)] out IMFMediaType ppMFType);

        [DllImport("mfplat.dll", ExactSpelling = true)]
        private static extern int MFCreateMemoryBuffer(
            uint cbMaxLength,
            [MarshalAs(UnmanagedType.Interface)] out IMFMediaBuffer ppBuffer);

        [DllImport("mfplat.dll", ExactSpelling = true)]
        private static extern int MFCreateSample(
            [MarshalAs(UnmanagedType.Interface)] out IMFSample ppIMFSample);

        private const uint MF_VERSION = 0x00020070;     // Windows 7+
        private const uint MFSTARTUP_FULL = 0;
        private const uint MFVideoInterlace_Progressive = 2;

        // ── GUIDs (MFAPI / mfobjects) ─────────────────────────────────────
        private static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00aa00389b71");
        private static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00aa00389b71");
        private static readonly Guid MFVideoFormat_RGB32 = new("00000016-0000-0010-8000-00aa00389b71");

        private static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
        private static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        private static readonly Guid MF_MT_AVG_BITRATE = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
        private static readonly Guid MF_MT_INTERLACE_MODE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
        private static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
        private static readonly Guid MF_MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
        private static readonly Guid MF_MT_PIXEL_ASPECT_RATIO = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
        private static readonly Guid MF_MT_DEFAULT_STRIDE = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");

        // ── State ─────────────────────────────────────────────────────────
        private readonly int _srcW, _srcH;          // source frame dimensions
        private readonly int _encW, _encH;          // encoder dimensions (even)
        private readonly int _encStride;            // bytes per row in encoder
        private readonly int _encFrameBytes;
        private IMFSinkWriter? _sink;
        private int _streamIndex;
        private bool _started;
        private bool _mfStartedHere;
        private long _firstTime = -1;
        private long _lastTime;

        public Mp4Writer(string path, int sourceWidth, int sourceHeight,
                         int fpsNum = 30, int fpsDen = 1, uint bitrate = 8_000_000u)
        {
            if (sourceWidth < 2 || sourceHeight < 2)
                throw new ArgumentException("Source dimensions too small.");

            _srcW = sourceWidth;
            _srcH = sourceHeight;
            _encW = sourceWidth & ~1;
            _encH = sourceHeight & ~1;
            _encStride = _encW * 4;
            _encFrameBytes = _encStride * _encH;

            ThrowIfFailed(MFStartup(MF_VERSION, MFSTARTUP_FULL), "MFStartup");
            _mfStartedHere = true;

            ThrowIfFailed(MFCreateSinkWriterFromURL(path, IntPtr.Zero, IntPtr.Zero, out _sink),
                "MFCreateSinkWriterFromURL");

            // Output type: H.264 to MP4 container.
            ThrowIfFailed(MFCreateMediaType(out IMFMediaType outType), "MFCreateMediaType(out)");
            try
            {
                SetGuid(outType, MF_MT_MAJOR_TYPE, MFMediaType_Video);
                SetGuid(outType, MF_MT_SUBTYPE, MFVideoFormat_H264);
                SetU32(outType, MF_MT_AVG_BITRATE, bitrate);
                SetU32(outType, MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
                SetU64(outType, MF_MT_FRAME_SIZE, PackU64((uint)_encW, (uint)_encH));
                SetU64(outType, MF_MT_FRAME_RATE, PackU64((uint)fpsNum, (uint)fpsDen));
                SetU64(outType, MF_MT_PIXEL_ASPECT_RATIO, PackU64(1, 1));

                ThrowIfFailed(_sink!.AddStream(outType, out _streamIndex), "AddStream");
            }
            finally { Marshal.ReleaseComObject(outType); }

            // Input type: RGB32 (= D3DFMT_X8R8G8B8 = BGRA in memory). Positive
            // stride flags top-down rows so frames aren't vertically flipped.
            ThrowIfFailed(MFCreateMediaType(out IMFMediaType inType), "MFCreateMediaType(in)");
            try
            {
                SetGuid(inType, MF_MT_MAJOR_TYPE, MFMediaType_Video);
                SetGuid(inType, MF_MT_SUBTYPE, MFVideoFormat_RGB32);
                SetU32(inType, MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
                SetU64(inType, MF_MT_FRAME_SIZE, PackU64((uint)_encW, (uint)_encH));
                SetU64(inType, MF_MT_FRAME_RATE, PackU64((uint)fpsNum, (uint)fpsDen));
                SetU64(inType, MF_MT_PIXEL_ASPECT_RATIO, PackU64(1, 1));
                SetU32(inType, MF_MT_DEFAULT_STRIDE, (uint)_encStride);

                ThrowIfFailed(_sink!.SetInputMediaType(_streamIndex, inType, IntPtr.Zero),
                    "SetInputMediaType");
            }
            finally { Marshal.ReleaseComObject(inType); }

            ThrowIfFailed(_sink!.BeginWriting(), "BeginWriting");
            _started = true;
        }

        /// <summary>
        /// Submit a frame. <paramref name="bgra"/> must contain at least
        /// <c>sourceWidth * sourceHeight</c> pixels in BGRA32 memory order.
        /// </summary>
        /// <param name="timestamp100ns">Presentation time in 100-ns ticks
        /// (e.g. <c>Stopwatch.Elapsed.Ticks</c>). The first call establishes
        /// time zero; subsequent calls must be monotonically increasing.</param>
        public unsafe void WriteFrame(uint[] bgra, long timestamp100ns)
        {
            if (_sink == null) throw new ObjectDisposedException(nameof(Mp4Writer));
            if (bgra.Length < _srcW * _srcH)
                throw new ArgumentException("Frame buffer too small for configured source size.");

            if (_firstTime < 0) _firstTime = timestamp100ns;
            long t = timestamp100ns - _firstTime;
            // Duration is from this frame to "now"; finalize-time the encoder
            // will use the last-set duration for the trailing frame. Floor at
            // ~33ms (30 fps) so duplicate timestamps don't produce zero-length
            // samples that confuse downstream demuxers.
            const long MinDur = 333_333L;
            long dur = Math.Max(t - _lastTime, MinDur);

            ThrowIfFailed(MFCreateMemoryBuffer((uint)_encFrameBytes, out IMFMediaBuffer buf),
                "MFCreateMemoryBuffer");
            try
            {
                ThrowIfFailed(buf.Lock(out IntPtr dst, out _, out _), "MediaBuffer.Lock");
                try
                {
                    int srcStrideUints = _srcW;        // bytes / 4
                    int encStrideUints = _encW;
                    fixed (uint* srcPtr = bgra)
                    {
                        uint* d = (uint*)dst.ToPointer();
                        for (int y = 0; y < _encH; y++)
                        {
                            uint* srcRow = srcPtr + y * srcStrideUints;
                            uint* dstRow = d + y * encStrideUints;
                            Buffer.MemoryCopy(srcRow, dstRow, _encStride, _encStride);
                        }
                    }
                }
                finally { buf.Unlock(); }
                ThrowIfFailed(buf.SetCurrentLength((uint)_encFrameBytes), "SetCurrentLength");

                ThrowIfFailed(MFCreateSample(out IMFSample sample), "MFCreateSample");
                try
                {
                    ThrowIfFailed(sample.AddBuffer(buf), "Sample.AddBuffer");
                    ThrowIfFailed(sample.SetSampleTime(t), "SetSampleTime");
                    ThrowIfFailed(sample.SetSampleDuration(dur), "SetSampleDuration");
                    ThrowIfFailed(_sink!.WriteSample(_streamIndex, sample), "WriteSample");
                }
                finally { Marshal.ReleaseComObject(sample); }
            }
            finally { Marshal.ReleaseComObject(buf); }

            _lastTime = t;
        }

        public void Dispose()
        {
            try
            {
                if (_sink != null && _started)
                {
                    try { _sink.Finalize_(); }
                    catch { /* finalize failure shouldn't leak the MF startup */ }
                }
            }
            finally
            {
                if (_sink != null)
                {
                    Marshal.ReleaseComObject(_sink);
                    _sink = null;
                }
                if (_mfStartedHere)
                {
                    try { MFShutdown(); } catch { }
                    _mfStartedHere = false;
                }
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────
        private static void ThrowIfFailed(int hr, string op)
        {
            if (hr < 0)
                throw new System.ComponentModel.Win32Exception(hr,
                    $"{op} failed (HRESULT 0x{hr:X8})");
        }
        private static ulong PackU64(uint hi, uint lo) => ((ulong)hi << 32) | lo;

        private static void SetGuid(IMFMediaType t, Guid key, Guid val)
        {
            Guid k = key, v = val;
            ThrowIfFailed(t.SetGUID(ref k, ref v), "SetGUID");
        }
        private static void SetU32(IMFMediaType t, Guid key, uint val)
        {
            Guid k = key;
            ThrowIfFailed(t.SetUINT32(ref k, val), "SetUINT32");
        }
        private static void SetU64(IMFMediaType t, Guid key, ulong val)
        {
            Guid k = key;
            ThrowIfFailed(t.SetUINT64(ref k, val), "SetUINT64");
        }

        // ── COM interfaces ────────────────────────────────────────────────
        //
        // Slots that the writer never invokes are declared with placeholder
        // signatures (no params, PreserveSig int) so the v-table layout is
        // preserved without forcing us to model every parameter of every
        // unused method. The CLR only synthesises call stubs for methods we
        // actually invoke.

        [ComImport, Guid("3137f1cd-fe5e-4805-a5d8-fb477448cb3d"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFSinkWriter
        {
            [PreserveSig] int AddStream([MarshalAs(UnmanagedType.Interface)] IMFMediaType pTargetMediaType, out int pdwStreamIndex);
            [PreserveSig] int SetInputMediaType(int dwStreamIndex, [MarshalAs(UnmanagedType.Interface)] IMFMediaType pInputMediaType, IntPtr pEncodingParameters);
            [PreserveSig] int BeginWriting();
            [PreserveSig] int WriteSample(int dwStreamIndex, [MarshalAs(UnmanagedType.Interface)] IMFSample pSample);
            [PreserveSig] int SendStreamTick();
            [PreserveSig] int PlaceMarker();
            [PreserveSig] int NotifyEndOfSegment();
            [PreserveSig] int Flush();
            [PreserveSig] int Finalize_();
            [PreserveSig] int GetServiceForStream();
            [PreserveSig] int GetStatistics();
        }

        [ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFMediaType  // : IMFAttributes
        {
            // IMFAttributes (30 methods) ── slots 0-29
            [PreserveSig] int GetItem();
            [PreserveSig] int GetItemType();
            [PreserveSig] int CompareItem();
            [PreserveSig] int Compare();
            [PreserveSig] int GetUINT32();
            [PreserveSig] int GetUINT64();
            [PreserveSig] int GetDouble();
            [PreserveSig] int GetGUID();
            [PreserveSig] int GetStringLength();
            [PreserveSig] int GetString();
            [PreserveSig] int GetAllocatedString();
            [PreserveSig] int GetBlobSize();
            [PreserveSig] int GetBlob();
            [PreserveSig] int GetAllocatedBlob();
            [PreserveSig] int GetUnknown();
            [PreserveSig] int SetItem();
            [PreserveSig] int DeleteItem();
            [PreserveSig] int DeleteAllItems();
            [PreserveSig] int SetUINT32(ref Guid guidKey, uint unValue);
            [PreserveSig] int SetUINT64(ref Guid guidKey, ulong unValue);
            [PreserveSig] int SetDouble();
            [PreserveSig] int SetGUID(ref Guid guidKey, ref Guid guidValue);
            [PreserveSig] int SetString();
            [PreserveSig] int SetBlob();
            [PreserveSig] int SetUnknown();
            [PreserveSig] int LockStore();
            [PreserveSig] int UnlockStore();
            [PreserveSig] int GetCount();
            [PreserveSig] int GetItemByIndex();
            [PreserveSig] int CopyAllItems();
            // IMFMediaType (slots 30-33) — unused here.
            [PreserveSig] int GetMajorType();
            [PreserveSig] int IsCompressedFormat();
            [PreserveSig] int IsEqual();
            [PreserveSig] int GetRepresentation();
        }

        [ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFSample  // : IMFAttributes
        {
            // IMFAttributes (slots 0-29) — all unused on sample, declared as
            // placeholders to preserve the v-table layout.
            [PreserveSig] int A00(); [PreserveSig] int A01(); [PreserveSig] int A02();
            [PreserveSig] int A03(); [PreserveSig] int A04(); [PreserveSig] int A05();
            [PreserveSig] int A06(); [PreserveSig] int A07(); [PreserveSig] int A08();
            [PreserveSig] int A09(); [PreserveSig] int A10(); [PreserveSig] int A11();
            [PreserveSig] int A12(); [PreserveSig] int A13(); [PreserveSig] int A14();
            [PreserveSig] int A15(); [PreserveSig] int A16(); [PreserveSig] int A17();
            [PreserveSig] int A18(); [PreserveSig] int A19(); [PreserveSig] int A20();
            [PreserveSig] int A21(); [PreserveSig] int A22(); [PreserveSig] int A23();
            [PreserveSig] int A24(); [PreserveSig] int A25(); [PreserveSig] int A26();
            [PreserveSig] int A27(); [PreserveSig] int A28(); [PreserveSig] int A29();
            // IMFSample (slots 30+)
            [PreserveSig] int GetSampleFlags();
            [PreserveSig] int SetSampleFlags();
            [PreserveSig] int GetSampleTime();
            [PreserveSig] int SetSampleTime(long hnsSampleTime);
            [PreserveSig] int GetSampleDuration();
            [PreserveSig] int SetSampleDuration(long hnsSampleDuration);
            [PreserveSig] int GetBufferCount();
            [PreserveSig] int GetBufferByIndex();
            [PreserveSig] int ConvertToContiguousBuffer();
            [PreserveSig] int AddBuffer([MarshalAs(UnmanagedType.Interface)] IMFMediaBuffer pBuffer);
            [PreserveSig] int RemoveBufferByIndex();
            [PreserveSig] int RemoveAllBuffers();
            [PreserveSig] int GetTotalLength();
            [PreserveSig] int CopyToBuffer();
        }

        [ComImport, Guid("045fa593-8799-42b8-bc8d-8968c6453507"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFMediaBuffer
        {
            [PreserveSig] int Lock(out IntPtr ppbBuffer, out uint pcbMaxLength, out uint pcbCurrentLength);
            [PreserveSig] int Unlock();
            [PreserveSig] int GetCurrentLength(out uint pcbCurrentLength);
            [PreserveSig] int SetCurrentLength(uint cbCurrentLength);
            [PreserveSig] int GetMaxLength(out uint pcbMaxLength);
        }
    }
}