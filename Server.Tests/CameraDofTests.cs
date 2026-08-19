// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Roadmap slice S3 (3D-Rendering-Roadmap.md, parent #389) — the thin-lens DOF
// math. Contract: aperture 0 is the pinhole identity (byte-identical default);
// the lens sample lands inside the aperture disc; and every lens ray passes
// through the shared focal point (the defining property of depth of field —
// the focal plane stays sharp while the lens is integrated).

using System;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class CameraDofTests
{
    // Canonical camera: origin, looking down +Z, right +X, up +Y.
    private const double Cx = 0, Cy = 0, Cz = 0;
    private const double Dx = 0, Dy = 0, Dz = 1;
    private const double Rx = 1, Ry = 0, Rz = 0;
    private const double Ux = 0, Uy = 1, Uz = 0;

    // Zero aperture is the pinhole ray, unchanged (the default is DOF-free).
    [Fact]
    public void ZeroAperture_Is_Pinhole_Identity()
    {
        var (ox, oy, oz, dx, dy, dz) = CameraDof.ThinLensRay(
            Cx, Cy, Cz, Dx, Dy, Dz, Rx, Ry, Rz, Ux, Uy, Uz,
            focusDist: 10, apertureRadius: 0, diskX: 0.7, diskY: -0.3);
        Assert.Equal((Cx, Cy, Cz), (ox, oy, oz));
        Assert.Equal((Dx, Dy, Dz), (dx, dy, dz));
    }

    // A non-positive focus distance also degenerates to the pinhole ray.
    [Fact]
    public void NonPositiveFocus_Is_Pinhole_Identity()
    {
        var r = CameraDof.ThinLensRay(Cx, Cy, Cz, Dx, Dy, Dz, Rx, Ry, Rz, Ux, Uy, Uz,
            focusDist: 0, apertureRadius: 0.5, diskX: 0.5, diskY: 0.5);
        Assert.Equal((0.0, 0.0, 1.0), (r.dx, r.dy, r.dz));
    }

    // Every lens ray, extended to its own hit-length, passes through the shared
    // focal point F = cam + dir*focus. This is what keeps the focal plane sharp.
    [Fact]
    public void All_Lens_Rays_Pass_Through_Focal_Point()
    {
        double focus = 12.0, aperture = 0.8;
        double fx = Cx + Dx * focus, fy = Cy + Dy * focus, fz = Cz + Dz * focus;

        (double, double)[] samples = { (0, 0), (1, 0), (-0.6, 0.6), (0.3, -0.9), (0.5, 0.5) };
        foreach (var (u, v) in samples)
        {
            var (ox, oy, oz, dx, dy, dz) = CameraDof.ThinLensRay(
                Cx, Cy, Cz, Dx, Dy, Dz, Rx, Ry, Rz, Ux, Uy, Uz, focus, aperture, u, v);
            // Distance from the jittered origin to F, marched along the new dir,
            // must land on F.
            double len = Math.Sqrt((fx - ox) * (fx - ox) + (fy - oy) * (fy - oy) + (fz - oz) * (fz - oz));
            Assert.Equal(fx, ox + dx * len, 6);
            Assert.Equal(fy, oy + dy * len, 6);
            Assert.Equal(fz, oz + dz * len, 6);
        }
    }

    // The jittered origin sits on the lens disc: displacement ≤ aperture radius,
    // and lies in the camera right/up plane (no forward component).
    [Fact]
    public void Origin_Lies_On_Aperture_Disc()
    {
        double aperture = 0.5;
        var (ox, oy, oz, _, _, _) = CameraDof.ThinLensRay(
            Cx, Cy, Cz, Dx, Dy, Dz, Rx, Ry, Rz, Ux, Uy, Uz, 10, aperture, 1.0, 0.0);
        double disp = Math.Sqrt(ox * ox + oy * oy + oz * oz);
        Assert.True(disp <= aperture + 1e-9, $"origin off disc: {disp} > {aperture}");
        Assert.Equal(0.0, oz, 9);   // no forward (+Z) component in the lens plane
    }

    // The returned direction is unit length.
    [Fact]
    public void Direction_Is_Normalized()
    {
        var (_, _, _, dx, dy, dz) = CameraDof.ThinLensRay(
            Cx, Cy, Cz, Dx, Dy, Dz, Rx, Ry, Rz, Ux, Uy, Uz, 9, 0.4, -0.7, 0.2);
        Assert.Equal(1.0, Math.Sqrt(dx * dx + dy * dy + dz * dz), 9);
    }

    // Concentric disc mapping: centre maps to centre, and every input in the unit
    // square maps inside the unit disc.
    [Fact]
    public void ConcentricDisk_Maps_Into_Unit_Disc()
    {
        var (cx, cy) = CameraDof.ConcentricSampleDisk(0.5, 0.5);
        Assert.Equal(0.0, cx, 9);
        Assert.Equal(0.0, cy, 9);

        for (int i = 0; i <= 10; i++)
        for (int j = 0; j <= 10; j++)
        {
            var (x, y) = CameraDof.ConcentricSampleDisk(i / 10.0, j / 10.0);
            Assert.True(x * x + y * y <= 1.0 + 1e-9, $"({x},{y}) outside unit disc");
        }
    }
}
