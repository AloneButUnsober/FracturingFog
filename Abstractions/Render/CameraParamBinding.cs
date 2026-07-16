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

        private static ArgumentOutOfRangeException NotSupported(FractalType type)
            => new(nameof(type), type, "FractalType has no orbit camera (not a 3D raymarch type).");
    }
}
