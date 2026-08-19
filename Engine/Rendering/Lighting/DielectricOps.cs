// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Rendering/Lighting/DielectricOps.cs
//
// Roadmap slice S5 (3D-Rendering-Roadmap.md, parent #389): refractive /
// transmissive materials — glass fractals. Cook-Torrance GGX is opaque today;
// transmission + IOR let the raymarcher refract the primary ray at the surface
// and keep marching (refract-and-continue), the DE-native way to render glass.
// This module is the pure dielectric math the shade path will call at a
// transmissive hit:
//
//   * Refract  — Snell's law refraction vector + total-internal-reflection flag.
//   * Reflect  — mirror reflection (the TIR / Fresnel-reflection branch).
//   * Fresnel  — Schlick reflectance for the reflect/transmit split.
//   * Beer-Lambert absorption — per-channel transmittance through a tinted medium.
//
// All deterministic + allocation-free, so it is twinnable across the CPU shade
// path, the relief parity twin and the GPU kernels (mirroring the reflection
// path already twinned in ReliefRaymarchGpu.Reflections). Transmission 0 keeps
// the surface opaque, so a non-glass scene is byte-for-byte unchanged.

using System;

namespace FracturingFog.Rendering.Lighting;

/// <summary>Pure dielectric (glass) refraction / Fresnel / absorption math
/// (roadmap S5).</summary>
public static class DielectricOps
{
    /// <summary>Snell's-law refraction. <paramref name="ix"/>… is the normalized
    /// incident direction (the ray, pointing INTO the surface);
    /// <paramref name="nx"/>… is the surface normal pointing AGAINST the ray
    /// (outward). <paramref name="eta"/> is the ratio of indices n_i / n_t
    /// (e.g. entering glass from air = 1 / 1.5). Returns the normalized refracted
    /// direction and <c>tir = false</c>; on total internal reflection returns
    /// <c>tir = true</c> and the reflected direction instead.</summary>
    public static (double x, double y, double z, bool tir) Refract(
        double ix, double iy, double iz,
        double nx, double ny, double nz, double eta)
    {
        double cosI = -(ix * nx + iy * ny + iz * nz);   // ≥ 0 when N faces the ray
        double k = 1.0 - eta * eta * (1.0 - cosI * cosI);
        if (k < 0.0)
        {
            // Total internal reflection — no transmitted ray, reflect instead.
            var (rx, ry, rz) = Reflect(ix, iy, iz, nx, ny, nz);
            return (rx, ry, rz, true);
        }
        double f = eta * cosI - Math.Sqrt(k);
        double x = eta * ix + f * nx;
        double y = eta * iy + f * ny;
        double z = eta * iz + f * nz;
        double il = 1.0 / Math.Sqrt(x * x + y * y + z * z);
        return (x * il, y * il, z * il, false);
    }

    /// <summary>Mirror reflection of incident direction <paramref name="ix"/>…
    /// about the normal <paramref name="nx"/>… (both normalized).</summary>
    public static (double x, double y, double z) Reflect(
        double ix, double iy, double iz, double nx, double ny, double nz)
    {
        double d = 2.0 * (ix * nx + iy * ny + iz * nz);
        return (ix - d * nx, iy - d * ny, iz - d * nz);
    }

    /// <summary>Reflectance at normal incidence for a dielectric interface,
    /// <c>((n1 - n2) / (n1 + n2))²</c> — the Schlick F0.</summary>
    public static double F0(double n1, double n2)
    {
        double r = (n1 - n2) / (n1 + n2);
        return r * r;
    }

    /// <summary>Schlick Fresnel reflectance at incidence cosine
    /// <paramref name="cosTheta"/> (clamped ≥ 0) given <paramref name="f0"/>.
    /// Normal incidence → f0; grazing → 1.</summary>
    public static double FresnelSchlick(double cosTheta, double f0)
    {
        double c = 1.0 - (cosTheta < 0.0 ? 0.0 : (cosTheta > 1.0 ? 1.0 : cosTheta));
        double c2 = c * c;
        return f0 + (1.0 - f0) * (c2 * c2 * c);   // (1-cos)^5
    }

    /// <summary>Beer-Lambert per-channel transmittance through a tinted medium.
    /// <paramref name="tint"/> is 0xAARRGGBB — the color that survives one
    /// <paramref name="refDistance"/> of travel (white = clear). Returns the RGB
    /// multipliers in [0,1] for a path of <paramref name="distance"/>. White tint
    /// or non-positive reference distance → (1,1,1) (no absorption).</summary>
    public static (double r, double g, double b) BeerLambert(
        uint tint, double refDistance, double distance)
    {
        double tr = ((tint >> 16) & 0xFF) / 255.0;
        double tg = ((tint >> 8) & 0xFF) / 255.0;
        double tb = (tint & 0xFF) / 255.0;
        if (refDistance <= 0.0 || (tr >= 1.0 && tg >= 1.0 && tb >= 1.0) || distance <= 0.0)
            return (1.0, 1.0, 1.0);

        // Absorption coefficient a where exp(-a·refDistance) = tint, so one
        // reference distance reproduces the tint exactly, then scale by distance.
        double d = distance / refDistance;
        return (AbsorbChannel(tr, d), AbsorbChannel(tg, d), AbsorbChannel(tb, d));
    }

    private static double AbsorbChannel(double tint, double d)
    {
        if (tint <= 0.0) return 0.0;      // fully absorbs this channel
        if (tint >= 1.0) return 1.0;      // clear channel
        // exp(-a) = tint at d=1  →  transmittance at d = tint^d = exp(d·ln tint).
        return Math.Exp(d * Math.Log(tint));
    }
}
