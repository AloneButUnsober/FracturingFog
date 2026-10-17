// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1275 — re-scoped. The cluster-rebase port the issue asked for turned out
// unnecessary: with BLA off, the CPU perturbation path matches "rebase every
// pixel" to the chaotic floor on every view tried, so no glitch handling is
// missing. What was wrong was BLA's fixed linearisation tolerance ε = 1e-6: near a
// deep minibrot (period 998, 1.6e15, 30 000 iterations) it left ~13 % of the
// exterior > 2 iterations off direct OD iteration. ε is now
// FractalParameters.BlaTolerance (default unchanged), carried by the Mandelbrot
// region snapshot (so batch --param and the Command builder carry it), and the
// BLA lookup starts at the level the iteration is aligned to.
//   • the aligned Lookup returns exactly what the old top-down walk did;
//   • against direct OD iteration (independent of the perturbation code) a tight
//     ε is as accurate as BLA off, and the default is measurably not — the view
//     discriminates (precondition);
//   • changing the tolerance on a live calculator rebuilds the table;
//   • snapshot / --param / Command builder round trip, family reset;
//   • the poster (batch / export) path honours it.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.FFMath;
using FracturingFog.Imaging;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1275BlaToleranceTests(ITestOutputHelper output)
{
    // Framed period-998 minibrot (NucleusFinder.Frame of the minibrot found near
    // the seahorse valley at radius 1e-12).
    private static readonly double[] Re = [-0.7436438870371591, 4.113735014488743E-17, -2.305746675780485E-33, 1.0000171637720302E-49];
    private static readonly double[] Im = [0.13182590420531254, -5.421673941589486E-18, 3.2629712147478705E-35, 1.5849070032816E-51];
    private const double Zoom = 1597376414482044.2;
    private const int MaxIter = 30000, W = 96, H = 72;

    private static MandelbrotCalculator Calc(double tol, bool blaOff = false) => new(W, H)
    {
        CenterX = Re[0], CenterXLo = Re[1], CenterX2 = Re[2], CenterX3 = Re[3],
        CenterY = Im[0], CenterYLo = Im[1], CenterY2 = Im[2], CenterY3 = Im[3],
        Zoom = Zoom, MaxIterations = MaxIter, Quality = QualityPreset.Extreme,
        ColorMap = new HsvPalette(), BlaTolerance = tol, DisableAcceleration = blaOff,
    };

    private static int[] Iter(double tol, bool blaOff = false)
    {
        var c = Calc(tol, blaOff);
        c.Calculate();
        return (int[])c.IterationBuffer.Clone();
    }

    // ── Lookup ───────────────────────────────────────────────────────────────

    private static int NaiveLookup(BlaTable t, int n, double dMag2, int maxIter)
    {
        for (int k = t.Levels - 1; k >= 0; k--)
        {
            int l = 1 << k;
            if ((n & (l - 1)) != 0) continue;
            int idx = n >> k;
            if (idx >= t.LevelLen[k]) continue;
            if (n + l > t.RefLen) continue;
            if (n + l > maxIter) continue;
            var b = t.Data[t.LevelStart[k] + idx];
            if (b.R2 > 0.0 && dMag2 < b.R2) return t.LevelStart[k] + idx;
        }
        return -1;
    }

    [Theory]
    [InlineData(1e-6)]
    [InlineData(1e-12)]
    public void Aligned_Lookup_Returns_What_The_Full_Walk_Did(double eps)
    {
        // A non-escaping reference (inside a period-3 bulb) so the table is deep.
        const int len = 6000;
        var zr = new double[len]; var zi = new double[len];
        double x = 0, y = 0;
        for (int n = 0; n < len; n++) { zr[n] = x; zi[n] = y; double nx = x * x - y * y - 0.1225611668766536; y = 2 * x * y + 0.7448617666197442; x = nx; }
        var t = new BlaTable(zr, zi, len, 1e-30, eps);
        Assert.Equal(eps, t.Epsilon);
        Assert.True(t.Levels >= 12);

        var rng = new Random(1275);
        int hits = 0;
        for (int i = 0; i < 50000; i++)
        {
            int n = rng.Next(4) == 0 ? rng.Next(len / 64) * 64 : rng.Next(len + 5);
            double dMag2 = Math.Pow(10, -40 + 34 * rng.NextDouble());
            int maxIter = n + rng.Next(len);
            int expect = NaiveLookup(t, n, dMag2, maxIter);
            Assert.Equal(expect, t.Lookup(n, dMag2, maxIter));
            if (expect >= 0 && t.Data[expect].L > 1) hits++;
        }
        Assert.True(hits > 1000, $"only {hits} multi-step BLAs chosen: the comparison would be vacuous");
    }

    // ── Accuracy against direct OD iteration ───────────────────────────────

    private static (int[] Px, int[] It) DirectOd(int samples)
    {
        double scale = (3.5 / Math.Max(W, H)) / Zoom;
        var cRe = new OD(Re[0], Re[1], Re[2], Re[3], 0, 0, 0, 0);
        var cIm = new OD(Im[0], Im[1], Im[2], Im[3], 0, 0, 0, 0);
        var rng = new Random(5);
        var px = new int[samples]; var its = new int[samples];
        for (int s = 0; s < samples; s++)
        {
            int xx = rng.Next(W), yy = rng.Next(H);
            OD cx = OD.FromCenterOffset(cRe, xx - W * 0.5, scale), cy = OD.FromCenterOffset(cIm, yy - H * 0.5, scale);
            OD zr = new(0.0), zi = new(0.0);
            int it;
            for (it = 0; it < MaxIter; it++)
            {
                if (zr.X0 * zr.X0 + zi.X0 * zi.X0 >= 512.0 * 512.0) break;   // the calculator's escape radius
                OD nzi = (zr * zi) * 2.0 + cy; zr = zr.Square() - zi.Square() + cx; zi = nzi;
            }
            px[s] = yy * W + xx; its[s] = it;
        }
        return (px, its);
    }

    private static int Off((int[] Px, int[] It) truth, int[] iter)
    {
        int bad = 0;
        for (int s = 0; s < truth.Px.Length; s++) if (Math.Abs(iter[truth.Px[s]] - truth.It[s]) > 2) bad++;
        return bad;
    }

    [Fact]
    public void A_Tight_Tolerance_Tracks_Direct_Iteration_Like_Bla_Off()
    {
        var truth = DirectOd(120);
        int fast = Off(truth, Iter(1e-6));
        int exact = Off(truth, Iter(1e-12));
        int off = Off(truth, Iter(1e-6, blaOff: true));
        output.WriteLine($"pixels > 2 iterations off direct OD (of 120): ε 1e-6 {fast}, ε 1e-12 {exact}, BLA off {off}");
        // Precondition: this view tells the tolerances apart.
        Assert.True(fast >= exact + 6, $"default ε {fast} vs tight ε {exact} pixels off of 120: the view does not discriminate");
        // Tight ε is as good as no BLA (chaotic floor), within a pixel or two.
        Assert.True(exact <= off + 2, $"tight ε {exact} pixels off vs BLA off {off}");
        Assert.True(exact <= 6, $"tight ε {exact} of 120 pixels off direct OD iteration");
    }

    [Fact]
    public void Changing_The_Tolerance_On_A_Live_Calculator_Rebuilds_The_Table()
    {
        Assert.Equal(1e-6, new FractalParameters().BlaTolerance);
        Assert.Equal(BlaTable.DefaultEpsilon, new FractalParameters().BlaTolerance);
        Assert.Equal(BlaTable.DefaultEpsilon, new MandelbrotCalculator(4, 4).BlaTolerance);

        var c = Calc(1e-6);
        c.Calculate();
        var loose = (int[])c.IterationBuffer.Clone();
        c.BlaTolerance = 1e-12;
        c.Calculate();
        var tight = Iter(1e-12);
        Assert.Equal(tight, c.IterationBuffer);
        Assert.NotEqual(loose, tight);

        // Nonsense values fall back to the default rather than disabling BLA's guard.
        Assert.Equal(loose, Iter(0.0));
        Assert.Equal(loose, Iter(double.NaN));
    }

    // ── Region snapshot / batch / Command builder ──────────────────────────

    [Fact]
    public void The_Mandelbrot_Snapshot_Carries_It_Only_When_Set()
    {
        Assert.Null(RegionFractalParams.Snapshot(FractalType.Mandelbrot, new FractalParameters()));

        var p = new FractalParameters { BlaTolerance = 1e-10 };
        var snap = RegionFractalParams.Snapshot(FractalType.Mandelbrot, p);
        Assert.Equal(1e-10, snap!.BlaTolerance);
        var back = System.Text.Json.JsonSerializer.Deserialize<RegionFractalParams>(System.Text.Json.JsonSerializer.Serialize(snap));
        var q = new FractalParameters();
        back!.ApplyTo(q);
        Assert.Equal(1e-10, q.BlaTolerance);
        Assert.Equal(1e-10, p.Clone().BlaTolerance);

        // Region recall resets it: the family owns the property (#960).
        Assert.Contains(RegionFractalParams.FamilyProperties(FractalType.Mandelbrot), pi => pi.Name == nameof(FractalParameters.BlaTolerance));
        RegionFractalParams.ResetFamilyToDefaults(FractalType.Mandelbrot, q);
        Assert.Equal(1e-6, q.BlaTolerance);
    }

    [Fact]
    public void The_Command_Builder_Emits_It_And_The_Batch_Parser_Reads_It_Back()
    {
        var p = new FractalParameters { BlaTolerance = 1e-10 };
        var snap = new BatchCommandSnapshot
        {
            Fractal = FractalType.Mandelbrot, Parameters = p,
            FamilyParams = RegionFractalParams.Snapshot(FractalType.Mandelbrot, p)!.ToKeyValues(),
        };
        string cmd = BatchCommandBuilder.Build(snap);
        Assert.Contains("BlaTolerance=", cmd);
        Assert.DoesNotContain(BatchCommandBuilder.DetectGaps(snap), g => g.Contains("BLA", StringComparison.OrdinalIgnoreCase));

        var pair = snap.FamilyParams.Single(k => k.Key == "BlaTolerance");
        Assert.True(BatchOptions.TryParse(new[] { "--x", "0", "--y", "0", "--zoom", "1", "--out", "o.png",
            BatchFlags.Param, pair.Key + "=" + pair.Value }, 0, out var opts, out var err), err);
        var overlay = RegionFractalParams.FromKeyValues(opts.Params, FractalType.Mandelbrot, out var error);
        Assert.True(overlay != null, error);
        var fresh = new FractalParameters();
        overlay!.ApplyTo(fresh);
        Assert.Equal(1e-10, fresh.BlaTolerance);
    }

    [Fact]
    public void The_Poster_Path_Honours_It()
    {
        uint[] Poster(double tol) => PosterRenderer.RenderToPixels(new PosterRequest
        {
            FractalType = FractalType.Mandelbrot, Width = W, Height = H,
            CenterX = Re[0], CenterXLo = Re[1], CenterX2 = Re[2], CenterX3 = Re[3],
            CenterY = Im[0], CenterYLo = Im[1], CenterY2 = Im[2], CenterY3 = Im[3],
            Zoom = Zoom, MaxIterations = MaxIter, Quality = QualityPreset.Extreme,
            ColorMap = new HsvPalette(),
            FractalParameters = new FractalParameters { BlaTolerance = tol },
            Path = string.Empty,
        }, CancellationToken.None, out _, out _);

        // #1344 — export runs the preset's sub-pixel AA, as the live view does.
        uint[] Live(double tol)
        {
            var c = Calc(tol);
            c.Calculate();
            FracturingFog.Rendering.Supersampling.AccumulateMandelbrot(c, c.Quality.AaSamples,
                FracturingFog.Rendering.AdaptiveAa.Off, CancellationToken.None);
            return (uint[])c.ColorBuffer.Clone();
        }

        var tight = Poster(1e-12);
        Assert.Equal(Live(1e-12), tight);
        Assert.NotEqual(Poster(1e-6), tight);
    }
}
