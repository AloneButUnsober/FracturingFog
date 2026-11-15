// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Calculators/DeepRoute/DeepRouteCalibration.cs
// #1786 (S2) — when the calibration pass runs.
//
//   • In the background, once per process, on the first deep-zoom frame of a device
//     that has no stored result (RequestBackground, called by the render host). Only
//     where the host turned AutoCalibrate on (the live shell: never --batch, whose
//     renders must not change under it; #1788 adds its flag). It runs on its own
//     below-normal thread with its own GPU kernel (GpuKernelFactory: a device of its
//     own, never the live frames' kernel, whose buffers are not shared), and never
//     blocks a frame. The live frames read the result from the next frame on.
//   • On demand (RunNow): the probe, the Control Center (#1787), --batch (#1788).
// FF_DEEP_ROUTE_CALIBRATION=0 turns both the store and the auto pass off.

using System;
using System.Threading;
using FracturingFog.Rendering;

namespace FracturingFog
{
    public enum DeepRouteCalibrationState { Idle, Running, Done, Failed, Cancelled }

    public static class DeepRouteCalibration
    {
        /// <summary>Start the background pass on a device's first deep frame. Off unless
        /// the host turns it on (the live shell does).</summary>
        public static bool AutoCalibrate { get; set; }

        /// <summary>Opens the GPU kernel a calibration measures on (its own device; the
        /// caller's to dispose via the runner). Null = CPU routes only.</summary>
        public static Func<IGpuKernel?>? GpuKernelFactory { get; set; }

        /// <summary>The background pass's budget.</summary>
        public static TimeSpan BackgroundBudget { get; set; } = DeepRouteCalibrator.DefaultBudget;

        /// <summary>Builds the runner from the opened kernel (default: real frames,
        /// CalculatorDeepRouteRunner owning the kernel). Tests inject fakes.</summary>
        public static Func<IGpuKernel?, IDeepRouteRunner>? RunnerFactory { get; set; }

        private static readonly object s_lock = new();
        private static Thread? s_thread;
        private static CancellationTokenSource? s_cts;
        private static bool s_requested;

        public static DeepRouteCalibrationState State { get; private set; } = DeepRouteCalibrationState.Idle;

        /// <summary>The last pass's result (for the HUD / Control Center, #1787).</summary>
        public static DeepRouteCalibrationResult? LastResult { get; private set; }

        /// <summary>Raised on the calibration thread when a pass ends.</summary>
        public static event Action<DeepRouteCalibrationResult?>? Completed;

        /// <summary>True when the store already holds this CPU's row and, for a device
        /// key, that GPU's row.</summary>
        public static bool HasResultFor(string? deviceKey)
            => DeepRouteStore.LookupCpu() != null && (deviceKey == null || DeepRouteStore.LookupGpu(deviceKey) != null);

        /// <summary>The render host's hook: a deep frame rendered on a device (its key, or
        /// null on the CPU). Starts the background pass at most once per process.</summary>
        public static void RequestBackground(string? liveDeviceKey)
        {
            if (!AutoCalibrate || !MandelbrotCalculator.UseDeepRouteCalibration || s_requested) return;
            lock (s_lock)
            {
                if (s_requested) return;
                if (HasResultFor(liveDeviceKey)) { s_requested = true; return; }
                s_requested = true;
                StartLocked(BackgroundBudget);
            }
        }

        /// <summary>#1787 — the Control Center's "Re-run calibration": start a background
        /// pass now, whatever is stored (it replaces the rows). False when one is running
        /// or calibration is off (FF_DEEP_ROUTE_CALIBRATION=0).</summary>
        public static bool RequestRun()
        {
            if (!MandelbrotCalculator.UseDeepRouteCalibration) return false;
            lock (s_lock)
            {
                if (State == DeepRouteCalibrationState.Running) return false;
                s_requested = true;
                StartLocked(BackgroundBudget);
                return true;
            }
        }

        private static void StartLocked(TimeSpan budget)
        {
            s_cts = new CancellationTokenSource();
            var token = s_cts.Token;
            State = DeepRouteCalibrationState.Running;
            s_thread = new Thread(() => Body(budget, token))
            {
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal,
                Name = "FF deep-route calibration",
            };
            s_thread.Start();
        }

        private static void Body(TimeSpan budget, CancellationToken token)
        {
            DeepRouteCalibrationResult? result = null;
            try
            {
                result = RunNow(budget, token, log: m => Console.Error.WriteLine("[deep-route] " + m));
                State = token.IsCancellationRequested ? DeepRouteCalibrationState.Cancelled : DeepRouteCalibrationState.Done;
            }
            catch (Exception ex)
            {
                State = DeepRouteCalibrationState.Failed;
                Console.Error.WriteLine($"[deep-route] calibration failed: {ex.Message}");
            }
            try { Completed?.Invoke(result); } catch { }
        }

        /// <summary>Run a pass now on this thread (own GPU kernel from <see cref="GpuKernelFactory"/>)
        /// and store it. A cancelled pass stores nothing.</summary>
        public static DeepRouteCalibrationResult RunNow(TimeSpan? budget = null, CancellationToken ct = default, Action<string>? log = null)
        {
            IGpuKernel? kernel = null;
            try { kernel = GpuKernelFactory?.Invoke(); } catch { kernel = null; }
            var runner = RunnerFactory?.Invoke(kernel) ?? new CalculatorDeepRouteRunner(kernel, ownsKernel: true);
            try
            {
            log?.Invoke($"calibrating on {DeepRouteStore.CpuKey}" + (kernel != null ? $" + {kernel.DeviceName ?? kernel.BackendLabel}" : " (no GPU)"));
            var r = DeepRouteCalibrator.Run(runner, budget: budget, store: false, ct: ct, log: log);
            if (!ct.IsCancellationRequested && r.Timings.Count > 0)
            {
                DeepRouteStore.StoreCpu(r.Cpu);
                if (r.Gpu != null && r.GpuDeviceKey is { Length: > 0 } key) DeepRouteStore.StoreGpu(key, r.Gpu);
            }
            LastResult = r;
            Log(log, r, kernel);
            return r;
            }
            finally { (runner as IDisposable)?.Dispose(); }
        }

        private static void Log(Action<string>? log, DeepRouteCalibrationResult r, IGpuKernel? kernel)
        {
            log?.Invoke($"chose: LA from {r.Cpu.LaMinZoom?.ToString("0e0") ?? "default"}, LA SIMD {Show(r.Cpu.LaSimd)}"
                + (r.Gpu is { } g ? $", GPU deep {Show(g.GpuDeep)}, GPU LA {Show(g.GpuLa)}, kernel {g.Precision?.ToString() ?? "default"}" : "")
                + $" ({r.ElapsedMs / 1000:F1} s{(r.Complete ? "" : ", stopped early")})");
        }

        private static string Show(bool? b) => b switch { true => "on", false => "off", _ => "default" };

        /// <summary>Stop a running background pass (it stores nothing).</summary>
        public static void Cancel()
        {
            lock (s_lock) s_cts?.Cancel();
        }

        /// <summary>Test hook: forget that this process requested a pass.</summary>
        public static void ResetForTests()
        {
            lock (s_lock)
            {
                s_cts?.Cancel();
                s_thread?.Join(TimeSpan.FromSeconds(30));
                s_thread = null; s_cts = null; s_requested = false;
                State = DeepRouteCalibrationState.Idle;
                LastResult = null;
            }
        }
    }
}
