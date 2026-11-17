// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1833 (epic #1428, C2b) — the exact centre (#1832) through the other carriers: the CX / CY
// text (ViewCentreText, CoordNotation), the server request DTO and the cluster planners.
// History snapshots and video keyframes are in the WinExe / UI (not loadable here); they
// call SetCenterExact / FractalRegion.CopyCentreTo, which S1832 covers.
//   • A decimal longer than 28 digits now lands in all eight limbs: their exact sum is the
//     decimal to within OD's ~2^-424 (rational check), where decimal's 28 digits stopped.
//   • Up to 28 digits the text parses as before (the limbs of the old path).
//   • The CX / CY boxes: exact text in, exact centre set; exact centre out as plain decimal
//     that reads back to the same centre; one box edited keeps the other's exact part.
//   • The DTO round-trips the exact centre through JSON (absent: no new keys), rewrites edited
//     limbs from it and drops damaged text; the image and video planners hand it to every tile.

using System;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using FracturingFog.FFMath;
using FracturingFog.Server.Cluster;
using FracturingFog.Server.Protocol;
using FracturingFog.ViewState;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1833ExactCentreCarriersTests
{
    private static string Digits(Random r, int n) => new(Enumerable.Range(0, n).Select(_ => (char)('0' + r.Next(10))).ToArray());
    private static readonly string Xs = "-0.0280410543055045481329219950301" + Digits(new Random(18331), 290);
    private static readonly string Ys = "-0.6948927538996524051894898383054" + Digits(new Random(18332), 290);
    private static ExactCentre Centre() => ExactCentre.TryParse(Xs, Ys, out var c) ? c : throw new InvalidOperationException();

    // The decimal text as an exact rational n / d.
    private static (BigInteger N, BigInteger D) Rational(string dec)
    {
        string t = dec.TrimStart('-');
        int dot = t.IndexOf('.');
        BigInteger n = BigInteger.Parse(t.Replace(".", ""), CultureInfo.InvariantCulture) * (dec.StartsWith('-') ? -1 : 1);
        return (n, BigInteger.Pow(10, dot < 0 ? 0 : t.Length - dot - 1));
    }

    // |sum of limbs − decimal| / |decimal| as a power of two.
    private static double RelErrLog2(string dec, double[] limbs)
    {
        var (n, d) = Rational(dec);
        var sum = BigFloat.FromOD(new OD(limbs[0], limbs[1], limbs[2], limbs[3], limbs[4], limbs[5], limbs[6], limbs[7]), 2000);
        BigInteger sn = sum.Exponent >= 0 ? sum.Mantissa << (int)sum.Exponent : sum.Mantissa;
        BigInteger sd = sum.Exponent >= 0 ? BigInteger.One : BigInteger.One << (int)-sum.Exponent;
        BigInteger diff = BigInteger.Abs(sn * d - n * sd);
        if (diff.IsZero) return double.NegativeInfinity;
        return BigInteger.Log(diff, 2) - BigInteger.Log(BigInteger.Abs(n) * sd, 2);
    }

    private static double[] Parse(string s)
    {
        Assert.True(CoordNotation.TryParse(s, out double a, out double b, out double c, out double d,
            out double e, out double f, out double g, out double h));
        return new[] { a, b, c, d, e, f, g, h };
    }

    [Fact]
    public void A_Long_Decimal_Fills_All_Eight_Limbs_Exactly()
    {
        string s = Xs.Substring(0, 130);   // 127 significant digits: more than decimal's 28, about OD's limit
        var l = Parse(s);
        double err = RelErrLog2(s, l);
        Assert.True(err < -400, $"limb sum off by 2^{err:F0}");
        Assert.NotEqual(0, l[4]);   // the OD tail is used, not left at zero
    }

    [Theory]
    [InlineData("0.1")]
    [InlineData("-0.7436438870371587")]
    [InlineData("-0.028041054305504548132921995")]   // 26 digits
    public void Up_To_28_Digits_The_Limbs_Are_The_Decimal_Paths(string s)
    {
        var l = Parse(s);
        // The decimal path: hi = the nearest double, then the remainders (decimal-exact), four limbs at most.
        Assert.Equal(double.Parse(s, CultureInfo.InvariantCulture), l[0]);
        Assert.Equal(0, l[4]); Assert.Equal(0, l[5]); Assert.Equal(0, l[6]); Assert.Equal(0, l[7]);
        // The 26-digit text is still the decimal within decimal's own precision (a short
        // one is its shortest-form double by design, #1252).
        if (s.Length > 20) Assert.True(RelErrLog2(s, l) < -85);
    }

    [Fact]
    public void The_Exact_Format_Is_Plain_And_Reads_Back_To_The_Same_Value()
    {
        var c = Centre();
        foreach (var v in new[] { c.Re, c.Im, BigFloat.Mul(c.Re, c.Im, c.Precision), c.Re.ScaleB(-40) })
        {
            string t = CoordNotation.FormatExact(v);
            Assert.DoesNotContain("e", t);
            Assert.DoesNotContain("E", t);
            // Round trip at the value's own precision (the text's digits are its round-trip digits).
            Assert.True(CoordNotation.TryParseExact(t, v.Precision, out var back));
            Assert.Equal(v, back);
        }
        Assert.Equal("0", CoordNotation.FormatExact(BigFloat.Zero(64)));
        Assert.Equal("1.5", CoordNotation.FormatExact(BigFloat.FromDouble(1.5, 64)));
        Assert.Equal("-1024", CoordNotation.FormatExact(BigFloat.FromDouble(-1024, 64)));
    }

    [Fact]
    public void The_Boxes_Set_And_Show_The_Exact_Centre()
    {
        var vs = new FractalViewState();
        Assert.True(ViewCentreText.Apply(vs, Xs, Ys));
        var e = vs.CenterExact;
        Assert.NotNull(e);
        Assert.Equal(Centre(), e!.Value.WithPrecision(Centre().Precision));
        Assert.True(ExactCentre.SameLimbs(e.Value.ToDeep(), vs.GetCenter()));

        // Shown as plain exact text, which applied again gives the same centre.
        string x = ViewCentreText.FormatX(vs, pipe: false), y = ViewCentreText.FormatY(vs, pipe: true);
        Assert.StartsWith("-0.0280410543055045481329219950301", x);
        // Read back (a paste into another view): the text's own value, parsed at its digit
        // count, so it can differ from the shown centre past its precision (double rounding),
        // never by more than 2^-(P-2) relative. An untouched box is not re-read at all.
        var vs2 = new FractalViewState();
        Assert.True(ViewCentreText.Apply(vs2, x, y));
        int p = e.Value.Precision;
        foreach (var (a, b) in new[] { (e.Value.Re, vs2.CenterExact!.Value.Re), (e.Value.Im, vs2.CenterExact!.Value.Im) })
        {
            var d = BigFloat.Sub(a, b, 2 * p).Abs();
            Assert.True(d.IsZero || d.ScaleB(p - 2) <= a.Abs(), "the shown text does not read back to the centre");
        }
    }

    [Fact]
    public void Editing_One_Box_Keeps_The_Other_Exact_Part()
    {
        var vs = new FractalViewState();
        ViewCentreText.Apply(vs, Xs, Ys);
        var before = vs.CenterExact!.Value;

        // A new long X: Y keeps its exact value.
        string x2 = "-0.0280410543055045481329219950302" + Digits(new Random(5), 200);
        Assert.True(ViewCentreText.Apply(vs, x2, null));
        Assert.Equal(before.Im, vs.CenterExact!.Value.Im.WithPrecision(before.Im.Precision));
        Assert.True(CoordNotation.TryParseExact(x2, vs.CenterExact!.Value.Precision, out var wantX));
        Assert.Equal(wantX, vs.CenterExact!.Value.Re);

        // A short X: Y still needs more than OD, so the exact centre stays, X from its limbs.
        Assert.True(ViewCentreText.Apply(vs, "-0.75", null));
        Assert.NotNull(vs.CenterExact);
        Assert.Equal(-0.75, vs.CenterExact!.Value.Re.ToDouble());
        Assert.Equal(before.Im, vs.CenterExact!.Value.Im.WithPrecision(before.Im.Precision));

        // Both short: no exact centre; the limbs are the text.
        Assert.True(ViewCentreText.Apply(vs, "-0.75", "0.1"));
        Assert.Null(vs.CenterExact);
        Assert.Equal(-0.75, vs.CenterX);
        Assert.Equal(0.1, vs.CenterY);
    }

    [Fact]
    public void Without_An_Exact_Centre_The_Boxes_Show_What_They_Did()
    {
        var vs = new FractalViewState { CenterX = -0.743643887037151, CenterXLo = 1.2e-17, CenterY = 0.131825904205330, CenterYLo = -3e-18 };
        foreach (bool pipe in new[] { false, true })
        {
            Assert.Equal(CoordNotation.Format(pipe, vs.CenterX, vs.CenterXLo), ViewCentreText.FormatX(vs, pipe));
            Assert.Equal(CoordNotation.Format(pipe, vs.CenterY, vs.CenterYLo), ViewCentreText.FormatY(vs, pipe));
        }
        Assert.False(ViewCentreText.Apply(vs, null, null));
    }

    // ── The request DTO and the planners ─────────────────────────────────────────

    private static RenderRequestDto Request(ExactCentre? c)
    {
        var r = new RenderRequestDto { Mode = "image", FractalType = "Mandelbrot", Width = 1024, Height = 512, Zoom = 1e300, CenterX = -0.5, CenterY = 0 };
        if (c is { } e)
        {
            var d = e.ToDeep();
            r.CenterX = d.Re.X0; r.CenterXLo = d.Re.X1; r.CenterX2 = d.Re.X2; r.CenterX3 = d.Re.X3;
            r.CenterX4 = d.Re.X4; r.CenterX5 = d.Re.X5; r.CenterX6 = d.Re.X6; r.CenterX7 = d.Re.X7;
            r.CenterY = d.Im.X0; r.CenterYLo = d.Im.X1; r.CenterY2 = d.Im.X2; r.CenterY3 = d.Im.X3;
            r.CenterY4 = d.Im.X4; r.CenterY5 = d.Im.X5; r.CenterY6 = d.Im.X6; r.CenterY7 = d.Im.X7;
            (r.CenterExactX, r.CenterExactY) = e.ToStrings();
            r.CenterExactBits = e.Precision;
        }
        return r;
    }

    [Fact]
    public void The_Request_Round_Trips_It_And_Normalises_On_Load()
    {
        var c = Centre();
        var back = JsonSerializer.Deserialize<RenderRequestDto>(JsonSerializer.Serialize(Request(c)))!;
        Assert.Equal(c, back.CentreExact());

        string plain = JsonSerializer.Serialize(Request(null));
        Assert.DoesNotContain("centerExact", plain);
        Assert.Null(JsonSerializer.Deserialize<RenderRequestDto>(plain)!.CentreExact());

        var edited = Request(c);
        edited.CenterX = 0.25; edited.CenterY5 = 0;
        var e2 = JsonSerializer.Deserialize<RenderRequestDto>(JsonSerializer.Serialize(edited))!;
        Assert.Equal(c.ToDeep().Re.X0, e2.CenterX);
        Assert.Equal(c.ToDeep().Im.X5, e2.CenterY5);

        var damaged = Request(null);
        damaged.CenterExactX = "x"; damaged.CenterExactY = "0.5";
        var e3 = JsonSerializer.Deserialize<RenderRequestDto>(JsonSerializer.Serialize(damaged))!;
        Assert.Null(e3.CenterExactX);
        Assert.Equal(-0.5, e3.CenterX);
    }

    [Fact]
    public void The_Image_And_Video_Planners_Hand_It_To_Every_Tile_At_Its_Own_Offset()
    {
        var c = Centre();
        var image = TilePlanner.PlanImage(Request(c), 256);
        Assert.True(image.TileCount > 1);
        // #1838 — each tile's exact centre is the image's plus the tile's own offset, in
        // pixels (pixel = 3.5 / 1024 / 1e300, from decimal text). Before #1838 every tile
        // carried the image's, which the worker's load turned into the image centre.
        int p = c.Precision + 64;
        var pixel = BigFloat.Parse("3.5e-300", p) / BigFloat.FromDouble(1024, p);
        Assert.All(image.Tiles, t =>
        {
            var e = t.Render.CentreExact()!.Value;
            double ox = t.OffsetX + t.Render.Width * 0.5 - 1024 * 0.5;
            double oy = t.OffsetY + t.Render.Height * 0.5 - 512 * 0.5;
            Assert.Equal(ox, BigFloat.Div(BigFloat.Sub(e.Re, c.Re, p), pixel, p).ToDouble(), 6);
            Assert.Equal(oy, BigFloat.Div(BigFloat.Sub(e.Im, c.Im, p), pixel, p).ToDouble(), 6);
            Assert.Equal(e.ToDeep().Re.X0, t.Render.CenterX);   // the limbs are the tile's too
        });
        Assert.Contains(image.Tiles, t => t.OffsetX > 0);

        var v = Request(c);
        v.Mode = "video"; v.VideoFps = 10; v.VideoSeconds = 1;
        var video = FramePlanner.PlanVideo(v, framesPerTileHint: 2);
        Assert.True(video.TileCount > 1);
        Assert.All(video.Tiles, t => Assert.Equal(c, t.Render.CentreExact()));

        // Without one, the tiles carry none.
        Assert.All(TilePlanner.PlanImage(Request(null), 256).Tiles, t => Assert.Null(t.Render.CenterExactX));
    }
}
