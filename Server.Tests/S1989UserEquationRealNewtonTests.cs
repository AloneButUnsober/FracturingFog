// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1989 — the User Equation real 2D Newton mode (Newton Φ). Done-when of the issue: the Three-Body
// Lagrange Basins preset reproduced as a User Equation with the same basin map, up to boundary
// pixels, carried by the batch / Command builder. Independent checks: the preset's root constants
// against ThreeBodySystem.LagrangePoints (bisection, not Newton), and the gradient the symbolic
// derivative produces against ThreeBodySystem.Gradient (hand-written).

using System;
using System.Linq;
using System.Threading;
using FracturingFog;
using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.Models;
using FracturingFog.Scattering;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1989UserEquationRealNewtonTests
{
    private const string PresetName = "User Equation - Lagrange Basins (Newton on a potential)";

    private static UserEquationCalculator RenderUe(FractalParameters p, int w, int h, int maxIt, double zoom = 1.0, double cx = 0, double cy = 0)
    {
        var c = new UserEquationCalculator(w, h)
        {
            Zoom = zoom, CenterX = cx, CenterY = cy, MaxIterations = maxIt, ColorMap = new HsvPalette(), FractalParameters = p,
        };
        c.Calculate(CancellationToken.None);
        return c;
    }

    private static FractalParameters PresetParams(out FractalRegion region)
    {
        region = FractalRegionLibrary.Instance.BuiltIns.Single(r => r.Name == PresetName);
        Assert.Equal(FractalType.UserEquation, region.FractalType);
        var p = new FractalParameters();
        region.Params!.ApplyTo(p);
        return p;
    }

    [Fact]
    public void Preset_Roots_Are_The_Lagrange_Points_In_Three_Body_Order()
    {
        var p = PresetParams(out _);
        Assert.True(RealNewtonModel.TryParseRoots(p.UserEquationNewtonRoots, out var roots, out var err), err);
        var l = ThreeBodySystem.LagrangePoints(0.5);
        Assert.Equal(l.Length, roots.Length);
        for (int i = 0; i < l.Length; i++)
        {
            Assert.True(Math.Abs(roots[i].X - l[i].X) < 1e-9 && Math.Abs(roots[i].Y - l[i].Y) < 1e-9,
                $"L{i + 1}: {roots[i]} vs {l[i]}");
        }
    }

    [Fact]
    public void Symbolic_Gradient_Matches_The_Hand_Written_One()
    {
        var p = PresetParams(out _);
        Assert.True(RealExpression.TryParse(p.UserEquationPotential, RealNewtonModel.Variables, out var phi, out var err), err);
        var gx = phi!.Derivative(0); var gy = phi.Derivative(1);
        var rng = new Random(1989);
        for (int k = 0; k < 200; k++)
        {
            double x = rng.NextDouble() * 4 - 2, y = rng.NextDouble() * 4 - 2;
            var (ox, oy) = ThreeBodySystem.Gradient(0.5, x, y);
            double ax = gx.Eval(new[] { x, y }), ay = gy.Eval(new[] { x, y });
            Assert.True(Math.Abs(ax - ox) <= 1e-9 * Math.Max(1, Math.Abs(ox)), $"gx at ({x}, {y}): {ax} vs {ox}");
            Assert.True(Math.Abs(ay - oy) <= 1e-9 * Math.Max(1, Math.Abs(oy)), $"gy at ({x}, {y}): {ay} vs {oy}");
        }
    }

    [Fact]
    public void Preset_Reproduces_The_Three_Body_Lagrange_Basins_Up_To_Boundary_Pixels()
    {
        const int w = 192, h = 108;
        var p = PresetParams(out var region);
        Assert.Equal(60, region.Iterations);
        var ue = RenderUe(p, w, h, region.Iterations, region.Zoom, region.CenterX, region.CenterY);
        Assert.Equal(string.Empty, ue.RealNewtonError);

        var tbRegion = FractalRegionLibrary.Instance.BuiltIns.Single(r => r.Name == "Three-Body - Lagrange Basins");
        var tp = new FractalParameters();
        tbRegion.Params!.ApplyTo(tp);
        var tb = new ThreeBodyCalculator(w, h) { ColorMap = new HsvPalette(), FractalParameters = tp, Zoom = tbRegion.Zoom,
                                                 CenterX = tbRegion.CenterX, CenterY = tbRegion.CenterY };
        tb.Calculate(CancellationToken.None);

        int differ = 0, interiorDiffer = 0, colourDiffer = 0, matched = 0;
        var seen = new bool[5];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                int a = ue.RootBuffer[i], b = tb.GateBuffer[i];
                if (b >= 0) seen[b] = true;
                if (a != b)
                {
                    differ++;
                    // A boundary pixel: some 4-neighbour of the Three-Body map has another gate.
                    bool boundary = (x > 0 && tb.GateBuffer[i - 1] != b) || (x < w - 1 && tb.GateBuffer[i + 1] != b)
                                 || (y > 0 && tb.GateBuffer[i - w] != b) || (y < h - 1 && tb.GateBuffer[i + w] != b);
                    if (!boundary) interiorDiffer++;
                    continue;
                }
                if (a >= 0 && ue.SmoothBuffer[i] == tb.SmoothBuffer[i])
                {
                    matched++;
                    if (ue.ColorBuffer[i] != tb.ColorBuffer[i]) colourDiffer++;
                }
            }
        Assert.All(seen, s => Assert.True(s, "precondition: all five basins in frame"));
        Assert.True(differ <= w * h / 200, $"{differ} of {w * h} pixels in another basin");
        Assert.True(interiorDiffer <= w * h / 2000, $"{interiorDiffer} off-boundary pixels differ");
        Assert.True(matched > w * h / 2, $"precondition: {matched} pixels with equal basin and step count");
        Assert.Equal(0, colourDiffer);
    }

    [Fact]
    public void Blank_Roots_Are_Discovered_From_The_Frame()
    {
        var p = PresetParams(out _);
        p.UserEquationNewtonRoots = null;
        var ue = RenderUe(p, 96, 54, 60);
        var roots = ue.LastRealNewtonRoots!;
        var l = ThreeBodySystem.LagrangePoints(0.5);
        Assert.Equal(5, roots.Length);
        foreach (var lp in l)
            Assert.Contains(roots, r => Math.Abs(r.X - lp.X) < 1e-6 && Math.Abs(r.Y - lp.Y) < 1e-6);
        // Sorted by x, then y: L3 first, L2 last, the three x = 0 roots by y in between.
        Assert.True(roots[0].X < -1 && roots[4].X > 1);
        Assert.True(roots[1].Y < roots[2].Y && roots[2].Y < roots[3].Y);
    }

    [Fact]
    public void A_Double_Well_Picks_The_Nearest_Minimum()
    {
        var p = new FractalParameters { UserEquationPotential = "(x^2 - 1)^2 + y^2", UserEquationNewtonRoots = "-1, 0; 0, 0; 1, 0" };
        const int w = 64, h = 36;
        var ue = RenderUe(p, w, h, 50);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var (px, _) = UserEquationCalculator.RealNewtonPlaneAt(x + 0.5, y + 0.5, w, h, 0, 0, 1.0, 1, 0);
                int g = ue.RootBuffer[y * w + x];
                // Newton on ∂Φ/∂x = 4x(x² − 1) converges to the closest root for |x| far from the
                // inflection points ±1/√3 (where the Hessian vanishes); the y part is linear (one step).
                if (Math.Abs(px) > 0.9) Assert.Equal(px > 0 ? 2 : 0, g);
                else if (Math.Abs(px) < 0.3) Assert.Equal(1, g);
            }
    }

    [Fact]
    public void Off_Is_The_Escape_Time_Render_And_A_Bad_Potential_Reports()
    {
        var plain = RenderUe(new FractalParameters { UserEquationSource = "z*z + c" }, 48, 27, 100);
        var blank = RenderUe(new FractalParameters { UserEquationSource = "z*z + c", UserEquationPotential = "  " }, 48, 27, 100);
        Assert.Equal(plain.ColorBuffer, blank.ColorBuffer);
        Assert.Null(plain.LastRealNewtonRoots);

        var bad = RenderUe(new FractalParameters { UserEquationPotential = "x^2 + (" }, 16, 9, 20);
        Assert.NotEqual(string.Empty, bad.RealNewtonError);
        Assert.All(bad.RootBuffer, g => Assert.Equal(-1, g));

        var badRoots = RenderUe(new FractalParameters { UserEquationPotential = "x^2 + y^2", UserEquationNewtonRoots = "1, 2; 3" }, 16, 9, 20);
        Assert.Contains("roots", badRoots.RealNewtonError);
    }

    [Fact]
    public void Roots_Text_Round_Trips()
    {
        var roots = new[] { (0.1, -2.5), (1.19840614455492, 0.0), (-3e-7, 0.8660254037844386) };
        Assert.True(RealNewtonModel.TryParseRoots(RealNewtonModel.FormatRoots(roots), out var back, out var err), err);
        Assert.Equal(roots, back);
    }

    [Fact]
    public void Potential_And_Roots_Survive_The_Region_Snapshot_And_The_Command_Line()
    {
        var p = PresetParams(out _);
        var snap = RegionFractalParams.Snapshot(FractalType.UserEquation, p)!;
        var report = BatchCommandBuilder.BuildWithReport(new BatchCommandSnapshot
        {
            Fractal = FractalType.UserEquation, Parameters = p, FamilyParams = snap.ToKeyValues(),
        });
        Assert.True(BatchOptions.TryParse(report.Args.ToArray(), 0, out var o, out var err), err);
        var q = new FractalParameters();
        RegionFractalParams.FromKeyValues(o.Params, FractalType.UserEquation, out var perr)!.ApplyTo(q);
        Assert.Null(perr);
        Assert.Equal(p.UserEquationPotential, q.UserEquationPotential);
        Assert.Equal(p.UserEquationNewtonRoots, q.UserEquationNewtonRoots);

        // Off stays off (nothing captured), and recall into a fresh UE region resets it.
        Assert.Null(RegionFractalParams.Snapshot(FractalType.UserEquation, new FractalParameters())?.UserEquationPotential);
        RegionFractalParams.ResetFamilyToDefaults(FractalType.UserEquation, q);
        Assert.Null(q.UserEquationPotential);
        Assert.Null(q.UserEquationNewtonRoots);
    }
}
