// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1049 — scene camera keys drive a Relief 3D shot's oblique camera; #1058 —
// picking a shot's region sets its fractal type (and Relief 3D-ness).
//
// The export lock is an independent invariant, not self-consistency: with two
// identical keys every frame must be identical; with keys that orbit the
// azimuth, the frames must change. Before #1049 the camera track was dropped for
// a relief shot (2D FractalType → no camera binding), so the orbit scene would
// have rendered identical frames too.
//
// Runs under the test data-root redirect (FractalRegionLibraryCollection).

using System;
using System.IO;
using System.Linq;
using System.Threading;

using FracturingFog.Abstractions.Animation;
using FracturingFog.Export;
using FracturingFog.Models;
using FracturingFog.Render;
using FracturingFog.UI.Avalonia.ViewModels;
using SkiaSharp;
using Xunit;

using LibraryService = FracturingFog.Server.Tests.SceneEditorPickerRefreshTests.LibraryService;

namespace FracturingFog.Server.Tests;

[Collection(FractalRegionLibraryCollection.Name)]
public sealed class SceneReliefCameraTests
{
    private static FractalParameters ReliefParams() => new()
    {
        Relief2DEnabled = true,
        Relief2DRaymarch = true,
    };

    // ── Binding ───────────────────────────────────────────────────────────

    [Fact]
    public void ReliefBinding_RoundTrips_AndMapsUnits()
    {
        var p = ReliefParams();
        CameraParamBinding.ApplyRelief(p, new CameraState(0.5, Math.PI / 2, Math.PI / 6));

        Assert.Equal(90.0, p.Relief2DCameraAzimuthDeg, 9);
        Assert.Equal(30.0, p.Relief2DCameraElevationDeg, 9);
        Assert.Equal(2.0, p.Relief2DCameraZoom, 9);           // distance 0.5 = zoom 2

        var back = CameraParamBinding.ReadRelief(p);
        Assert.Equal(0.5, back.Distance, 9);
        Assert.Equal(Math.PI / 2, back.Theta, 9);
        Assert.Equal(Math.PI / 6, back.Phi, 9);
    }

    [Theory]
    [InlineData(190.0, -170.0)]
    [InlineData(-190.0, 170.0)]
    [InlineData(540.0, 180.0)]
    [InlineData(180.0, 180.0)]
    public void ReliefBinding_WrapsAzimuth(double inDeg, double outDeg)
    {
        var p = ReliefParams();
        CameraParamBinding.ApplyRelief(p, new CameraState(1, inDeg * Math.PI / 180, Math.PI / 4));
        Assert.Equal(outDeg, p.Relief2DCameraAzimuthDeg, 9);
    }

    [Fact]
    public void ReliefBinding_ClampsToThePanelRanges()
    {
        var p = ReliefParams();
        CameraParamBinding.ApplyRelief(p, new CameraState(100, 0, Math.PI));     // far + straight up
        Assert.Equal(CameraParamBinding.ReliefMaxElevationDeg, p.Relief2DCameraElevationDeg);
        Assert.Equal(CameraParamBinding.ReliefMinZoom, p.Relief2DCameraZoom);

        CameraParamBinding.ApplyRelief(p, new CameraState(0.001, 0, -1));       // near + below ground
        Assert.Equal(CameraParamBinding.ReliefMinElevationDeg, p.Relief2DCameraElevationDeg);
        Assert.Equal(CameraParamBinding.ReliefMaxZoom, p.Relief2DCameraZoom);
    }

    [Fact]
    public void ApplyFor_PicksTheReliefCamera_OnlyWhenTheRaymarchIsOn()
    {
        var relief = ReliefParams();
        Assert.True(CameraParamBinding.Supports(FractalType.Mandelbrot, relief));
        CameraParamBinding.ApplyFor(relief, FractalType.Mandelbrot, new CameraState(1, Math.PI / 4, Math.PI / 4));
        Assert.Equal(45.0, relief.Relief2DCameraAzimuthDeg, 9);

        var flat = new FractalParameters { Relief2DEnabled = true, Relief2DRaymarch = false };
        Assert.False(CameraParamBinding.Supports(FractalType.Mandelbrot, flat));

        var bulb = new FractalParameters();
        double az = bulb.Relief2DCameraAzimuthDeg;
        CameraParamBinding.ApplyFor(bulb, FractalType.Mandelbulb, new CameraState(3, 1, 0.5));
        Assert.Equal(3.0, bulb.BulbCameraDistance);
        Assert.Equal(az, bulb.Relief2DCameraAzimuthDeg);    // relief camera untouched
    }

    [Fact]
    public void Animator_DrivesTheReliefCamera_ForA2DType()
    {
        var p = ReliefParams();
        var track = new CameraTrack { Interpolation = CameraInterpolation.Linear };
        track.Add(new CameraKey(0, new CameraState(1, 0, Math.PI / 4)));
        track.Add(new CameraKey(2, new CameraState(1, Math.PI, Math.PI / 4)));

        var anim = new CameraTrackAnimator(track, p, FractalType.Mandelbrot);
        anim.Tick(1.0);

        Assert.Equal(90.0, p.Relief2DCameraAzimuthDeg, 6);
        Assert.Equal("Camera (Relief 3D)", anim.Name);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CameraTrackAnimator(track, new FractalParameters(), FractalType.Mandelbrot));
    }

    // ── Export end to end ─────────────────────────────────────────────────

    [Fact]
    public void Export_ReliefShot_CameraKeysMoveTheFrames_StaticKeysDont()
    {
        var lib = FractalRegionLibrary.Instance;
        string name = "FF-1049-ReliefCam-" + Guid.NewGuid().ToString("N");
        var region = new FractalRegion
        {
            Name = name, FractalType = FractalType.Mandelbrot,
            CenterX = -0.75, CenterY = 0.0, Zoom = 1.0, Iterations = 200,
            Relief3D = new Relief3DSettings
            {
                Enabled = true, Raymarch = true, HeightScale = 1.0,
                CameraElevationDeg = 45.0, Supersample = 1, HiResField = false,
            },
        };
        Assert.True(lib.AddUserRegion(region));
        try
        {
            var still = RenderFrames(name, endThetaDeg: 0);
            var orbit = RenderFrames(name, endThetaDeg: 90);

            Assert.True(still.Count >= 3);
            Assert.All(still.Skip(1), f => Assert.Equal(still[0], f));   // static camera → identical
            // Every orbit frame moves on from the last (frames sample mid-interval,
            // so even frame 0 is already slightly rotated vs the static pose).
            for (int i = 1; i < orbit.Count; i++) Assert.NotEqual(orbit[i - 1], orbit[i]);
        }
        finally { lib.RemoveUserRegion(name); }
    }

    private static System.Collections.Generic.List<uint[]> RenderFrames(string regionName, double endThetaDeg)
    {
        var track = new CameraTrack { Interpolation = CameraInterpolation.Linear };
        track.Add(new CameraKey(0, new CameraState(1, 0, Math.PI / 4)));
        track.Add(new CameraKey(1, new CameraState(1, endThetaDeg * Math.PI / 180, Math.PI / 4)));
        var scene = new SceneData
        {
            Name = "FF-1049",
            Shots =
            {
                new SceneShot
                {
                    RegionName = regionName, FractalType = FractalType.Mandelbrot,
                    DurationSeconds = 1.0, Transition = SceneTransitionKind.Cut, Camera = track,
                },
            },
        };
        string outDir = Path.Combine(Path.GetTempPath(), "FracturingFog", "1049-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outDir);
        try
        {
            var result = SceneVideoRenderer.Render(scene, new SceneVideoOptions
            {
                Width = 48, Height = 32, OutputPath = outDir, KeepFrames = true, EncodeGif = true,
                Settings = new SceneRenderSettings { Fps = 4, MotionBlurSubframes = 1 },
            }, null, CancellationToken.None);
            Assert.True(result.FramesWritten > 0, result.Message);

            var frames = new System.Collections.Generic.List<uint[]>();
            for (int f = 1; f <= result.FramesWritten; f++)
            {
                using var bmp = SKBitmap.Decode(Path.Combine(result.FrameFolder!, $"frame_{f:000000}.png"));
                frames.Add(bmp.Pixels.Select(c => (uint)c).ToArray());
            }
            try { Directory.Delete(result.FrameFolder!, true); } catch { }
            return frames;
        }
        finally { try { Directory.Delete(outDir, true); } catch { } }
    }

    // ── Editor (#1058 / #1049) ────────────────────────────────────────────

    [Fact]
    public void PickingARegion_SetsItsType_AndLocksTheFractalCombo()
    {
        var (_, row) = Setup(st =>
        {
            st.Regions = new() { "Bulb" };
            st.RegionInfo["Bulb"] = (FractalType.Mandelbulb, false);
        });

        row.SelectedRegion = "Bulb";

        Assert.Equal(FractalType.Mandelbulb, row.FractalType);
        Assert.Equal(FractalType.Mandelbulb, row.ToShot().FractalType);
        Assert.False(row.IsFractalTypeEditable);
        Assert.True(row.Supports3DCamera);

        row.SelectedRegion = SceneShotRowViewModel.RegionNone;
        Assert.True(row.IsFractalTypeEditable);
    }

    [Fact]
    public void ReliefRegion_ShowsTheCameraRow_WithReliefSeedAndHeader()
    {
        var (_, row) = Setup(st =>
        {
            st.Regions = new() { "Terrain" };
            st.RegionInfo["Terrain"] = (FractalType.Mandelbrot, true);
        });

        row.SelectedRegion = "Terrain";
        Assert.True(row.IsRelief3D);
        Assert.True(row.Supports3DCamera);
        Assert.Equal("Camera (Relief 3D)", row.CameraHeader);

        row.AddCameraKeyCommand.Execute().Subscribe();
        var key = row.CameraKeys.Single();
        Assert.Equal(1.0, key.Distance);
        Assert.Equal(Math.PI / 4, key.Phi, 9);
        Assert.NotNull(row.ToShot().Camera);             // emitted for a 2D-type relief shot
    }

    [Fact]
    public void Populate_SyncsTypeFromTheRegion_OverAStaleShotType()
    {
        var (_, row) = Setup(st =>
        {
            st.Regions = new() { "Box" };
            st.RegionInfo["Box"] = (FractalType.Mandelbox, false);
        });

        row.Populate(new SceneShot { RegionName = "Box", FractalType = FractalType.Mandelbrot, DurationSeconds = 5 });

        Assert.Equal(FractalType.Mandelbox, row.FractalType);
    }

    [Fact]
    public void CaptureKey_UsesTheLiveCamera_OrExplainsWhyNot()
    {
        var (vm, row) = Setup(st =>
        {
            st.Regions = new() { "Terrain" };
            st.RegionInfo["Terrain"] = (FractalType.Mandelbrot, true);
        });
        row.SelectedRegion = "Terrain";
        int messages = 0;
        vm.MessageRequested += (_, _) => messages++;

        vm.CaptureLiveCamera = (_, relief) => relief ? new CameraState(0.8, 1.0, 0.6) : null;
        row.CaptureCameraKeyCommand.Execute().Subscribe();
        Assert.Equal((0.8, 1.0, 0.6), (row.CameraKeys[0].Distance, row.CameraKeys[0].Theta, row.CameraKeys[0].Phi));

        vm.CaptureLiveCamera = (_, _) => null;
        row.CaptureCameraKeyCommand.Execute().Subscribe();
        Assert.Single(row.CameraKeys);
        Assert.Equal(1, messages);
    }

    private static (SceneEditorViewModel Vm, SceneShotRowViewModel Row) Setup(
        Action<SceneEditorPickerRefreshTests.LibraryState> arrange)
    {
        var (svc, state) = LibraryService.Create();
        arrange(state);
        var vm = new SceneEditorViewModel(svc);
        return (vm, vm.Shots.Single());
    }
}
