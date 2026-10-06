// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Render/CameraParamBinding.cs
//
// Scene Engine Roadmap — Phase S3: bind a CameraState onto the per-type camera
// fields the raymarchers actually read.
//
// Each of the 8 distance-estimation raymarchers owns its own camera triple on
// FractalParameters (MandelboxCamera*, KifsCamera*, QJuliaCamera*, QMandelCamera*,
// KleinianCamera*, BicomplexCamera*, BulbCamera* = Mandelbulb, UserBulbCamera*).
// This is the seam between the type-agnostic CameraTrack and those concrete
// fields. It is data-driven off a single FractalType -> (distance, theta, phi)
// property-name map so the round-trip test can assert every claimed field
// exists on FractalParameters and is a read/write double — i.e. every field a
// CameraKey claims is a field a raymarcher consumes.

using System;
using System.Collections.Generic;
using System.Reflection;

using FracturingFog.Models;

namespace FracturingFog.Render
{
    /// <summary>Maps a <see cref="CameraState"/> onto the per-<see cref="FractalType"/>
    /// camera fields on <see cref="FractalParameters"/>, and back. Only the 8
    /// 3D raymarch types are supported (see <see cref="SupportedTypes"/>);
    /// everything else has no orbit camera.</summary>
    public static class CameraParamBinding
    {
        /// <summary>The authoritative FractalType → camera property-name triple.
        /// The one place the per-type wiring is declared; the round-trip test
        /// validates it against <see cref="FractalParameters"/>.</summary>
        private static readonly IReadOnlyDictionary<FractalType, (string Distance, string Theta, string Phi)> Names =
            new Dictionary<FractalType, (string, string, string)>
            {
                [FractalType.Mandelbulb]           = ("BulbCameraDistance",      "BulbCameraTheta",      "BulbCameraPhi"),
                [FractalType.Mandelbox]            = ("MandelboxCameraDistance", "MandelboxCameraTheta", "MandelboxCameraPhi"),
                [FractalType.Kifs]                 = ("KifsCameraDistance",      "KifsCameraTheta",      "KifsCameraPhi"),
                [FractalType.QuaternionJulia]      = ("QJuliaCameraDistance",    "QJuliaCameraTheta",    "QJuliaCameraPhi"),
                [FractalType.QuaternionMandelbrot] = ("QMandelCameraDistance",   "QMandelCameraTheta",   "QMandelCameraPhi"),
                [FractalType.Kleinian]             = ("KleinianCameraDistance",  "KleinianCameraTheta",  "KleinianCameraPhi"),
                [FractalType.BicomplexMandelbrot]  = ("BicomplexCameraDistance", "BicomplexCameraTheta", "BicomplexCameraPhi"),
                [FractalType.Coquaternion]         = ("CoquaternionCameraDistance", "CoquaternionCameraTheta", "CoquaternionCameraPhi"),
                [FractalType.Julibrot]      = ("DualOrbitVolumeCameraDistance", "DualOrbitVolumeCameraTheta", "DualOrbitVolumeCameraPhi"),
                [FractalType.UserBulb]             = ("UserBulbCameraDistance",  "UserBulbCameraTheta",  "UserBulbCameraPhi"),
            };

        // Cached PropertyInfo resolved once from Names. A wrong / renamed
        // property surfaces as a null entry, which Apply/Read turn into a clear
        // exception and the round-trip test flags — static init never throws.
        private sealed record Accessor(PropertyInfo? Distance, PropertyInfo? Theta, PropertyInfo? Phi);

        private static readonly IReadOnlyDictionary<FractalType, Accessor> Props = BuildAccessors();

        private static Dictionary<FractalType, Accessor> BuildAccessors()
        {
            var t = typeof(FractalParameters);
            var map = new Dictionary<FractalType, Accessor>(Names.Count);
            foreach (var (type, n) in Names)
            {
                map[type] = new Accessor(
                    t.GetProperty(n.Distance, BindingFlags.Public | BindingFlags.Instance),
                    t.GetProperty(n.Theta,    BindingFlags.Public | BindingFlags.Instance),
                    t.GetProperty(n.Phi,      BindingFlags.Public | BindingFlags.Instance));
            }
            return map;
        }

        /// <summary>The fractal types that carry an orbit camera — exactly the
        /// 3D distance-estimation raymarchers.</summary>
        public static IReadOnlyCollection<FractalType> SupportedTypes => (IReadOnlyCollection<FractalType>)Names.Keys;

        /// <summary>True when <paramref name="type"/> has an orbit camera a
        /// <see cref="CameraTrack"/> can drive.</summary>
        public static bool Supports(FractalType type) => Names.ContainsKey(type);

        /// <summary>The (distance, theta, phi) property names for a supported
        /// type. Used by the round-trip test and by consumers building captured
        /// setters.</summary>
        public static (string Distance, string Theta, string Phi) ParamNames(FractalType type)
            => Names.TryGetValue(type, out var n)
                ? n
                : throw NotSupported(type);

        /// <summary>Write <paramref name="state"/> onto the camera fields for
        /// <paramref name="type"/>.</summary>
        public static void Apply(FractalParameters parameters, FractalType type, in CameraState state)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            var a = Resolve(type);
            a.Distance!.SetValue(parameters, state.Distance);
            a.Theta!.SetValue(parameters, state.Theta);
            a.Phi!.SetValue(parameters, state.Phi);
        }

        /// <summary>Read the current camera pose off <paramref name="parameters"/>
        /// for <paramref name="type"/>. Inverse of <see cref="Apply"/>.</summary>
        public static CameraState Read(FractalParameters parameters, FractalType type)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            var a = Resolve(type);
            return new CameraState(
                (double)a.Distance!.GetValue(parameters)!,
                (double)a.Theta!.GetValue(parameters)!,
                (double)a.Phi!.GetValue(parameters)!);
        }

        private static Accessor Resolve(FractalType type)
        {
            if (!Props.TryGetValue(type, out var a))
                throw NotSupported(type);
            if (a.Distance is null || a.Theta is null || a.Phi is null)
                throw new InvalidOperationException(
                    $"CameraParamBinding: a camera property for {type} is missing on FractalParameters " +
                    "(the name map is out of sync). Run the round-trip test.");
            return a;
        }

        // ── #1049 — Relief 3D oblique camera ──────────────────────────────────
        // A Relief 3D region renders a 2D fractal type as an oblique raymarched
        // heightfield, so its camera is not keyed by FractalType: it is whichever
        // params have the relief raymarch on. The orbit triple maps onto the
        // relief camera as θ (radians) ↔ azimuth (degrees, wrapped to ±180),
        // φ (radians) ↔ elevation above the ground (degrees, 5–89, the panel's
        // range) and distance ↔ 1 / frame-fill zoom (zoom 0.2–5), so "further"
        // means the same thing for every camera a scene drives.

        /// <summary>Relief elevation limits (degrees) — the Relief 3D panel's range.</summary>
        public const double ReliefMinElevationDeg = 5.0, ReliefMaxElevationDeg = 89.0;
        /// <summary>Relief frame-fill zoom limits — the Relief 3D panel's range.</summary>
        public const double ReliefMinZoom = 0.2, ReliefMaxZoom = 5.0;

        /// <summary>True when <paramref name="parameters"/> render a Relief 3D
        /// raymarch, whose oblique camera a track can drive.</summary>
        public static bool IsReliefCamera(FractalParameters? parameters)
            => parameters is { Relief2DEnabled: true, Relief2DRaymarch: true };

        /// <summary>True when a camera track can drive these params: a Relief 3D
        /// raymarch, or one of the 3D orbit-camera types.</summary>
        public static bool Supports(FractalType type, FractalParameters? parameters)
            => IsReliefCamera(parameters) || Supports(type);

        /// <summary>Write <paramref name="state"/> onto whichever camera these params
        /// use — the relief camera when the relief raymarch is on, else the
        /// <paramref name="type"/> orbit camera.</summary>
        public static void ApplyFor(FractalParameters parameters, FractalType type, in CameraState state)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            if (IsReliefCamera(parameters)) ApplyRelief(parameters, state);
            else Apply(parameters, type, state);
        }

        /// <summary>Inverse of <see cref="ApplyFor"/>.</summary>
        public static CameraState ReadFor(FractalParameters parameters, FractalType type)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            return IsReliefCamera(parameters) ? ReadRelief(parameters) : Read(parameters, type);
        }

        /// <summary>Write an orbit pose onto the Relief 3D camera (clamped to the
        /// panel's ranges; azimuth wrapped to (−180, 180]).</summary>
        public static void ApplyRelief(FractalParameters parameters, in CameraState state)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            double az = state.Theta * (180.0 / System.Math.PI);
            az -= 360.0 * System.Math.Ceiling((az - 180.0) / 360.0);
            double el = System.Math.Clamp(state.Phi * (180.0 / System.Math.PI), ReliefMinElevationDeg, ReliefMaxElevationDeg);
            double dist = state.Distance > 0 ? state.Distance : 1.0;
            parameters.Relief2DCameraAzimuthDeg = az;
            parameters.Relief2DCameraElevationDeg = el;
            parameters.Relief2DCameraZoom = System.Math.Clamp(1.0 / dist, ReliefMinZoom, ReliefMaxZoom);
        }

        /// <summary>Read the Relief 3D camera as an orbit pose.</summary>
        public static CameraState ReadRelief(FractalParameters parameters)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            double zoom = parameters.Relief2DCameraZoom > 0 ? parameters.Relief2DCameraZoom : 1.0;
            return new CameraState(
                1.0 / zoom,
                parameters.Relief2DCameraAzimuthDeg * (System.Math.PI / 180.0),
                parameters.Relief2DCameraElevationDeg * (System.Math.PI / 180.0));
        }

        private static ArgumentOutOfRangeException NotSupported(FractalType type)
            => new(nameof(type), type, "FractalType has no orbit camera (not a 3D raymarch type).");
    }
}
