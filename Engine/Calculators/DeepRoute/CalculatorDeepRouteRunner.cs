// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Calculators/DeepRoute/CalculatorDeepRouteRunner.cs
// #1786 (S2) — the real IDeepRouteRunner: renders a calibration scene with a fresh
// MandelbrotCalculator forced onto one route (CalibrationRoute, per calculator: the
// live frames' switches are never touched) and times the whole frame, reference orbit
// included (as --gpulaprobe). Checks the route actually ran: a GPU frame that fell
// back to the CPU (too slow, gate closed) or an LA frame that didn't run LA is a
// fallback, not a timing. Every route warms up once, untimed.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.Intrinsics.X86;
using System.Threading;
using FracturingFog.Models;
using FracturingFog.Rendering;

namespace FracturingFog
{
    public sealed class CalculatorDeepRouteRunner : IDeepRouteRunner, IDisposable
    {
        private readonly IGpuKernel? _kernel;
        private readonly bool _ownsKernel;
        private readonly int _w, _h;
        private readonly HashSet<DeepRouteCandidate> _warm = new();
        private bool _settled;

        /// <summary>Untimed CPU LA frames run before the first timing until two in a row
        /// agree within 10 % (at most this many): measured, a single warm-up still left
        /// the first scene's CPU frames up to 2x slow (JIT tiering, thread-pool ramp).</summary>
        public const int MaxSettleRuns = 8;

        /// <summary>Default frame: small enough for a short pass, large enough that the
        /// GPU's fixed costs don't decide it alone.</summary>
        public const int DefaultWidth = 480, DefaultHeight = 270;   // measured: 240x135 frames took 7-50 ms, noise-level

        public CalculatorDeepRouteRunner(IGpuKernel? kernel, bool ownsKernel = false, int width = DefaultWidth, int height = DefaultHeight)
        {
            _kernel = kernel;
            _ownsKernel = ownsKernel;
            _w = width;
            _h = height;
            var c = new List<DeepRouteCandidate> { DeepRouteCandidate.CpuLa, DeepRouteCandidate.CpuBla };
            if (Avx2.IsSupported) c.Add(DeepRouteCandidate.CpuLaSimd);
            if (kernel != null)
            {
                if (kernel.SupportsPerturbation) c.Add(DeepRouteCandidate.GpuSaFp64);
                if (kernel.SupportsPerturbationFloatExp) c.Add(DeepRouteCandidate.GpuFloatExp);
                if (kernel.SupportsPerturbationLa) c.Add(DeepRouteCandidate.GpuLaFp64);
            }
            Candidates = c;
        }

        public IReadOnlyList<DeepRouteCandidate> Candidates { get; }

        public string? GpuDeviceKey => _kernel?.DeviceKey;

        private MandelbrotCalculator Make(DeepRouteScene s, DeepRouteCandidate r)
        {
            bool gpu = r is DeepRouteCandidate.GpuSaFp64 or DeepRouteCandidate.GpuFloatExp or DeepRouteCandidate.GpuLaFp64;
            static double L(double[] v, int i) => i < v.Length ? v[i] : 0;
            return new MandelbrotCalculator(_w, _h)
            {
                Quality = QualityPreset.Extreme,
                CenterX = L(s.X, 0), CenterXLo = L(s.X, 1), CenterX2 = L(s.X, 2), CenterX3 = L(s.X, 3),
                CenterX4 = L(s.X, 4), CenterX5 = L(s.X, 5), CenterX6 = L(s.X, 6), CenterX7 = L(s.X, 7),
                CenterY = L(s.Y, 0), CenterYLo = L(s.Y, 1), CenterY2 = L(s.Y, 2), CenterY3 = L(s.Y, 3),
                CenterY4 = L(s.Y, 4), CenterY5 = L(s.Y, 5), CenterY6 = L(s.Y, 6), CenterY7 = L(s.Y, 7),
                Zoom = s.Zoom, MaxIterations = s.MaxIter, ColorMap = new HsvPalette(),
                GpuKernel = gpu ? _kernel : null, CalibrationRoute = r, WavefrontSeedAllowed = false,
            };
        }

        /// <summary>Did the frame run <paramref name="r"/>?</summary>
        public static bool RanRoute(MandelbrotCalculator c, DeepRouteCandidate r) => r switch
        {
            DeepRouteCandidate.CpuBla => !c.LastFrameUsedGpuPerturbation && !c.LastFrameUsedLa,
            DeepRouteCandidate.CpuLa => !c.LastFrameUsedGpuPerturbation && c.LastFrameUsedLa && !c.LastFrameUsedLaSimd,
            DeepRouteCandidate.CpuLaSimd => !c.LastFrameUsedGpuPerturbation && c.LastFrameUsedLa && c.LastFrameUsedLaSimd,
            DeepRouteCandidate.GpuSaFp64 => c.LastFrameUsedGpuPerturbation && !c.LastFrameUsedGpuFloatExp && !c.LastFrameUsedGpuLa,
            DeepRouteCandidate.GpuFloatExp => c.LastFrameUsedGpuPerturbation && c.LastFrameUsedGpuFloatExp,
            DeepRouteCandidate.GpuLaFp64 => c.LastFrameUsedGpuPerturbation && c.LastFrameUsedGpuLa,
            _ => false,
        };

        public DeepRouteRun Run(DeepRouteScene scene, DeepRouteCandidate route, CancellationToken ct)
        {
            bool gpu = route is DeepRouteCandidate.GpuSaFp64 or DeepRouteCandidate.GpuFloatExp or DeepRouteCandidate.GpuLaFp64;
            if (gpu && _kernel == null) return DeepRouteRun.Fallback;
            try
            {
                if (!_settled)
                {
                    _settled = true;
                    double prev = double.NaN;
                    for (int i = 0; i < MaxSettleRuns && !ct.IsCancellationRequested; i++)
                    {
                        var w0 = Make(scene, DeepRouteCandidate.CpuLa);
                        var t0 = Stopwatch.StartNew();
                        w0.Calculate(ct);
                        double ms0 = t0.Elapsed.TotalMilliseconds;
                        if (Math.Abs(ms0 - prev) <= 0.1 * ms0) break;
                        prev = ms0;
                    }
                }
                if (_warm.Add(route))
                {
                    // Once per route, untimed: shader compile / pipeline creation on the GPU,
                    // JIT tiering and the thread pool on the CPU (measured: a cold first CPU
                    // pass ran up to 3x slower than the next).
                    var w = Make(scene, route);
                    w.Calculate(ct);
                    if (!RanRoute(w, route)) return DeepRouteRun.Fallback;
                }
                var c = Make(scene, route);
                var sw = Stopwatch.StartNew();
                c.Calculate(ct);
                double ms = sw.Elapsed.TotalMilliseconds;
                if (ct.IsCancellationRequested || !RanRoute(c, route)) return DeepRouteRun.Fallback;
                return new DeepRouteRun(ms, (int[])c.IterationBuffer.Clone(), false);
            }
            catch (OperationCanceledException) { return DeepRouteRun.Fallback; }
            catch (Exception) { return DeepRouteRun.Fallback; }
        }

        public void Dispose()
        {
            if (_ownsKernel) _kernel?.Dispose();
        }
    }
}
