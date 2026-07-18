// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Render/PerformanceTier.cs
//
// Scene Engine Roadmap — Phase S2: hardware-tier profiles (the single knob).
//
// The perf knobs already exist and overlap — preview resolution scale
// (FractalParameters.LowResPreviewScale), volume-march steps
// (LightingFxData.VolumeSteps), the animated-param ceiling
// (AnimatedParamCeilingPolicy), GPU dispatch (LightingFxData.UseGpuRender),
// and the QualityTier / AA presets. A novice should not have to balance five
// controls. This phase collapses them behind ONE selector — Potato / Balanced
// / Wow — that resolves to a concrete TierKnobs set. An Advanced drawer (UI
// consumer, later) edits the TierKnobs directly for users who want manual
// control (the "user-tunable parameters" ask).
//
// Two independent pure operations, both side-effect-free + unit-tested:
//   1. DefaultTier(HardwareProfile) — pick a starting tier from the same probe
//      the animation ceiling already uses (logical cores + discrete-GPU flag).
//   2. Resolve(baseline, qualityScale) — fold the live ResourceGovernor (S1)
//      quality scale [floor..1] onto a tier's baseline knobs. This is the
//      "apply-knobs" half of S2's sample→evaluate→apply loop: the governor
//      hands us a scalar, we turn it into concrete continuous-knob values while
//      leaving structural knobs (precision tier, GPU gate) untouched — dropping
//      the precision tier mid-zoom would change the zoom limits, so the
//      governor only steers the smooth knobs.
//
// Ships behind current behaviour: nothing reads TierKnobs yet. The periodic
// driver (sampler.Sample -> governor.Evaluate -> Resolve -> push onto
// FractalParameters / LightingFxData) is the UI-side consumer, wired through
// AvaloniaShellBootstrap later — same "infrastructure lands first" cadence as
// S0 (RenderMode) and S1 (ResourceGovernor).

using System;

using FracturingFog.Abstractions.Animation;
using FracturingFog.Models;

namespace FracturingFog.Render
{
    /// <summary>The one performance selector a novice picks. Each tier resolves
    /// to a full <see cref="TierKnobs"/> set via
    /// <see cref="PerformanceTierProfile.Baseline"/>.</summary>
    public enum PerformanceTier
    {
        /// <summary>iGPU / old laptop. Half-res preview, minimal effects, tight
        /// param ceiling, tolerates the CPU fallback path.</summary>
        Potato = 0,

        /// <summary>Mid GPU. Leans slightly toward performance (the low-mid
        /// hardware target).</summary>
        Balanced = 1,

        /// <summary>Discrete-GPU workstation. Full effect stack, high sample
        /// counts, sits near the 90% cap.</summary>
        Wow = 2,
    }

    /// <summary>
    /// Concrete, resolved performance knob values. Produced by
    /// <see cref="PerformanceTierProfile.Baseline"/> (per tier) and then folded
    /// with the live governor quality scale by
    /// <see cref="PerformanceTierProfile.Resolve"/>. The UI consumer pushes each
    /// field onto its real home (named in each summary).
    /// </summary>
    /// <param name="PreviewResolutionScale">Realtime preview render scale
    /// [0.25..1.0] → <c>FractalParameters.LowResPreviewScale</c> /
    /// <c>LowResPreview.ComputeDims</c>.</param>
    /// <param name="VolumeSteps">Volumetric-march step budget →
    /// <c>LightingFxData.VolumeSteps</c>.</param>
    /// <param name="AnimatedParamCeiling">Max simultaneously-animated params →
    /// <c>AnimatedParamCeilingPolicy</c> ceiling override.</param>
    /// <param name="AaSamples">Preview anti-alias samples (N² grid, 1 = off) →
    /// the active <c>QualityPreset.AaSamples</c> cap.</param>
    /// <param name="QualityTier">Precision / zoom-depth tier →
    /// <c>QualityPreset.Get</c>. Structural — the governor never changes it.</param>
    /// <param name="AllowGpuRender">Whether the float GPU raymarch may run →
    /// gates <c>LightingFxData.UseGpuRender</c>. Structural.</param>
    /// <param name="AllowCpuFallback">Whether a missing / weak GPU may fall back
    /// to the CPU path rather than refusing 3D. Structural.</param>
    public readonly record struct TierKnobs(
        double PreviewResolutionScale,
        int VolumeSteps,
        int AnimatedParamCeiling,
        int AaSamples,
        QualityTier QualityTier,
        bool AllowGpuRender,
        bool AllowCpuFallback);

    /// <summary>
    /// Pure tier → knob profiles plus the governor-quality fold. No I/O, no
    /// state — the periodic driver + knob application live in the UI consumer.
    /// </summary>
    public static class PerformanceTierProfile
    {
        /// <summary>Lowest preview scale the fold drops to — matches the
        /// <c>LowResPreview</c> / <c>FractalParameters.LowResPreviewScale</c>
        /// clamp so a resolved value is always a legal preview scale.</summary>
        public const double MinPreviewScale = 0.25;

        /// <summary>Floor for the volume-march budget under throttle — below
        /// this, volumetrics look broken rather than merely coarse, so the
        /// governor disables the effect elsewhere instead of starving it.</summary>
        public const int MinVolumeSteps = 8;

        /// <summary>iGPU / old-laptop baseline.</summary>
        public static readonly TierKnobs Potato = new(
            PreviewResolutionScale: 0.50,
            VolumeSteps: 24,
            AnimatedParamCeiling: AnimatedParamCeilingPolicy.ThreeDIntegratedGpuCeiling, // 4
            AaSamples: 1,
            QualityTier: QualityTier.Draft,
            AllowGpuRender: true,   // use the GPU if present…
            AllowCpuFallback: true); // …but tolerate the CPU path when it isn't.

        /// <summary>Mid-GPU baseline. Leans toward performance per the low-mid
        /// hardware target.</summary>
        public static readonly TierKnobs Balanced = new(
            PreviewResolutionScale: 0.75,
            VolumeSteps: 48,
            AnimatedParamCeiling: AnimatedParamCeilingPolicy.ThreeDDiscreteGpuCeiling, // 6
            AaSamples: 2,
            QualityTier: QualityTier.Standard,
            AllowGpuRender: true,
            AllowCpuFallback: true);

        /// <summary>Discrete-GPU workstation baseline. Full stack, sits near the
        /// 90% cap (the governor reels it back in when it gets there).</summary>
        public static readonly TierKnobs Wow = new(
            PreviewResolutionScale: 1.00,
            VolumeSteps: 96,
            AnimatedParamCeiling: AnimatedParamCeilingPolicy.TwoDCeiling, // 12
            AaSamples: 4,
            QualityTier: QualityTier.High,
            AllowGpuRender: true,
            AllowCpuFallback: false); // workstation — a GPU is expected.

        /// <summary>The default knob baseline for a tier. Callers edit the
        /// returned value (Advanced drawer) then feed it to
        /// <see cref="Resolve"/>.</summary>
        public static TierKnobs Baseline(PerformanceTier tier) => tier switch
        {
            PerformanceTier.Potato   => Potato,
            PerformanceTier.Balanced => Balanced,
            PerformanceTier.Wow      => Wow,
            _                        => Balanced,
        };

        /// <summary>
        /// Pick a starting tier from the hardware probe. Discrete GPU with a
        /// healthy core count earns Wow; a lone iGPU on few cores gets Potato;
        /// everything in between is Balanced (the safe low-mid default).
        /// </summary>
        public static PerformanceTier DefaultTier(HardwareProfile hw)
        {
            if (!hw.DiscreteGpu)
                return hw.LogicalCores <= 4 ? PerformanceTier.Potato : PerformanceTier.Balanced;

            return hw.LogicalCores >= 8 ? PerformanceTier.Wow : PerformanceTier.Balanced;
        }

        /// <summary>
        /// Fold the live governor quality scale [floor..1] onto a baseline. The
        /// continuous knobs (preview scale, volume steps, AA, animated-param
        /// ceiling) scale down proportionally and clamp at their floors; the
        /// structural knobs (precision tier, GPU gate, CPU fallback) pass
        /// through unchanged. At <paramref name="qualityScale"/> == 1 the result
        /// equals <paramref name="baseline"/> exactly; values above 1 are
        /// clamped (the governor never boosts past a tier's baseline).
        /// </summary>
        public static TierKnobs Resolve(in TierKnobs baseline, double qualityScale)
        {
            double q = Math.Clamp(qualityScale, 0.0, 1.0);

            double previewScale = Math.Clamp(
                baseline.PreviewResolutionScale * q,
                MinPreviewScale,
                baseline.PreviewResolutionScale);

            int volumeSteps = Math.Clamp(
                (int)Math.Round(baseline.VolumeSteps * q),
                MinVolumeSteps,
                baseline.VolumeSteps);

            int aa = Math.Clamp(
                (int)Math.Round(baseline.AaSamples * q),
                1,
                baseline.AaSamples);

            int ceiling = Math.Clamp(
                (int)Math.Round(baseline.AnimatedParamCeiling * q),
                1,
                baseline.AnimatedParamCeiling);

            return baseline with
            {
                PreviewResolutionScale = previewScale,
                VolumeSteps            = volumeSteps,
                AaSamples              = aa,
                AnimatedParamCeiling   = ceiling,
            };
        }
    }
}
