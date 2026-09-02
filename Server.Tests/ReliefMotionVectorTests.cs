// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Roadmap slice S1 (3D-Rendering-Roadmap.md, parent #389 / #398) — the
// motion-vector AOV operator. Contract: Project is the exact inverse of the ray
// generator (a hit reprojects to its own pixel under its own camera); identical
// current/previous cameras give zero motion; translating the camera shifts the
// projection predictably; a point behind the camera reports "behind" / no motion;
// the AOV Motion channel allocates only when requested (default byte-identical).

using FracturingFog.Rendering.Lighting;
using Xunit;
using static FracturingFog.Rendering.Lighting.HeightfieldRaymarch2D;

namespace FracturingFog.Server.Tests;

public sealed class ReliefMotionVectorTests
{
    // Axis-aligned camera at (0,0,-dist) looking +Z, unit basis, fov 1, square.
    private static ReliefMotionVector.CameraView Cam(double px, double py, double pz,
        double fovScale = 1.0, double aspect = 1.0)
        => new(px, py, pz,
               1, 0, 0,   // right +X
               0, 1, 0,   // up    +Y
               0, 0, 1,   // fwd   +Z
               fovScale, aspect, 0.0, 0.0);

    // Reconstruct the world point on pixel (x,y)'s ray at forward-distance t,
    // using the SAME ray-gen the calculators use.
    private static (double wx, double wy, double wz) RayPoint(
        in ReliefMotionVector.CameraView cam, int w, int h, int x, int y, double t)
    {
        double u = (2.0 * (x + 0.5) / w - 1.0) * cam.FovScale * cam.Aspect + cam.PanU;
        double v = (1.0 - 2.0 * (y + 0.5) / h) * cam.FovScale + cam.PanV;
        double dxv = cam.RX * u + cam.UX * v + cam.FX;
        double dyv = cam.RY * u + cam.UY * v + cam.FY;
        double dzv = cam.RZ * u + cam.UZ * v + cam.FZ;
        return (cam.PosX + dxv * t, cam.PosY + dyv * t, cam.PosZ + dzv * t);
    }

    [Fact]
    public void Project_Is_Inverse_Of_RayGen()
    {
        int w = 320, h = 200;
        var cam = Cam(0, 0, -5);
        foreach (var (x, y) in new[] { (0, 0), (160, 100), (319, 199), (40, 170) })
        {
            var (wx, wy, wz) = RayPoint(in cam, w, h, x, y, t: 3.7);
            var (px, py) = ReliefMotionVector.Project(wx, wy, wz, in cam, w, h, out bool behind);
            Assert.False(behind);
            Assert.Equal(x + 0.5, px, 6);   // pixel-centre round-trip
            Assert.Equal(y + 0.5, py, 6);
        }
    }

    [Fact]
    public void Identical_Cameras_Give_Zero_Motion()
    {
        int w = 256, h = 256;
        var cam = Cam(0, 0, -4);
        var (wx, wy, wz) = RayPoint(in cam, w, h, 100, 130, t: 2.5);
        var (du, dv) = ReliefMotionVector.ScreenMotion(wx, wy, wz, 100.5, 130.5, in cam, w, h);
        Assert.Equal(0.0, du, 6);
        Assert.Equal(0.0, dv, 6);
    }

    [Fact]
    public void Camera_Pan_Right_Moves_Projection_Left()
    {
        // A surface point fixed in the world: when the previous camera sat to the
        // LEFT of the current one (smaller X), the point projected further RIGHT
        // last frame, so the reprojection motion is +du.
        int w = 200, h = 200;
        var cur = Cam(0, 0, -6);
        var (wx, wy, wz) = RayPoint(in cur, w, h, 100, 100, t: 6.0);
        var (curPx, curPy) = ReliefMotionVector.Project(wx, wy, wz, in cur, w, h, out _);

        var prev = Cam(-0.5, 0, -6);   // camera was 0.5 to the left
        var (du, dv) = ReliefMotionVector.ScreenMotion(wx, wy, wz, curPx, curPy, in prev, w, h);
        Assert.True(du > 1.0, $"expected a rightward previous projection (du>0), got du={du}");
        Assert.Equal(0.0, dv, 3);   // pure horizontal camera move → no vertical motion
    }

    [Fact]
    public void Point_Behind_Camera_Reports_Behind_And_No_Motion()
    {
        int w = 64, h = 64;
        var cam = Cam(0, 0, -3);
        // A point behind the camera (negative Z, further back than the camera).
        ReliefMotionVector.Project(0, 0, -10, in cam, w, h, out bool behind);
        Assert.True(behind);
        var (du, dv) = ReliefMotionVector.ScreenMotion(0, 0, -10, 32, 32, in cam, w, h);
        Assert.Equal(0.0, du, 6);
        Assert.Equal(0.0, dv, 6);
    }

    [Fact]
    public void Motion_Channel_Allocates_Only_When_Requested()
    {
        Assert.Null(new ReliefAovBuffers(8, 8).Motion);                 // default off
        Assert.Null(new ReliefAovBuffers(8, 8, captureComponents: true).Motion);
        var withMotion = new ReliefAovBuffers(8, 8, false, true);
        Assert.NotNull(withMotion.Motion);
        Assert.Equal(8 * 8 * 2, withMotion.Motion!.Length);            // interleaved du,dv
    }
}
