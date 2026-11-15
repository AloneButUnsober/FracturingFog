// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Calculators/MandelbrotCalculator.DeepRoute.cs
// #1665 / #1785 (S1) — the deep-zoom route switches read the per-device
// calibration (DeepRouteStore) when it holds a result for this CPU / GPU.
//
// Precedence, for each switch in #1663's tables:
//   1. its env switch, either way (FF_GPU_PERTURB, FF_GPU_LA, FF_LA_SIMD; the LA mode's
//      FF_LA and FF_GPU_PERTURB_PRECISION already set non-Auto modes);
//   2. an explicit setting on (UseGpuPerturbation / UseGpuLa / LaSimd = true: the host,
//      a probe, a test);
//   3. the stored, measured result for this device;
//   4. today's default.
// FF_DEEP_ROUTE_CALIBRATION=0 (UseDeepRouteCalibration = false) ignores the store.
// The session latches (GPU perturbation too slow / device lost) still win over all.

using System;

namespace FracturingFog
{
    public partial class MandelbrotCalculator
    {
        /// <summary>#1665 — "1/true/on/yes" → true, "0/false/off/no" → false, else null.</summary>
        public static bool? ParseEnvSwitch(string? value) => value?.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "on" or "yes" => true,
            "0" or "false" or "off" or "no" => false,
            _ => null,
        };

        /// <summary>#1665 — read the per-device calibration store. On unless
        /// FF_DEEP_ROUTE_CALIBRATION=0.</summary>
        public static bool UseDeepRouteCalibration { get; set; } =
            ParseEnvSwitch(Environment.GetEnvironmentVariable("FF_DEEP_ROUTE_CALIBRATION")) != false;

        /// <summary>#1665 — FF_GPU_PERTURB as an override (null = unset). True is applied
        /// by the host (UseGpuPerturbation); false keeps GPU deep off whatever was measured.</summary>
        public static bool? GpuPerturbEnvSwitch { get; set; } = ParseEnvSwitch(Environment.GetEnvironmentVariable("FF_GPU_PERTURB"));

        /// <summary>#1665 — FF_GPU_LA as an override (null = unset).</summary>
        public static bool? GpuLaEnvSwitch { get; set; } = ParseEnvSwitch(Environment.GetEnvironmentVariable("FF_GPU_LA"));

        /// <summary>#1665 — FF_LA_SIMD as an override (null = unset).</summary>
        public static bool? LaSimdEnvSwitch { get; set; } = ParseEnvSwitch(Environment.GetEnvironmentVariable("FF_LA_SIMD"));

        /// <summary>#1665 — the stored GPU row for this CPU and <paramref name="kernel"/>'s
        /// device, or null (no kernel, no device key, no row, calibration off).</summary>
        public static DeepRouteGpuResult? CalibratedGpu(FracturingFog.Rendering.IGpuKernel? kernel)
            => UseDeepRouteCalibration && kernel?.DeviceKey is { Length: > 0 } key ? DeepRouteStore.LookupGpu(key) : null;

        /// <summary>#1665 — the stored CPU row for this CPU, or null.</summary>
        public static DeepRouteCpuResult? CalibratedCpu => UseDeepRouteCalibration ? DeepRouteStore.LookupCpu() : null;

        /// <summary>#1665 — GPU deep perturbation for frames on <paramref name="kernel"/>:
        /// on when set explicitly (<see cref="UseGpuPerturbation"/>), else when measured
        /// faster on this device, unless FF_GPU_PERTURB=0 or a session latch turned it off.</summary>
        public static bool GpuPerturbationEnabled(FracturingFog.Rendering.IGpuKernel? kernel)
        {
            if (s_gpuPerturbDisabled != null) return false;
            if (GpuPerturbUserOverride is { } user) return user;   // #1787: the Control Center toggle
            if (UseGpuPerturbation)
                // #1786 — a default-on (a Vulkan session's, not an env switch's) yields to a
                // measured "GPU slower" on this device.
                return !(UseGpuPerturbationIsDefault && GpuPerturbEnvSwitch == null && CalibratedGpu(kernel)?.GpuDeep == false);
            return GpuPerturbEnvSwitch != false && CalibratedGpu(kernel)?.GpuDeep == true;
        }

        /// <summary>#1786 — <see cref="UseGpuPerturbation"/> was turned on as a session
        /// default (the Vulkan backend's), not asked for: a measured "GPU slower" on the
        /// device turns it off. Set by the host next to UseGpuPerturbation.</summary>
        public static bool UseGpuPerturbationIsDefault { get; set; }

        /// <summary>#1786 — test hook: clear the session's GPU-perturbation latch
        /// (too slow / device lost).</summary>
        public static void ResetGpuPerturbationLatchForTests() => s_gpuPerturbDisabled = null;

        /// <summary>#1786 — the session latch is set (GPU perturbation switched off).</summary>
        public static bool GpuPerturbationLatched => s_gpuPerturbDisabled != null;

        /// <summary>#1786 — the deep-zoom calibration (DeepRouteCalibrator) forces this
        /// calculator onto one candidate route, whatever the switches, the store and the
        /// env say; per calculator, so a background calibration never touches the live
        /// frames' settings. Null = the normal resolution.</summary>
        public DeepRouteCandidate? CalibrationRoute { get; set; }

        private static bool IsGpuRoute(DeepRouteCandidate r) => r is DeepRouteCandidate.GpuSaFp64 or DeepRouteCandidate.GpuFloatExp or DeepRouteCandidate.GpuLaFp64;

        // #1786 — this calculator's GPU deep / GPU LA / float-exp decisions (the
        // calibration's forced route first).
        private bool GpuDeepHere => CalibrationRoute is { } r ? IsGpuRoute(r) && s_gpuPerturbDisabled == null : GpuPerturbationEnabled(GpuKernel);
        private bool GpuLaHere => CalibrationRoute is { } r ? r == DeepRouteCandidate.GpuLaFp64 : GpuLaEnabled(GpuKernel);
        private bool FloatExpHere(FracturingFog.Rendering.IGpuKernel k)
            => CalibrationRoute is { } r ? r == DeepRouteCandidate.GpuFloatExp && k.SupportsPerturbationFloatExp : UseFloatExpKernel(k);

        // #1786 — the forced route's LA mode (null = not forced).
        private DeepLaMode? CalibrationLaMode => CalibrationRoute switch
        {
            null => null,
            DeepRouteCandidate.CpuLa or DeepRouteCandidate.CpuLaSimd or DeepRouteCandidate.GpuLaFp64 => DeepLaMode.On,
            _ => DeepLaMode.Off,
        };

        /// <summary>#1665 — the GPU LA stage kernel for frames on <paramref name="kernel"/>.</summary>
        public static bool GpuLaEnabled(FracturingFog.Rendering.IGpuKernel? kernel)
        {
            if (GpuLaEnvSwitch is { } env) return env;
            if (UseGpuLa) return true;
            return CalibratedGpu(kernel)?.GpuLa == true;
        }

        /// <summary>#1665 — the zoom from which Auto LA mode runs the LA stages: the
        /// measured crossover for this CPU, else <see cref="LaAutoMinZoom"/>.</summary>
        public static double EffectiveLaAutoMinZoom
            => CalibratedCpu?.LaMinZoom is double z && !double.IsNaN(z) && z > 0 ? z : LaAutoMinZoom;

        /// <summary>#1665 — the LA SIMD tail wanted (before its own preconditions).</summary>
        private bool LaSimdWanted
        {
            get
            {
                if (CalibrationRoute is { } r) return r == DeepRouteCandidate.CpuLaSimd;   // #1786
                if (LaSimdEnvSwitch is { } env) return env;
                if (LaSimd) return true;
                return CalibratedCpu?.LaSimd == true;
            }
        }

        /// <summary>#1665 — GpuDeepPrecision.Auto's choice on <paramref name="kernel"/>: the
        /// measured faster kernel where both exist, else the fp32 kernels only without fp64.</summary>
        private static bool AutoPicksFloatExp(FracturingFog.Rendering.IGpuKernel kernel)
            => CalibratedGpu(kernel)?.Precision switch
            {
                GpuDeepPrecision.Fp32 => true,
                GpuDeepPrecision.Double when kernel.SupportsPerturbation => false,
                _ => !kernel.SupportsPerturbation,
            };
    }
}
