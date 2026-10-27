// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Collections.Generic;
using System.Text.Json;
using Xunit;
using FracturingFog;
using FracturingFog.Imaging;
using FracturingFog.Models;

namespace FracturingFog.Server.Tests;

// #1440 — Wada mirror spheres. The oracles come from the geometry, not from the
// tracer itself:
//   • a ray in the plane through three ball centres stays in that plane (every
//     normal it meets lies in it) and the fourth ball sits off the plane, so the
//     trace must reduce exactly to the 2D three-disk billiard (#627 / #1445);
//   • the cyclic coordinate shift (x, y, z) → (y, z, x) is a symmetry of the
//     even-parity corners, so it must permute balls and openings and keep bounce counts;
//   • the four exit basins share one boundary (the Wada property the 1999
//     experiment demonstrates): close to a boundary point all four openings appear.
public class MirrorSpheresTests
{
    private static MirrorSpheresScene Scene(double scale = 1.0, int maxBounces = 256)
        => MirrorSpheresScene.Build(new FractalParameters { MirrorSpheresRadiusScale = scale, MirrorSpheresMaxBounces = maxBounces });

    private static (double X, double Y, double Z) C(int k)
        => (MirrorSpheresScene.CentreX(k), MirrorSpheresScene.CentreY(k), MirrorSpheresScene.CentreZ(k));

    private static (double, double, double) Sub((double X, double Y, double Z) a, (double X, double Y, double Z) b) => (a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    private static double Dot((double X, double Y, double Z) a, (double X, double Y, double Z) b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    private static (double, double, double) Scale((double X, double Y, double Z) a, double s) => (a.X * s, a.Y * s, a.Z * s);
    private static (double, double, double) Unit((double X, double Y, double Z) a) => Scale(a, 1.0 / Math.Sqrt(Dot(a, a)));

    [Fact]
    public void Default_Balls_Touch_And_Camera_Looks_In_Through_An_Opening()
    {
        var s = Scene();
        for (int i = 0; i < 4; i++)
        for (int j = i + 1; j < 4; j++)
        {
            var d = Sub(C(i), C(j));
            Assert.Equal(2 * s.Radius, Math.Sqrt(Dot(d, d)), 12);   // tangent
        }
        // The default camera sits on the outward normal of the face opposite ball 3.
        var p = new FractalParameters();
        var cam = MirrorSpheresCamera.From(p, 1.0, 0, 0, 0, s.BoundingRadius);
        var dir = Unit((cam.X, cam.Y, cam.Z));
        var n3 = Unit(Scale(C(3), -1));
        Assert.Equal(1.0, Dot(dir, n3), 9);
        // Its centre ray runs along that normal through the chamber centre, meets ball 3
        // head-on and comes straight back out of the viewing opening: you see yourself.
        cam.Ray(50, 50, 101, 101, out double dx, out double dy, out double dz);
        var o = s.Trace(cam.X, cam.Y, cam.Z, dx, dy, dz);
        Assert.True(o.EnteredChamber);
        Assert.Equal(1, o.Bounces);
        Assert.Equal(3, o.Gate);
    }

    [Fact]
    public void InPlane_Rays_Reduce_To_The_ThreeDisk_Billiard()
    {
        var s = Scene();
        // Plane through the centres of balls 1, 2, 3; ball 0 is its normal direction.
        var g = Scale((C(1).X + C(2).X + C(3).X, C(1).Y + C(2).Y + C(3).Y, C(1).Z + C(2).Z + C(3).Z), 1.0 / 3);
        var n = Unit(C(0));
        double planeToBall0 = Dot(Sub(C(0), g), n);
        Assert.True(planeToBall0 > s.Radius, "precondition: the plane must miss ball 0");
        double sep = Math.Sqrt(Dot(Sub(C(1), g), Sub(C(1), g)));

        // 2D frame: ball 1 at 90°, ball 2 at 210°, ball 3 at 330° (the ThreeDisk layout).
        var e2 = Unit(Sub(C(1), g));
        var c2rel = Sub(C(2), g);
        var e1 = Unit(Sub(c2rel, Scale(e2, Dot(c2rel, e2))));
        if (Dot(c2rel, e1) > 0) e1 = Scale(e1, -1);
        double a3 = Math.Atan2(Dot(Sub(C(3), g), e2), Dot(Sub(C(3), g), e1)) * 180 / Math.PI;
        Assert.Equal(-30.0, a3, 6);   // precondition: ball 3 lands at 330°

        var billiard = BilliardScene.Build(new FractalParameters
        {
            BilliardGeometry = BilliardGeometry.ThreeDisk,
            BilliardSeparation = sep,
            BilliardDiskRadius = s.Radius,
            BilliardMaxBounces = s.MaxBounces,
            BilliardGateMode = BilliardGateMode.ExitGap,
        });

        int compared = 0, agree = 0, deep = 0;
        var gates = new HashSet<int>();
        const int n2 = 120;
        for (int i = 0; i < n2; i++)
        for (int j = 0; j < n2; j++)
        {
            double b = -2.2 + 4.4 * (i + 0.5) / n2;
            double phi = 2 * Math.PI * (j + 0.5) / n2;
            var o2 = billiard.LaunchFarField(b, phi);
            if (o2.Bounces == 0) continue;
            double dx2 = Math.Cos(phi), dy2 = Math.Sin(phi);
            double px2 = -dy2 * b - dx2 * billiard.PlayRadius, py2 = dx2 * b - dy2 * billiard.PlayRadius;
            var o3 = s.Trace(
                g.Item1 + px2 * e1.Item1 + py2 * e2.Item1, g.Item2 + px2 * e1.Item2 + py2 * e2.Item2, g.Item3 + px2 * e1.Item3 + py2 * e2.Item3,
                dx2 * e1.Item1 + dy2 * e2.Item1, dx2 * e1.Item2 + dy2 * e2.Item2, dx2 * e1.Item3 + dy2 * e2.Item3,
                stopAtChamberExit: false);   // plain reflection, gated by final direction like the billiard
            compared++;
            if (o2.Bounces >= 4) deep++;
            gates.Add(o2.GateId);
            // 2D gap g (between disks g, g+1) is the opening opposite disk g+2 = ball (g+2)%3 + 1.
            int expect = o2.GateId < 0 ? -1 : (o2.GateId + 2) % 3 + 1;
            if (o3.Gate == expect && o3.Bounces == o2.Bounces) agree++;
        }
        Assert.True(compared > 1000, $"only {compared} scattered rays");
        Assert.Contains(0, gates); Assert.Contains(1, gates); Assert.Contains(2, gates);
        Assert.True(deep > compared / 100, $"only {deep} of {compared} rays bounced 4+ times");
        Assert.True(agree >= compared * 0.99, $"3D matched the billiard for {agree}/{compared}");
    }

    [Fact]
    public void Cyclic_Coordinate_Shift_Permutes_The_Openings()
    {
        var s = Scene();
        // (x, y, z) → (y, z, x) maps corner k to corner perm[k].
        var perm = new int[4];
        for (int k = 0; k < 4; k++)
            for (int m = 0; m < 4; m++)
                if (C(m) == (C(k).Y, C(k).Z, C(k).X)) perm[k] = m;
        var rng = new Random(1440);
        int compared = 0, agree = 0, deep = 0;
        for (int i = 0; i < 20000; i++)
        {
            // Rays from a shell of radius 6 aimed near the centre.
            var o = Unit((rng.NextDouble() * 2 - 1, rng.NextDouble() * 2 - 1, rng.NextDouble() * 2 - 1));
            var origin = Scale(o, 6.0);
            var aim = Scale((rng.NextDouble() - 0.5, rng.NextDouble() - 0.5, rng.NextDouble() - 0.5), 1.5);
            var d = Unit(Sub(aim, origin));
            var a = s.Trace(origin.Item1, origin.Item2, origin.Item3, d.Item1, d.Item2, d.Item3);
            if (a.Bounces == 0) continue;
            var b = s.Trace(origin.Item2, origin.Item3, origin.Item1, d.Item2, d.Item3, d.Item1);
            compared++;
            if (a.Bounces >= 4) deep++;
            int expect = a.Gate < 0 ? -1 : perm[a.Gate];
            if (b.Gate == expect && b.Bounces == a.Bounces) agree++;
        }
        Assert.True(compared > 2000, $"only {compared} rays hit a ball");
        Assert.True(deep > 0, "no multi-bounce rays in the sample");
        Assert.True(agree >= compared * 0.99, $"symmetry held for {agree}/{compared}");
    }

    [Fact]
    public void Boundary_Points_See_All_Four_Openings()
    {
        // Wada check in the default view: bisect to a boundary between two basins,
        // then sample a tiny window around it. Sweet, Ott and Yorke (1999) report that
        // every magnification of a boundary point shows all four basin colours.
        var s = Scene();
        var p = new FractalParameters();
        var cam = MirrorSpheresCamera.From(p, 1.0, 0, 0, 0, s.BoundingRadius);
        const int w = 400, h = 400;
        int Gate(double fx, double fy)
        {
            // Sub-pixel ray: interpolate between pixel centres.
            cam.Ray(0, 0, w, h, out _, out _, out _);
            int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
            cam.Ray(x0, y0, w, h, out double ax, out double ay, out double az);
            cam.Ray(x0 + 1, y0, w, h, out double bx, out double by, out double bz);
            cam.Ray(x0, y0 + 1, w, h, out double cx, out double cy, out double cz);
            double tx = fx - x0, ty = fy - y0;
            var d = Unit((ax + (bx - ax) * tx + (cx - ax) * ty, ay + (by - ay) * tx + (cy - ay) * ty, az + (bz - az) * tx + (cz - az) * ty));
            var r = s.Trace(cam.X, cam.Y, cam.Z, d.Item1, d.Item2, d.Item3);
            return r.EnteredChamber ? r.Gate : -2;   // -2: not in any basin
        }

        int tested = 0, allFour = 0;
        for (int y = 120; y < 300 && tested < 24; y += 7)
        for (int x = 120; x < 300 && tested < 24; x += 5)
        {
            int ga = Gate(x, y), gb = Gate(x + 1, y);
            if (ga < 0 || gb < 0 || ga == gb) continue;
            double lo = x, hi = x + 1;
            for (int it = 0; it < 30; it++)
            {
                double mid = 0.5 * (lo + hi);
                if (Gate(mid, y) == ga) lo = mid; else hi = mid;
            }
            var seen = new HashSet<int>();
            const double half = 1e-4;   // pixels
            for (int i = 0; i < 24; i++)
            for (int j = 0; j < 24; j++)
            {
                int gg = Gate(lo - half + 2 * half * i / 23, y - half + 2 * half * j / 23);
                if (gg >= 0) seen.Add(gg);
            }
            tested++;
            if (seen.Count == 4) allFour++;
        }
        Assert.True(tested >= 12, $"precondition: found only {tested} boundary points");
        Assert.True(allFour >= tested * 3 / 4, $"all four openings near {allFour}/{tested} boundary points");
    }

    [Fact]
    public void Render_Shows_All_Four_Openings_And_Is_Deterministic()
    {
        MirrorSpheresCalculator Render()
        {
            var c = new MirrorSpheresCalculator(96, 96) { FractalParameters = new FractalParameters() };
            c.Calculate(default);
            return c;
        }
        var a = Render();
        var b = Render();
        Assert.Equal(a.ColorBuffer, b.ColorBuffer);
        var full = new HashSet<uint>();
        for (int k = 0; k < 4; k++) full.Add(MirrorSpheresCalculator.OpeningColor(k));
        var seen = new HashSet<uint>();
        foreach (uint c in a.ColorBuffer) if (full.Contains(c)) seen.Add(c);
        // Zero-bounce rays are muted, so an unshaded opening colour means a one-
        // bounce-or-more ray at reflectance 1 only; check with lossless mirrors.
        var lossless = new MirrorSpheresCalculator(96, 96) { FractalParameters = new FractalParameters { MirrorSpheresReflectance = 1.0 } };
        lossless.Calculate(default);
        seen.Clear();
        foreach (uint c in lossless.ColorBuffer) if (full.Contains(c)) seen.Add(c);
        Assert.Equal(4, seen.Count);
    }

    [Fact]
    public void PosterPath_Honours_The_Radius()
    {
        PosterRequest Req(double scale) => new()
        {
            FractalType = FractalType.MirrorSpheres,
            CenterX = 0, CenterY = 0, Zoom = 1.0, MaxIterations = 64,
            Width = 64, Height = 64,
            ColorMap = new BilliardGatesMap(),
            Quality = QualityPreset.Standard,
            FractalParameters = new FractalParameters { MirrorSpheresRadiusScale = scale },
            Path = "unused.png", Format = ImageFileFormat.Png,
        };
        var a = PosterRenderer.RenderToPixels(Req(1.0), default, out _, out _);
        var b = PosterRenderer.RenderToPixels(Req(0.7), default, out _, out _);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Region_Snapshot_Round_Trips()
    {
        var src = new FractalParameters
        {
            MirrorSpheresRadiusScale = 0.8, MirrorSpheresMaxBounces = 300, MirrorSpheresReflectance = 0.6,
            MirrorSpheresMuteMisses = false, MirrorSpheresCameraDistance = 7.5,
            MirrorSpheresCameraTheta = 0.4, MirrorSpheresCameraPhi = 1.2,
        };
        var json = JsonSerializer.Serialize(RegionFractalParams.Snapshot(FractalType.MirrorSpheres, src));
        var dst = new FractalParameters();
        JsonSerializer.Deserialize<RegionFractalParams>(json)!.ApplyTo(dst);
        Assert.Equal(0.8, dst.MirrorSpheresRadiusScale);
        Assert.Equal(300, dst.MirrorSpheresMaxBounces);
        Assert.Equal(0.6, dst.MirrorSpheresReflectance);
        Assert.False(dst.MirrorSpheresMuteMisses);
        Assert.Equal(7.5, dst.MirrorSpheresCameraDistance);
        Assert.Equal(0.4, dst.MirrorSpheresCameraTheta);
        Assert.Equal(1.2, dst.MirrorSpheresCameraPhi);
    }
}
