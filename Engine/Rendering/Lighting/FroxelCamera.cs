// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Rendering/Lighting/FroxelCamera.cs
//
// #1067 (froxel F1, parent #1061) — the camera a froxel volume is framed from,
// decoupled from the Relief 3D oblique camera so the 3D raymarchers (F2) can
// drive the same FroxelVolumePass / GPU kernels.
//
// It carries the full pinhole (or ortho) basis plus the froxel framing:
//   - Pos, F (forward), R (right), U (up), TanHalf (vertical half-FOV tangent),
//     Ortho / OrthoHalfV — enough to reconstruct true frustum positions (F3's
//     shadow rays need them; the populate today uses a screen-aligned box);
//   - Near / Far — the froxel slice range along the view ray;
//   - Extent — the lateral half-size of the populate's box and the reprojection
//     extent (relief: the terrain slab's largest half-extent).
//
// FromRelief reproduces exactly the numbers the relief path used inline (near/far
// from the slab diagonal, extent from the slab, RY = 0), so relief froxel output
// stays byte-identical.

using System;

namespace FracturingFog.Rendering.Lighting;

/// <summary>Camera + framing for a froxel volume (#1067).</summary>
public readonly record struct FroxelCamera(
    double PosX, double PosY, double PosZ,
    double Fx, double Fy, double Fz,
    double Rx, double Ry, double Rz,
    double Ux, double Uy, double Uz,
    double TanHalf, bool Ortho, double OrthoHalfV,
    double Near, double Far, double Extent)
{
    /// <summary>The basis FroxelHistory reprojects with.</summary>
    public FroxelHistory.CamBasis Basis => new(PosX, PosY, PosZ, Rx, Ry, Rz, Ux, Uy, Uz, Fx, Fy, Fz);

    /// <summary>The relief oblique camera as a froxel camera — the framing the
    /// relief froxel pass always used: near/far bracket the height-field slab
    /// ([-Bx,Bx]×[0,By]×[-Bz,Bz]) along the camera distance, extent is the slab's
    /// largest half-size, and the right vector is horizontal (RY = 0).</summary>
    public static FroxelCamera FromRelief(in HeightfieldRaymarch2D.ReliefCamera cam)
    {
        double camDist = Math.Sqrt(cam.CamX * cam.CamX + cam.CamY * cam.CamY + cam.CamZ * cam.CamZ);
        double diag = Math.Sqrt((2 * cam.Bx) * (2 * cam.Bx) + cam.By * cam.By + (2 * cam.Bz) * (2 * cam.Bz));
        double near = Math.Max(1e-3, camDist - diag);
        double far = camDist + diag;
        if (far <= near) far = near * 100.0;
        double extent = Math.Max(cam.Bx, Math.Max(cam.By, cam.Bz));
        return new FroxelCamera(
            cam.CamX, cam.CamY, cam.CamZ,
            cam.FX, cam.FY, cam.FZ,
            cam.RX, 0.0, cam.RZ,
            cam.UX, cam.UY, cam.UZ,
            cam.TanHalf, cam.Ortho, cam.OrthoHalfV,
            near, far, extent);
    }

    /// <summary>A pinhole camera at <paramref name="pos"/> looking at
    /// <paramref name="target"/> (world up = +Y), framed from <paramref name="near"/>
    /// to <paramref name="far"/> with lateral <paramref name="extent"/>. The 3D
    /// orbit cameras build exactly this basis (#1068).</summary>
    public static FroxelCamera LookAt(
        (double X, double Y, double Z) pos, (double X, double Y, double Z) target,
        double verticalFovRad, double near, double far, double extent)
    {
        double fx = target.X - pos.X, fy = target.Y - pos.Y, fz = target.Z - pos.Z;
        double fl = Math.Sqrt(fx * fx + fy * fy + fz * fz);
        if (fl <= 0) { fx = 0; fy = 0; fz = -1; fl = 1; }
        fx /= fl; fy /= fl; fz /= fl;
        // right = forward × worldUp(0,1,0); fall back to +X when looking straight up/down.
        double rx = -fz, ry = 0.0, rz = fx;
        double rl = Math.Sqrt(rx * rx + rz * rz);
        if (rl < 1e-9) { rx = 1; rz = 0; rl = 1; }
        rx /= rl; rz /= rl;
        // up = right × forward
        double ux = ry * fz - rz * fy, uy = rz * fx - rx * fz, uz = rx * fy - ry * fx;
        return new FroxelCamera(pos.X, pos.Y, pos.Z, fx, fy, fz, rx, ry, rz, ux, uy, uz,
            Math.Tan(verticalFovRad / 2), false, 0.0,
            Math.Max(1e-3, near), Math.Max(far, near * 1.001 + 1e-3), extent > 0 ? extent : 1.0);
    }
}
