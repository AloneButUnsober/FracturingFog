// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Calculators/MandelbrotCalculator.DeepRouteUi.cs
// #1787 (#1665 S3, folds in #1493) — what the UI needs from the deep-zoom route:
//   • the Control Center's "GPU deep zoom" toggle (GpuPerturbUserOverride, above the
//     env switches, the host default and the store; turning it on re-arms the session
//     latch);
//   • why a device can't do GPU deep zoom at all (the toggle's disabled reason);
//   • the route the perf HUD shows, each choice with where it came from (measured /
//     default / env / setting / toggle / latch).

using System;
using System.Globalization;

namespace FracturingFog
{
    /// <summary>#1787 — where a deep-route choice came from.</summary>
    public enum DeepRouteSource
    {
        /// <summary>Today's fixed default.</summary>
        Default,
        /// <summary>The per-device calibration (DeepRouteStore).</summary>
        Measured,
        /// <summary>An environment switch.</summary>
        Env,
        /// <summary>A setting: the view's LA mode, a probe / host turning a switch on.</summary>
        Setting,
        /// <summary>The Control Center toggle.</summary>
        Toggle,
        /// <summary>The session latch (GPU too slow / device lost).</summary>
        Latch,
        /// <summary>The Vulkan session's default-on.</summary>
        BackendDefault,
    }

    /// <summary>#1787 — the deep route a frame resolves to, with sources.</summary>
    public readonly record struct DeepRouteDescription(
        bool GpuDeep, DeepRouteSource GpuDeepSource,
        double LaMinZoom, DeepRouteSource LaSource, DeepLaMode LaMode,
        bool LaSimd, DeepRouteSource LaSimdSource,
        bool GpuLa, DeepRouteSource GpuLaSource,
        DeepRouteCalibrationState Calibration)
    {
        public static string Label(DeepRouteSource s) => s switch
        {
            DeepRouteSource.Measured => "measured",
            DeepRouteSource.Env => "env",
            DeepRouteSource.Setting => "set",
            DeepRouteSource.Toggle => "toggle",
            DeepRouteSource.Latch => "session off",
            DeepRouteSource.BackendDefault => "Vulkan default",
            _ => "default",
        };

        /// <summary>One perf-HUD line, e.g. "deep   LA ≥1e27 measured · SIMD on measured ·
        /// GPU off default".</summary>
        public string HudLine
        {
            get
            {
                string la = LaMode switch
                {
                    DeepLaMode.On => $"LA on {Label(LaSource)}",
                    DeepLaMode.Off => $"LA off {Label(LaSource)}",
                    _ => double.IsPositiveInfinity(LaMinZoom) ? $"LA never {Label(LaSource)}"
                        : $"LA ≥{LaMinZoom.ToString("0e0", CultureInfo.InvariantCulture)} {Label(LaSource)}",
                };
                string gpu = $"GPU {(GpuDeep ? "on" : "off")} {Label(GpuDeepSource)}"
                    + (GpuDeep ? $" (LA kernel {(GpuLa ? "on" : "off")} {Label(GpuLaSource)})" : "");
                string cal = Calibration == DeepRouteCalibrationState.Running ? " · calibrating…" : "";
                return $"deep   {la} · SIMD {(LaSimd ? "on" : "off")} {Label(LaSimdSource)} · {gpu}{cal}";
            }
        }
    }

    public partial class MandelbrotCalculator
    {
        /// <summary>#1787 — the Control Center's GPU deep zoom toggle: null = not touched
        /// this session; true / false beats the env switch, the backend default and the
        /// store (the session latch still turns it off until the toggle re-arms it).
        /// Per session: not persisted.</summary>
        public static bool? GpuPerturbUserOverride { get; set; }

        /// <summary>#1787 — the toggle. On also re-arms GPU perturbation after the
        /// session latch (too slow / device lost) turned it off.</summary>
        public static void SetGpuPerturbationFromUser(bool on)
        {
            if (on)
            {
                s_gpuPerturbDisabled = null;
                UseGpuPerturbation = true;
            }
            GpuPerturbUserOverride = on;
        }

        /// <summary>#1493 / #1787 — the perf HUD detail when GPU deep zoom is off: why, and
        /// how to turn it on.</summary>
        private static string DeepZoomOffDetail(FracturingFog.Rendering.IGpuKernel? kernel)
        {
            const string how = "Control Center \u2192 Advanced \u2192 GPU deep zoom turns it on (FF_GPU_PERTURB=1 at launch)";
            if (GpuPerturbUserOverride == false) return "deep zoom renders on the CPU: GPU deep zoom is off in the Control Center";
            if (GpuPerturbEnvSwitch == false) return "deep zoom renders on the CPU: FF_GPU_PERTURB=0; " + how;
            if (CalibratedGpu(kernel)?.GpuDeep == false)
                return "deep zoom renders on the CPU: measured faster than this GPU (deep-route calibration); " + how;
            return "deep zoom renders on the CPU by default; " + how;
        }

        /// <summary>#1787 — why <paramref name="kernel"/> can't run deep zoom on the GPU
        /// at all, or null when it can (the toggle is disabled with this reason).</summary>
        public static string? GpuDeepUnavailableReason(FracturingFog.Rendering.IGpuKernel? kernel)
        {
            if (kernel == null) return "No GPU compute kernel is attached (GPU compute is off, or no device).";
            if (kernel.SupportsPerturbation) return null;
            if (kernel.SupportsPerturbationFloatExp)
                return GpuDeepPrecisionMode == GpuDeepPrecision.Double
                    ? $"{kernel.DeviceName ?? kernel.BackendLabel} has no fp64, and FF_GPU_PERTURB_PRECISION=double rules out the fp32 deep-zoom kernel."
                    : null;
            return $"{kernel.DeviceName ?? kernel.BackendLabel} has no deep-zoom kernel (no fp64).";
        }

        /// <summary>#1787 — the deep route this calculator's frames resolve to, with
        /// where each choice came from.</summary>
        public DeepRouteDescription DescribeDeepRoute()
        {
            var k = GpuKernel;
            var gpuRow = CalibratedGpu(k);
            var cpuRow = CalibratedCpu;

            bool gpuDeep = GpuPerturbationEnabled(k) && k != null && (k.SupportsPerturbation || UseFloatExpKernel(k));
            DeepRouteSource gpuSrc =
                s_gpuPerturbDisabled != null ? DeepRouteSource.Latch
                : GpuPerturbUserOverride != null ? DeepRouteSource.Toggle
                : UseGpuPerturbation && UseGpuPerturbationIsDefault && GpuPerturbEnvSwitch == null
                    ? (gpuRow?.GpuDeep == false ? DeepRouteSource.Measured : DeepRouteSource.BackendDefault)
                : GpuPerturbEnvSwitch != null ? DeepRouteSource.Env
                : UseGpuPerturbation ? DeepRouteSource.Setting
                : gpuRow?.GpuDeep != null ? DeepRouteSource.Measured
                : DeepRouteSource.Default;

            var mode = EffectiveLaMode;
            DeepRouteSource laSrc = LaModeOverride != null ? DeepRouteSource.Env
                : mode != DeepLaMode.Auto ? DeepRouteSource.Setting
                : cpuRow?.LaMinZoom is double z && !double.IsNaN(z) && z > 0 ? DeepRouteSource.Measured
                : DeepRouteSource.Default;

            bool simd = LaSimdWanted && System.Runtime.Intrinsics.X86.Avx2.IsSupported;
            DeepRouteSource simdSrc = LaSimdEnvSwitch != null ? DeepRouteSource.Env
                : LaSimd ? DeepRouteSource.Setting
                : cpuRow?.LaSimd != null ? DeepRouteSource.Measured
                : DeepRouteSource.Default;

            bool gpuLa = GpuLaEnabled(k) && k is { SupportsPerturbationLa: true };
            DeepRouteSource gpuLaSrc = GpuLaEnvSwitch != null ? DeepRouteSource.Env
                : UseGpuLa ? DeepRouteSource.Setting
                : gpuRow?.GpuLa != null ? DeepRouteSource.Measured
                : DeepRouteSource.Default;

            return new DeepRouteDescription(gpuDeep, gpuSrc, EffectiveLaAutoMinZoom, laSrc, mode,
                simd, simdSrc, gpuLa, gpuLaSrc, DeepRouteCalibration.State);
        }
    }
}
