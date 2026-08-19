// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Rendering/Lighting/LightSampler.cs
//
// Roadmap slice S8 (3D-Rendering-Roadmap.md, parent #389): richer light types —
// point / spot / area — on top of the three directional lights + IBL FF has
// today. In a DE raymarcher these are a cheap per-sample change: resolve the
// direction-to-light and an attenuation at each shade point instead of using a
// constant direction. Point adds inverse-square + range falloff; Spot adds a
// cone; Directional is the legacy light at infinity (constant direction,
// attenuation 1).
//
// This module is the pure sampling math — deterministic and allocation-free, so
// it is twinnable across the CPU shade pipeline, its relief parity twin and a
// future GPU port. Directional returns the incoming direction unchanged with
// attenuation 1, so a scene of directional lights is byte-for-byte unchanged.

using System;

namespace FracturingFog.Rendering.Lighting;

/// <summary>Pure per-shade-point light sampling (direction + attenuation) for
/// directional / point / spot lights (roadmap S8).</summary>
public static class LightSampler
{
    /// <summary>Resolve the unit direction toward the light and a scalar
    /// attenuation at surface point (<paramref name="sx"/>,<paramref name="sy"/>,
    /// <paramref name="sz"/>).
    /// <para><paramref name="toDirX"/>… is the legacy "toward light" unit
    /// direction (from the light's Theta/Phi) — returned as-is for Directional and
    /// used as the Spot cone axis.</para>
    /// <para><paramref name="innerCos"/>/<paramref name="outerCos"/> are the
    /// cosines of the spot inner/outer half-angles (cos(inner) ≥ cos(outer)).</para>
    /// Directional → (toDir, 1). Point → (dir to light, inverse-square × range
    /// window). Spot → point × smooth cone factor.</summary>
    public static (double lx, double ly, double lz, double atten) Sample(
        LightType type,
        double toDirX, double toDirY, double toDirZ,
        double posX, double posY, double posZ,
        double range, double innerCos, double outerCos,
        double sx, double sy, double sz)
    {
        if (type == LightType.Directional)
            return (toDirX, toDirY, toDirZ, 1.0);

        // Direction from the surface toward the light + distance.
        double dx = posX - sx, dy = posY - sy, dz = posZ - sz;
        double dist2 = dx * dx + dy * dy + dz * dz;
        double dist = Math.Sqrt(dist2);
        double inv = dist > 1e-12 ? 1.0 / dist : 0.0;
        double lx = dx * inv, ly = dy * inv, lz = dz * inv;

        // Inverse-square falloff with a soft range window (Karis / UE4: smoothly
        // reach zero at the range so there is no hard clip). range ≤ 0 = pure 1/d².
        double atten = 1.0 / Math.Max(dist2, 1e-6);
        if (range > 0.0)
        {
            double t = dist / range;
            double t4 = t * t * t * t;
            double win = Saturate(1.0 - t4);
            atten *= win * win;
        }

        if (type == LightType.Spot)
        {
            // Cone axis = the light's shine direction. cos(angle between the
            // surface→light dir and toDir) peaks at 1 on the axis. Smooth the
            // inner→outer band into a penumbra.
            double cosA = lx * toDirX + ly * toDirY + lz * toDirZ;
            atten *= SmoothCone(cosA, innerCos, outerCos);
        }

        return (lx, ly, lz, atten);
    }

    /// <summary>Smooth spot cone factor: 1 at/above <paramref name="innerCos"/>,
    /// 0 at/below <paramref name="outerCos"/>, smoothstep between.</summary>
    public static double SmoothCone(double cosA, double innerCos, double outerCos)
    {
        double denom = innerCos - outerCos;
        if (denom <= 1e-9) return cosA >= innerCos ? 1.0 : 0.0;   // degenerate = hard edge
        double t = Saturate((cosA - outerCos) / denom);
        return t * t * (3.0 - 2.0 * t);
    }

    private static double Saturate(double x) => x < 0.0 ? 0.0 : (x > 1.0 ? 1.0 : x);
}
