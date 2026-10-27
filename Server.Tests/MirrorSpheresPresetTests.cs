// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Linq;
using System.Text.Json;
using Xunit;
using FracturingFog;
using FracturingFog.Models;

namespace FracturingFog.Server.Tests;

// #1444 — the Sweet, Ott & Yorke 1999 photograph as a built-in region. Oracles from the
// setup the paper describes, not from the renderer:
//   • VertexUp is a rotation (orthonormal, right-handed) that puts ball 0 straight up;
//   • the preset camera sits on the outward normal of an upper opening (a face that
//     holds the top ball) and keeps the top ball up in the frame;
//   • you see yourself in the middle (the viewing opening is dark), the room around the
//     stack is dark, and the three covered openings (white, red, blue) all show up.
public class MirrorSpheresPresetTests
{
    private const string PresetName = "Wada Mirror Spheres - 1999 Photograph";

    private static double Dot((double X, double Y, double Z) a, (double X, double Y, double Z) b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    private static (double, double, double) Cross((double X, double Y, double Z) a, (double X, double Y, double Z) b)
        => (a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    private static (double X, double Y, double Z) Unit((double X, double Y, double Z) a)
    {
        double l = Math.Sqrt(Dot(a, a));
        return (a.X / l, a.Y / l, a.Z / l);
    }

    private static FractalParameters PresetParams()
    {
        var region = FractalRegionLibrary.Instance.BuiltIns.Single(r => r.Name == PresetName);
        Assert.Equal(FractalType.MirrorSpheres, region.FractalType);
        var p = new FractalParameters();
        region.Params!.ApplyTo(p);
        return p;
    }

    [Fact]
    public void VertexUp_Is_A_Rotation_That_Puts_Ball_Zero_Up()
    {
        var x = MirrorSpheresCamera.VertexUpX;
        var y = MirrorSpheresCamera.VertexUpY;
        var z = MirrorSpheresCamera.VertexUpZ;
        foreach (var v in new[] { x, y, z }) Assert.Equal(1.0, Dot(v, v), 12);
        Assert.Equal(0.0, Dot(x, y), 12);
        Assert.Equal(0.0, Dot(y, z), 12);
        Assert.Equal(0.0, Dot(x, z), 12);
        var c = Cross(x, y);
        Assert.Equal(z.X, c.Item1, 12); Assert.Equal(z.Y, c.Item2, 12); Assert.Equal(z.Z, c.Item3, 12);   // right-handed
        var top = Unit((MirrorSpheresScene.CentreX(0), MirrorSpheresScene.CentreY(0), MirrorSpheresScene.CentreZ(0)));
        Assert.Equal(1.0, Dot(y, top), 12);
        // The three other balls sit level below it (the stack rests on the table).
        for (int k = 1; k < 4; k++)
            Assert.Equal(-1.0 / Math.Sqrt(3.0), Dot(y, (MirrorSpheresScene.CentreX(k), MirrorSpheresScene.CentreY(k), MirrorSpheresScene.CentreZ(k))), 12);
    }

    [Fact]
    public void Preset_Camera_Looks_In_Through_An_Upper_Opening_With_The_Top_Ball_Up()
    {
        var p = PresetParams();
        Assert.Equal(MirrorSpheresMode.Photo, p.MirrorSpheresMode);
        Assert.Equal(MirrorSpheresOrientation.VertexUp, p.MirrorSpheresOrientation);
        var scene = MirrorSpheresScene.Build(p);
        var cam = MirrorSpheresCamera.From(p, 1.0, 0, 0, 0, scene.BoundingRadius);
        var dir = Unit((cam.X, cam.Y, cam.Z));
        var n3 = Unit((-MirrorSpheresScene.CentreX(3), -MirrorSpheresScene.CentreY(3), -MirrorSpheresScene.CentreZ(3)));
        Assert.Equal(1.0, Dot(dir, n3), 9);   // on the face-3 normal
        // Face 3 holds balls 0, 1, 2: an upper opening (it includes the top ball).
        var top = Unit((1, 1, 1));
        Assert.True(Dot(n3, top) > 0, "the viewing opening must face upward");
        // Roll: the top ball is up in the frame (screen up has a positive top-ball part,
        // screen right none).
        var gp = cam.ToGpuParams(64, 64);
        Assert.Equal(0.0, Dot((gp.RightX, gp.RightY, gp.RightZ), top), 9);
        Assert.True(Dot((gp.UpX, gp.UpY, gp.UpZ), top) > 0);
    }

    [Fact]
    public void Preset_Render_Shows_You_The_Dark_Room_And_All_Three_Boards()
    {
        const int w = 120, h = 160;
        var c = new MirrorSpheresCalculator(w, h) { FractalParameters = PresetParams(), Zoom = 2.2 };
        c.Calculate(default);
        uint px(int x, int y) => c.ColorBuffer[y * w + x];
        Assert.Equal(0xFF000000u, px(w / 2, h / 2));   // the centre ray comes back out: the dark viewing opening
        Assert.Equal(0xFF000000u, px(0, 0));           // the dark room in the corner

        // Every board shows (a pixel whose colour is the board's colour scaled by the kept
        // light: the same hue ratios, within rounding).
        var paints = MirrorSpheresPalette.Parse(PresetParams().MirrorSpheresOpeningColors);
        for (int k = 0; k < 3; k++)
        {
            uint target = paints[k].Color;
            bool found = c.ColorBuffer.Any(col => SameHue(col, target));
            Assert.True(found, $"board {k} ({target:X8}) never appears");
        }
    }

    private static bool SameHue(uint col, uint paint)
    {
        int r = (int)((col >> 16) & 0xFF), g = (int)((col >> 8) & 0xFF), b = (int)(col & 0xFF);
        int pr = (int)((paint >> 16) & 0xFF), pg = (int)((paint >> 8) & 0xFF), pb = (int)(paint & 0xFF);
        if (r + g + b < 120) return false;   // too dark to tell
        double k = Math.Max(r, Math.Max(g, b)) / (double)Math.Max(pr, Math.Max(pg, pb));
        return Math.Abs(r - pr * k) <= 3 && Math.Abs(g - pg * k) <= 3 && Math.Abs(b - pb * k) <= 3;
    }

    [Fact]
    public void Plain_Room_Colours_Rays_That_Never_Enter()
    {
        const uint room = 0xFF5A3C78u;
        var p = new FractalParameters
        {
            MirrorSpheresMode = MirrorSpheresMode.Photo, MirrorSpheresRoomColor = "#5A3C78",
            MirrorSpheresCameraDistance = 20.0,   // far back: the corner ray clears every ball
        };
        var c = new MirrorSpheresCalculator(64, 64) { FractalParameters = p };
        c.Calculate(default);
        Assert.Equal(room, c.ColorBuffer[0]);   // the corner ray misses every ball: the room, unscaled
        // "env" (the default) keeps the S2 behaviour.
        Assert.Equal("env", new FractalParameters().MirrorSpheresRoomColor);
        var paint = MirrorSpheresPalette.ParseOne("nonsense", new MirrorSpheresPalette.Paint(0, true));
        Assert.True(paint.Environment);   // a malformed room falls back to env
    }

    [Fact]
    public void CubeCorners_Default_Camera_Is_Unchanged()
    {
        // The default orientation builds the camera exactly as before #1444: spherical
        // position about the origin in world coordinates, world +y up.
        var p = new FractalParameters();
        var scene = MirrorSpheresScene.Build(p);
        var cam = MirrorSpheresCamera.From(p, 1.0, 0, 0, 0, scene.BoundingRadius);
        double d = p.MirrorSpheresCameraDistance, th = p.MirrorSpheresCameraTheta, ph = p.MirrorSpheresCameraPhi;
        Assert.Equal(d * Math.Sin(ph) * Math.Cos(th), cam.X, 12);
        Assert.Equal(d * Math.Cos(ph), cam.Y, 12);
        Assert.Equal(d * Math.Sin(ph) * Math.Sin(th), cam.Z, 12);
        Assert.Equal(0.0, cam.ToGpuParams(8, 8).RightY, 12);   // world-up roll
    }

    [Fact]
    public void Orientation_And_Room_Round_Trip_Through_The_Region_Snapshot()
    {
        var src = new FractalParameters
        {
            MirrorSpheresOrientation = MirrorSpheresOrientation.VertexUp, MirrorSpheresRoomColor = "#102030",
        };
        var json = JsonSerializer.Serialize(RegionFractalParams.Snapshot(FractalType.MirrorSpheres, src));
        var dst = new FractalParameters();
        JsonSerializer.Deserialize<RegionFractalParams>(json)!.ApplyTo(dst);
        Assert.Equal(MirrorSpheresOrientation.VertexUp, dst.MirrorSpheresOrientation);
        Assert.Equal("#102030", dst.MirrorSpheresRoomColor);
    }
}
