// Rendering/PerfStats.cs
//
// Lightweight per-phase frame-time collector for the in-app perf HUD.
//
// Sampling points (all in FractalRenderHost):
//   • calc-ms    — RunFrameJobCalc, around the Calculate(token) call
//   • upload-ms  — UploadProcessedBuffer body excluding the swap-chain Present
//   • present-ms — the IFractalRenderer.Render() call (swap-chain Present)
//   • frame-ms   — Trigger → FrameCompleted (job.Sw.ElapsedMilliseconds)
//
// Rolling-window buffers (capacity 60 samples). Reads + writes serialised by
// a single lock; the writers are the dedicated calc thread + threadpool
// upload workers, the reader is the compositor on whatever thread the
// post-FX upload runs on. Lock cost is irrelevant against the surrounding
// 5-100 ms work.
//
// HUD overhead target: < 0.1 ms per frame. Snapshot() = 4 array sweeps of 60
// doubles + a Stopwatch read; well inside budget.

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FracturingFog.Rendering
{
    internal sealed class PerfStats
    {
        private const int CAP = 60;

        private readonly double[] _calc    = new double[CAP];
        private readonly double[] _upload  = new double[CAP];
        private readonly double[] _present = new double[CAP];
        private readonly double[] _frame   = new double[CAP];

        private int _idxCalc, _idxUp, _idxPres, _idxFrame;
        private int _cntCalc, _cntUp, _cntPres, _cntFrame;

        private readonly object _lock = new();

        private long _startTicks = Stopwatch.GetTimestamp();
        private int _gen0Start = GC.CollectionCount(0);
        private int _gen1Start = GC.CollectionCount(1);
        private int _gen2Start = GC.CollectionCount(2);

        public void RecordCalc(double ms)
        {
            lock (_lock)
            {
                _calc[_idxCalc] = ms;
                _idxCalc = (_idxCalc + 1) % CAP;
                if (_cntCalc < CAP) _cntCalc++;
            }
        }

        public void RecordUpload(double ms)
        {
            lock (_lock)
            {
                _upload[_idxUp] = ms;
                _idxUp = (_idxUp + 1) % CAP;
                if (_cntUp < CAP) _cntUp++;
            }
        }

        public void RecordPresent(double ms)
        {
            lock (_lock)
            {
                _present[_idxPres] = ms;
                _idxPres = (_idxPres + 1) % CAP;
                if (_cntPres < CAP) _cntPres++;
            }
        }

        public void RecordFrame(double ms)
        {
            lock (_lock)
            {
                _frame[_idxFrame] = ms;
                _idxFrame = (_idxFrame + 1) % CAP;
                if (_cntFrame < CAP) _cntFrame++;
            }
        }

        public PerfSnapshot Snapshot()
        {
            lock (_lock)
            {
                double aCalc = Avg(_calc, _cntCalc);
                double aUp   = Avg(_upload, _cntUp);
                double aPres = Avg(_present, _cntPres);
                double aFr   = Avg(_frame, _cntFrame);
                double fMin  = Min(_frame, _cntFrame);
                double fMax  = Max(_frame, _cntFrame);
                long now = Stopwatch.GetTimestamp();
                double secs = Math.Max(1e-6, (now - _startTicks) / (double)Stopwatch.Frequency);
                int g0Now = GC.CollectionCount(0);
                int g1Now = GC.CollectionCount(1);
                int g2Now = GC.CollectionCount(2);
                double g0Rate = (g0Now - _gen0Start) / secs;
                double g1Rate = (g1Now - _gen1Start) / secs;
                double g2Rate = (g2Now - _gen2Start) / secs;
                double fps = aFr > 0 ? 1000.0 / aFr : 0;
                return new PerfSnapshot(
                    aCalc, aUp, aPres, aFr,
                    fMin, fMax, fps,
                    g0Rate, g1Rate, g2Rate,
                    _cntFrame);
            }
        }

        public void Reset()
        {
            lock (_lock)
            {
                Array.Clear(_calc); Array.Clear(_upload);
                Array.Clear(_present); Array.Clear(_frame);
                _idxCalc = _idxUp = _idxPres = _idxFrame = 0;
                _cntCalc = _cntUp = _cntPres = _cntFrame = 0;
                _startTicks = Stopwatch.GetTimestamp();
                _gen0Start = GC.CollectionCount(0);
                _gen1Start = GC.CollectionCount(1);
                _gen2Start = GC.CollectionCount(2);
            }
        }

        private static double Avg(double[] buf, int cnt)
        {
            if (cnt <= 0) return 0;
            double s = 0;
            for (int i = 0; i < cnt; i++) s += buf[i];
            return s / cnt;
        }

        private static double Min(double[] buf, int cnt)
        {
            if (cnt <= 0) return 0;
            double m = buf[0];
            for (int i = 1; i < cnt; i++) if (buf[i] < m) m = buf[i];
            return m;
        }

        private static double Max(double[] buf, int cnt)
        {
            if (cnt <= 0) return 0;
            double m = buf[0];
            for (int i = 1; i < cnt; i++) if (buf[i] > m) m = buf[i];
            return m;
        }
    }

    internal readonly struct PerfSnapshot
    {
        public readonly double CalcMs;
        public readonly double UploadMs;
        public readonly double PresentMs;
        public readonly double FrameMs;
        public readonly double FrameMin;
        public readonly double FrameMax;
        public readonly double Fps;
        public readonly double Gen0PerSec;
        public readonly double Gen1PerSec;
        public readonly double Gen2PerSec;
        public readonly int SampleCount;

        public PerfSnapshot(double calcMs, double uploadMs, double presentMs, double frameMs,
            double frameMin, double frameMax, double fps,
            double g0, double g1, double g2,
            int sampleCount)
        {
            CalcMs = calcMs; UploadMs = uploadMs; PresentMs = presentMs; FrameMs = frameMs;
            FrameMin = frameMin; FrameMax = frameMax; Fps = fps;
            Gen0PerSec = g0; Gen1PerSec = g1; Gen2PerSec = g2;
            SampleCount = sampleCount;
        }
    }

    /// <summary>
    /// One-shot hardware probe — cached static fields. Cheap; no per-frame
    /// cost. Used by the perf HUD to label captures with the host's
    /// SIMD / core configuration so cross-machine comparison is unambiguous.
    /// </summary>
    internal static class HardwareProbe
    {
        public static readonly int ProcessorCount = Environment.ProcessorCount;
        public static readonly string OsArch = RuntimeInformation.OSArchitecture.ToString();
        public static readonly string RuntimeVer = Environment.Version.ToString();

        public static readonly bool Sse2    = System.Runtime.Intrinsics.X86.Sse2.IsSupported;
        public static readonly bool Sse41   = System.Runtime.Intrinsics.X86.Sse41.IsSupported;
        public static readonly bool Avx     = System.Runtime.Intrinsics.X86.Avx.IsSupported;
        public static readonly bool Avx2    = System.Runtime.Intrinsics.X86.Avx2.IsSupported;
        public static readonly bool Fma     = System.Runtime.Intrinsics.X86.Fma.IsSupported;
        public static readonly bool Avx512F = System.Runtime.Intrinsics.X86.Avx512F.IsSupported;

        public static string Summary
        {
            get
            {
                string simd =
                    Avx512F ? "AVX-512" :
                    Avx2    ? (Fma ? "AVX2+FMA" : "AVX2") :
                    Avx     ? "AVX" :
                    Sse41   ? "SSE4.1" :
                    Sse2    ? "SSE2" : "scalar";
                return $"{ProcessorCount}c {OsArch}  {simd}  .NET {RuntimeVer}";
            }
        }
    }
}
