// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #27 Phase 6 — CalcGen "Compile & Load" (generate typed C# → Roslyn) must reach
// functional + semantic parity with the live SandboxExpression interpreter on the
// shared feature set, INCLUDING the newly-added expression functions re / im /
// abs / clamp. For a corpus using those:
//   1. the generated calculator compiles with NO CS errors (the #213-class
//      failure), and
//   2. it computes the SAME dynamics as SandboxCalculator — proven by the
//      in-set membership mask (a pixel is in-set iff its orbit never escaped
//      |z|² > 1024 within MaxIter). Both engines share that exact escape test
//      and the same pixel→plane mapping, so identical maths ⇒ the same escape
//      set. This is pipeline-independent: it ignores the smooth-iteration /
//      colour differences between the two renderers (which are NOT part of the
//      DSL contract) and isolates the equation semantics.
//
// Parity is defined at SHALLOW zoom on the shared maths — CalcGen's deep-zoom
// machinery (SIMD / SA / perturbation / DD-QD) is out of scope by design; the
// non-holomorphic new ops disable it, so both engines run the direct path.

using System;
using FracturingFog.CalculatorGen;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class CalcGenSandboxParityTests
{
    private const int W = 96, H = 72;
    private const int MaxIter = 200;
    private const double Cx = -0.5, Cy = 0.0, Zoom = 1.0;

    // Equations exercising the Phase 6 additions (re / im / abs / clamp) plus a
    // z*z + c control. Each is valid for BOTH the CalcGen DSL and SandboxExpression.
    public static TheoryData<string, string> Corpus() => new()
    {
        { "control_z2",   "z*z + c" },
        { "abs_c",        "z*z + abs(c)" },
        { "re_im_c",      "z*z + re(c) + im(c)" },
        { "clamp_re_c",   "z*z + clamp(re(c), -2.0, 2.0)" },
        // abs/re/clamp applied to the ITERATE z, but multiplied by 0 so the
        // dynamics stay z*z + c. This exercises the complex-operand OpAbs / OpRe
        // / OpClamp emitters across all precision paths (scalar / AVX2 / DD / QD)
        // and proves they compile + run, without the sensitive-dependence a
        // genuinely iterated fold (e.g. abs(z)+c, a burning-ship-style map) would
        // introduce between two independent FP implementations.
        { "z_ops_zeroed", "z*z + c + (abs(z) + re(z) + clamp(im(z), -1.0, 1.0)) * 0.0" },
        { "mixed_c",      "sqr(z) + abs(c) + clamp(re(c), -1.5, 1.5)" },
    };

    [Theory]
    [MemberData(nameof(Corpus))]
    public void CalcGenRender_MatchesSandbox_AtShallowZoom(string label, string dsl)
    {
        // (1) Generate + Roslyn-compile — must not produce CS errors.
        var hot = CalculatorGenHotLoad.TryCompileAndLoad(dsl, label + "Cmp");
        Assert.True(hot.Ok, $"[{label}] generate+compile failed: {hot.Error}");

        var map1 = new HsvPalette();
        var calc = (IFractalCalculator)Activator.CreateInstance(hot.CalculatorType!, W, H)!;
        calc.CenterX = Cx; calc.CenterY = Cy; calc.Zoom = Zoom; calc.MaxIterations = MaxIter;
        SetColorMap(calc, map1);
        calc.Calculate(default);

        // (2) Reference: SandboxExpression interpreter over the same source.
        var map2 = new HsvPalette();
        var sbx = new SandboxCalculator(W, H)
        {
            CenterX = Cx, CenterY = Cy, Zoom = Zoom, MaxIterations = MaxIter,
            ColorMap = map2,
            FractalParameters = new FractalParameters { SandboxSource = dsl },
        };
        sbx.Calculate(default);

        Assert.Equal(sbx.ColorBuffer.Length, calc.ColorBuffer.Length);

        // In-set pixels are painted InSetColor by both engines; compare the
        // binary escape mask (pipeline-independent — isolates the maths).
        uint inset1 = ((IColorMap)map1).InSetColor;
        uint inset2 = ((IColorMap)map2).InSetColor;
        int match = 0, insetCount = 0, escapedCount = 0;
        for (int i = 0; i < calc.ColorBuffer.Length; i++)
        {
            bool gIn = calc.ColorBuffer[i] == inset1;
            bool sIn = sbx.ColorBuffer[i] == inset2;
            if (gIn == sIn) match++;
            if (sIn) insetCount++; else escapedCount++;
        }
        double ratio = (double)match / calc.ColorBuffer.Length;

        // Same map + same escape test + same mapping ⇒ the same escape set; the
        // few disagreements sit on the set boundary where a lane's FP
        // association (AVX vs scalar interpreter) flips escape-at-exactly-MaxIter.
        Assert.True(ratio >= 0.98,
            $"[{label}] CalcGen vs Sandbox in-set mask match {ratio:P2} < 98% " +
            $"(in-set {insetCount}, escaped {escapedCount}, dsl: {dsl})");
    }

    [Fact]
    public void Control_RendersNonDegenerate_BothEngines()
    {
        // The classic map must have BOTH in-set and escaped pixels in this frame
        // — guards against a corpus entry that trivially matches by being uniform.
        var map = new HsvPalette();
        var calc = (IFractalCalculator)Activator.CreateInstance(
            CalculatorGenHotLoad.TryCompileAndLoad("z*z + c", "NonDegen").CalculatorType!, W, H)!;
        calc.CenterX = Cx; calc.CenterY = Cy; calc.Zoom = Zoom; calc.MaxIterations = MaxIter;
        SetColorMap(calc, map);
        calc.Calculate(default);

        uint inset = ((IColorMap)map).InSetColor;
        int inCount = 0, escCount = 0;
        foreach (var px in calc.ColorBuffer) { if (px == inset) inCount++; else escCount++; }
        Assert.True(inCount > 0 && escCount > 0, $"expected a real fractal (in-set {inCount}, escaped {escCount})");
    }

    // The generated calculator and SandboxCalculator both expose a settable
    // ColorMap property; assign the same palette so only the maths is compared.
    private static void SetColorMap(IFractalCalculator calc, IColorMap map)
    {
        var prop = calc.GetType().GetProperty("ColorMap");
        prop?.SetValue(calc, map);
    }
}
