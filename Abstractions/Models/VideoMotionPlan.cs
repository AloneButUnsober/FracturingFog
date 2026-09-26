// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Models/VideoMotionPlan.cs
//
// #947 — frame-indexed motion math for batch (offline) video, shared so the
// headless renderer moves every fractal family the way the interactive video
// slideshow does (#91-#94, #806) without depending on the engine's wall-clock
// loop. Pure functions of the eased progress e ∈ [0, 1]:
//
//   • Zoomable2D  → plane log-zoom (FractalMotionCapabilities.SupportsVideoZoomLeg)
//   • Raymarch3D  → camera dolly: log-lerp Zoom from Authored/6 to Authored
//                   (every 3D calc uses CameraDistance / Zoom), optional orbit
//   • NonSpatial  → param sweep (Logistic r-window pan, AcidWarp flow) or a
//                   Ken-Burns pan/zoom of a single rendered frame
//
// The constants mirror FractalRenderHost.Video (CameraLegEstablishingFactor,
// Ken-Burns 1.08–1.14×, Logistic 3.5/Zoom span, AcidWarp +1.5 lengths).
// NB: System.Math is spelled out — FracturingFog.Abstractions.Math shadows it.

using System;

using FracturingFog.Batch;

namespace FracturingFog.Models
{
    /// <summary>A resolved per-video motion for one fractal family.</summary>
    public enum VideoMotionKind
    {
        /// <summary>Plane log-zoom (2D).</summary>
        Zoom,
        /// <summary>Camera dolly via the Zoom → CameraDistance/Zoom contract (3D).</summary>
        Dolly,
        /// <summary>One render, held.</summary>
        Hold,
        /// <summary>One render, image-space pan + zoom.</summary>
        KenBurns,
        /// <summary>Per-frame re-render with a swept family param.</summary>
        Sweep,
    }

    public static class VideoMotionPlan
    {
        /// <summary>Dolly starts this many times wider than the authored framing.</summary>
        public const double CameraEstablishingFactor = 6.0;
        /// <summary>Authored 3D zoom floor (region plane zoom is meaningless for 3D).</summary>
        public const double CameraAuthoredZoomFloor = 1.0;
        /// <summary>AcidWarp flow advance over a sweep, in pattern lengths.</summary>
        public const double AcidWarpSweepLengths = 1.5;
        /// <summary>Logistic visible r-window width at Zoom 1 (LogisticCalculator, W ≥ H).</summary>
        public const double LogisticSpanAtZoom1 = 3.5;

        /// <summary>Cubic smoothstep, the batch zoom easing.</summary>
        public static double SmoothStep(double t)
        {
            t = System.Math.Clamp(t, 0.0, 1.0);
            return t * t * (3.0 - 2.0 * t);
        }

        /// <summary>Quintic smootherstep, the hold / sweep easing.</summary>
        public static double SmootherStep(double t)
        {
            t = System.Math.Clamp(t, 0.0, 1.0);
            return t * t * t * (t * (t * 6.0 - 15.0) + 10.0);
        }

        /// <summary>
        /// Resolve the motion for <paramref name="type"/>. <see cref="BatchVideoMotion.Auto"/>
        /// picks by family; an explicit request that doesn't fit the family is
        /// adapted (Zoom on a non-spatial type → Ken-Burns, Sweep on a non-sweepable
        /// type → Ken-Burns) and <paramref name="note"/> explains why.
        /// </summary>
        public static VideoMotionKind Resolve(FractalType type, BatchVideoMotion requested, out string? note)
        {
            note = null;
            var cls = FractalMotionCapabilities.MotionClass(type);
            bool sweepable = FractalMotionCapabilities.SupportsVideoParamSweep(type);
            switch (requested)
            {
                case BatchVideoMotion.Hold: return VideoMotionKind.Hold;
                case BatchVideoMotion.KenBurns: return VideoMotionKind.KenBurns;
                case BatchVideoMotion.Sweep:
                    if (sweepable) return VideoMotionKind.Sweep;
                    note = $"{type} has no smooth param sweep (only Logistic / AcidWarp) — using Ken-Burns.";
                    return VideoMotionKind.KenBurns;
                case BatchVideoMotion.Zoom:
                    if (cls == FractalMotionClass.NonSpatial)
                    {
                        note = $"Zoom is a no-op for non-spatial {type} — using Ken-Burns.";
                        return VideoMotionKind.KenBurns;
                    }
                    return cls == FractalMotionClass.Raymarch3D ? VideoMotionKind.Dolly : VideoMotionKind.Zoom;
                default:
                    return cls switch
                    {
                        FractalMotionClass.Zoomable2D => VideoMotionKind.Zoom,
                        FractalMotionClass.Raymarch3D => VideoMotionKind.Dolly,
                        _ => sweepable ? VideoMotionKind.Sweep : VideoMotionKind.KenBurns,
                    };
            }
        }

        /// <summary>3D dolly endpoints: (wide establishing zoom, authored zoom).</summary>
        public static (double Wide, double Authored) CameraDolly(double authoredZoom)
        {
            double a = System.Math.Max(authoredZoom, CameraAuthoredZoomFloor);
            return (a / CameraEstablishingFactor, a);
        }

        /// <summary>Log-interpolated zoom between two endpoints at eased progress e.</summary>
        public static double LogLerpZoom(double z0, double z1, double e)
            => System.Math.Exp(System.Math.Log(z0) + (System.Math.Log(z1) - System.Math.Log(z0)) * e);

        /// <summary>Logistic sweep: pan the r-window half its width.</summary>
        public static double LogisticSweepX(double startX, double zoom, double e)
            => startX + 0.5 * (LogisticSpanAtZoom1 / System.Math.Max(1e-6, zoom)) * e;

        /// <summary>AcidWarp sweep: advance the flow position.</summary>
        public static double AcidWarpSweepFlow(double startFlow, double e)
            => startFlow + AcidWarpSweepLengths * e;

        /// <summary>Orbit azimuth (radians) at eased progress e.</summary>
        public static double OrbitTheta(double theta0, double orbitDegrees, double e)
            => theta0 + orbitDegrees * System.Math.PI / 180.0 * e;
    }

    /// <summary>A Ken-Burns path: zoom in to <see cref="ZoomEnd"/> drifting toward a
    /// point, eased; frame 0 is the full frame.</summary>
    public readonly record struct KenBurnsPath(double ZoomEnd, double EndFracX, double EndFracY)
    {
        /// <summary>Random path in the interactive range (1.08–1.14×, any target).</summary>
        public static KenBurnsPath Random(Random rng)
            => new(1.08 + 0.06 * rng.NextDouble(), rng.NextDouble(), rng.NextDouble());

        /// <summary>Source sub-rectangle (x, y, w, h) of a w×h frame at eased progress e.</summary>
        public (double X, double Y, double W, double H) Rect(double e, int w, int h)
        {
            double z = 1.0 + (ZoomEnd - 1.0) * e;
            double vw = w / z, vh = h / z;
            return ((w - vw) * (0.5 + (EndFracX - 0.5) * e),
                    (h - vh) * (0.5 + (EndFracY - 0.5) * e),
                    vw, vh);
        }
    }
}
