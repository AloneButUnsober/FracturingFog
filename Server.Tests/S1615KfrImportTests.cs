// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1615 — Kalles Fraktaler .kfr locations (format: Karl Runmo / Claude
// Heiland-Allen, KF 2+; Docs/Resources-Bibliography.md #kf-kfr-format).
//   • The centre splits exactly: limb 0 is .NET's correctly rounded parse of the
//     decimal, limbs never overlap (each ≤ half an ulp of the one before), and
//     their exact sum is the decimal to ~2^-424 relative (8 × 53 bits).
//   • Zoom: KF spans the height with 4/zoom, FF's Mandelbrot the longer side with
//     3.5/zoom: one pixel is the same size in both.
//   • Orientation: FF image row j has Im = cy + (j − H/2)·scale, the direction KF's
//     GetPixelCoordinates uses without "ImagPointsUp" (checked against a direct
//     escape-time count on each half of the frame), so the centre is NOT
//     conjugated unless ImagPointsUp: 1.
//   • --kfr fills the batch view; explicit --x / --y / --zoom / --iter win; bad
//     files, non-Mandelbrot formulas and rotated views are refused.
// Sample: bench-kit/ladder/40-mini-p128-1e13.kfr (fractadyne bench-kit,
// WindySnowOwl), its location lines only.

using System;
using System.IO;
using System.Linq;
using System.Numerics;
using FracturingFog.Batch;
using FracturingFog.Models;
using FracturingFog.ViewState;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1615KfrImportTests
{
    private const string Re = "-7.145519235131990488163335631423597550599502471617637408317e-1";
    private const string Im = "3.540207347648327221719984333721934753758406620526068439482e-1";
    private const string Sample =
        "Re: " + Re + "\r\nIm: " + Im + "\r\nZoom: 1.256030E13\r\nIterations: 3840\r\nIterDiv: 0.500000\r\n"
        + "Rotate: 0.000000\r\nRatio: 360.000000\r\nPower: 2\r\nFractalType: 0\r\n";

    private static KfrLocation Parse(string text)
    {
        Assert.True(KfrLocation.TryParse(text, out var loc, out string? err), err);
        return loc!;
    }

    // Exact value of a double, independent of the code under test.
    private static (BigInteger Num, BigInteger Den) Exact(double d)
    {
        if (d == 0) return (0, 1);
        long bits = BitConverter.DoubleToInt64Bits(d);
        int exp = (int)((bits >> 52) & 0x7FF);
        long mant = bits & 0xFFFFFFFFFFFFFL;
        if (exp == 0) exp = 1; else mant |= 1L << 52;
        int e = exp - 1075;
        BigInteger m = d < 0 ? -mant : mant;
        return e >= 0 ? (m << e, 1) : (m, BigInteger.One << -e);
    }

    private static (BigInteger Num, BigInteger Den) ExactDecimal(string s)
    {
        int ePos = s.IndexOfAny(new[] { 'e', 'E' });
        int exp = ePos < 0 ? 0 : int.Parse(s[(ePos + 1)..]);
        string mant = ePos < 0 ? s : s[..ePos];
        bool neg = mant.StartsWith('-');
        mant = mant.TrimStart('-', '+');
        int dot = mant.IndexOf('.');
        int frac = dot < 0 ? 0 : mant.Length - dot - 1;
        BigInteger n = BigInteger.Parse(mant.Replace(".", ""));
        if (neg) n = -n;
        int p = exp - frac;
        return p >= 0 ? (n * BigInteger.Pow(10, p), 1) : (n, BigInteger.Pow(10, -p));
    }

    [Theory]
    [InlineData(Re)]
    [InlineData(Im)]
    [InlineData("-0.0494700290631040937516922267273536301187457124882248793181049402326421947726869034279915499747594190000000000000000000")]
    [InlineData("1.5")]
    [InlineData("0")]
    [InlineData("-2.5e-30")]
    public void A_Decimal_Splits_Exactly_Into_Non_Overlapping_Limbs(string s)
    {
        var limbs = KfrLocation.DecimalToLimbs(s, 8);
        Assert.Equal(double.Parse(s, System.Globalization.CultureInfo.InvariantCulture), limbs[0]);
        for (int k = 1; k < limbs.Length; k++)
            if (limbs[k] != 0)
                Assert.True(Math.Abs(limbs[k]) <= (Math.BitIncrement(Math.Abs(limbs[k - 1])) - Math.Abs(limbs[k - 1])) / 2,
                    $"limb {k} ({limbs[k]:R}) overlaps limb {k - 1} ({limbs[k - 1]:R})");

        // Σ limbs vs the decimal, exactly: |Σ − dec| ≤ |dec| · 2^-420.
        var (dn, dd) = ExactDecimal(s);
        BigInteger sn = 0, sd = 1;
        foreach (double l in limbs)
        {
            var (ln, ld) = Exact(l);
            sn = sn * ld + ln * sd; sd *= ld;
        }
        BigInteger errNum = BigInteger.Abs(sn * dd - dn * sd), errDen = sd * dd;
        if (dn.IsZero) { Assert.True(errNum.IsZero); return; }
        // errNum/errDen ≤ |dn|/dd · 2^-420  ⇔  errNum · dd · 2^420 ≤ |dn| · errDen
        Assert.True(errNum * dd * (BigInteger.One << 420) <= BigInteger.Abs(dn) * errDen, "limb sum is not the decimal");
    }

    [Fact]
    public void The_Kit_Sample_Parses_Unconjugated_With_The_Height_Zoom_Converted()
    {
        var loc = Parse(Sample);
        Assert.Equal(1.256030e13, loc.KfZoom);
        Assert.Equal(3840, loc.Iterations);
        Assert.False(loc.ImagPointsUp);
        var (x, y) = loc.Centre();
        Assert.Equal(double.Parse(Re, System.Globalization.CultureInfo.InvariantCulture), x[0]);
        Assert.Equal(double.Parse(Im, System.Globalization.CultureInfo.InvariantCulture), y[0]);   // not −Im
        Assert.Equal(1.256030e13 * 0.4921875, loc.FfZoom(3840, 2160));
        Assert.Equal(1.256030e13 * 0.875, loc.FfZoom(2160, 3840));

        // Same pixel size: KF 4/(zoom·H), FF 3.5/(zoom'·max(W, H)).
        foreach (var (w, h) in new[] { (3840, 2160), (2160, 3840), (1000, 1000), (733, 211) })
        {
            double kf = 4.0 / (loc.KfZoom * h), ff = 3.5 / (loc.FfZoom(w, h) * Math.Max(w, h));
            Assert.Equal(kf, ff, kf * 1e-14);
        }
    }

    [Fact]
    public void Imaginary_Axis_Up_Conjugates_The_Centre()
    {
        var (_, y) = Parse(Sample + "ImagPointsUp: 1\n").Centre();
        var (_, y0) = Parse(Sample).Centre();
        Assert.Equal(y0.Select(v => -v), y);
    }

    [Theory]
    [InlineData("FractalType: 1\n", "Mandelbrot")]
    [InlineData("Power: 3\n", "Mandelbrot")]
    [InlineData("RotateAngle: 30\n", "rotat")]
    [InlineData("StretchAmount: 0.5\n", "rotat")]
    [InlineData("Ratio: 200\n", "rotat")]
    public void Unsupported_Views_Are_Refused(string extra, string why)
    {
        string text = string.Join("\n", Sample.Split('\n').Where(l => !l.StartsWith(extra.Split(':')[0] + ":"))) + extra;
        Assert.False(KfrLocation.TryParse(text, out _, out string? err));
        Assert.Contains(why, err!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Im: 0\nZoom: 1\n")]
    [InlineData("Re: x\nIm: 0\nZoom: 1\n")]
    [InlineData("Re: 0\nIm: 0\nZoom: -1\n")]
    [InlineData("Re: 0\nIm: 0\nZoom: 1\nIterations: 0\n")]
    public void Malformed_Files_Are_Refused(string text)
        => Assert.False(KfrLocation.TryParse(text, out _, out _));

    // The orientation claim, against an independent escape-time count: with KF's map
    // (Im = cy + (row − H/2)·scale, no ImagPointsUp) FF's frame must have the same
    // in-set count in each half. Centre +0.5i: the set's body is in the top half.
    [Fact]
    public void Ff_Rows_Run_The_Same_Way_As_Kf_Rows()
    {
        const int W = 64, H = 64, Iter = 200;
        double cx = -0.12, cy = 0.5, zoom = 1;
        var c = new MandelbrotCalculator(W, H)
        {
            CenterX = cx, CenterY = cy, Zoom = zoom, MaxIterations = Iter,
            Quality = QualityPreset.Standard, ColorMap = new HsvPalette(),
        };
        c.Calculate();
        double s = 3.5 / Math.Max(W, H) / zoom;
        int ffTop = 0, ffBottom = 0, kfTop = 0, kfBottom = 0;
        for (int j = 0; j < H; j++)
            for (int i = 0; i < W; i++)
            {
                bool ffIn = c.IterationBuffer[j * W + i] >= Iter;
                double re = cx + (i - W / 2.0) * s, im = cy + (j - H / 2.0) * s;
                double zr = 0, zi = 0; int n = 0;
                while (n < Iter && zr * zr + zi * zi <= 4) { (zr, zi) = (zr * zr - zi * zi + re, 2 * zr * zi + im); n++; }
                bool kfIn = n >= Iter;
                if (j < H / 2) { if (ffIn) ffTop++; if (kfIn) kfTop++; }
                else { if (ffIn) ffBottom++; if (kfIn) kfBottom++; }
            }
        Assert.True(kfTop > 2 * kfBottom, $"precondition: KF map puts the body on top ({kfTop} vs {kfBottom})");
        Assert.InRange(ffTop, kfTop * 0.9, kfTop * 1.1);
        Assert.InRange(ffBottom, kfBottom * 0.8 - 5, kfBottom * 1.2 + 5);
    }

    // ── batch ──

    private static string WriteKfr(string text)
    {
        string p = Path.Combine(Path.GetTempPath(), $"ff-s1615-{Guid.NewGuid():N}.kfr");
        File.WriteAllText(p, text);
        return p;
    }

    // Parse, then resolve the .kfr as BatchEntry does.
    private static bool Parse(out BatchOptions opts, out string? err, params string[] extra)
        => BatchOptions.TryParse(new[] { "app.exe", "--batch", "--out", "o.png" }.Concat(extra).ToArray(), 2, out opts, out err)
           && BatchOptions.ResolveKfr(opts, out err);

    [Fact]
    public void Kfr_Fills_The_Batch_View()
    {
        string p = WriteKfr(Sample);
        try
        {
            Assert.True(Parse(out var o, out string? err, "--kfr", p, "--width", "3840", "--height", "2160"), err);
            var (x, y) = Parse(Sample).Centre();
            Assert.Equal(x[0], o.CenterX);
            Assert.Equal(x, o.CenterXLimbs);
            Assert.Equal(y, o.CenterYLimbs);
            Assert.Equal(1.256030e13 * 0.4921875, o.Zoom);
            Assert.Equal(3840, o.Iterations);

            // Order doesn't matter; explicit flags win.
            Assert.True(Parse(out var o2, out err, "--zoom", "5e12", "--iter", "100", "--kfr", p, "--y", "0.25"), err);
            Assert.Equal(5e12, o2.Zoom);
            Assert.Equal(100, o2.Iterations);
            Assert.Equal(0.25, o2.CenterY);
            Assert.Null(o2.CenterYLimbs);
            Assert.Equal(x, o2.CenterXLimbs);
        }
        finally { File.Delete(p); }
    }

    [Fact]
    public void Kfr_Errors_Are_Reported()
    {
        string p = WriteKfr(Sample);
        string deep = WriteKfr(Sample.Replace("Zoom: 1.256030E13", "Zoom: 1e120"));
        try
        {
            Assert.False(Parse(out _, out string? err, "--kfr", p, "--region", "Seahorse Valley"));
            Assert.Contains("--region", err);
            Assert.False(Parse(out _, out err, "--kfr", p, "--fractal", "Julia"));
            Assert.Contains("Mandelbrot", err);
            Assert.False(Parse(out _, out err, "--kfr", p + ".missing"));
            Assert.Contains("can't read", err);
            Assert.False(Parse(out _, out err, "--kfr", deep));
            Assert.Contains("zoom", err, StringComparison.OrdinalIgnoreCase);
        }
        finally { File.Delete(p); File.Delete(deep); }
    }
}
