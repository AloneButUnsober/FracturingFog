// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1550 (Math Plane S3) — the plot language, curve objects, and the adaptive
// sampler. The sampler is checked against System.Math evaluated here, not
// against the plot language (which it uses), and against geometric invariants
// (chord deviation, poles, radius).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using FracturingFog;
using FracturingFog.Models;
using FracturingFog.UI.Avalonia.ViewModels;
using FracturingFog.ViewState;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class MathPlaneCurveTests
{
    private static readonly MathPlaneViewBox View = new(-4, 4, -2.25, 2.25, 8.0 / 1280);

    private static MathPlaneObject Curve(string stmt)
    {
        Assert.True(MathPlaneScene.TryParse(stmt, out var s, out var e), e?.ToString());
        return s.Objects.Single();
    }

    // ── Plot language ──

    [Fact]
    public void PlotLanguage_SharesThe2DFunctionTable_AndReadsRealVariables()
    {
        Assert.True(PlotExpression.TryParse("sin(x) + pow(t, 2) * atan2(theta, 1) - pi", out var p, out _));
        double want = Math.Sin(0.3) + Math.Pow(1.5, 2) * Math.Atan2(0.7, 1) - Math.PI;
        Assert.Equal(want, p!.Eval(x: 0.3, t: 1.5, theta: 0.7), 12);
        Assert.Equal(new[] { "x", "t", "theta" }, p.UsedVariables());
    }

    [Fact]
    public void PlotLanguage_ComplexResultIsAGap()
    {
        Assert.True(PlotExpression.TryParse("sqrt(x)", out var p, out _));
        Assert.Equal(2.0, p!.Eval(x: 4), 12);
        Assert.True(double.IsNaN(p.Eval(x: -1)));
    }

    [Fact]
    public void PlotLanguage_StatementsAndLetWork()
    {
        Assert.True(PlotExpression.TryParse("a = x * x; return a + 1", out var p, out var err), err);
        Assert.Equal(10.0, p!.Eval(x: 3), 12);
        Assert.True(PlotExpression.TryParse("let u = x + 1 in u * u", out var q, out _));
        Assert.Equal(16.0, q!.Eval(x: 3), 12);
    }

    [Theory]
    [InlineData("sin(q)", "Did you mean")]
    [InlineData("sinn(x)", "Unknown function")]
    [InlineData("pow(x)", "takes 2 arg")]
    [InlineData("", "empty")]
    public void PlotLanguage_Errors(string src, string why)
    {
        Assert.False(PlotExpression.TryParse(src, out _, out var err));
        Assert.Contains(why, err);
    }

    // ── Scene format ──

    [Fact]
    public void Curves_RoundTrip_IncludingSemicolonsInsideQuotes()
    {
        const string text = "curve f explicit \"a = x * x; return a - 1\" -2 2 #E69F00; " +
                            "curve c parametric \"cos(t)\" \"sin(t)\" 0 6.25 nolabel; curve r polar \"1 + cos(theta)\"; point A 1 1";
        Assert.True(MathPlaneScene.TryParse(text, out var s, out var e), e?.ToString());
        Assert.Equal(4, s.Objects.Count);
        Assert.Equal("a = x * x; return a - 1", s.Objects[0].Expressions.Single());
        Assert.Equal((-2.0, 2.0), s.Objects[0].Domain);
        Assert.Equal(MathPlaneCurveKind.Parametric, s.Objects[1].CurveKind);
        Assert.Null(s.Objects[2].Domain);
        Assert.True(MathPlaneScene.TryParse(s.Serialize(), out var back, out _));
        Assert.Equal(s, back);
    }

    [Theory]
    [InlineData("curve f explicit \"sin(t)\"", "reads only x, not t")]
    [InlineData("curve f polar \"x\"", "reads only theta, not x")]
    [InlineData("curve f explicit sin(x)", "needs a quoted expression")]
    [InlineData("curve f explicit \"sin(x)", "unterminated quote")]
    [InlineData("curve f parametric \"cos(t)\" \"sin(t)\"", "needs tmin tmax")]
    [InlineData("curve f parametric \"cos(t)\"", "two quoted expressions")]
    [InlineData("curve f explicit \"sin(x)\" 2 1", "max must be above")]
    [InlineData("curve f explicit \"sin(x)\" 1", "needs min and max")]
    [InlineData("curve f spiral \"x\"", "unknown curve kind")]
    [InlineData("curve f explicit \"sinn(x)\"", "expression: Unknown function")]
    public void CurveErrors(string text, string why)
    {
        Assert.False(MathPlaneScene.TryParse(text, out _, out var e));
        Assert.Contains(why, e!.Message);
    }

    // ── Sampler ──

    [Fact]
    public void Explicit_EverySegmentStaysWithinTolerance_OfTheTrueCurve()
    {
        var lines = MathPlaneCurveSampler.Sample(Curve("curve f explicit \"sin(3*x)\""), View);
        Assert.Single(lines);
        var pts = lines[0];
        Assert.True(pts[0].X <= View.XMin && pts[^1].X >= View.XMax, "spans the view");
        for (int i = 0; i < pts.Count; i++)
            Assert.Equal(Math.Sin(3 * pts[i].X), pts[i].Y, 12);
        // The straight segment between samples stays within 1 px of the curve.
        for (int i = 1; i < pts.Count; i++)
        {
            double xm = 0.5 * (pts[i - 1].X + pts[i].X), chord = 0.5 * (pts[i - 1].Y + pts[i].Y);
            Assert.True(Math.Abs(Math.Sin(3 * xm) - chord) < View.Pitch, $"segment {i} deviates");
        }
    }

    [Fact]
    public void Tan_NoSegmentCrossesAPole()
    {
        var lines = MathPlaneCurveSampler.Sample(Curve("curve g explicit \"tan(x)\""), View);
        Assert.True(lines.Count >= 3, $"expected a branch per pole interval, got {lines.Count}");
        foreach (var line in lines)
            for (int i = 1; i < line.Count; i++)
            {
                double a = line[i - 1].X, b = line[i].X;
                double k = Math.Ceiling((a - Math.PI / 2) / Math.PI);   // first pole at or after a
                double pole = Math.PI / 2 + k * Math.PI;
                Assert.False(pole > a && pole < b, $"segment {a}..{b} crosses the pole at {pole}");
            }
    }

    [Fact]
    public void ReciprocalWithoutDomain_NoConnectorThroughZero()
    {
        var lines = MathPlaneCurveSampler.Sample(Curve("curve k explicit \"1/x\""), View);
        foreach (var line in lines)
            for (int i = 1; i < line.Count; i++)
                Assert.False(line[i - 1].X < 0 && line[i].X > 0, "drew across x = 0");
        Assert.Contains(lines, l => l.All(p => p.X < 0));
        Assert.Contains(lines, l => l.All(p => p.X > 0));
    }

    [Fact]
    public void Sqrt_StartsAtItsDomainEdge()
    {
        var lines = MathPlaneCurveSampler.Sample(Curve("curve h explicit \"sqrt(x)\""), View);
        Assert.Single(lines);
        Assert.InRange(lines[0][0].X, 0.0, 2 * View.Pitch);
    }

    [Fact]
    public void PolarUnitCircle_HasRadiusOne_AndCloses()
    {
        var lines = MathPlaneCurveSampler.Sample(Curve("curve r polar \"1\""), View);
        var pts = lines.Single();
        Assert.All(pts, p => Assert.Equal(1.0, Math.Sqrt(p.X * p.X + p.Y * p.Y), 12));
        Assert.Equal(pts[0].X, pts[^1].X, 9);
        Assert.Equal(pts[0].Y, pts[^1].Y, 9);
    }

    [Fact]
    public void Parametric_HonoursItsRange()
    {
        var pts = MathPlaneCurveSampler.Sample(Curve("curve c parametric \"2*cos(t)\" \"sin(t)\" 0 3.141592653589793"), View).Single();
        Assert.Equal((2.0, 0.0), (pts[0].X, pts[0].Y));
        Assert.Equal(-2.0, pts[^1].X, 12);
        Assert.All(pts, p => Assert.True(p.Y >= -1e-12));   // upper half only
        Assert.All(pts, p => Assert.Equal(1.0, p.X * p.X / 4 + p.Y * p.Y, 9));
    }

    [Fact]
    public void ExplicitDomain_ClipsTheCurve()
    {
        var pts = MathPlaneCurveSampler.Sample(Curve("curve f explicit \"x\" -1 0.5"), View).Single();
        Assert.Equal(-1.0, pts[0].X, 12);
        Assert.Equal(0.5, pts[^1].X, 12);
    }

    [Fact]
    public void OffViewCurve_StaysCheap()
    {
        var c = Curve("curve f explicit \"x + 1000\"");
        Assert.Empty(MathPlaneCurveSampler.Sample(c, View).Where(l => l.Any(p => p.Y < 10)));
    }

    // ── Rendering + editor ──

    [Fact]
    public void Render_DrawsTheCurveWhereSinIs()
    {
        const int W = 640, H = 360;
        var calc = new MathPlaneCalculator(W, H)
        {
            Zoom = 0.5,
            FractalParameters = new FractalParameters { MathPlaneScene = "axes off; grid off; ticks off; curve f explicit \"sin(x)\" #FF00FF nolabel" },
        };
        calc.Calculate(CancellationToken.None);
        foreach (double x in new[] { -2.0, 0.5, 1.5707963, 3.0 })
        {
            var (px, py) = MathPlaneCalculator.ToPixel(x, Math.Sin(x), W, H, 0, 0, 0.5);
            int col = (int)px, row = (int)py;
            bool near = Enumerable.Range(row - 2, 5).Any(r => r >= 0 && r < H && Magentaish(calc.ColorBuffer[r * W + col]));
            Assert.True(near, $"no curve pixel near x = {x}");
        }
    }

    private static bool Magentaish(uint p) => ((p >> 16) & 0xFF) > 150 && ((p >> 8) & 0xFF) < 90 && (p & 0xFF) > 150;

    [Fact]
    public void AddCurve_StartersParse_AndHaveNoHandles()
    {
        var s = new MathPlaneScene();
        foreach (var k in new[] { MathPlaneCurveKind.Explicit, MathPlaneCurveKind.Parametric, MathPlaneCurveKind.Polar })
            s = MathPlaneSceneEdit.AddCurve(s, k).Scene;
        Assert.Equal(new[] { "f", "g", "h" }, s.Objects.Select(o => o.Name));
        Assert.True(MathPlaneScene.TryParse(s.Serialize(), out var back, out var e), e?.ToString());
        Assert.Equal(s, back);
        Assert.All(s.Objects, o => Assert.Equal(0, MathPlaneSceneEdit.HandleCount(o)));
    }

    [Fact]
    public void Editor_ExpressionEdits_ApplyOrAreRefused()
    {
        var view = new FractalViewState { FractalType = FractalType.MathPlane };
        view.FractalParameters.MathPlaneScene = "curve f explicit \"sin(x)\"";
        var vm = new MathPlaneSectionViewModel(view, () => { }, () => { });
        vm.SelectedIndex = 0;
        Assert.True(vm.SelectedIsCurve);
        vm.SelectedExpression = "x^2 - 1";
        Assert.Null(vm.EditError);
        Assert.Contains("\"x^2 - 1\"", view.FractalParameters.MathPlaneScene);

        vm.SelectedExpression = "t";                // wrong variable for explicit
        Assert.Contains("reads only x", vm.EditError);
        Assert.Contains("\"x^2 - 1\"", view.FractalParameters.MathPlaneScene);

        vm.SelectedCoords = "-1 1";
        Assert.Contains("-1 1", view.FractalParameters.MathPlaneScene);
        vm.SelectedCoords = "1 -1";
        Assert.NotNull(vm.EditError);
        Assert.Equal("f: y = x^2 - 1, -1..1", MathPlaneSectionViewModel.Readout(MathPlaneSceneEditTestsAccess(view)));
    }

    private static MathPlaneObject MathPlaneSceneEditTestsAccess(FractalViewState v)
    {
        Assert.True(MathPlaneScene.TryParse(v.FractalParameters.MathPlaneScene, out var s, out _));
        return s.Objects[0];
    }
}
