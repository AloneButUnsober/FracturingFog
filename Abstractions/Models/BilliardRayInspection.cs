// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Models/BilliardRayInspection.cs (#1447)
//
// Data contract for the chaotic-billiard ray-path inset: the real-space scene
// (disks, play circle, gate sectors) and one traced launch as a polyline. The
// Engine traces (BilliardRayInspector); the Avalonia inset only draws, so these
// types live here, UI-free, where both can see them.

using System.Collections.Generic;

namespace FracturingFog.Models
{
    /// <summary>A point in billiard real space (world units, y up).</summary>
    public readonly record struct BilliardPoint(double X, double Y);

    /// <summary>A mirror disk in real space.</summary>
    public readonly record struct BilliardDisk(double X, double Y, double R);

    /// <summary>The billiard scene the inset draws behind the paths.</summary>
    /// <param name="Disks">Mirror disks.</param>
    /// <param name="PlayRadius">Radius of the play circle the rays escape through.</param>
    /// <param name="GateStartAngles">Start angle (radians from +x) of each gate's sector of
    /// exit directions; gate g spans [GateStartAngles[g], GateStartAngles[g] + 2pi/n).</param>
    /// <param name="GateColors">Packed ARGB per gate, as the render colours that gate.</param>
    /// <param name="ExitGap">True when gates are the gaps between ring disks.</param>
    public sealed record BilliardInsetScene(
        IReadOnlyList<BilliardDisk> Disks,
        double PlayRadius,
        IReadOnlyList<double> GateStartAngles,
        IReadOnlyList<uint> GateColors,
        bool ExitGap);

    /// <summary>One traced launch: the plane point it came from, its outcome and its path.</summary>
    /// <param name="PlaneX">Plane x of the launch (the pixel's initial condition).</param>
    /// <param name="PlaneY">Plane y of the launch.</param>
    /// <param name="Valid">False for a phase-space point outside the section (no launch).</param>
    /// <param name="GateId">Exit gate, -1 when trapped at the bounce cap.</param>
    /// <param name="Bounces">Reflections before escape.</param>
    /// <param name="Points">Start, every bounce point, then the end point.</param>
    /// <param name="Color">Packed ARGB for the path (the gate's colour).</param>
    /// <param name="Summary">One line of text: launch values, gate, bounces.</param>
    public sealed record BilliardRayPath(
        double PlaneX,
        double PlaneY,
        bool Valid,
        int GateId,
        int Bounces,
        IReadOnlyList<BilliardPoint> Points,
        uint Color,
        string Summary);
}
