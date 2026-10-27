// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Models/BilliardLayout.cs (#1503)
//
// UI-free layout checks for the chaotic billiard, shared by the params panel
// (which does not reference the Engine) and the tests. Ring / ThreeDisk centres
// sit evenly on a circle of radius sep, so neighbours are 2 sep sin(pi / n)
// apart; once that is no more than 2R the disks touch or overlap and the gaps
// between them, the exits, are closed.

using System;

namespace FracturingFog.Models
{
    public static class BilliardLayout
    {
        /// <summary>Disks on the ring for a geometry (ThreeDisk is always 3; NDisk is not a ring: 0).</summary>
        public static int RingDiskCount(BilliardGeometry geometry, int diskCount) => geometry switch
        {
            BilliardGeometry.ThreeDisk => 3,
            BilliardGeometry.Ring => Math.Clamp(diskCount, 1, 64),
            _ => 0,
        };

        /// <summary>Edge-to-edge gap between neighbouring ring disks (negative = overlap).
        /// +infinity when the ring has fewer than two disks.</summary>
        public static double RingNeighbourGap(int n, double diskRadius, double separation)
            => n < 2 ? double.PositiveInfinity
                     : 2.0 * separation * Math.Sin(Math.PI / n) - 2.0 * diskRadius;

        /// <summary>Warning text for the params panel, or empty when the layout is open.</summary>
        public static string Warning(BilliardGeometry geometry, int diskCount, double diskRadius, double separation)
        {
            int n = RingDiskCount(geometry, diskCount);
            if (n < 2 || RingNeighbourGap(n, diskRadius, separation) > 0.0) return string.Empty;
            return "Neighbouring disks touch or overlap, so the gaps between them are closed. "
                 + "Reduce the radius, increase the separation or use fewer disks.";
        }
    }
}
