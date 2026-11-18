// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1876 (epic #1428, C7d) — .kfr import and Julia morph past 1e100. Checked:
//   • A .kfr whose Zoom is past 1e100 (1.5E150) or past double (2.4E1000) and whose Re / Im
//     hold 340 digits reads the log zoom (FF's for the frame: + log10(3.5·H / (4·max(W, H))),
//     the #1615 conversion) and the exact centre (the conjugate with ImagPointsUp: 1); a
//     short centre has none; a zoom in double's range is unchanged.
//   • --batch --kfr carries the exact centre (CliExactCentre) and the log zoom.
//   • A deep morph step from a 1e120 view toward the period-250 antenna minibrot (~1e-299,
//     found by DeepNucleusFinder) lands at the log midpoint on the exact nucleus, in the
//     Unbounded tier; α = 1 lands on the minibrot's own zoom; a target no deeper than the
//     view is refused.

using System;
using System.IO;
using FracturingFog.Abstractions.Explore;
using FracturingFog.Batch;
using FracturingFog.FFMath;
using FracturingFog.Models;
using FracturingFog.ViewState;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1876KfrAndMorphDeepTests
{
    private const string Mx = "-0.1010963638456221610257854457386225654638054428262534838769311776607808407404705842748212198105167790334045319085567411939715461442609118823557039076803016157584181434354544823068106664029419378267411891607819526448995396995334466823393126699009817557150425206538866991861344333961600610562775385144259609560703568221643279956133036926714760";
    private const string My = "0.9562865108091415007710960577299774358098333365105291700343143215005246590657167325269784107873398072043444724926469284366752406567465722656200815719741551313831228054884443810571677870203738395055477279309548311953190385170024877917670113105649462054553485859930615004427397447762045980085973915977603864276670744042253335354323566859404712";

    private static string Kfr(string zoom, string re = Mx, string im = My, bool up = false)
        => $"Re: {re}\r\nIm: {im}\r\nZoom: {zoom}\r\nIterations: 5000\r\n" + (up ? "ImagPointsUp: 1\r\n" : "");

    private static double Conv(int w, int h) => Math.Log10(3.5 * h / (4.0 * Math.Max(w, h)));

    [Fact]
    public void A_Deep_Kfr_Reads_The_Log_Zoom_And_Exact_Centre()
    {
        Assert.True(KfrLocation.TryParse(Kfr("1.5E150"), out var a, out var err), err);
        Assert.Equal(Math.Log10(1.5) + 150, a!.KfZoomLog10, 10);
        Assert.Equal(Math.Log10(1.5) + 150 + Conv(1920, 1080), a.FfZoomLog10(1920, 1080), 10);
        Assert.Equal(1.5e150 * 3.5 * 1080 / (4.0 * 1920), a.FfZoom(1920, 1080));   // in double: the #1615 formula
        Assert.True(ExactCentre.TryParse(Mx, My, out var exact));
        Assert.Equal(exact, a.CentreExact());

        // Past double: the zoom saturates, the log carries it.
        Assert.True(KfrLocation.TryParse(Kfr("2.4E1000"), out var b, out err), err);
        Assert.Equal(double.MaxValue, b!.KfZoom);
        Assert.Equal(Math.Log10(2.4) + 1000, b.KfZoomLog10, 9);
        Assert.Equal(Math.Log10(2.4) + 1000 + Conv(3840, 2160), b.FfZoomLog10(3840, 2160), 9);
        Assert.Equal(double.MaxValue, b.FfZoom(3840, 2160));

        // ImagPointsUp: the conjugate, as Centre() does with the limbs.
        Assert.True(KfrLocation.TryParse(Kfr("1E200", up: true), out var c, out err), err);
        Assert.Equal(exact.Re, c!.CentreExact()!.Value.Re);
        Assert.Equal(-exact.Im, c.CentreExact()!.Value.Im);
        Assert.Equal(-c.Centre().Y[0], exact.ToDeep().Im.X0);

        // A short centre (OD holds it) has no exact centre.
        Assert.True(KfrLocation.TryParse(Kfr("1E20", "-0.75", "0.1"), out var d, out err), err);
        Assert.Null(d!.CentreExact());
    }

    [Fact]
    public void Batch_Kfr_Carries_The_Exact_Centre_And_Log_Zoom()
    {
        string path = Path.Combine(Path.GetTempPath(), $"s1876-{Guid.NewGuid():N}.kfr");
        File.WriteAllText(path, Kfr("3E500"));
        try
        {
            Assert.True(BatchOptions.TryParse(new[] { "--kfr", path, "--width", "800", "--height", "600", "--out", "o.png" }, 0, out var opts, out var err), err);
            Assert.True(BatchOptions.ResolveKfr(opts, out err), err);
            Assert.True(ExactCentre.TryParse(Mx, My, out var exact));
            var got = opts.CliExactCentre();
            Assert.NotNull(got);
            Assert.Equal(exact.Re, got!.Value.Re.WithPrecision(exact.Re.Precision));
            Assert.Equal(exact.Im, got.Value.Im.WithPrecision(exact.Im.Precision));
            Assert.Equal(Math.Log10(3) + 500 + Conv(800, 600), opts.ZoomLog10!.Value, 9);
            Assert.Equal(double.MaxValue, opts.Zoom);
        }
        finally { File.Delete(path); }
    }

    // The period-250 antenna minibrot (~1e-299) by DeepNucleusFinder, in a view around it.
    private static DeepNucleusResult Antenna250()
    {
        const int n = 250, prec = 1300;
        var c = BigFloat.FromDouble(-2, prec);
        var one = BigFloat.FromDouble(1, prec);
        for (int step = 0; step < 40; step++)
        {
            var z = BigFloat.Zero(prec); var dz = BigFloat.Zero(prec);
            for (int k = 0; k < n; k++)
            {
                dz = BigFloat.Add(BigFloat.Mul(z, dz, prec).ScaleB(1), one, prec);
                z = BigFloat.Add(BigFloat.Mul(z, z, prec), c, prec);
            }
            c = BigFloat.Sub(c, BigFloat.Div(z, dz, prec), prec);
        }
        // Search a disk 1e-295 wide just off the nucleus (holds it; the minibrot is ~1e-299).
        var view = new ExactCentre(BigFloat.Add(c, BigFloat.Parse("3e-297", prec), prec), BigFloat.Zero(prec));
        var found = DeepNucleusFinder.FindMinibrot(view, -295);
        Assert.True(found.Found, found.Status.ToString());
        Assert.Equal(n, found.Period);
        return found;
    }

    [Fact]
    public void A_Deep_Morph_Step_Lands_At_The_Log_Midpoint_On_The_Nucleus()
    {
        var q = Antenna250();
        double mzl = -q.SizeLog10;
        Assert.InRange(mzl, 299, 300);

        var step = JuliaMorph.PlanDeep(q, 120, QualityPreset.Extreme, iterLocked: false, lockedIterations: 0);
        Assert.True(step.Ok);
        Assert.Equal(0.5 * 120 + 0.5 * mzl, step.Plan.ZoomLog10, 10);
        Assert.Equal(q.Nucleus, step.Plan.CenterExact);
        Assert.Same(QualityPreset.Unbounded, step.Plan.Quality);
        Assert.Equal(Math.Max(QualityPreset.Unbounded.ComputeIterationsLog10(step.Plan.ZoomLog10), 250 * 100), step.Plan.PreferredIterations);

        // α = 1: the minibrot's own zoom; a view already as deep is refused.
        Assert.Equal(mzl, JuliaMorph.PlanDeep(q, 120, QualityPreset.Extreme, false, 0, depth: 1).Plan.ZoomLog10, 10);
        Assert.Equal(JuliaMorphRefusal.NotDeeper, JuliaMorph.PlanDeep(q, mzl + 1, QualityPreset.Unbounded, false, 0).Refusal);
    }
}
