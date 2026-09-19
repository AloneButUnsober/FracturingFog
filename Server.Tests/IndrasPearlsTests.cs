// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Collections.Generic;
using System.Numerics;
using FracturingFog;
using FracturingFog.Abstractions.Animation;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

// Indra's Pearls 2D — slice S1 (#892). Möbius primitive, group builders,
// point-cloud plotter, type registration.
public sealed class IndrasPearlsTests
{
    private readonly ITestOutputHelper _out;
    public IndrasPearlsTests(ITestOutputHelper o) => _out = o;

    private static bool Close(Complex a, Complex b, double eps = 1e-9)
        => (a - b).Magnitude < eps;

    // ── Mobius primitive ─────────────────────────────────────────────────────

    [Fact]
    public void Identity_Applies_AsNoOp()
    {
        Assert.True(Mobius.Identity.TryApply(new Complex(3, -2), out var z));
        Assert.True(Close(z, new Complex(3, -2)));
    }

    [Fact]
    public void Multiply_Composes_InApplyOrder()
    {
        // (m ∘ n)(z) == m(n(z)).
        var m = new Mobius(new(2, 0), new(1, 0), new(0, 1), new(1, 0));
        var n = new Mobius(new(1, 1), new(0, 0), new(0, 0), new(1, -1));
        var z = new Complex(0.3, 0.7);
        var mn = m.Multiply(n);
        Assert.True(mn.TryApply(z, out var lhs));
        Assert.True(n.TryApply(z, out var nz));
        Assert.True(m.TryApply(nz, out var rhs));
        Assert.True(Close(lhs, rhs));
    }

    [Fact]
    public void Inverse_IsLeftAndRightInverseMap()
    {
        var m = new Mobius(new(2, 0), new(1, 0), new(1, 0), new(1, 0)); // det = 1
        var mi = m.Inverse();
        var z = new Complex(0.4, -0.9);
        Assert.True(m.TryApply(z, out var mz));
        Assert.True(mi.TryApply(mz, out var back));
        Assert.True(Close(back, z, 1e-8));
    }

    [Fact]
    public void Normalized_MakesDeterminantOne()
    {
        // Maskit a = [[2i,1],[1,0]] has det = -1.
        var a = new Mobius(new(0, 2), Complex.One, Complex.One, Complex.Zero);
        var n = a.Normalized();
        Assert.True(Close(n.Determinant, Complex.One, 1e-9));
        // Same Möbius action (an overall scalar is a no-op).
        var z = new Complex(0.5, 0.2);
        Assert.True(a.TryApply(z, out var za));
        Assert.True(n.TryApply(z, out var zn));
        Assert.True(Close(za, zn, 1e-8));
    }

    [Fact]
    public void FixedPoint_IsActuallyFixed()
    {
        var m = new Mobius(new(2, 0), new(1, 0), new(1, 0), new(0, 0)); // z -> 2 + 1/z
        foreach (var fp in m.FixedPoints())
        {
            Assert.True(m.TryApply(fp, out var img));
            Assert.True(Close(img, fp, 1e-7));
        }
    }

    // ── Group builders ───────────────────────────────────────────────────────

    [Fact]
    public void Maskit_A_IsParabolic_AtMu2i()
    {
        var g = IndrasGroup.Maskit(new Complex(0, 2));
        // Normalised (det = 1) trace of a; |tr| = 2 ⇒ parabolic.
        var a = g.Letters[0];
        Complex tr = a.A + a.D;
        Assert.Equal(2.0, tr.Magnitude, 6);
    }

    [Fact]
    public void Grandma_Generators_HaveRequestedTraces()
    {
        var ta = new Complex(1.87, 0.1);
        var tb = new Complex(1.87, -0.1);
        var g = IndrasGroup.Grandma(ta, tb);
        Complex trA = g.Letters[0].A + g.Letters[0].D;
        Complex trB = g.Letters[2].A + g.Letters[2].D;
        Assert.True(Close(trA, ta, 1e-6), $"tr(a)={trA} expected {ta}");
        Assert.True(Close(trB, tb, 1e-6), $"tr(b)={trB} expected {tb}");
    }

    [Fact]
    public void Grandma_Traces_SatisfyMarkovIdentity()
    {
        // The internal tab solves ta² + tb² + tab² = ta·tb·tab. Recover tab as
        // tr(ab) from the built (normalised) generators and check the identity.
        var ta = new Complex(2.4, 0.0);
        var tb = new Complex(2.37, 0.34);
        var g = IndrasGroup.Grandma(ta, tb);
        var ab = g.Letters[0].Multiply(g.Letters[2]);
        Complex tab = ab.A + ab.D;
        Complex lhs = ta * ta + tb * tb + tab * tab;
        Complex rhs = ta * tb * tab;
        Assert.True(Close(lhs, rhs, 1e-6), $"Markov lhs={lhs} rhs={rhs}");
    }

    [Fact]
    public void InverseLetter_Pairs_ByXor1()
    {
        Assert.Equal(1, IndrasGroup.InverseLetter(0));
        Assert.Equal(0, IndrasGroup.InverseLetter(1));
        Assert.Equal(3, IndrasGroup.InverseLetter(2));
        Assert.Equal(2, IndrasGroup.InverseLetter(3));
    }

    [Fact]
    public void SeedPoints_LieOnLimitSet_AreFinite()
    {
        var g = IndrasGroup.Maskit(new Complex(0, 2));
        var seeds = g.SeedPoints();
        Assert.NotEmpty(seeds);
        foreach (var s in seeds)
        {
            Assert.False(double.IsNaN(s.Real) || double.IsNaN(s.Imaginary));
            Assert.False(double.IsInfinity(s.Real) || double.IsInfinity(s.Imaginary));
        }
    }

    // ── Calculator ───────────────────────────────────────────────────────────

    [Fact]
    public void Calculate_PlotsNonEmpty_AtDefaultFraming()
    {
        var calc = new IndrasPearlsCalculator(256, 256)
        {
            CenterX = 0.0, CenterY = 1.0, Zoom = 0.6,
        };
        calc.Calculate();
        int nonZero = 0;
        foreach (var px in calc.ColorBuffer) if (px != 0) nonZero++;
        Assert.True(nonZero > 200, $"only {nonZero} pixels plotted");
    }

    [Fact]
    public void Calculate_IsDeterministic()
    {
        var a = new IndrasPearlsCalculator(200, 200) { CenterX = 0, CenterY = 1, Zoom = 0.6 };
        var b = new IndrasPearlsCalculator(200, 200) { CenterX = 0, CenterY = 1, Zoom = 0.6 };
        a.Calculate();
        b.Calculate();
        Assert.Equal(a.ColorBuffer, b.ColorBuffer);
    }

    [Fact]
    public void Calculate_RespondsToPan()
    {
        var a = new IndrasPearlsCalculator(200, 200) { CenterX = 0, CenterY = 1, Zoom = 0.6 };
        var b = new IndrasPearlsCalculator(200, 200) { CenterX = 5, CenterY = 1, Zoom = 0.6 };
        a.Calculate();
        b.Calculate();
        Assert.NotEqual(a.ColorBuffer, b.ColorBuffer);
    }

    [Fact]
    public void SupportsZoomPan_IsTrue()
        => Assert.True(new IndrasPearlsCalculator(8, 8).SupportsZoomPan);

    // ── Registration ─────────────────────────────────────────────────────────

    [Fact]
    public void MotionClass_IsZoomable2D()
        => Assert.Equal(FractalMotionClass.Zoomable2D,
                        FractalMotionCapabilities.MotionClass(FractalType.IndrasPearls));

    [Fact]
    public void Capabilities_SuppliesHistogramOnly()
        => Assert.Equal(FractalCapabilities.SuppliesHistogram,
                        FractalCapabilityMap.For(FractalType.IndrasPearls));

    [Fact]
    public void HasDisplayName()
        => Assert.Equal("Indra's Pearls", Fractals.FractalNameByNameType[FractalType.IndrasPearls]);

    // ── S5 colour drivers (#896) ─────────────────────────────────────────────

    private static uint[] RenderWithSource(IndrasColorSource src, IndrasRenderMode mode)
    {
        var p = new FractalParameters { IndrasFamily = IndrasGroupFamily.Maskit, IndrasMaskitMuIm = 2.0,
            IndrasColorSource = src, IndrasRenderMode = mode };
        var c = new IndrasPearlsCalculator(200, 200) { CenterX = 0, CenterY = 1, Zoom = 0.6, FractalParameters = p };
        c.Calculate();
        return (uint[])c.ColorBuffer.Clone();
    }

    [Theory]
    [InlineData(IndrasColorSource.WordLength)]
    [InlineData(IndrasColorSource.LastGenerator)]
    [InlineData(IndrasColorSource.Parity)]
    public void ColorSource_DiffersFromDensity_PointCloud(IndrasColorSource src)
    {
        var density = RenderWithSource(IndrasColorSource.Density, IndrasRenderMode.PointCloud);
        var other = RenderWithSource(src, IndrasRenderMode.PointCloud);
        Assert.NotEqual(density, other);
    }

    [Fact]
    public void ColorSource_LastGeneratorDiffersFromWordLength_PointCloud()
    {
        var wl = RenderWithSource(IndrasColorSource.WordLength, IndrasRenderMode.PointCloud);
        var lg = RenderWithSource(IndrasColorSource.LastGenerator, IndrasRenderMode.PointCloud);
        Assert.NotEqual(wl, lg);
    }

    [Fact]
    public void ColorSource_LastGeneratorDiffersFromDensity_CurveTrace()
    {
        var density = RenderWithSource(IndrasColorSource.Density, IndrasRenderMode.CurveTrace);
        var lg = RenderWithSource(IndrasColorSource.LastGenerator, IndrasRenderMode.CurveTrace);
        Assert.NotEqual(density, lg);
    }

    [Fact]
    public void ColorSource_ClonesAndPersists()
    {
        var p = new FractalParameters { IndrasColorSource = IndrasColorSource.LastGenerator };
        Assert.Equal(IndrasColorSource.LastGenerator, p.Clone().IndrasColorSource);

        var pp = new FractalParameters { IndrasFamily = IndrasGroupFamily.Maskit, IndrasColorSource = IndrasColorSource.Parity };
        var snap = RegionFractalParams.Snapshot(FractalType.IndrasPearls, pp);
        var restored = new FractalParameters();
        snap!.ApplyTo(restored);
        Assert.Equal(IndrasColorSource.Parity, restored.IndrasColorSource);
    }

    // ── S4 marquee animation (#895) ──────────────────────────────────────────

    [Fact]
    public void AnimatableMap_ExposesMaskitMu()
    {
        var names = new HashSet<string>();
        foreach (var d in FracturingFog.Abstractions.Animation.FractalAnimatableParamsMap.For(FractalType.IndrasPearls))
            names.Add(d.ParamName);
        Assert.Contains("IndrasMaskitMuRe", names);
        Assert.Contains("IndrasMaskitMuIm", names);
        Assert.Contains("IndrasGrandmaTaRe", names);
    }

    [Fact]
    public void Animator_DrivesMaskitMu()
    {
        var data = new FracturingFog.Abstractions.Animation.AnimationData
        {
            Name = "t",
            Tracks = new System.Collections.Generic.List<FracturingFog.Abstractions.Animation.AnimationTrack>
            {
                new() { ParamName = "IndrasMaskitMuRe",
                        Mode = FracturingFog.Abstractions.Animation.AnimationMode.Triangle,
                        Min = -1.0, Max = 1.0, FrequencyHz = 0.5, Enabled = true },
            },
        };
        var p = new FractalParameters { IndrasMaskitMuRe = 99.0 };   // outside [-1,1]
        var animators = new System.Collections.Generic.List<IParameterAnimator>(data.ToAnimators(p));
        Assert.NotEmpty(animators);
        foreach (var a in animators) a.Tick(0.3);
        // The reflection setter pulled it from the out-of-range sentinel into the
        // track's [Min, Max] band — proves the animator drives the property.
        Assert.True(p.IndrasMaskitMuRe >= -1.0 && p.IndrasMaskitMuRe <= 1.0,
            $"μRe = {p.IndrasMaskitMuRe}");
    }

    // ── S3 curve tracer (#894) ───────────────────────────────────────────────

    [Fact]
    public void RepellingFixedPoint_IsFixed()
    {
        var m = new Mobius(new(2, 0), new(1, 0), new(1, 0), new(0, 0)); // z -> 2 + 1/z
        var fp = m.RepellingFixedPoint();
        Assert.True(m.TryApply(fp, out var img));
        Assert.True(Close(img, fp, 1e-7), $"fp={fp} img={img}");
    }

    private static IndrasPearlsCalculator MakeCalc(int w, int h, FractalParameters p, double cx, double cy, double z)
        => new(w, h) { CenterX = cx, CenterY = cy, Zoom = z, FractalParameters = p };

    [Fact]
    public void CurveTrace_PlotsNonEmpty_ForAppleGroup()
    {
        var p = new FractalParameters { IndrasFamily = IndrasGroupFamily.Maskit, IndrasMaskitMuIm = 2.0, IndrasRenderMode = IndrasRenderMode.CurveTrace };
        var calc = MakeCalc(256, 256, p, 0, 1, 0.6);
        calc.Calculate();
        int nonZero = 0;
        foreach (var px in calc.ColorBuffer) if (px != 0) nonZero++;
        Assert.True(nonZero > 200, $"only {nonZero} pixels");
    }

    [Fact]
    public void CurveTrace_DiffersFromPointCloud()
    {
        var cloud = new FractalParameters { IndrasFamily = IndrasGroupFamily.GrandmaRecipe, IndrasRenderMode = IndrasRenderMode.PointCloud };
        var curve = new FractalParameters { IndrasFamily = IndrasGroupFamily.GrandmaRecipe, IndrasRenderMode = IndrasRenderMode.CurveTrace };
        var a = MakeCalc(220, 220, cloud, 0, 0, 1.0);
        var b = MakeCalc(220, 220, curve, 0, 0, 1.0);
        a.Calculate();
        b.Calculate();
        Assert.NotEqual(a.ColorBuffer, b.ColorBuffer);
    }

    [Fact]
    public void CurveTrace_IsDeterministic()
    {
        var p = new FractalParameters { IndrasFamily = IndrasGroupFamily.GrandmaRecipe, IndrasRenderMode = IndrasRenderMode.CurveTrace };
        var a = MakeCalc(200, 200, p, 0, 0, 1.0);
        var b = MakeCalc(200, 200, p, 0, 0, 1.0);
        a.Calculate();
        b.Calculate();
        Assert.Equal(a.ColorBuffer, b.ColorBuffer);
    }

    [Fact]
    public void CurveTrace_CantorGroup_FallsBackToPoints_StillPlots()
    {
        // A Cantor (disconnected) group never converges to a curve; the tracer
        // plots the fixed points at the depth cap instead of aborting — output
        // is still non-empty.
        var p = new FractalParameters { IndrasFamily = IndrasGroupFamily.GrandmaRecipe,
            IndrasGrandmaTaRe = 2.5, IndrasGrandmaTaIm = 1.5, IndrasGrandmaTbRe = 2.5, IndrasGrandmaTbIm = -1.5,
            IndrasRenderMode = IndrasRenderMode.CurveTrace };
        var calc = MakeCalc(256, 256, p, 0, 0, 0.8);
        calc.Calculate();
        int nonZero = 0;
        foreach (var px in calc.ColorBuffer) if (px != 0) nonZero++;
        Assert.True(nonZero > 50, $"only {nonZero} pixels");
    }

    // ── S2 params / presets / persistence (#893) ─────────────────────────────

    [Fact]
    public void Clone_RoundTripsIndrasFields()
    {
        var p = new FractalParameters
        {
            IndrasFamily = IndrasGroupFamily.GrandmaRecipe,
            IndrasMaskitMuRe = 0.3, IndrasMaskitMuIm = 1.7,
            IndrasGrandmaTaRe = 2.1, IndrasGrandmaTaIm = -0.2,
            IndrasGrandmaTbRe = 1.9, IndrasGrandmaTbIm = 0.4,
            IndrasGrandmaSecondSolution = true,
            IndrasRileyCRe = 0.25, IndrasRileyCIm = 0.8,
            IndrasMaxWordDepth = 9,
            IndrasRenderMode = IndrasRenderMode.CurveTrace,
        };
        var c = p.Clone();
        Assert.Equal(IndrasGroupFamily.GrandmaRecipe, c.IndrasFamily);
        Assert.Equal(1.7, c.IndrasMaskitMuIm);
        Assert.Equal(2.1, c.IndrasGrandmaTaRe);
        Assert.True(c.IndrasGrandmaSecondSolution);
        Assert.Equal(0.8, c.IndrasRileyCIm);
        Assert.Equal(9, c.IndrasMaxWordDepth);
        Assert.Equal(IndrasRenderMode.CurveTrace, c.IndrasRenderMode);
    }

    [Fact]
    public void Region_RoundTripsNonDefaultIndrasGroup()
    {
        var p = new FractalParameters
        {
            IndrasFamily = IndrasGroupFamily.GrandmaRecipe,
            IndrasGrandmaTaRe = 3.0, IndrasGrandmaTaIm = 0.0,
            IndrasGrandmaTbRe = 3.0, IndrasGrandmaTbIm = 0.0,
            IndrasMaxWordDepth = 10,
        };
        var snap = RegionFractalParams.Snapshot(FractalType.IndrasPearls, p);
        Assert.NotNull(snap);
        var restored = new FractalParameters();   // defaults (Maskit)
        snap!.ApplyTo(restored);
        Assert.Equal(IndrasGroupFamily.GrandmaRecipe, restored.IndrasFamily);
        Assert.Equal(3.0, restored.IndrasGrandmaTaRe);
        Assert.Equal(3.0, restored.IndrasGrandmaTbRe);
        Assert.Equal(10, restored.IndrasMaxWordDepth);
    }

    [Fact]
    public void Region_OmitsIndrasFieldsAtDefault()
    {
        var p = new FractalParameters();   // all Indras defaults
        var snap = RegionFractalParams.Snapshot(FractalType.IndrasPearls, p);
        // Block may exist but every Indras field is omitted (null) at default.
        Assert.Null(snap?.IndrasFamily);
        Assert.Null(snap?.IndrasMaskitMuIm);
        Assert.Null(snap?.IndrasMaxWordDepth);
    }

    [Fact]
    public void Calculate_RespondsToFamilySwitch()
    {
        var maskit = new IndrasPearlsCalculator(200, 200) { CenterX = 0, CenterY = 1, Zoom = 0.6 };
        var grandma = new IndrasPearlsCalculator(200, 200) { CenterX = 0, CenterY = 1, Zoom = 0.6 };
        grandma.FractalParameters = new FractalParameters { IndrasFamily = IndrasGroupFamily.GrandmaRecipe };
        maskit.Calculate();
        grandma.Calculate();
        Assert.NotEqual(maskit.ColorBuffer, grandma.ColorBuffer);
    }

    [Fact]
    public void Calculate_RespondsToMaskitMu()
    {
        var a = new IndrasPearlsCalculator(200, 200) { CenterX = 0, CenterY = 1, Zoom = 0.6 };
        var b = new IndrasPearlsCalculator(200, 200) { CenterX = 0, CenterY = 1, Zoom = 0.6 };
        b.FractalParameters = new FractalParameters { IndrasMaskitMuRe = 0.4, IndrasMaskitMuIm = 1.6 };
        a.Calculate();
        b.Calculate();
        Assert.NotEqual(a.ColorBuffer, b.ColorBuffer);
    }

    // ── Framing probe (not an assertion of intent — reports the limit-set
    // bounding box so MiniMapDefaults / FractalViewState can frame it). ────────
    [Fact]
    public void Probe_LimitSetBoundingBox()
    {
        var g = IndrasGroup.Maskit(new Complex(0, 2));
        var seeds = new List<Complex>(g.SeedPoints());
        double minX = double.MaxValue, maxX = double.MinValue;
        double minY = double.MaxValue, maxY = double.MinValue;
        var letters = g.Letters;

        void Descend(in Mobius m, int last, int depth)
        {
            foreach (var s in seeds)
                if (m.TryApply(s, out var z) && Math.Abs(z.Real) < 50 && Math.Abs(z.Imaginary) < 50)
                {
                    if (z.Real < minX) minX = z.Real; if (z.Real > maxX) maxX = z.Real;
                    if (z.Imaginary < minY) minY = z.Imaginary; if (z.Imaginary > maxY) maxY = z.Imaginary;
                }
            if (depth >= 11) return;
            int forbid = last < 0 ? -1 : IndrasGroup.InverseLetter(last);
            for (int l = 0; l < 4; l++)
                if (l != forbid) Descend(m.Multiply(letters[l]), l, depth + 1);
        }
        Descend(Mobius.Identity, -1, 0);
        _out.WriteLine($"BBOX x=[{minX:F3},{maxX:F3}] y=[{minY:F3},{maxY:F3}]  " +
                       $"centre=({(minX + maxX) / 2:F3},{(minY + maxY) / 2:F3})  " +
                       $"span=({maxX - minX:F3},{maxY - minY:F3})");
        Assert.True(maxX > minX && maxY > minY);
    }
}
