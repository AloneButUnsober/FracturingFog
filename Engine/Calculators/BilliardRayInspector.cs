// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// BilliardRayInspector.cs (#1447)
//
// The chaotic-billiard image is a map of launch conditions: a pixel is one whole
// ray, placed by how it started and coloured by how it ended, so the trajectories
// themselves never show. The ray-path inset traces the launch under the pointer
// (and pinned launches) in real space so the sensitive dependence across a basin
// boundary can be seen: two neighbouring pixels, two very different paths.
//
// Everything here goes through the render's own code: ChaoticBilliardCalculator
// .PlaneAt maps the pointer to the plane point, BilliardScene.Launch traces it
// (with the path recorded), ChaoticBilliardCalculator.GateColor colours it. The
// inset is a live inspection aid and is never part of a render or an export.

using System;
using System.Collections.Generic;
using System.Globalization;

using FracturingFog.Interefaces;
using FracturingFog.Models;

namespace FracturingFog;

public static class BilliardRayInspector
{
    /// <summary>Colour drawn for a trapped path or a gate colour too dark to see on
    /// the inset's dark background.</summary>
    public const uint DarkFallbackColor = 0xFFBBBBBBu;

    /// <summary>The real-space scene for <paramref name="p"/>: disks, play circle, gates.</summary>
    public static BilliardInsetScene Scene(FractalParameters p, IColorMap? map)
    {
        var scene = BilliardScene.Build(p);
        var disks = new BilliardDisk[scene.DiskCount];
        for (int i = 0; i < disks.Length; i++)
            disks[i] = new BilliardDisk(scene.DiskX(i), scene.DiskY(i), scene.DiskR(i));
        var starts = new double[scene.GateCount];
        var widths = new double[scene.GateCount];
        var colors = new uint[scene.GateCount];
        for (int g = 0; g < starts.Length; g++)
        {
            starts[g] = scene.GateStartAngle(g);
            widths[g] = scene.GateWidth(g);
            colors[g] = Legible(ChaoticBilliardCalculator.GateColor(map, g, scene.GateCount, scene.MaxBounces));
        }
        bool custom = p.BilliardGeometry == BilliardGeometry.Custom
                      && BilliardLayout.ParseCustomDisks(p.BilliardCustomDisks).Count > 0;
        return new BilliardInsetScene(disks, scene.PlayRadius, starts, colors, scene.ExitGap, widths, custom);
    }

    /// <summary>Trace the launch at plane point (<paramref name="planeX"/>, <paramref name="planeY"/>)
    /// with its path recorded.</summary>
    public static BilliardRayPath Trace(FractalParameters p, IColorMap? map, double planeX, double planeY)
    {
        var scene = BilliardScene.Build(p);
        var points = new List<BilliardPoint>(16);
        var o = scene.Launch(planeX, planeY, points);
        uint color = o.GateId < 0
            ? DarkFallbackColor
            : Legible(ChaoticBilliardCalculator.GateColor(map, o.GateId, scene.GateCount, scene.MaxBounces));
        return new BilliardRayPath(planeX, planeY, o.Valid, o.GateId, o.Bounces, points, color,
                                   Summary(scene, planeX, planeY, o));
    }

    /// <summary>Trace the launch under a pointer at (<paramref name="x"/>, <paramref name="y"/>)
    /// of a <paramref name="width"/> x <paramref name="height"/> client showing the frame
    /// centred on (<paramref name="centerX"/>, <paramref name="centerY"/>) at <paramref name="zoom"/>.</summary>
    public static BilliardRayPath TraceAt(FractalParameters p, IColorMap? map, double x, double y,
                                          double width, double height,
                                          double centerX, double centerY, double zoom)
    {
        var (px, py) = ChaoticBilliardCalculator.PlaneAt(x, y, width, height, centerX, centerY, zoom);
        return Trace(p, map, px, py);
    }

    private static string Summary(BilliardScene scene, double planeX, double planeY, in BilliardOutcome o)
    {
        var ci = CultureInfo.InvariantCulture;
        string launch = scene.LaunchMode == BilliardLaunchMode.PhaseSpace
            ? string.Format(ci, "s {0:0.0000}, sin a {1:0.0000}", planeX * (Math.PI / 2.0), planeY)
            : string.Format(ci, "b {0:0.0000}, phi {1:0.0000}", planeX, planeY);
        if (!o.Valid) return launch + " - outside the section (no launch)";
        string end = o.GateId < 0
            ? "trapped"
            : scene.ExitGap
                ? string.Format(ci, "gap {0}-{1}", o.GateId, (o.GateId + 1) % scene.DiskCount)
                : string.Format(ci, "gate {0}", o.GateId);
        return string.Format(ci, "{0} - {1}, {2} bounce{3}", launch, end, o.Bounces, o.Bounces == 1 ? "" : "s");
    }

    // A colour too dark to read on the inset background is drawn light grey.
    private static uint Legible(uint argb)
    {
        uint r = (argb >> 16) & 0xFF, g = (argb >> 8) & 0xFF, b = argb & 0xFF;
        return r * 299 + g * 587 + b * 114 < 60_000 ? DarkFallbackColor : argb | 0xFF000000u;
    }
}
