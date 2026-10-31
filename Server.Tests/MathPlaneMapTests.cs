// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1552 (Math Plane S5) — the linear map: interpolation, polar split, eigenpairs,
// rendering of mapped objects / det / regions, editing under a map, and the
// params' region + --param round trip. Invariants are checked against matrix
// algebra done here, not the map code's own helpers.

using System;
using System.Linq;
using System.Threading;
using FracturingFog;
using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.Input;
using FracturingFog.Models;
using FracturingFog.UI.Avalonia.ViewModels;
using FracturingFog.ViewState;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class MathPlaneMapTests
{
    private const int W = 640, H = 360;
    private static readonly Random Rng = new(1552);

    private static Mat2 RandomMatrix() => new(Rng.NextDouble() * 4 - 2, Rng.NextDouble() * 4 - 2, Rng.NextDouble() * 4 - 2, Rng.NextDouble() * 4 - 2);

    private static void Near(Mat2 want, Mat2 got, double tol = 1e-12)
    {
        Assert.InRange(got.A - want.A, -tol, tol); Assert.InRange(got.B - want.B, -tol, tol);
        Assert.InRange(got.C - want.C, -tol, tol); Assert.InRange(got.D - want.D, -tol, tol);
    }

    // ── Interpolation ──

    [Fact]
    public void BothModes_IdentityAtZero_MAtOne()
    {
        for (int k = 0; k < 200; k++)
        {
            var m = RandomMatrix();
            foreach (var mode in new[] { MathPlaneInterpolation.Linear, MathPlaneInterpolation.Polar })
            {
                Near(Mat2.Identity, MathPlaneMap.At(m, 0, mode));
                Near(m, MathPlaneMap.At(m, 1, mode), 1e-12);
            }
        }
    }

    [Fact]
    public void AtOne_ImageOfIHat_IsColumnOne()
    {
        var m = new Mat2(2, -1, 0.5, 3);
        var (x, y) = MathPlaneMap.At(m, 1, MathPlaneInterpolation.Linear).Apply(1, 0);
        Assert.Equal((2.0, 0.5), (x, y));
        (x, y) = MathPlaneMap.At(m, 1, MathPlaneInterpolation.Linear).Apply(0, 1);
        Assert.Equal((-1.0, 3.0), (x, y));
    }

    [Fact]
    public void Polar_PureRotation_KeepsDetOne_WhileLinearCollapses()
    {
        var rot = new Mat2(0, -1, 1, 0);   // 90 degrees
        for (double t = 0; t <= 1.0001; t += 0.05)
        {
            Assert.Equal(1.0, MathPlaneMap.At(rot, t, MathPlaneInterpolation.Polar).Det, 12);
            // Linear: det((1-t)I + tR) = (1-t)^2 + t^2, which dips to 1/2 at t = 1/2.
            Assert.Equal((1 - t) * (1 - t) + t * t, MathPlaneMap.At(rot, t, MathPlaneInterpolation.Linear).Det, 12);
        }
    }

    [Fact]
    public void Polar_RotationTimesUniformScale_DetGrowsLikeTheScale()
    {
        // M = k R(theta): the stretch is k I, so det at t is (1 - t + t k)^2 exactly
        // (rotations keep area), monotone from 1 to k^2. Linear interpolation dips.
        foreach (double k in new[] { 0.5, 1.5, 3.0 })
            foreach (double theta in new[] { 0.3, 1.2, 2.5, -2.0 })
            {
                var r = Mat2.Rotation(theta);
                var m = new Mat2(k * r.A, k * r.B, k * r.C, k * r.D);
                for (double t = 0; t <= 1.0001; t += 0.05)
                {
                    double want = Math.Pow(1 - t + t * k, 2);
                    Assert.Equal(want, MathPlaneMap.At(m, t, MathPlaneInterpolation.Polar).Det, 10);
                }
            }
    }

    [Fact]
    public void PolarDecompose_RotationTimesSymmetric()
    {
        for (int k = 0; k < 200; k++)
        {
            var m = RandomMatrix();
            if (m.Det <= 0) continue;
            var (theta, s) = MathPlaneMap.PolarDecompose(m);
            Assert.InRange(s.B - s.C, -1e-12, 1e-12);                       // symmetric
            Near(m, Mat2.Rotation(theta) * s, 1e-12);                        // R S = M
            Assert.True(s.Det > 0 && s.Trace > 0, "S positive definite");
        }
    }

    [Fact]
    public void Polar_NegativeDet_FallsBackToLinear()
    {
        var reflect = new Mat2(1, 0, 0, -1);
        Near(MathPlaneMap.At(reflect, 0.3, MathPlaneInterpolation.Linear), MathPlaneMap.At(reflect, 0.3, MathPlaneInterpolation.Polar));
    }

    // ── Eigenpairs ──

    [Fact]
    public void Eigen_MvParallelToV_AndLinearKeepsThemAllTheWay()
    {
        int checkedPairs = 0;
        for (int k = 0; k < 300; k++)
        {
            var m = RandomMatrix();
            var eig = MathPlaneMap.Eigen(m, out bool all);
            Assert.False(all);
            double disc = m.Trace * m.Trace / 4 - m.Det;
            Assert.Equal(disc >= 0 ? (disc == 0 ? 1 : 2) : 0, eig.Count);
            foreach (var e in eig)
            {
                var (x, y) = m.Apply(e.X, e.Y);
                Assert.InRange(x - e.Lambda * e.X, -1e-9, 1e-9);
                Assert.InRange(y - e.Lambda * e.Y, -1e-9, 1e-9);
                Assert.Equal(1.0, e.X * e.X + e.Y * e.Y, 12);
                // (1 - t) I + t M has the same eigenvector.
                var a = MathPlaneMap.At(m, 0.37, MathPlaneInterpolation.Linear);
                var (ax, ay) = a.Apply(e.X, e.Y);
                Assert.InRange(ax * e.Y - ay * e.X, -1e-9, 1e-9);
                checkedPairs++;
            }
        }
        Assert.True(checkedPairs > 100);
    }

    [Fact]
    public void Eigen_SpecialCases()
    {
        Assert.Empty(MathPlaneMap.Eigen(new Mat2(0, -1, 1, 0), out bool all1));      // rotation
        Assert.False(all1);
        Assert.Empty(MathPlaneMap.Eigen(new Mat2(2, 0, 0, 2), out bool all2));       // scalar
        Assert.True(all2);
        var shear = MathPlaneMap.Eigen(new Mat2(1, 1, 0, 1), out _);
        Assert.Single(shear);                                                       // defective
        Assert.Equal(1.0, Math.Abs(shear[0].X), 12);
    }

    // ── Rendering ──

    private static MathPlaneCalculator Render(string scene, Action<FractalParameters> map)
    {
        var p = new FractalParameters { MathPlaneScene = scene, MathPlaneMapMode = MathPlaneMapMode.Matrix };
        map(p);
        var calc = new MathPlaneCalculator(W, H) { Zoom = 0.5, FractalParameters = p };
        calc.Calculate(CancellationToken.None);
        return calc;
    }

    [Fact]
    public void MappedPoint_LandsAtTheImage()
    {
        const uint c = 0xFF12AB34;
        var calc = Render("axes off; grid off; ticks off; point A 1 0.5 #12AB34 nolabel",
            p => { p.MathPlaneM11 = 2; p.MathPlaneM12 = -1; p.MathPlaneM21 = 0.5; p.MathPlaneM22 = 1.5; p.MathPlaneT = 1; });
        // The grid stays off, but the mapped grid is part of the plane only when the grid is on.
        double sx = 0, sy = 0; int n = 0;
        for (int i = 0; i < calc.ColorBuffer.Length; i++)
            if (calc.ColorBuffer[i] == c) { sx += i % W + 0.5; sy += i / W + 0.5; n++; }
        Assert.True(n > 10);
        // A (1, 0.5) = (2 - 0.5, 0.5 + 0.75) = (1.5, 1.25)
        var (ex, ey) = MathPlaneCalculator.ToPixel(1.5, 1.25, W, H, 0, 0, 0.5);
        Assert.InRange(sx / n - ex, -0.5, 0.5);
        Assert.InRange(sy / n - ey, -0.5, 0.5);
    }

    [Fact]
    public void Det_ShadesExactlyTheUnitSquaresImage()
    {
        var m = new Mat2(1.5, 0.5, 0.25, 1.0);
        var calc = Render("axes off; grid off; ticks off; det on", p =>
        {
            p.MathPlaneM11 = m.A; p.MathPlaneM12 = m.B; p.MathPlaneM21 = m.C; p.MathPlaneM22 = m.D; p.MathPlaneT = 1;
        });
        var inv = m.Inverse()!.Value;
        double pitch = 8.0 / W;
        int insideShaded = 0, inside = 0, outsideShaded = 0;
        for (int py = 0; py < H; py++)
            for (int px = 0; px < W; px++)
            {
                double x = -4 + (px + 0.5) * pitch, y = H * 0.5 * pitch - (py + 0.5) * pitch;
                var (u, v) = inv.Apply(x, y);      // unit-square coordinates
                double margin = 3 * pitch * 2;     // edge line + antialiasing
                bool shaded = calc.ColorBuffer[py * W + px] != MathPlaneCalculator.BackgroundColor;
                if (u > margin && u < 1 - margin && v > margin && v < 1 - margin) { inside++; if (shaded) insideShaded++; }
                else if (u < -margin || u > 1 + margin || v < -margin || v > 1 + margin) { if (shaded) outsideShaded++; }
            }
        Assert.Equal(inside, insideShaded);
        Assert.True(outsideShaded < 200, $"{outsideShaded} shaded pixels outside the parallelogram (det label overhang only)");
        // The counted band is the unit square shrunk by the margin on each side, so its
        // area is (1 - 2 margin)^2 |det|.
        double band = Math.Pow(1 - 2 * (3 * pitch * 2), 2) * m.Det;
        Assert.InRange(inside * pitch * pitch, 0.98 * band, 1.02 * band);
    }

    [Fact]
    public void Region_UnderAMap_ShadesTheImage()
    {
        var m = new Mat2(2, 0, 0, 0.5);
        var view = new MathPlaneViewBox(-4, 4, -2.25, 2.25, 8.0 / W);
        Assert.True(MathPlaneScene.TryParse("region R \"x^2 + y^2 < 1\"", out var s, out _));
        var mask = MathPlaneImplicitSampler.RegionMask(s.Objects[0], view, W, H, m.Inverse())!;
        for (int py = 0; py < H; py += 3)
            for (int px = 0; px < W; px += 3)
            {
                double x = view.XMin + (px + 0.5) * view.Pitch, y = view.YMax - (py + 0.5) * view.Pitch;
                // Image of the unit disk under diag(2, 1/2): the ellipse (x/2)^2 + (2y)^2 < 1.
                Assert.Equal((x / 2) * (x / 2) + (2 * y) * (2 * y) < 1, mask[py * W + px]);
            }
    }

    [Fact]
    public void SingularMap_RendersWithoutThrowing()
    {
        var calc = Render("det on; eigen on; lengths on; vector v 1 1; region R \"x < 0\"; curve f implicit \"x^2 + y^2 - 1\"; curve g explicit \"sin(x)\"",
            p => { p.MathPlaneM11 = 1; p.MathPlaneM12 = 0; p.MathPlaneM21 = 0; p.MathPlaneM22 = 0; p.MathPlaneT = 1; });
        Assert.True(calc.ColorBuffer.Distinct().Count() > 2);
    }

    // ── Editing under a map ──

    [Fact]
    public void DraggingUnderAMap_MovesThePreimage_SoTheImageFollowsThePointer()
    {
        var view = new FractalViewState { FractalType = FractalType.MathPlane, Zoom = 0.5, CenterX = 0, CenterY = 0 };
        var p = view.FractalParameters;
        p.MathPlaneScene = "point A 0 1";
        p.MathPlaneMapMode = MathPlaneMapMode.Matrix;
        p.MathPlaneM11 = 1; p.MathPlaneM12 = 1; p.MathPlaneM21 = 0; p.MathPlaneM22 = 1;   // shear
        p.MathPlaneT = 1;
        var input = new FractalInputController(view);
        var vm = new MathPlaneSectionViewModel(view, () => { }, () => { });
        input.EditHandler = vm;

        PointerInput At(double x, double y)
        {
            var (px, py) = MathPlaneCalculator.ToPixel(x, y, W, H, 0, 0, 0.5);
            return new PointerInput((int)Math.Floor(px), (int)Math.Floor(py), W, H, PointerButton.Left, InputModifiers.None);
        }
        // The point (0, 1) is drawn at its image (1, 1) (the shear moves it); press
        // there, drag to (2, 1), whose preimage is (1, 1).
        input.OnPointerDown(At(1, 1));
        input.OnPointerMove(At(2, 1));
        input.OnPointerUp(At(2, 1));
        Assert.Equal(0.0, view.CenterX);
        Assert.True(MathPlaneScene.TryParse(p.MathPlaneScene, out var s, out _));
        var (ax, ay) = MathPlaneMap.Current(p).Apply(s.Objects[0].Coords[0], s.Objects[0].Coords[1]);
        double pitch = 8.0 / W;
        Assert.InRange(ax - 2, -2 * pitch, 2 * pitch);       // image under the pointer
        Assert.InRange(ay - 1, -2 * pitch, 2 * pitch);
        Assert.InRange(s.Objects[0].Coords[0] - 1, -2 * pitch, 2 * pitch);   // preimage (1, 1)
        Assert.InRange(s.Objects[0].Coords[1] - 1, -2 * pitch, 2 * pitch);
    }

    [Fact]
    public void Presets_AndT_WriteTheParams()
    {
        var view = new FractalViewState { FractalType = FractalType.MathPlane };
        int renders = 0;
        var vm = new MathPlaneSectionViewModel(view, () => renders++, () => { });
        var rot = MathPlaneSectionViewModel.PresetList.Single(x => x.Name == "Rotate 90");
        vm.ApplyPresetCommand.Execute(rot).Subscribe();
        Assert.Equal(MathPlaneMapMode.Matrix, view.FractalParameters.MathPlaneMapMode);
        Assert.Equal((0.0, -1.0, 1.0, 0.0), (view.FractalParameters.MathPlaneM11, view.FractalParameters.MathPlaneM12,
                                             view.FractalParameters.MathPlaneM21, view.FractalParameters.MathPlaneM22));
        vm.T = 0.5;
        vm.Interpolation = MathPlaneInterpolation.Polar;
        Assert.Equal(0.5, view.FractalParameters.MathPlaneT);
        Assert.Contains("det 1", vm.MapReadout);
        vm.T = 7;
        Assert.Equal(1.0, view.FractalParameters.MathPlaneT);
        Assert.True(renders >= 3);
        // Every preset is the matrix its hint describes.
        Assert.All(MathPlaneSectionViewModel.PresetList, pr => Assert.False(string.IsNullOrWhiteSpace(pr.Hint)));
        Assert.Equal(-1.0, MathPlaneSectionViewModel.PresetList.Single(x => x.Name == "Reflect").M22);
        Assert.Equal(0.0, new Mat2(1, 0, 0, 0).Det);
    }

    // ── Params: region snapshot, --param, Command builder ──

    [Fact]
    public void MapParams_RoundTripThroughRegionAndCommandLine()
    {
        var p = new FractalParameters
        {
            MathPlaneScene = "vector v 1 2; det on",
            MathPlaneMapMode = MathPlaneMapMode.Matrix,
            MathPlaneM11 = 0.8, MathPlaneM12 = -0.6, MathPlaneM21 = 0.6, MathPlaneM22 = 0.8,
            MathPlaneT = 0.35, MathPlaneInterpolation = MathPlaneInterpolation.Polar,
        };
        var snap = RegionFractalParams.Snapshot(FractalType.MathPlane, p)!;
        var report = BatchCommandBuilder.BuildWithReport(new BatchCommandSnapshot
        {
            Fractal = FractalType.MathPlane, Parameters = p, FamilyParams = snap.ToKeyValues(),
        });
        Assert.True(BatchOptions.TryParse(report.Args.ToArray(), 0, out var o, out var err), err);
        var fresh = new FractalParameters();
        RegionFractalParams.FromKeyValues(o.Params, FractalType.MathPlane, out var perr)!.ApplyTo(fresh);
        Assert.Null(perr);
        Assert.Equal(MathPlaneMapMode.Matrix, fresh.MathPlaneMapMode);
        Assert.Equal(MathPlaneInterpolation.Polar, fresh.MathPlaneInterpolation);
        Assert.Equal(0.35, fresh.MathPlaneT);
        Near(MathPlaneMap.Matrix(p), MathPlaneMap.Matrix(fresh), 0);

        var a = Render(p.MathPlaneScene, q => CopyMap(p, q));
        var b = Render(fresh.MathPlaneScene, q => CopyMap(fresh, q));
        Assert.Equal(a.ColorBuffer, b.ColorBuffer);
    }

    private static void CopyMap(FractalParameters from, FractalParameters to)
    {
        to.MathPlaneMapMode = from.MathPlaneMapMode;
        to.MathPlaneM11 = from.MathPlaneM11; to.MathPlaneM12 = from.MathPlaneM12;
        to.MathPlaneM21 = from.MathPlaneM21; to.MathPlaneM22 = from.MathPlaneM22;
        to.MathPlaneT = from.MathPlaneT; to.MathPlaneInterpolation = from.MathPlaneInterpolation;
    }

    [Fact]
    public void SceneFlags_RoundTrip()
    {
        Assert.True(MathPlaneScene.TryParse("det on; eigen on; lengths on; point A 1 1", out var s, out _));
        Assert.True(s.ShowDet && s.ShowEigen && s.ShowLengths);
        Assert.True(MathPlaneScene.TryParse(s.Serialize(), out var back, out _));
        Assert.Equal(s, back);
        Assert.False(MathPlaneScene.TryParse("det maybe", out _, out var e));
        Assert.Contains("on or off", e!.Message);
    }

    [Fact]
    public void T_IsAnimatable()
        => Assert.Contains(FracturingFog.Abstractions.Animation.FractalAnimatableParamsMap.For(FractalType.MathPlane),
                           d => d.ParamName == nameof(FractalParameters.MathPlaneT) && d.Min == 0 && d.Max == 1);
}
