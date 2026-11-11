// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1696 (scripted scenes SS2) — reveal actions. Checked against independent
// renders: an orbit / cobweb revealed to a whole k equals the scene written with
// k steps; a half-faded phase portrait is the average of off and on; a finished
// draw equals the plain object; the drawn part only grows.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using FracturingFog;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class MathPlaneScriptRevealTests
{
    private const int W = 240, H = 160;

    private static FractalParameters Params(string scene, double s = 0, double cRe = -1, double cIm = 0) => new()
    {
        MathPlaneScene = scene,
        MathPlaneCRe = cRe, MathPlaneCIm = cIm,
        MathPlaneScriptTime = s,
    };

    private static uint[] Render(FractalParameters p)
    {
        var calc = new MathPlaneCalculator(W, H) { CenterX = 0, CenterY = 0, Zoom = 0.3, MaxIterations = 64, FractalParameters = p };
        calc.Calculate(CancellationToken.None);
        Assert.Null(calc.LastSceneError);
        return (uint[])calc.ColorBuffer.Clone();
    }

    private static int Diff(uint[] a, uint[] b) => a.Zip(b).Count(x => x.First != x.Second);

    // ── s = 0 and the end ───────────────────────────────────────────────

    private const string Base =
        "v1; map \"z^2 + c\"; cobweb 0.1 1; curve f explicit \"sin(x)\" #E69F00 hidden; segment s -1 -1 1 -1 #56B4E9 hidden; "
        + "polyline q -2 1 -1 2 0 1 #009E73 hidden; vector v 1 1 hidden";
    private const string Script =
        "; step 1 caption \"Draw\"; step 2 draw f; with draw s; with draw q; with draw v; step 2 orbit 12; with cobweb 8; "
        + "step 1 phase on; step 0.5 caption \"\"";

    [Fact]
    public void AtTimeZero_ItIsTheSceneWithItsStepsRemoved()
    {
        Assert.Equal(Render(Params(Base)), Render(Params(Base + Script, 0)));
    }

    [Theory]
    [InlineData(6.5)]
    [InlineData(20)]
    public void AtTheEnd_ItIsAHandWrittenFinalScene(double s)
    {
        const string final =
            "v1; map \"z^2 + c\"; cobweb 0.1 8; phase on; orbit 12; curve f explicit \"sin(x)\" #E69F00; segment s -1 -1 1 -1 #56B4E9; "
            + "polyline q -2 1 -1 2 0 1 #009E73; vector v 1 1";
        Assert.Equal(Render(Params(final)), Render(Params(Base + Script, s)));
    }

    // ── Orbit and cobweb ─────────────────────────────────────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    public void AnOrbitRevealedToAWholeStep_IsTheOrbitWithThatManySteps(int k)
    {
        // Linear reveal 0 -> 10 over 10 s: at s = k exactly k steps.
        string scripted = "v1; point A 0 0; step 10 ease none orbit 10";
        Assert.Equal(Render(Params($"v1; point A 0 0; orbit {k}", cRe: -0.12, cIm: 0.75)),
                     Render(Params(scripted, k, -0.12, 0.75)));
    }

    [Fact]
    public void BetweenSteps_TheNewestOrbitStepIsPartlyDrawn()
    {
        string scripted = "v1; point A 0 0; step 10 ease none orbit 10";
        var two = Render(Params("v1; point A 0 0; orbit 2", cRe: -0.12, cIm: 0.75));
        var three = Render(Params("v1; point A 0 0; orbit 3", cRe: -0.12, cIm: 0.75));
        var mid = Render(Params(scripted, 2.5, -0.12, 0.75));
        Assert.NotEqual(two, mid);
        Assert.NotEqual(three, mid);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void ACobwebRevealedToAWholeStep_IsTheCobwebWithThatManySteps(int k)
    {
        string scripted = "v1; cobweb 0.1 1; step 8 ease none cobweb 9";   // k = 1 + s
        Assert.Equal(Render(Params($"v1; cobweb 0.1 {k}", cRe: -1.3)),
                     Render(Params(scripted, k - 1, -1.3)));
    }

    [Fact]
    public void BetweenSteps_TheNewestCobwebStepIsOnlyFaded()
    {
        // k = 2.5: steps 1-2 solid, step 3 at half alpha. Solid staircase pixels (the
        // exact colour) are then those of the two-step cobweb; a faded step adds none.
        int Stair(uint[] b) => b.Count(c => c == MathPlaneCalculator.CobwebStairColor);
        var two = Render(Params("v1; cobweb 0.1 2", cRe: -1.3));
        var three = Render(Params("v1; cobweb 0.1 3", cRe: -1.3));
        var mid = Render(Params("v1; cobweb 0.1 1; step 8 ease none cobweb 9", 1.5, -1.3));
        Assert.True(Stair(three) > Stair(two) + 10, "step 3 adds solid pixels when fully shown");
        Assert.InRange(Stair(mid), Stair(two) - 3, Stair(two) + 3);
        Assert.NotEqual(two, mid);
    }

    [Fact]
    public void ACobwebRevealedToZero_ShowsOnlyTheCurveAndDiagonal()
    {
        var zero = Render(Params("v1; cobweb 0.1 5; step 1 ease none cobweb 0", 1, -1.3));
        var five = Render(Params("v1; cobweb 0.1 5", cRe: -1.3));
        Assert.NotEqual(five, zero);
        // The staircase (Okabe-Ito blue) is gone; only the x0 marker keeps its colour.
        int Stair(uint[] b) => b.Count(c => c == MathPlaneCalculator.CobwebStairColor);
        Assert.True(Stair(zero) * 3 < Stair(five), $"{Stair(zero)} vs {Stair(five)}");
    }

    // ── Phase ────────────────────────────────────────────────────────────

    [Fact]
    public void AHalfFadedPhasePortrait_IsTheAverageOfOffAndOn()
    {
        string scene = "v1; map \"z^2 + c\"; axes off; grid off; ticks off";
        var off = Render(Params(scene));
        var on = Render(Params(scene + "; phase on"));
        var half = Render(Params(scene + "; step 2 ease none phase on", 1));
        for (int i = 0; i < half.Length; i++)
            for (int sh = 0; sh <= 16; sh += 8)
            {
                int a = (int)(off[i] >> sh) & 0xFF, b = (int)(on[i] >> sh) & 0xFF, m = (int)(half[i] >> sh) & 0xFF;
                Assert.InRange(m, (a + b) / 2 - 1, (a + b + 1) / 2 + 1);
            }
    }

    // ── Draw ─────────────────────────────────────────────────────────────

    [Fact]
    public void ADrawOnlyGrows_AndEndsAsThePlainObject()
    {
        const string plain = "v1; axes off; grid off; ticks off; curve f parametric \"cos(t)\" \"sin(t)\" 0 6.283185307179586 #E69F00 nolabel";
        string scripted = plain.Replace("nolabel", "nolabel hidden") + "; step 4 ease none draw f";
        var empty = Render(Params(plain.Replace("nolabel", "nolabel hidden")));
        int prev = 0;
        foreach (double s in new[] { 0.5, 1, 2, 3, 3.9 })
        {
            int drawn = Diff(empty, Render(Params(scripted, s)));
            Assert.True(drawn > prev, $"s = {s}: {drawn} pixels after {prev}");
            prev = drawn;
        }
        Assert.Equal(Render(Params(plain)), Render(Params(scripted, 4)));
    }

    [Fact]
    public void TruncateLines_KeepsThatFractionOfTheLength_InOrder()
    {
        var lines = new List<List<(double X, double Y)>>
        {
            new() { (0, 0), (3, 0), (3, 4) },     // length 7
            new() { (10, 0), (10, 3) },           // length 3
        };
        double Len(List<List<(double X, double Y)>> ls) =>
            ls.Sum(l => l.Zip(l.Skip(1)).Sum(p => Math.Sqrt(Math.Pow(p.Second.X - p.First.X, 2) + Math.Pow(p.Second.Y - p.First.Y, 2))));
        foreach (double f in new[] { 0.05, 0.3, 0.5, 0.7, 0.75, 0.95 })
        {
            var t = MathPlaneCalculator.TruncateLines(lines, f);
            Assert.Equal(10 * f, Len(t), 9);
            Assert.Equal((0.0, 0.0), t[0][0]);   // always the start
        }
        Assert.Equal((3.0, 2.0), MathPlaneCalculator.TruncateLines(lines, 0.5)[0][^1]);
        Assert.Empty(MathPlaneCalculator.TruncateLines(lines, 0));
        Assert.Same(lines, MathPlaneCalculator.TruncateLines(lines, 1));
    }

    [Fact]
    public void ADrawnVisibleObject_IsWholeUntilTheDrawBegins()
    {
        string scene = "v1; segment s -1 0 1 0; step 1 wait; step 2 draw s";
        var whole = Render(Params("v1; segment s -1 0 1 0"));
        Assert.Equal(whole, Render(Params(scene, 1)));          // at the draw's start
        Assert.NotEqual(whole, Render(Params(scene, 1.5)));     // part-drawn
        Assert.Equal(whole, Render(Params(scene, 3)));
    }

    // ── Caption ──────────────────────────────────────────────────────────

    [Fact]
    public void ACaption_DrawsOnlyWhileShown_AndFadesWithItsAlpha()
    {
        const string blank = "v1; axes off; grid off; ticks off";
        string scene = blank + "; step 2 ease none caption \"Period doubling\"; step 2 ease none caption \"\"";
        var none = Render(Params(blank));
        Assert.Equal(none, Render(Params(scene, 0)));
        int half = Diff(none, Render(Params(scene, 1)));
        int full = Diff(none, Render(Params(scene, 2)));
        Assert.True(full > 0 && half > 0);
        Assert.Equal(none, Render(Params(scene, 4)));
        // A brighter caption: the text pixels at full alpha are lighter than at half.
        var f = Render(Params(scene, 2)); var hlf = Render(Params(scene, 1));
        long Sum(uint[] b) => b.Sum(c => (long)((c >> 16) & 0xFF) + ((c >> 8) & 0xFF) + (c & 0xFF));
        Assert.True(Sum(f) != Sum(hlf));
    }

    [Fact]
    public void Captions_CrossFade()
    {
        var state = MathPlaneScript.Evaluate(
            Parse("v1; step 1 caption \"A\"; step 2 ease none caption \"B\""), new FractalParameters(), 2);
        var r = state.Reveal!;
        Assert.Equal("B", r.Caption);
        Assert.Equal(0.5, r.CaptionAlpha, 12);
        Assert.Equal("A", r.PreviousCaption);
        Assert.Equal(0.5, r.PreviousCaptionAlpha, 12);
    }

    // ── Parse ────────────────────────────────────────────────────────────

    private static MathPlaneScene Parse(string text)
    {
        Assert.True(MathPlaneScene.TryParse(text, out var s, out var e), e?.ToString());
        return s;
    }

    [Theory]
    [InlineData("v1; point p 1 1; step 1 draw p", "use 'show'")]
    [InlineData("v1; region r \"x < 0\"; step 1 draw r", "use 'show'")]
    [InlineData("v1; curve c implicit \"x^2 + y^2 - 1\"; step 1 draw c", "use 'show'")]
    [InlineData("v1; step 1 phase on", "map")]
    [InlineData("v1; step 1 cobweb 5", "cobweb 0.1")]
    [InlineData("v1; step 1 orbit -1", "step count")]
    [InlineData("v1; step 1 orbit 99999", "step count")]
    [InlineData("v1; map \"z\"; step 1 phase dim", "on, contours or off")]
    [InlineData("v1; step 1 caption hello", "quoted text")]
    [InlineData("v1; segment s 0 0 1 1; step 2 draw s; with 1 hide s", "already animated")]
    [InlineData("v1; step 2 caption \"a\"; with 1 caption \"b\"", "already animated")]
    public void BadRevealScripts_AreErrors(string text, string expect)
    {
        Assert.False(MathPlaneScene.TryParse(text, out _, out var err));
        Assert.Contains(expect, err!.Message);
    }

    [Fact]
    public void ALongCaption_IsAnError()
    {
        Assert.False(MathPlaneScene.TryParse("v1; step 1 caption \"" + new string('a', MathPlaneScene.MaxCaptionLength + 1) + "\"", out _, out var err));
        Assert.Contains("at most", err!.Message);
    }

    [Fact]
    public void RevealActions_RoundTripThroughTheText()
    {
        var scene = Parse(Base + Script + "; step 1 phase contours; step 1 phase off; step 1 caption \"two\\nlines\"");
        Assert.False(scene.Equals(null));
        var again = Parse(scene.Serialize());
        Assert.Equal(scene, again);
        Assert.Equal(scene.Serialize(), again.Serialize());
    }
}
