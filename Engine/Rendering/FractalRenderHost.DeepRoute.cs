// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Rendering/FractalRenderHost.DeepRoute.cs
// #1787 (#1665 S3, folds in #1493) — the deep-zoom route for the Control Center:
// the GPU deep zoom toggle, why a device can't, the stored calibration as text,
// re-run and reset. UI.Avalonia reaches it through IFractalRenderHost.

using System;
using System.Globalization;
using System.Text;

namespace FracturingFog.Rendering
{
    public sealed partial class FractalRenderHost
    {
        /// <inheritdoc />
        public bool GpuDeepZoom
        {
            get
            {
                var k = _calculator.GpuKernel;
                return MandelbrotCalculator.GpuDeepUnavailableReason(k) == null && MandelbrotCalculator.GpuPerturbationEnabled(k);
            }
            set
            {
                MandelbrotCalculator.SetGpuPerturbationFromUser(value);
                Trigger();
            }
        }

        /// <inheritdoc />
        public bool? GpuDeepZoomUserSetting => MandelbrotCalculator.GpuPerturbUserOverride;

        /// <inheritdoc />
        public string? GpuDeepZoomUnavailableReason => MandelbrotCalculator.GpuDeepUnavailableReason(_calculator.GpuKernel);

        /// <inheritdoc />
        public string DeepRouteSummary => BuildDeepRouteSummary(_calculator.GpuKernel);

        /// <summary>#1787 — the stored rows for this CPU and <paramref name="kernel"/>'s
        /// device, as two or three short lines.</summary>
        public static string BuildDeepRouteSummary(IGpuKernel? kernel)
        {
            if (!MandelbrotCalculator.UseDeepRouteCalibration)
                return "Calibration off (FF_DEEP_ROUTE_CALIBRATION=0): the fixed defaults apply.";
            static string B(bool? b) => b switch { true => "on", false => "off", _ => "default" };
            var sb = new StringBuilder();
            var cpu = DeepRouteStore.LookupCpu();
            if (cpu == null) sb.Append("CPU: not measured yet (runs on the first deep-zoom frame).");
            else
            {
                string la = cpu.LaMinZoom switch
                {
                    null => "default (1e30)",
                    double z when double.IsPositiveInfinity(z) => "never",
                    double z => "from " + z.ToString("0e0", CultureInfo.InvariantCulture),
                };
                sb.Append($"CPU: LA {la}, LA SIMD {B(cpu.LaSimd)}");
                if (cpu.Measured != null) sb.Append($" (measured {cpu.Measured[..Math.Min(10, cpu.Measured.Length)]})");
                sb.Append('.');
            }
            if (kernel?.DeviceKey is { Length: > 0 } key)
            {
                string name = kernel.DeviceName ?? kernel.BackendLabel;
                var gpu = DeepRouteStore.LookupGpu(key);
                sb.Append('\n').Append(gpu == null
                    ? $"GPU ({name}): not measured yet."
                    : $"GPU ({name}): deep zoom {B(gpu.GpuDeep)}, LA kernel {B(gpu.GpuLa)}, kernel {gpu.Precision?.ToString() ?? "default"}.");
            }
            switch (DeepRouteCalibration.State)
            {
                case DeepRouteCalibrationState.Running: sb.Append("\nCalibrating in the background…"); break;
                case DeepRouteCalibrationState.Failed: sb.Append("\nThe last calibration failed (see the log)."); break;
                case DeepRouteCalibrationState.Cancelled: sb.Append("\nThe last calibration was cancelled."); break;
            }
            return sb.ToString();
        }

        /// <inheritdoc />
        public bool RerunDeepRouteCalibration() => DeepRouteCalibration.RequestRun();

        /// <inheritdoc />
        public void ResetDeepRouteCalibration()
        {
            DeepRouteStore.Reset();
            Trigger();
        }

        /// <inheritdoc />
        public event EventHandler? DeepRouteCalibrationCompleted
        {
            add { lock (s_calLock) { if (s_calHandlers == null) DeepRouteCalibration.Completed += RaiseCalibrationCompleted; s_calHandlers += value; } }
            remove { lock (s_calLock) { s_calHandlers -= value; } }
        }

        private static readonly object s_calLock = new();
        private static EventHandler? s_calHandlers;
        private static void RaiseCalibrationCompleted(DeepRouteCalibrationResult? _)
        {
            EventHandler? h;
            lock (s_calLock) h = s_calHandlers;
            h?.Invoke(null, EventArgs.Empty);
        }
    }
}
