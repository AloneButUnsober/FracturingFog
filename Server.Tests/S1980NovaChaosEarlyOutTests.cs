// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1980 — Nova's opt-in chaos early-out (idea: pauldelbrot, fractalforums.org topic 5693).
//   • against direct iteration written out here: the step
//     z − (z³ − 1)/(3z²) + c in Cartesian form, the multiplier by the quotient rule
//     N′ = 1 − (f′² − f·f″)/f′² (not the calculator's z^−d form), the same order of tests (step stop
//     first). The double loop at 1e5 against double iteration, the engine at 1e20 against BigFloat
//     iteration; the product runs from the first iterate (z₀ = 1 is critical, N′(z₀) = 0). The
//     threshold is the median of the sampled orbits' largest multiplier (their orbits shadow a repelling 2-cycle, then converge to a superattracting root), so it splits the view.
//   • default off: the region snapshot omits it; on, the snapshot, --param and the Command builder
//     carry both settings for Nova and Newton.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using FracturingFog.FFMath;
using FracturingFog.Models;
using Xunit;
using static FracturingFog.Server.Tests.S1909ConjR2Tests;
using static FracturingFog.Server.Tests.S1921DeepFxTests;
using static FracturingFog.Server.Tests.S1959NovaDeepTests;
using static FracturingFog.Server.Tests.S1964NovaSwitchTests;

namespace FracturingFog.Server.Tests;

public sealed class S1980NovaChaosEarlyOutTests(ITestOutputHelper output)
{
    // ── #1980 ───────────────────────────────────────────────────────────────

    // One step and its multiplier, Cartesian (d = 3, R = 1): f = z³ − 1, f′ = 3z², f″ = 6z.
    private static (double, double, double Ln) Step(double zr, double zi, double cr, double ci)
    {
        double z2r = zr * zr - zi * zi, z2i = 2 * zr * zi;
        double fr = z2r * zr - z2i * zi - 1, fi = z2r * zi + z2i * zr;
        double pr = 3 * z2r, pi = 3 * z2i, qr = 6 * zr, qi = 6 * zi;
        double den = pr * pr + pi * pi;
        double ur = (fr * pr + fi * pi) / den, ui = (fi * pr - fr * pi) / den;   // f / f′
        // (f′² − f·f″)/f′²
        double ar = pr * pr - pi * pi - (fr * qr - fi * qi), ai = 2 * pr * pi - (fr * qi + fi * qr);
        double br = pr * pr - pi * pi, bi = 2 * pr * pi, b2 = br * br + bi * bi;
        double tr = (ar * br + ai * bi) / b2, ti = (ai * br - ar * bi) / b2;
        double mr = 1 - tr, mi = -ti;
        return (zr - ur + cr, zi - ui + ci, 0.5 * Math.Log(mr * mr + mi * mi));
    }

    private const double Eps2 = 1e-12;

    // (basin, smooth) by the rule: step stop first, then the multiplier against 10^k (then basin −1, the cap).
    private static (int Basin, float Smooth, double MaxLn) OracleDouble(double zr, double zi, double cr, double ci, int cap, double k)
    {
        double lnT = k * Math.Log(10), l = 0, prev2 = double.MaxValue, max = 0;
        for (int n = 0; n < cap; n++)
        {
            var (ar, ai, ln) = Step(zr, zi, cr, ci);
            double sr = ar - zr, si = ai - zi, step2 = sr * sr + si * si;
            zr = ar; zi = ai; if (n > 0) l += ln;   // from the first iterate on (z₀ = 1 is critical)
            if (step2 < Eps2) return (NewtonCalculator.RootSector(zr, zi, 3), NewtonCalculator.NovaSmooth(n, prev2, step2, Eps2), max);
            max = Math.Max(max, l);
            if (l > lnT) return (-1, cap, max);
            prev2 = step2;
        }
        return (-1, cap, max);
    }

    private static (int Basin, float Smooth, double MaxLn) OracleBig(BigFloat cr, BigFloat ci, int p, int cap, double k)
    {
        BigFloat zr = BigFloat.FromDouble(1, p), zi = BigFloat.Zero(p);
        double lnT = k * Math.Log(10), l = 0, prev2 = double.MaxValue, ccr = cr.ToDouble(), cci = ci.ToDouble(), max = 0;
        for (int n = 0; n < cap; n++)
        {
            double a = zr.ToDouble(), b = zi.ToDouble();
            var (nr, ni) = NovaStep(zr, zi, cr, ci, p);
            double sr = nr.ToDouble() - a, si = ni.ToDouble() - b, step2 = sr * sr + si * si;
            if (n > 0) l += Step(a, b, ccr, cci).Ln;
            zr = nr; zi = ni;
            if (step2 < Eps2) return (NewtonCalculator.RootSector(zr.ToDouble(), zi.ToDouble(), 3), NewtonCalculator.NovaSmooth(n, prev2, step2, Eps2), max);
            max = Math.Max(max, l);
            if (l > lnT) return (-1, cap, max);
            prev2 = step2;
        }
        return (-1, cap, max);
    }

    private static NewtonCalculator ChaosFrame(View v, double k, bool on, bool engine)
    {
        var c = Nova(W, H);
        c.FractalParameters.ChaosEarlyOut = on;
        c.FractalParameters.ChaosEarlyOutLog10 = k;
        c.AllowPerturbation = engine;
        c.SetDeepCentre(v.Centre.ToDeep(), v.Centre);
        c.SetZoomLog10(v.Zl);
        c.Calculate(CancellationToken.None);
        return c;
    }

    [Theory]
    [InlineData(5)]     // the double loop (below the switch)
    [InlineData(20)]    // the engine (BLA skips: the multiplier is det A across them)
    public void The_Chaos_Early_Out_Matches_Direct_Iteration(int zl)
    {
        var v = ViewFor(0, zl);
        bool engine = zl >= 7;
        int pf = BigFloat.PrecisionForZoom(zl) + 96;
        var samples = (from y in Enumerable.Range(0, H) where y % 3 == 1 from x in Enumerable.Range(0, W) where x % 3 == 1
                       select (x, y, pr: v.Off(x, W, v.Centre.Re, pf), pi: v.Off(y, H, v.Centre.Im, pf))).ToArray();
        (int, float, double) Oracle(BigFloat pr, BigFloat pi, double kk)
            => engine ? OracleBig(pr, pi, pf, MaxIt, kk) : OracleDouble(1, 0, pr.ToDouble(), pi.ToDouble(), MaxIt, kk);
        var maxLn = samples.AsParallel().Select(s => Oracle(s.pr, s.pi, double.PositiveInfinity).Item3).OrderBy(m => m).ToArray();
        double k = maxLn[maxLn.Length / 2] / Math.Log(10);   // the median orbit's largest multiplier, log10
        var on = ChaosFrame(v, k, on: true, engine);
        var off = ChaosFrame(v, k, on: false, engine);
        Assert.Equal(engine ? "PT" : "SP", on.LastPrecisionLabel);
        int total = 0, eq = 0, stoppedOracle = 0;
        string ex = "";
        foreach (var (x, y, pr, pi) in samples)
            {
                var (b, s, _) = Oracle(pr, pi, k);
                int idx = y * W + x;
                total++;
                if (b < 0) stoppedOracle++;
                if (on.BasinBuffer[idx] == b && Math.Abs(on.SmoothBuffer[idx] - s) < 1e-3) eq++;
                else if (ex.Length < 200) ex += $"({x},{y}) b{on.BasinBuffer[idx]} s{on.SmoothBuffer[idx]:F2} vs b{b} s{s:F2}; ";
            }
        int ncOff = off.BasinBuffer.Count(b => b < 0), ncOn = on.BasinBuffer.Count(b => b < 0);
        output.WriteLine($"1e{zl}, 10^{k:F3}: {eq}/{total} equal; oracle stops {stoppedOracle}; non-converged off {ncOff}, on {ncOn}; stats {on.LastEarlyOuts}");
        Assert.True(ncOff == 0, $"precondition: without the early-out every pixel converges ({ncOff})");
        Assert.True(stoppedOracle >= total / 10 && stoppedOracle <= total * 9 / 10, $"precondition: the threshold splits the view ({stoppedOracle}/{total})");
        Assert.Equal(on.BasinBuffer.Count(b => b < 0), (int)on.LastEarlyOuts.ChaosStops);
        Assert.True(eq >= total * 0.97, $"{eq}/{total}. {ex}");
    }

    [Fact]
    public void Off_By_Default_And_Carried_By_The_Snapshot_Param_And_Command_Builder()
    {
        Assert.False(new FractalParameters().ChaosEarlyOut);
        foreach (var t in new[] { FractalType.Nova, FractalType.Newton })
        {
            var stock = RegionFractalParams.Snapshot(t, new FractalParameters());
            Assert.True(stock?.ChaosEarlyOut == null && stock?.ChaosEarlyOutLog10 == null, t.ToString());
            var p = new FractalParameters { ChaosEarlyOut = true, ChaosEarlyOutLog10 = 120 };
            var snap = RegionFractalParams.Snapshot(t, p)!;
            var back = System.Text.Json.JsonSerializer.Deserialize<RegionFractalParams>(System.Text.Json.JsonSerializer.Serialize(snap))!;
            var q = new FractalParameters();
            back.ApplyTo(q);
            Assert.True(q.ChaosEarlyOut, t.ToString());
            Assert.Equal(120, q.ChaosEarlyOutLog10);

            var cmdSnap = new FracturingFog.Cli.BatchCommandSnapshot { Fractal = t, Parameters = p, FamilyParams = snap.ToKeyValues() };
            Assert.Contains("ChaosEarlyOut=", FracturingFog.Cli.BatchCommandBuilder.Build(cmdSnap));
            var args = new List<string> { "--fractal", t.ToString(), "--x", "0", "--y", "0", "--zoom", "1", "--out", "o.png" };
            foreach (var kv in cmdSnap.FamilyParams) { args.Add(FracturingFog.Batch.BatchFlags.Param); args.Add(kv.Key + "=" + kv.Value); }
            Assert.True(FracturingFog.Batch.BatchOptions.TryParse(args.ToArray(), 0, out var opts, out var err), err);
            var overlay = RegionFractalParams.FromKeyValues(opts.Params, t, out var error);
            Assert.True(overlay != null, error);
            var fresh = new FractalParameters();
            overlay!.ApplyTo(fresh);
            Assert.True(fresh.ChaosEarlyOut, t.ToString());
            Assert.Equal(120, fresh.ChaosEarlyOutLog10);
        }
        Assert.Null(RegionFractalParams.Snapshot(FractalType.Halley, new FractalParameters { ChaosEarlyOut = true })?.ChaosEarlyOut);
    }
}
