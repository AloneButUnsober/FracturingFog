// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1551 (Math Plane S4) — implicit curves (marching squares) and regions,
// checked against analytic geometry computed here (circle radius, lemniscate
// equation, tan poles) and an independent per-pixel sign test.

using System;
using System.Linq;
using System.Threading;
using FracturingFog;
using FracturingFog.Models;
using FracturingFog.UI.Avalonia.ViewModels;
using FracturingFog.ViewState;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class MathPlaneImplicitTests
{
    private const int W = 640, H = 360;
    private static readonly MathPlaneViewBox View = new(-4, 4, -2.25, 2.25, 8.0 / W);

    private static MathPlaneObject Obj(string stmt)
    {
        Assert.True(MathPlaneScene.TryParse(stmt, out var s, out var e), e?.ToString());
        return s.Objects.Single();
    }

    // ── Format ──

    [Fact]
    public void ImplicitAndRegion_RoundTrip()
    {
        const string text = "curve c implicit \"x^2 + y^2 - 4\" #E69F00; region R \"y < sin(x) && x > 0\" #56B4E9 nolabel";
        Assert.True(MathPlaneScene.TryParse(text, out var s, out var e), e?.ToString());
        Assert.Equal(MathPlaneCurveKind.Implicit, s.Objects[0].CurveKind);
        Assert.Equal(MathPlaneObjectKind.Region, s.Objects[1].Kind);
        Assert.Equal("y < sin(x) && x > 0", s.Objects[1].Expressions.Single());
        Assert.True(MathPlaneScene.TryParse(s.Serialize(), out var back, out _));
        Assert.Equal(s, back);
    }

    [Theory]
    [InlineData("curve c implicit \"x + t\"", "reads only x, y, not t")]
    [InlineData("curve c implicit \"x^2 + y^2 - 1\" -1 1", "has no domain")]
    [InlineData("region R \"theta < 1\"", "reads only x, y, not theta")]
    [InlineData("region R x < 1", "quoted condition")]
    [InlineData("region R", "quoted condition")]
    [InlineData("region R \"x <\"", "expression:")]
    public void Errors(string text, string why)
    {
        Assert.False(MathPlaneScene.TryParse(text, out _, out var e));
        Assert.Contains(why, e!.Message);
    }

    // ── Marching squares ──

    [Theory]
    [InlineData(2.0)]
    [InlineData(0.5)]
    [InlineData(1.37)]
    public void Circle_EveryVertexOnTheCircle_AndTheLengthIsTheCircumference(double r)
    {
        var segs = MathPlaneImplicitSampler.Segments(Obj($"curve c implicit \"x^2 + y^2 - {r}^2\""), View);
        Assert.NotEmpty(segs);
        double tol = View.Pitch * 0.05;   // bisection places crossings far inside a pixel
        foreach (var (a, b) in segs)
        {
            Assert.InRange(Math.Sqrt(a.X * a.X + a.Y * a.Y) - r, -tol, tol);
            Assert.InRange(Math.Sqrt(b.X * b.X + b.Y * b.Y) - r, -tol, tol);
        }
        double length = segs.Sum(s => Math.Sqrt(Math.Pow(s.B.X - s.A.X, 2) + Math.Pow(s.B.Y - s.A.Y, 2)));
        Assert.InRange(length / (2 * Math.PI * r), 0.99, 1.001);   // chords sit just inside the arc
    }

    [Fact]
    public void Lemniscate_SaddleAtTheOrigin_NoSpuriousSegments()
    {
        // (x^2 + y^2)^2 = 4 (x^2 - y^2): Bernoulli's lemniscate, crossing itself at 0.
        var segs = MathPlaneImplicitSampler.Segments(Obj("curve L implicit \"(x^2 + y^2)^2 - 4*(x^2 - y^2)\""), View);
        Assert.NotEmpty(segs);
        foreach (var (a, b) in segs)
            foreach (var p in new[] { a, b })
            {
                double f = Math.Pow(p.X * p.X + p.Y * p.Y, 2) - 4 * (p.X * p.X - p.Y * p.Y);
                // |F| relative to |grad F| ~ distance to the curve: well under a pixel.
                double gx = 4 * p.X * (p.X * p.X + p.Y * p.Y) - 8 * p.X, gy = 4 * p.Y * (p.X * p.X + p.Y * p.Y) + 8 * p.Y;
                double g = Math.Sqrt(gx * gx + gy * gy);
                Assert.True(Math.Abs(f) <= 0.5 * View.Pitch * Math.Max(g, 1e-9) + 1e-9, $"vertex {p} off the curve");
            }
        // No segment longer than a cell diagonal (a spurious cross-cell join would be).
        double diag = MathPlaneImplicitSampler.CellPixels * View.Pitch * Math.Sqrt(2) * 1.01;
        Assert.All(segs, s => Assert.True(Math.Sqrt(Math.Pow(s.B.X - s.A.X, 2) + Math.Pow(s.B.Y - s.A.Y, 2)) <= diag));
    }

    [Fact]
    public void Saddle_ResolvedByTheCentreValue()
    {
        // F = xy - eps with a lattice cell centred on the origin: corners (+,+) and
        // (-,-) positive, the others negative, F(0, 0) = -eps < 0. The zero set is the
        // hyperbola xy = eps, one branch in quadrant I and one in III, so no segment
        // may join the two quadrants (the wrong saddle join does exactly that).
        const double pitch = 0.01;
        double h = MathPlaneImplicitSampler.CellPixels * pitch;
        var view = new MathPlaneViewBox(-10.5 * h, 10.5 * h, -10.5 * h, 10.5 * h, pitch);
        double eps = h * h / 16;
        var segs = MathPlaneImplicitSampler.Segments(
            Obj($"curve s implicit \"x*y - {eps.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}\""), view);
        Assert.NotEmpty(segs);
        var nearOrigin = segs.Where(s => Math.Abs(s.A.X) < h && Math.Abs(s.A.Y) < h).ToList();
        Assert.NotEmpty(nearOrigin);
        foreach (var (a, b) in segs)
            Assert.True(Math.Sign(a.X) == Math.Sign(b.X) && Math.Sign(a.Y) == Math.Sign(b.Y),
                $"segment {a} -> {b} joins the hyperbola's two branches");
    }

    [Fact]
    public void YMinusTan_NoSegmentsAtThePoles()
    {
        var segs = MathPlaneImplicitSampler.Segments(Obj("curve t implicit \"y - tan(x)\""), View);
        Assert.NotEmpty(segs);
        foreach (var (a, b) in segs)
            foreach (var p in new[] { a, b })
            {
                Assert.InRange(p.Y - Math.Tan(p.X), -0.05, 0.05);
                double k = Math.Round((p.X - Math.PI / 2) / Math.PI);
                double toPole = Math.Abs(p.X - (Math.PI / 2 + k * Math.PI));
                // Near a pole the curve is steep but real; a pole segment would sit at
                // |y| well inside the view with x on the pole.
                Assert.False(toPole < View.Pitch && Math.Abs(p.Y) < 2.0, $"segment on the pole at {p}");
            }
    }

    [Fact]
    public void NoZeroSet_NoSegments()
        => Assert.Empty(MathPlaneImplicitSampler.Segments(Obj("curve c implicit \"x^2 + y^2 + 1\""), View));

    // ── Regions ──

    [Fact]
    public void RegionMask_EqualsAnIndependentSignTest()
    {
        var mask = MathPlaneImplicitSampler.RegionMask(Obj("region R \"y < sin(x) && x^2 + y^2 > 1\""), View, W, H)!;
        int diffs = 0, inside = 0;
        for (int py = 0; py < H; py++)
            for (int px = 0; px < W; px++)
            {
                double x = View.XMin + (px + 0.5) * View.Pitch, y = View.YMax - (py + 0.5) * View.Pitch;
                bool want = y < Math.Sin(x) && x * x + y * y > 1;
                if (want) inside++;
                if (mask[py * W + px] != want) diffs++;
            }
        Assert.Equal(0, diffs);
        Assert.InRange(inside, W * H / 5, W * H * 4 / 5);
    }

    [Fact]
    public void Region_ShadesExactlyItsPixels_InTheRender()
    {
        var calc = new MathPlaneCalculator(W, H)
        {
            Zoom = 0.5,
            FractalParameters = new FractalParameters { MathPlaneScene = "axes off; grid off; ticks off; region R \"x > 0\" #FFFFFF nolabel" },
        };
        calc.Calculate(CancellationToken.None);
        uint bg = MathPlaneCalculator.BackgroundColor;
        for (int px = 0; px < W; px += 7)
        {
            double x = -4 + (px + 0.5) * View.Pitch;
            uint p = calc.ColorBuffer[(H / 2) * W + px];
            Assert.Equal(x > 0, p != bg);
        }
        // 30 % white over the background.
        uint shaded = calc.ColorBuffer[(H / 2) * W + W - 1];
        int expect = (int)(((bg >> 16) & 0xFF) + (255 - ((bg >> 16) & 0xFF)) * MathPlaneCalculator.RegionAlpha);
        Assert.InRange((int)((shaded >> 16) & 0xFF), expect - 2, expect + 2);
    }

    // ── Editor ──

    [Fact]
    public void Editor_AddsImplicitAndRegion_ExpressionsEditable()
    {
        var view = new FractalViewState { FractalType = FractalType.MathPlane };
        view.FractalParameters.MathPlaneScene = "";
        var vm = new MathPlaneSectionViewModel(view, () => { }, () => { });
        vm.AddImplicitCommand.Execute().Subscribe();
        vm.AddRegionCommand.Execute().Subscribe();
        Assert.True(MathPlaneScene.TryParse(view.FractalParameters.MathPlaneScene, out var s, out var e), e?.ToString());
        Assert.Equal(MathPlaneCurveKind.Implicit, s.Objects[0].CurveKind);
        Assert.Equal(MathPlaneObjectKind.Region, s.Objects[1].Kind);

        Assert.Equal(1, vm.SelectedIndex);
        Assert.True(vm.SelectedIsCurve);
        vm.SelectedExpression = "abs(x) + abs(y) < 1";
        Assert.Null(vm.EditError);
        vm.SelectedExpression = "t < 1";
        Assert.Contains("reads only x, y", vm.EditError);
        vm.SelectedCoords = "0 1";
        Assert.Contains("no domain", vm.EditError);
        Assert.Contains("abs(x) + abs(y) < 1", view.FractalParameters.MathPlaneScene);
    }
}
