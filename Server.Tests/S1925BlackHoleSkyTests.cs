// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using FracturingFog;
using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.Imaging;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using FracturingFog.Scattering;
using FracturingFog.UI.Avalonia.ViewModels;
using Xunit;

namespace FracturingFog.Server.Tests;

// #1925 (epic #1922) — black hole shadows S3: the sky behind the holes and the pinhole
// projection. Independent checks:
//   • with every mass zero, the pinhole image of a sky is the straight pinhole projection of
//     that sky, computed here from the camera geometry alone (no tracing);
//   • with every mass zero, the parallel projection sees one sky direction (a flat image);
//   • the pinhole shadow of one hole sits where the launch ray's L/E = 4m (extremal RN),
//     L/E = D sin(angle) U(D)² for a ray leaving the camera at that angle;
//   • the procedural skies are deterministic by seed and periodic in rotation;
//   • a missing HDRI file is reported as a batch fidelity gap.
public class S1925BlackHoleSkyTests
{
    private const int W = 48, H = 32;

    private static BlackHoleShadowCalculator Render(FractalParameters p, double zoom = BlackHoleShadowCalculator.DefaultZoom)
    {
        var c = new BlackHoleShadowCalculator(W, H) { FractalParameters = p, ColorMap = new HsvPalette(), Zoom = zoom };
        c.Calculate(TestContext.Current.CancellationToken);
        return c;
    }

    private static FractalParameters Massless(BlackHoleSky sky, BlackHoleProjection proj) => new()
    {
        BlackHoleMass1 = 0.0, BlackHoleMass2 = 0.0, BlackHoleSky = sky, BlackHoleProjection = proj,
        BlackHoleSkyDivisions = 6, BlackHoleCameraDistance = 50.0,
    };

    [Theory]
    [InlineData(BlackHoleSky.Checker)]
    [InlineData(BlackHoleSky.Grid)]
    [InlineData(BlackHoleSky.Stars)]
    [InlineData(BlackHoleSky.Environment)]
    public void Massless_Pinhole_Image_Is_The_Straight_Projection_Of_The_Sky(BlackHoleSky kind)
    {
        var p = Massless(kind, BlackHoleProjection.Pinhole);
        var c = Render(p);
        var scene = BlackHoleShadowCalculator.Scene.Build(p);
        var sky = scene.Sky;
        // Side-on default camera (θ 90°, φ 0): at (D, 0, 0) looking down -x, image u along +z,
        // v along +y. A pinhole through (u, v) at the origin looks along (-D, v, u).
        const double D = 50.0;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                var (u, v) = BlackHoleShadowCalculator.PlaneAt(x + 0.5, y + 0.5, W, H, 0, 0, BlackHoleShadowCalculator.DefaultZoom);
                Assert.Equal(scene.EscapedGate, c.FateBuffer[y * W + x]);   // escaped: nothing to fall into
                uint want = BlackHoleSkies.Sample(sky, -D, v, u) | 0xFF000000u;
                Assert.Equal(want, c.ColorBuffer[y * W + x]);
            }
    }

    [Fact]
    public void Massless_Parallel_Projection_Sees_One_Direction()
    {
        var c = Render(Massless(BlackHoleSky.Checker, BlackHoleProjection.Parallel));
        Assert.Single(c.ColorBuffer.Distinct());
    }

    [Fact]
    public void Massless_Pinhole_Rays_Are_Not_Deflected()
    {
        var p = Massless(BlackHoleSky.Off, BlackHoleProjection.Pinhole);
        p.BlackHoleChannel = BlackHoleChannel.Deflection;
        var c = Render(p);
        Assert.All(c.SmoothBuffer, v => Assert.InRange(v, 0f, 1e-6f));
    }

    [Fact]
    public void Pinhole_Shadow_Edge_Is_At_L_Over_E_Of_4m()
    {
        const double D = 3000.0;
        var p = new FractalParameters
        {
            BlackHoleLayout = BlackHoleLayout.Single, BlackHoleMass1 = 1.0, BlackHoleProjection = BlackHoleProjection.Pinhole,
            BlackHoleCameraDistance = D, BlackHoleTolerance = 1e-11,
        };
        var scene = BlackHoleShadowCalculator.Scene.Build(p);
        bool Captured(double u) => scene.Trace(u, 0.0).Outcome.Fate == ScatterFate.Captured;
        double lo = 3.8, hi = 4.2;
        Assert.True(Captured(lo) && !Captured(hi));
        for (int i = 0; i < 30; i++) { double m = 0.5 * (lo + hi); if (Captured(m)) lo = m; else hi = m; }
        double edge = 0.5 * (lo + hi);
        // The ray through (u, 0) leaves the camera at angle a, tan a = u / D; its L/E is
        // D sin a U(D)² (the launch momentum is U(D)² along the ray). Extremal RN: L/E = 4.
        double uD = 1.0 + 1.0 / D;
        double sinA = 4.0 / (D * uD * uD);
        double want = D * Math.Tan(Math.Asin(sinA));
        Assert.InRange(edge, want - 2e-4, want + 2e-4);
    }

    [Fact]
    public void A_Lensed_Sky_Differs_From_The_Unlensed_One_Near_The_Shadows()
    {
        var lensed = new FractalParameters { BlackHoleSky = BlackHoleSky.Checker, BlackHoleProjection = BlackHoleProjection.Pinhole, BlackHoleSkyDivisions = 24 };
        var flat = Massless(BlackHoleSky.Checker, BlackHoleProjection.Pinhole);
        flat.BlackHoleSkyDivisions = 24;
        var a = Render(lensed);
        var b = Render(flat);
        int escaped = 0, changed = 0;
        for (int i = 0; i < W * H; i++)
        {
            if (a.FateBuffer[i] != 2) continue;
            escaped++;
            if (a.ColorBuffer[i] != b.ColorBuffer[i]) changed++;
        }
        Assert.True(escaped > 0 && changed > escaped / 20, $"escaped {escaped}, changed {changed}");
    }

    [Fact]
    public void Procedural_Skies_Are_Seeded_And_Periodic_In_Rotation()
    {
        var p = new FractalParameters { BlackHoleSky = BlackHoleSky.Stars, BlackHoleProjection = BlackHoleProjection.Pinhole, BlackHoleSkySeed = 7, BlackHoleSkyDivisions = 2 };
        var a = Render(p);
        Assert.Equal(a.ColorBuffer, Render(p).ColorBuffer);
        var q = p.Clone(); q.BlackHoleSkySeed = 8;
        Assert.NotEqual(a.ColorBuffer, Render(q).ColorBuffer);
        var r = p.Clone(); r.BlackHoleSkyRotation = 360.0;
        var rr = Render(r);
        int diff = Enumerable.Range(0, W * H).Count(i => a.ColorBuffer[i] != rr.ColorBuffer[i]);
        Assert.True(diff <= 2, $"rotation by a full turn changed {diff} pixels");   // cell-edge rounding only

        var sky = new BlackHoleSkies.Settings { Kind = BlackHoleSky.Checker, Divisions = 4 };
        Assert.Equal(BlackHoleSkies.CheckerLight, BlackHoleSkies.Sample(sky, 1, 0.01, 0.01));
        Assert.Equal(BlackHoleSkies.CheckerDark, BlackHoleSkies.Sample(sky, Math.Cos(1.0), 0.01, Math.Sin(1.0)));   // one cell (π/4) on
    }

    [Fact]
    public void Sky_Leaves_The_Shadows_And_Other_Channels_Alone()
    {
        var off = Render(new FractalParameters());
        var on = Render(new FractalParameters { BlackHoleSky = BlackHoleSky.Grid });
        Assert.Equal(off.FateBuffer, on.FateBuffer);
        for (int i = 0; i < W * H; i++)
            if (off.FateBuffer[i] is 0 or 1) Assert.Equal(off.ColorBuffer[i], on.ColorBuffer[i]);
        var ch = Render(new FractalParameters { BlackHoleChannel = BlackHoleChannel.Winding });
        var chSky = Render(new FractalParameters { BlackHoleChannel = BlackHoleChannel.Winding, BlackHoleSky = BlackHoleSky.Grid });
        Assert.Equal(ch.ColorBuffer, chSky.ColorBuffer);
    }

    [Fact]
    public void Live_And_Poster_Agree_With_A_Sky()
    {
        var p = new FractalParameters { BlackHoleSky = BlackHoleSky.Stars, BlackHoleProjection = BlackHoleProjection.Pinhole };
        var a = Render(p);
        var req = new PosterRequest
        {
            FractalType = FractalType.BlackHoleShadow, Width = W, Height = H,
            CenterX = 0, CenterY = 0, Zoom = BlackHoleShadowCalculator.DefaultZoom, MaxIterations = a.MaxIterations,
            Quality = QualityPreset.Standard, FractalParameters = p, ColorMap = new HsvPalette(),
        };
        Assert.Equal(a.ColorBuffer, PosterRenderer.RenderToPixels(req, TestContext.Current.CancellationToken, out _, out _));
    }

    private static void SetEnv(FractalParameters p, string path)
    {
        var fx = p.Lighting;   // a struct: copy, edit, assign back
        fx.SkyMode = SkyMode.Hdri;
        fx.EnvironmentName = path;
        p.Lighting = fx;
    }

    [Fact]
    public void Missing_Hdri_File_Is_A_Batch_Gap()
    {
        string missing = Path.Combine(Path.GetTempPath(), "ff-no-such-sky-" + Guid.NewGuid().ToString("N") + ".hdr");
        var p = new FractalParameters { BlackHoleSky = BlackHoleSky.Environment };
        SetEnv(p, missing);
        var gaps = BatchCommandBuilder.DetectGaps(new BatchCommandSnapshot { Fractal = FractalType.BlackHoleShadow, Parameters = p });
        Assert.Contains(gaps, g => g.Contains("HDRI") && g.Contains("#1925"));

        string present = Path.GetTempFileName();
        try
        {
            SetEnv(p, present);
            gaps = BatchCommandBuilder.DetectGaps(new BatchCommandSnapshot { Fractal = FractalType.BlackHoleShadow, Parameters = p });
            Assert.DoesNotContain(gaps, g => g.Contains("#1925"));
            p.BlackHoleSky = BlackHoleSky.Checker;
            SetEnv(p, missing);
            gaps = BatchCommandBuilder.DetectGaps(new BatchCommandSnapshot { Fractal = FractalType.BlackHoleShadow, Parameters = p });
            Assert.DoesNotContain(gaps, g => g.Contains("#1925"));   // the HDRI is not used
        }
        finally { File.Delete(present); }
    }

    [Fact]
    public void Sky_Settings_Round_Trip_The_Region_Snapshot_And_The_Command_Line()
    {
        var p = new FractalParameters
        {
            BlackHoleProjection = BlackHoleProjection.Pinhole, BlackHoleSky = BlackHoleSky.Stars,
            BlackHoleSkyDivisions = 5, BlackHoleSkySeed = -42, BlackHoleSkyRotation = 37.5,
        };
        var snap = RegionFractalParams.Snapshot(FractalType.BlackHoleShadow, p)!;
        var viaJson = new FractalParameters();
        JsonSerializer.Deserialize<RegionFractalParams>(JsonSerializer.Serialize(snap))!.ApplyTo(viaJson);
        var report = BatchCommandBuilder.BuildWithReport(new BatchCommandSnapshot
        {
            Fractal = FractalType.BlackHoleShadow, Parameters = p, FamilyParams = snap.ToKeyValues(),
        });
        Assert.True(BatchOptions.TryParse(report.Args.ToArray(), 0, out var o, out var err), err);
        var viaCli = new FractalParameters();
        RegionFractalParams.FromKeyValues(o.Params, FractalType.BlackHoleShadow, out var perr)!.ApplyTo(viaCli);
        Assert.Null(perr);
        foreach (var q in new[] { viaJson, viaCli, p.Clone() })
        {
            Assert.Equal(p.BlackHoleProjection, q.BlackHoleProjection);
            Assert.Equal(p.BlackHoleSky, q.BlackHoleSky);
            Assert.Equal(p.BlackHoleSkyDivisions, q.BlackHoleSkyDivisions);
            Assert.Equal(p.BlackHoleSkySeed, q.BlackHoleSkySeed);
            Assert.Equal(p.BlackHoleSkyRotation, q.BlackHoleSkyRotation);
        }
        Assert.Equal(Render(p).ColorBuffer, Render(viaCli).ColorBuffer);
    }

    [Fact]
    public void Params_Panel_Shows_Sky_Controls_For_The_Chosen_Sky()
    {
        var vm = new FractalParamsViewModel(FractalType.BlackHoleShadow, new FractalParameters());
        Assert.False(vm.BhSkyOn || vm.BhSkyProcedural || vm.BhSkyStars);
        vm.BlackHoleSky = BlackHoleSky.Environment;
        Assert.True(vm.BhSkyOn && !vm.BhSkyProcedural);
        vm.BlackHoleSky = BlackHoleSky.Stars;
        Assert.True(vm.BhSkyOn && vm.BhSkyProcedural && vm.BhSkyStars);
        vm.BlackHoleSkyDivisions = 0;
        Assert.Equal(1, vm.BlackHoleSkyDivisions);
    }
}
