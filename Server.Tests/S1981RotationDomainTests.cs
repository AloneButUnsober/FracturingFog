// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1981 — rotation-domain colouring (idea: pauldelbrot, fractalforums.org topic 5693).
//   • the jets: each step's value against the render's own kernel step (Magnet 1 / 2) or the map
//     written out here, its f′ and f″ against central differences of the value and of f′;
//   • classification on the golden-mean Siegel Julia set, c = λ/2 − λ²/4 with λ = e^{2πiθ},
//     θ = (√5 − 1)/2 (z² + c has the fixed point λ/2 with multiplier λ): its non-escaping pixels
//     are the Siegel disk and its preimages, all rotation domain. Control: c = −0.5 (an attracting
//     fixed point), none;
//   • the distance estimate against a direct measurement of the disk's boundary, the closure of
//     the critical orbit (Docs/Resources-Bibliography.md → milnor-dynamics-one-variable);
//   • Phoenix's largest exponent against a two-orbit (Benettin) estimate written out here;
//   • default off byte-identical, on: only in-set pixels change, HE keeps the stamp, interior alpha
//     scales it; Nova's attracting-cycle interior keeps its colour;
//   • the snapshot, --param and the Command builder carry it for its families.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using FracturingFog.Models;
using FracturingFog.Models.FractalKernels;
using Xunit;
using RD = FracturingFog.RotationDomainColoring;

namespace FracturingFog.Server.Tests;

public sealed class S1981RotationDomainTests(ITestOutputHelper output)
{
    private static readonly double Theta = (Math.Sqrt(5) - 1) / 2;
    private static readonly Complex Lambda = Complex.FromPolarCoordinates(1, 2 * Math.PI * Theta);
    internal static readonly Complex SiegelC = Lambda / 2 - Lambda * Lambda / 4;
    private static readonly RD.Settings S = RD.Settings.From(new FractalParameters());

    // ── jets ────────────────────────────────────────────────────────────────

    private static void CheckJet(string name, Func<Complex, RD.Jet> f, Complex z, Func<Complex, Complex>? value = null)
    {
        const double h = 1e-5;
        var j = f(z);
        if (value != null)
            Assert.True((j.V - value(z)).Magnitude <= 1e-12 * Math.Max(1, j.V.Magnitude), $"{name} value at {z}: {j.V} vs {value(z)}");
        Complex fd1 = (f(z + h).V - f(z - h).V) / (2 * h);
        Complex fd2 = (f(z + h).D1 - f(z - h).D1) / (2 * h);
        Assert.True((j.D1 - fd1).Magnitude <= 1e-6 * Math.Max(1, fd1.Magnitude), $"{name} f′ at {z}: {j.D1} vs {fd1}");
        Assert.True((j.D2 - fd2).Magnitude <= 1e-5 * Math.Max(1, fd2.Magnitude), $"{name} f″ at {z}: {j.D2} vs {fd2}");
        // Holomorphic: the imaginary-direction difference agrees as well (f′ = ∂f/∂x = −i ∂f/∂y).
        Complex fdy = (f(z + new Complex(0, h)).V - f(z - new Complex(0, h)).V) / (2 * h);
        Assert.True((j.D1 - fdy / Complex.ImaginaryOne).Magnitude <= 1e-6 * Math.Max(1, fd1.Magnitude), $"{name} ∂y at {z}");
    }

    private static Complex KernelStep<TK>(TK k, Complex z, Complex c) where TK : struct, Interefaces.IFractalKernel
    {
        double zr = z.Real, zi = z.Imaginary, dr = 0, di = 0;
        k.Step(ref zr, ref zi, ref dr, ref di, c.Real, c.Imaginary);
        return new(zr, zi);
    }

    [Fact]
    public void Jets_Match_The_Maps_And_Central_Differences()
    {
        var zs = new[] { new Complex(0.3, 0.2), new Complex(-0.7, 0.45), new Complex(1.2, -0.9), new Complex(-0.1, -1.3) };
        var c = new Complex(0.8, 0.6);
        foreach (var z in zs)
        {
            CheckJet("Julia", q => new RD.JuliaStep(SiegelC, 1e6).Apply(q), z, q => q * q + SiegelC);
            CheckJet("Magnet1", q => new RD.Magnet1Step(c, 1e6, 1e-9).Apply(q), z, q => KernelStep(new MagnetOneKernel(), q, c));
            CheckJet("Magnet2", q => new RD.Magnet2Step(c, 1e6, 1e-9).Apply(q), z, q => KernelStep(new MagnetTwoKernel(), q, c));
            foreach (int d in new[] { 2, 3, 5 })
                foreach (double r in new[] { 1.0, 0.7 })
                    CheckJet($"Newton d={d} R={r}", q => new RD.NewtonStep(d, r, c).Apply(q), z,
                        q => q - r * (Complex.Pow(q, d) - 1) / (d * Complex.Pow(q, d - 1)) + c);
        }
    }

    // ── classification on the Siegel Julia set ──────────────────────────────

    internal static EscapeTimeCalculator Julia(Complex c, bool on, int w = 160, int h = 120, int maxIt = 2000,
                                               Action<FractalParameters>? tweak = null)
    {
        var p = new FractalParameters { JuliaC = c, RotationDomainColoring = on };
        tweak?.Invoke(p);
        var calc = new EscapeTimeCalculator(w, h)
        {
            FractalType = FractalType.Julia, MaxIterations = maxIt, Zoom = 1.3,
            ColorMap = ColorPalette.BuiltIns[0], FractalParameters = p,
        };
        calc.Calculate(CancellationToken.None);
        return calc;
    }

    [Fact]
    public void Siegel_Julia_Is_All_Rotation_And_An_Attracting_Julia_Has_None()
    {
        var calc = Julia(SiegelC, on: true);
        int inSet = calc.IterationBuffer.Count(i => i >= calc.MaxIterations);
        var st = calc.LastRotationDomainStats;
        output.WriteLine($"Siegel: in-set {inSet}, rotation {st.Rotation}, chaotic {st.Chaotic}, attracting {st.Attracting}");
        Assert.True(inSet >= 1000, $"precondition: the disk and its preimages ({inSet})");
        Assert.Equal(inSet, st.Rotation + st.Chaotic + st.Attracting);
        Assert.True(st.Rotation >= inSet * 0.95, $"rotation {st.Rotation}/{inSet}");

        var att = Julia(new Complex(-0.5, 0), on: true);
        int attIn = att.IterationBuffer.Count(i => i >= att.MaxIterations);
        var sa = att.LastRotationDomainStats;
        output.WriteLine($"c = -0.5: in-set {attIn}, rotation {sa.Rotation}, chaotic {sa.Chaotic}, attracting {sa.Attracting}");
        Assert.True(attIn >= 1000, $"precondition ({attIn})");
        Assert.Equal(0, sa.Rotation);
        Assert.Equal(attIn, sa.Attracting);
    }

    // z² − 2 on [−2, 2] is conjugate to the doubling map (z = 2cos t, t → 2t): λ = ln 2, chaotic.
    [Fact]
    public void Chebyshev_Orbit_Is_Chaotic_With_Exponent_Ln2()
    {
        var r = RD.Classify(new RD.JuliaStep(-2, 1e6), 0.3, 5000, false, S);
        output.WriteLine($"z² − 2 from 0.3: λ {r.Lyapunov:F5} ({r.Class})");
        Assert.Equal(RD.OrbitClass.Chaotic, r.Class);
        Assert.InRange(r.Lyapunov, Math.Log(2) - 0.05, Math.Log(2) + 0.05);
        Assert.Equal(RD.ColorFor(r, 1e-3, S), S.Chaos);
    }

    // The disk's boundary is the closure of the critical orbit; the distance to it measured directly.
    [Fact]
    public void Siegel_Distance_Estimate_Tracks_The_Boundary()
    {
        const int n = 200_000;
        var orbit = new Complex[n];
        Complex z = 0;
        for (int i = 0; i < n; i++) { orbit[i] = z; z = z * z + SiegelC; }
        double Boundary(Complex p) { double m = double.MaxValue; foreach (var o in orbit) m = Math.Min(m, (o - p).Magnitude); return m; }

        Complex alpha = Lambda / 2;   // the fixed point at the disk's centre
        var ratios = new List<double>();
        for (int k = 0; k < 24; k++)
        {
            double prev = double.MaxValue;
            for (int i = 1; i <= 60; i++)
            {
                var p = alpha + Complex.FromPolarCoordinates(0.01 * i, 2 * Math.PI * k / 24);
                double b = Boundary(p);
                if (b > prev) break;   // past the nearest boundary point along this ray: out of the disk
                prev = b;
                var r = RD.Classify(new RD.JuliaStep(SiegelC, 1e6), p, 2000, false, S);
                Assert.Equal(RD.OrbitClass.Rotation, r.Class);
                ratios.Add(r.Distance / b);
            }
        }
        ratios.Sort();
        double Q(double f) => ratios[(int)(f * (ratios.Count - 1))];
        output.WriteLine($"{ratios.Count} points; estimate / distance: 5% {Q(0.05):F3}, median {Q(0.5):F3}, 95% {Q(0.95):F3}, max {ratios[^1]:F3}");
        Assert.True(ratios.Count >= 200, $"precondition ({ratios.Count})");
        int within = ratios.Count(x => x >= 0.25 && x <= 4);
        Assert.True(within >= ratios.Count * 0.95, $"{within}/{ratios.Count} within [1/4, 4]");
        Assert.InRange(Q(0.5), 1.0, 3.0);
    }

    // ── Phoenix ─────────────────────────────────────────────────────────────

    // Benettin: two orbits δ apart, the separation renormalised each step.
    private static double Benettin(Complex c, Complex p, int n)
    {
        Complex z = 0, w = 0, z2 = 1e-8, w2 = 0;
        double sum = 0;
        for (int i = 0; i < n; i++)
        {
            Complex nz = z * z + c + p * w, nz2 = z2 * z2 + c + p * w2;
            w = z; z = nz; w2 = z2; z2 = nz2;
            Complex dz = z2 - z, dw = w2 - w;
            double sep = Math.Sqrt(dz.Real * dz.Real + dz.Imaginary * dz.Imaginary + dw.Real * dw.Real + dw.Imaginary * dw.Imaginary);
            sum += Math.Log(sep / 1e-8);
            z2 = z + dz * (1e-8 / sep); w2 = w + dw * (1e-8 / sep);
        }
        return sum / n;
    }

    [Fact]
    public void Phoenix_Largest_Exponent_Matches_Two_Orbits()
    {
        var p = new Complex(-0.5, 0);
        foreach (var c in new[] { new Complex(0.1, 0), new Complex(-0.2, 0.1), new Complex(0.2, 0.2), new Complex(-0.6, 0.05) })
        {
            var r = RD.ClassifyPhoenix(c, p, 4, 3000, S);
            Assert.True(double.IsFinite(r.Lyapunov), $"precondition: {c} bounded");
            double b = Benettin(c, p, 3000);
            output.WriteLine($"c {c}: λ1 {r.Lyapunov:F6} ({r.Class}), Benettin {b:F6}");
            Assert.True(Math.Abs(r.Lyapunov - b) < 2e-3, $"{c}: {r.Lyapunov} vs {b}");
        }
    }

    // ── frames ──────────────────────────────────────────────────────────────

    [Fact]
    public void Off_Is_Byte_Identical_And_On_Changes_Only_In_Set_Pixels()
    {
        var off = Julia(SiegelC, on: false);
        var offTweaked = Julia(SiegelC, on: false, tweak: p =>
        {
            p.RotationDomainEpsilon = 0.1; p.RotationDomainBound = 3; p.RotationDomainShadeWidth = 5;
            p.RotationDomainColor = 0xFF112233; p.RotationDomainEdgeColor = 0xFF445566; p.RotationDomainChaosColor = 0xFF778899;
        });
        Assert.Equal(off.ColorBuffer, offTweaked.ColorBuffer);
        Assert.Equal(default, offTweaked.LastRotationDomainStats);

        var on = Julia(SiegelC, on: true);
        int changed = 0;
        for (int i = 0; i < off.ColorBuffer.Length; i++)
        {
            if (off.IterationBuffer[i] < off.MaxIterations) Assert.Equal(off.ColorBuffer[i], on.ColorBuffer[i]);
            else if (off.ColorBuffer[i] != on.ColorBuffer[i]) changed++;
        }
        Assert.Equal(off.IterationBuffer, on.IterationBuffer);
        Assert.True(changed >= on.LastRotationDomainStats.Rotation * 0.99, $"{changed} recoloured");

        // Histogram equalization repaints in-set pixels: the stamp survives it.
        var before = (uint[])on.ColorBuffer.Clone();
        on.ApplyHistogramEqualization(1.0);
        for (int i = 0; i < before.Length; i++)
            if (on.IterationBuffer[i] >= on.MaxIterations) Assert.Equal(before[i], on.ColorBuffer[i]);

        // Interior alpha scales the stamped colours.
        var half = Julia(SiegelC, on: true, tweak: p => { });
        half.InteriorAlpha = 128; half.Calculate(CancellationToken.None);
        for (int i = 0; i < before.Length; i++)
            if (half.IterationBuffer[i] >= half.MaxIterations) Assert.Equal(128u, half.ColorBuffer[i] >> 24);
    }

    [Fact]
    public void Nova_Minibrot_Interior_Is_Attracting_And_Keeps_Its_Colour()
    {
        NewtonCalculator Make(bool on)
        {
            var calc = new NewtonCalculator(96, 54)
            {
                FractalType = FractalType.Nova, MaxIterations = 1500, CenterX = -0.45, Zoom = 12,
                ColorMap = ColorPalette.BuiltIns[0],
                FractalParameters = new FractalParameters { NewtonExponent = 3, RotationDomainColoring = on },
            };
            calc.Calculate(CancellationToken.None);
            return calc;
        }
        var off = Make(false);
        var on = Make(true);
        int nonConv = on.BasinBuffer.Count(b => b < 0);
        var st = on.LastRotationDomainStats;
        output.WriteLine($"Nova minibrot: non-converged {nonConv}, rotation {st.Rotation}, chaotic {st.Chaotic}, attracting {st.Attracting}");
        Assert.True(nonConv >= 300, $"precondition ({nonConv})");
        Assert.Equal(nonConv, st.Rotation + st.Chaotic + st.Attracting);
        Assert.True(st.Attracting >= nonConv * 0.8, $"attracting {st.Attracting}/{nonConv}");
        // z₀ = 1 is critical (N′ = 0): a product from z₀ pins every λ to −∞, so nothing would be neutral.
        Assert.True(st.Rotation + st.Chaotic > 0, "the product starts at the first iterate");
        var fromZ1 = RD.Classify(new RD.NewtonStep(3, 1, new Complex(-0.45, 0)), new RD.NewtonStep(3, 1, new Complex(-0.45, 0)).Apply(1).V, 1499, false, S);
        var fromZ0 = RD.Classify(new RD.NewtonStep(3, 1, new Complex(-0.45, 0)), 1, 1500, true, S);
        Assert.Equal(fromZ1.Lyapunov, fromZ0.Lyapunov, 12);
        int kept = 0;
        for (int i = 0; i < off.ColorBuffer.Length; i++)
        {
            if (off.BasinBuffer[i] >= 0) Assert.Equal(off.ColorBuffer[i], on.ColorBuffer[i]);
            else if (off.ColorBuffer[i] == on.ColorBuffer[i]) kept++;
        }
        Assert.True(kept >= st.Attracting, $"{kept} kept");
    }

    // ── batch parity ────────────────────────────────────────────────────────

    [Fact]
    public void Off_By_Default_And_Carried_By_The_Snapshot_Param_And_Command_Builder()
    {
        Assert.False(new FractalParameters().RotationDomainColoring);
        foreach (var t in new[] { FractalType.Julia, FractalType.Phoenix, FractalType.Magnet1, FractalType.Magnet2, FractalType.Newton, FractalType.Nova })
        {
            Assert.Null(RegionFractalParams.Snapshot(t, new FractalParameters())?.RotationDomainColoring);
            var p = new FractalParameters
            {
                RotationDomainColoring = true, RotationDomainEpsilon = 2e-4, RotationDomainBound = 9,
                RotationDomainShadeWidth = 7, RotationDomainColor = 0xFF102030, RotationDomainEdgeColor = 0xFF405060,
                RotationDomainChaosColor = 0xFF708090,
            };
            var snap = RegionFractalParams.Snapshot(t, p)!;
            var back = System.Text.Json.JsonSerializer.Deserialize<RegionFractalParams>(System.Text.Json.JsonSerializer.Serialize(snap))!;
            var q = new FractalParameters();
            back.ApplyTo(q);
            Check(t, q);

            var cmdSnap = new FracturingFog.Cli.BatchCommandSnapshot { Fractal = t, Parameters = p, FamilyParams = snap.ToKeyValues() };
            Assert.Contains("RotationDomainColoring=", FracturingFog.Cli.BatchCommandBuilder.Build(cmdSnap));
            var args = new List<string> { "--fractal", t.ToString(), "--x", "0", "--y", "0", "--zoom", "1", "--out", "o.png" };
            foreach (var kv in cmdSnap.FamilyParams) { args.Add(FracturingFog.Batch.BatchFlags.Param); args.Add(kv.Key + "=" + kv.Value); }
            Assert.True(FracturingFog.Batch.BatchOptions.TryParse(args.ToArray(), 0, out var opts, out var err), err);
            var overlay = RegionFractalParams.FromKeyValues(opts.Params, t, out var error);
            Assert.True(overlay != null, error);
            var fresh = new FractalParameters();
            overlay!.ApplyTo(fresh);
            Check(t, fresh);
        }
        Assert.Null(RegionFractalParams.Snapshot(FractalType.Mandelbrot, new FractalParameters { RotationDomainColoring = true })?.RotationDomainColoring);

        static void Check(FractalType t, FractalParameters q)
        {
            Assert.True(q.RotationDomainColoring, t.ToString());
            Assert.Equal(2e-4, q.RotationDomainEpsilon);
            Assert.Equal(9, q.RotationDomainBound);
            Assert.Equal(7, q.RotationDomainShadeWidth);
            Assert.Equal(0xFF102030u, q.RotationDomainColor);
            Assert.Equal(0xFF405060u, q.RotationDomainEdgeColor);
            Assert.Equal(0xFF708090u, q.RotationDomainChaosColor);
        }
    }
}
