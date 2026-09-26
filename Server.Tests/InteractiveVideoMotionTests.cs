// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #954 — interactive video motion types: the slideshow preset carries the
// motion override + orbit through save / clone, the camera-only snapshot drives
// just the 3D camera, and the orbit animator sweeps the azimuth on the shared
// smootherstep curve without touching other params.

using System;
using System.Text.Json;

using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class InteractiveVideoMotionTests
{
    [Fact]
    public void Slideshow_preset_round_trips_motion_and_orbit()
    {
        var cfg = new SlideshowConfig { VideoMotion = VideoMotionMode.KenBurns, VideoOrbitDegrees = 135 };
        var clone = cfg.Clone();
        Assert.Equal(VideoMotionMode.KenBurns, clone.VideoMotion);
        Assert.Equal(135, clone.VideoOrbitDegrees);

        var back = JsonSerializer.Deserialize<SlideshowConfig>(JsonSerializer.Serialize(cfg))!;
        Assert.Equal(VideoMotionMode.KenBurns, back.VideoMotion);
        Assert.Equal(135, back.VideoOrbitDegrees);

        // Legacy JSON without the fields → Auto / no orbit.
        var legacy = JsonSerializer.Deserialize<SlideshowConfig>("{\"Name\":\"old\"}")!;
        Assert.Equal(VideoMotionMode.Auto, legacy.VideoMotion);
        Assert.Equal(0, legacy.VideoOrbitDegrees);
    }

    [Fact]
    public void Camera_snapshot_holds_only_the_camera()
    {
        var p = new FractalParameters { BulbPower = 11, BulbCameraTheta = 0.3 };
        var cam = RegionFractalParams.CameraSnapshot(FractalType.Mandelbulb, p)!;
        Assert.Equal(0.3, cam.Cam3DTheta);
        Assert.Equal((int)FractalType.Mandelbulb, cam.Cam3DFamily);

        // Re-applying it must not reset a param something else is animating.
        var live = new FractalParameters { BulbPower = 5 };
        cam.ApplyTo(live);
        Assert.Equal(0.3, live.BulbCameraTheta);
        Assert.Equal(5, live.BulbPower);

        Assert.Null(RegionFractalParams.CameraSnapshot(FractalType.Julia, new FractalParameters()));
    }

    [Fact]
    public void Orbit_animator_sweeps_azimuth_on_smootherstep()
    {
        var p = new FractalParameters { BulbCameraTheta = 1.0, BulbPower = 7 };
        var orbit = OrbitLegAnimator.TryBuild(FractalType.Mandelbulb, p, degrees: 90, legSeconds: 4)!;
        Assert.Equal(1.0, orbit.ThetaAt(0.0), 12);
        Assert.Equal(1.0 + Math.PI / 4, orbit.ThetaAt(0.5), 12);   // smootherstep(0.5) = 0.5

        orbit.Tick(2.0);
        Assert.Equal(1.0 + Math.PI / 4, p.BulbCameraTheta, 12);
        orbit.Tick(10.0);                                            // clamps at the leg end
        Assert.Equal(1.0 + Math.PI / 2, p.BulbCameraTheta, 12);
        Assert.Equal(7, p.BulbPower);
    }

    [Theory]
    [InlineData(FractalType.Julia, 90.0)]         // 2D — nothing to orbit
    [InlineData(FractalType.Mandelbulb, 0.0)]     // zero degrees
    [InlineData(FractalType.UserBulb, 90.0)]      // no generic camera (own fields)
    public void Orbit_is_null_when_there_is_nothing_to_orbit(FractalType t, double degrees)
        => Assert.Null(OrbitLegAnimator.TryBuild(t, new FractalParameters(), degrees, 4));
}
