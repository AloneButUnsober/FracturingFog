// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Engine/Models/OrbitLegAnimator.cs
//
// #954 — swings a raymarched-3D camera's azimuth by a fixed number of degrees
// over a video leg (single-shot Video Zoom or a video-slideshow camera leg).
// Works for every raymarch family without per-type code: it drives the generic
// Cam3DTheta of a camera-only RegionFractalParams snapshot and re-applies just
// that block (ApplyTo is null-guarded per field), so other animated params are
// left alone. Eased with VideoMotionPlan.SmootherStep so the swing starts and
// ends gently, the same curve batch video's --orbit uses (#947).

using System;

using FracturingFog.Abstractions.Animation;

namespace FracturingFog.Models
{
    public sealed class OrbitLegAnimator : IParameterAnimator
    {
        private readonly RegionFractalParams _camera;
        private readonly FractalParameters _target;
        private readonly double _theta0;
        private readonly double _degrees;
        private readonly double _legSeconds;
        private double _elapsed;

        private OrbitLegAnimator(RegionFractalParams camera, FractalParameters target,
                                 double degrees, double legSeconds)
        {
            _camera = camera;
            _target = target;
            _theta0 = camera.Cam3DTheta ?? 0.0;
            _degrees = degrees;
            _legSeconds = legSeconds > 0.0 ? legSeconds : 1.0;
        }

        /// <summary>An orbit for <paramref name="type"/>'s camera in
        /// <paramref name="p"/>, or null when there is nothing to orbit (zero
        /// degrees, not raymarched 3D, or no generic camera — e.g. UserBulb).</summary>
        public static OrbitLegAnimator? TryBuild(FractalType type, FractalParameters? p,
                                                 double degrees, double legSeconds)
        {
            if (p == null || degrees == 0.0) return null;
            if (FractalMotionCapabilities.MotionClass(type) != FractalMotionClass.Raymarch3D) return null;
            var cam = RegionFractalParams.CameraSnapshot(type, p);
            return cam == null ? null : new OrbitLegAnimator(cam, p, degrees, legSeconds);
        }

        public string Name => "CameraOrbit";
        public bool IsEnabled { get; set; } = true;
        public AnimatableParamCost Cost => AnimatableParamCost.Moderate;

        /// <summary>Azimuth (radians) at normalised leg progress u ∈ [0, 1].</summary>
        public double ThetaAt(double u)
            => VideoMotionPlan.OrbitTheta(_theta0, _degrees, VideoMotionPlan.SmootherStep(u));

        public void Tick(double dt)
        {
            if (!IsEnabled || dt <= 0.0) return;
            _elapsed += dt;
            _camera.Cam3DTheta = ThetaAt(Math.Clamp(_elapsed / _legSeconds, 0.0, 1.0));
            _camera.ApplyTo(_target);
        }
    }
}
