// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Rendering/Lighting/CameraDof.cs
//
// Roadmap slice S3 (3D-Rendering-Roadmap.md, parent #389): a cinematic camera —
// depth of field, exposure, motion blur. DOF is nearly free in a raymarcher:
// instead of one pinhole ray per pixel, jitter the ray ORIGIN across a lens
// aperture disc and re-aim each ray through the same focal point. Samples on the
// focal plane converge (sharp); everything nearer/farther spreads into a circle
// of confusion (bokeh). Averaging the supersample taps integrates the lens.
//
// This module is the pure thin-lens math — deterministic, allocation-free, and
// twinnable (the seeded disc sample mirrors the HashPair discipline the GGX /
// reflection paths already use), so the CPU render, its parity twin and a future
// GPU port all sample the identical lens. aperture <= 0 is the pinhole identity,
// so the default render is byte-for-byte unchanged.

namespace FracturingFog.Rendering.Lighting;

/// <summary>Pure thin-lens depth-of-field ray math (roadmap S3).</summary>
public static class CameraDof
{
    /// <summary>Shirley's concentric disc mapping: a uniform pair in [0,1)² →
    /// a uniform point in the unit disc, with low distortion (square corners map
    /// to the rim, the centre stays the centre). Used to place the lens sample.</summary>
    public static (double x, double y) ConcentricSampleDisk(double u1, double u2)
    {
        // Map [0,1) → [-1,1).
        double ox = 2.0 * u1 - 1.0;
        double oy = 2.0 * u2 - 1.0;
        if (ox == 0.0 && oy == 0.0) return (0.0, 0.0);

        double r, theta;
        if (System.Math.Abs(ox) > System.Math.Abs(oy))
        {
            r = ox;
            theta = (System.Math.PI / 4.0) * (oy / ox);
        }
        else
        {
            r = oy;
            theta = (System.Math.PI / 2.0) - (System.Math.PI / 4.0) * (ox / oy);
        }
        return (r * System.Math.Cos(theta), r * System.Math.Sin(theta));
    }

    /// <summary>Thin-lens ray. Given the pinhole camera origin, the centre ray
    /// direction (must be normalized), the camera right/up basis, a focus
    /// distance and an aperture radius, plus a unit-disc lens sample
    /// (<paramref name="diskX"/>, <paramref name="diskY"/>), returns the jittered
    /// origin on the lens and the direction re-aimed through the focal point.
    /// <paramref name="apertureRadius"/> ≤ 0 returns the pinhole ray unchanged.</summary>
    public static (double ox, double oy, double oz, double dx, double dy, double dz) ThinLensRay(
        double camX, double camY, double camZ,
        double dirX, double dirY, double dirZ,
        double rX, double rY, double rZ,
        double uX, double uY, double uZ,
        double focusDist, double apertureRadius,
        double diskX, double diskY)
    {
        if (apertureRadius <= 0.0 || focusDist <= 0.0)
            return (camX, camY, camZ, dirX, dirY, dirZ);

        // Focal point along the centre ray — the plane the lens keeps sharp.
        double fx = camX + dirX * focusDist;
        double fy = camY + dirY * focusDist;
        double fz = camZ + dirZ * focusDist;

        // Lens sample offset in the camera plane.
        double lx = diskX * apertureRadius;
        double ly = diskY * apertureRadius;
        double ox = camX + rX * lx + uX * ly;
        double oy = camY + rY * lx + uY * ly;
        double oz = camZ + rZ * lx + uZ * ly;

        // Re-aim through the focal point and renormalize.
        double dx = fx - ox, dy = fy - oy, dz = fz - oz;
        double il = 1.0 / System.Math.Sqrt(dx * dx + dy * dy + dz * dz);
        return (ox, oy, oz, dx * il, dy * il, dz * il);
    }
}
