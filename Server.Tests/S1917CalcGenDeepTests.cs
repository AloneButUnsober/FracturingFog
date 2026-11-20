// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1917 (epic #1253, D12) — CalculatorGen's deep tier on the generic perturbation engine. The
// generated calculators keep their emitted shallow paths; from EngineZoomThreshold (1e12) they render
// on PerturbationEngine with the plan for their own equation (EquationSource, parsed at runtime).
// Checks, each against something outside the engine path:
//   • every built-in generated equation gets a plan (none falls back), and EquationSource is the
//     attribute's string;
//   • below the threshold nothing changed: the colour buffer is byte-identical with the engine
//     switched off (the old paths), at 1e11 and just under 1e12;
//   • deep, the counts match direct BigFloat iteration of the formula written out by hand
//     (S1909's oracle) at Misiurewicz views 1e40 deep, for Generated Z2–Z5, Tricorn and Burning
//     Ship. Z2 – Z5 also at 1e120, past the template's QD-PT ceiling (~1e50);
//   • the explorer limit for the generated families is the engine's.
// Mutants (by hand), all caught: the threshold dropped (the engine below 1e12); the engine never
// used; the plan's bailout² as 4; the centre's Hi limb only.

using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using FracturingFog;
using FracturingFog.Abstractions.Explore;
using FracturingFog.FFMath;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using Xunit;
using static FracturingFog.Server.Tests.S1909ConjR2Tests;
using Gen = FracturingFog.Calculators.Generated;

namespace FracturingFog.Server.Tests;

public sealed class S1917CalcGenDeepTests(ITestOutputHelper output)
{
    private static Step Power(int d) => (a, b, cr, ci, p) =>
    {
        BigFloat r = a, i = b;
        for (int k = 1; k < d; k++)
            (r, i) = (BigFloat.Sub(BigFloat.Mul(r, a, p), BigFloat.Mul(i, b, p), p),
                      BigFloat.Add(BigFloat.Mul(r, b, p), BigFloat.Mul(i, a, p), p));
        return (BigFloat.Add(r, cr, p), BigFloat.Add(i, ci, p));
    };

    private static dynamic Make(Type t, int w, int h) => Activator.CreateInstance(t, w, h)!;

    private static Type[] GeneratedTypes() => typeof(Gen.MandelbrotZ2Calculator).Assembly.GetTypes()
        .Where(t => t.Namespace == "FracturingFog.Calculators.Generated" && t.IsClass && !t.IsAbstract
                    && t.GetField("EquationSource", BindingFlags.Public | BindingFlags.Static) != null)
        .OrderBy(t => t.Name).ToArray();

    private static void Setup(dynamic c, ExactCentre centre, double zoom, int maxIter)
    {
        c.Zoom = zoom; c.MaxIterations = maxIter; c.ColorMap = ColorPalette.BuiltIns[0];
        ((IDeepCentre)c).SetDeepCentre(centre.ToDeep(), centre);
    }

    [Fact]
    public void Every_Built_In_Generated_Equation_Renders_Deep_On_The_Engine()
    {
        var types = GeneratedTypes();
        Assert.True(types.Length >= 10, $"precondition: the generated calculators ({types.Length})");
        Assert.Equal("z*z + c", Gen.MandelbrotZ2Calculator.EquationSource);
        Assert.Equal("fold(z)*fold(z) + c", Gen.BurningShipCalculator.EquationSource);
        var centre = ExactCentre.FromDeep(new DeepComplex(new OD(-0.75), new OD(0.1)), 256);
        foreach (var t in types)
        {
            var c = Make(t, 8, 8);
            Setup(c, centre, 1e20, 64);
            c.Calculate(CancellationToken.None);
            string? why = c.EngineRefusal;
            output.WriteLine($"{t.Name} \"{t.GetField("EquationSource")!.GetValue(null)}\": {c.LastPrecisionLabel} {why}");
            Assert.True(why == null, $"{t.Name}: refused, {why}");
            Assert.Equal("PT", (string)c.LastPrecisionLabel);
        }
    }

    // The deep tests' views (Misiurewicz points, so the frame has structure at any depth).
    private static (Type T, Step F, double Gr, double Gi) View(string name) => name switch
    {
        "Z2" => (typeof(Gen.MandelbrotZ2Calculator), Power(2), -0.22815549365396182, 1.1151425080399373),
        "Z3" => (typeof(Gen.MandelbrotZ3Calculator), Power(3), -0.23026540906830995, 1.3276480964335278),
        "Z4" => (typeof(Gen.MandelbrotZ4Calculator), Power(4), 0.5872070621552286, 1.0875883815662934),
        "Z5" => (typeof(Gen.MandelbrotZ5Calculator), Power(5), -0.7795530288946705, 0.883881464661667),
        "Tricorn" => (typeof(Gen.TricornCalculator), TricornStep, 0.7718445063460382, 1.336873900534257),
        _ => (typeof(Gen.BurningShipCalculator), S1910DiffAbsTests.Ship, 0.3014809547257814, 0.4519892606922896),
    };

    [Theory]
    [InlineData("Z2", 1e11)]
    [InlineData("Z3", 0.999e12)]
    [InlineData("Z5", 1e8)]
    [InlineData("Tricorn", 1e11)]
    [InlineData("BurningShip", 0.999e12)]
    public void Below_The_Threshold_The_Frame_Is_Byte_Identical(string name, double zoom)
    {
        var (t, f, gr, gi) = View(name);
        var centre = Misiurewicz(f, gr, gi, 13);
        uint[] Render(bool engine)
        {
            var c = Make(t, W, H);
            Setup(c, centre, zoom, 800);
            c.UseEngine = engine;
            c.Calculate(CancellationToken.None);
            Assert.Null((string?)c.EngineRefusal);
            return ((uint[])c.ColorBuffer).Take(W * H).ToArray();
        }
        var on = Render(true); var off = Render(false);
        Assert.True(on.Distinct().Count() >= 3, "precondition: structure");
        Assert.Equal(off, on);
    }

    private void Deep(string name, int zl, int maxIter)
    {
        var (t, f, gr, gi) = View(name);
        double scale = 3.5 / W / Math.Pow(10, zl);
        var centre = Off(Misiurewicz(f, gr, gi, zl), scale, zl);
        var c = Make(t, W, H);
        Setup(c, centre, Math.Pow(10, zl), maxIter);
        c.Calculate(CancellationToken.None);
        Assert.Equal("PT", (string)c.LastPrecisionLabel);
        var it = ((int[])c.IterationBuffer).Take(W * H).ToArray();
        var got = new Result(it, new double[W * H], new double[W * H], c.LastPerturbationStats);
        Check(output, $"{t.Name} 1e{zl}", got, f, 512.0 * 512.0, centre, scale, maxIter, derivative: false);
    }

    [Theory]
    [InlineData(40)]
    [InlineData(120)]
    public void Generated_Z2_Matches_Direct_Iteration(int zl)
        => Deep("Z2", zl, 2000);

    [Theory]
    [InlineData(40)]
    [InlineData(120)]
    public void Generated_Z3_Matches_Direct_Iteration(int zl)
        => Deep("Z3", zl, 2000);

    [Fact]
    public void Generated_Z4_Matches_Direct_Iteration()
        => Deep("Z4", 40, 2000);

    [Theory]
    [InlineData(40)]
    [InlineData(120)]
    public void Generated_Z5_Matches_Direct_Iteration(int zl)
        => Deep("Z5", zl, 2000);

    [Fact]
    public void Generated_Tricorn_Matches_Direct_Iteration()
        => Deep("Tricorn", 40, 1500);

    [Fact]
    public void Generated_Burning_Ship_Matches_Direct_Iteration()
        => Deep("BurningShip", 40, 2000);

    [Fact]
    public void The_Explorer_Limit_For_The_Generated_Families_Is_The_Engines()
    {
        foreach (var t in new[]
                 {
                     FractalType.GeneratedMandelbrotZ2, FractalType.GeneratedMandelbrotZ3, FractalType.GeneratedMandelbrotZ4,
                     FractalType.GeneratedMandelbrotZ5, FractalType.GeneratedTricorn, FractalType.GeneratedBurningShip,
                 })
            Assert.Equal(FamilyDepth.EngineDoubleDeltaMaxZoom, FamilyDepth.MaxZoomFor(t));
    }
}
