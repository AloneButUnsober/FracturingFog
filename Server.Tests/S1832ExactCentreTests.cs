// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1832 (epic #1428, C2a) — the exact view centre (ExactCentre, a BigFloat pair) in the
// view state and in region JSON. Checked:
//   • A centre given as 320-digit decimals (zoom ~1e300) survives the view state, a region,
//     JSON and the region store unchanged: the parsed value is the decimal to within half
//     an ulp at its precision (rational check), and every hop returns that same value.
//   • The OD limbs that travel with it are its greedy split, so the ≤1e100 readers agree.
//   • A limb-only write (a pan, old code) drops the exact centre; the limbs then rule.
//   • Without an exact centre nothing changes: no new JSON keys, the same limbs.
//   • Tolerant load: damaged or half-present text is dropped, an exact pair whose limbs were
//     edited rewrites them.

using System;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using FracturingFog.FFMath;
using FracturingFog.Models;
using FracturingFog.ViewState;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1832ExactCentreTests
{
    // 320 significant digits each (about a 1e300-zoom centre). The leading digits are the
    // bench-kit p951,094 centre; the tail is arbitrary, beyond OD's ~124 digits.
    private static string Digits(Random r, int n) => new(Enumerable.Range(0, n).Select(_ => (char)('0' + r.Next(10))).ToArray());
    private static readonly string Xs = "-0.0280410543055045481329219950301" + Digits(new Random(18321), 290);
    private static readonly string Ys = "-0.6948927538996524051894898383054" + Digits(new Random(18322), 290);

    private static ExactCentre Centre() => ExactCentre.TryParse(Xs, Ys, out var c) ? c : throw new InvalidOperationException();

    // |value − decimal| ≤ 2^-P · |decimal|: the parse is the decimal, rounded once.
    private static void AssertIsDecimal(string dec, BigFloat v)
    {
        string t = dec.TrimStart('-');
        int dot = t.IndexOf('.');
        BigInteger n = BigInteger.Parse(t.Replace(".", ""), CultureInfo.InvariantCulture) * (dec.StartsWith('-') ? -1 : 1);
        BigInteger d = BigInteger.Pow(10, dot < 0 ? 0 : t.Length - dot - 1);
        // v = M·2^E as a rational, against n / d.
        BigInteger vn = v.Exponent >= 0 ? v.Mantissa << (int)v.Exponent : v.Mantissa, vd = v.Exponent >= 0 ? BigInteger.One : BigInteger.One << (int)-v.Exponent;
        BigInteger diff = BigInteger.Abs(vn * d - n * vd);           // |v − x| · vd · d
        BigInteger bound = BigInteger.Abs(n) * vd;                    // |x| · vd · d
        Assert.True(diff << v.Precision <= bound, "the exact centre is not the decimal rounded once");
    }

    [Fact]
    public void The_Exact_Centre_Is_The_Decimal_And_Its_Limbs_Are_Its_Split()
    {
        var c = Centre();
        Assert.True(c.Precision >= 320 * 3.32);
        AssertIsDecimal(Xs, c.Re);
        AssertIsDecimal(Ys, c.Im);
        var (x, y) = c.ToStrings();
        Assert.True(ExactCentre.TryParse(x, y, out var back, c.Precision));   // at the precision it was written at
        Assert.Equal(c.Re, back.Re);
        Assert.Equal(c.Im, back.Im);
        // The limbs: the nearest double first, then each remainder's.
        var od = c.ToDeep();
        Assert.Equal(c.Re.ToDouble(), od.Re.X0);
        Assert.Equal(c.Im.ToDouble(), od.Im.X0);
        var rest = BigFloat.Sub(c.Re, BigFloat.FromDouble(od.Re.X0, c.Precision), c.Precision);
        Assert.Equal(rest.ToDouble(), od.Re.X1);
    }

    [Fact]
    public void The_View_State_Keeps_The_Exact_Centre_Until_The_Limbs_Move()
    {
        var c = Centre();
        var vs = new FractalViewState();
        Assert.Null(vs.CenterExact);
        vs.SetCenterExact(c);
        Assert.Equal(c, vs.CenterExact);
        Assert.True(ExactCentre.SameLimbs(c.ToDeep(), vs.GetCenter()));
        Assert.Equal(c, vs.GetCenterExact(c.Precision));

        // A limb-only writer (any pre-#1832 code: a pan, a jitter) drops it; the limbs rule.
        vs.CenterX7 += Math.ScaleB(Math.Abs(vs.CenterX7) + 1e-300, -10);
        Assert.Null(vs.CenterExact);
        Assert.Equal(ExactCentre.FromDeep(vs.GetCenter(), c.Precision), vs.GetCenterExact(c.Precision));

        // SetCenter (the OD path) drops it as well, unless it writes the same limbs.
        vs.SetCenterExact(c);
        vs.SetCenter(c.ToDeep());
        Assert.Equal(c, vs.CenterExact);
        vs.SetCenter(new DeepComplex(new OD(-0.5), new OD(0)));
        Assert.Null(vs.CenterExact);
    }

    [Fact]
    public void A_Region_Carries_It_Through_Json_And_Back_Into_A_View()
    {
        var c = Centre();
        var vs = new FractalViewState();
        vs.SetCenterExact(c);
        var r = new FractalRegion { Name = "exact-1832", Zoom = 1e300 };
        r.CopyCentreFrom(vs);
        Assert.NotNull(r.CenterExactX);

        string json = JsonSerializer.Serialize(r);
        var back = JsonSerializer.Deserialize<FractalRegion>(json)!;
        Assert.Equal(c, back.CentreExact);
        Assert.True(ExactCentre.SameLimbs(c.ToDeep(), back.Centre));

        var vs2 = new FractalViewState();
        back.CopyCentreTo(vs2);
        Assert.Equal(c, vs2.CenterExact);
        Assert.True(ExactCentre.SameLimbs(c.ToDeep(), vs2.GetCenter()));
    }

    [Fact]
    public void The_Region_Store_Saves_And_Loads_It()
    {
        var c = Centre();
        var lib = FractalRegionLibrary.Instance;
        string name = "S1832 exact centre " + Guid.NewGuid().ToString("N");
        var r = new FractalRegion { Name = name, Zoom = 1e300 };
        r.SetCentreExact(c);
        try
        {
            Assert.True(lib.AddUserRegion(r));   // saves
            lib.Load();                           // reads regions.json back
            var loaded = lib.FindByName(name);
            Assert.NotNull(loaded);
            Assert.Equal(c, loaded!.CentreExact);
            Assert.True(ExactCentre.SameLimbs(c.ToDeep(), loaded.Centre));
        }
        finally { lib.RemoveUserRegion(name); }
    }

    [Fact]
    public void Without_An_Exact_Centre_Nothing_Changes()
    {
        var vs = new FractalViewState { CenterX = -0.743643887037151, CenterXLo = 1.2e-17, CenterY = 0.131825904205330 };
        var r = new FractalRegion { Name = "plain" };
        r.CopyCentreFrom(vs);
        Assert.Null(r.CenterExactX);
        Assert.Null(r.CenterExactY);
        string json = JsonSerializer.Serialize(r);
        Assert.DoesNotContain("CenterExact", json);
        var back = JsonSerializer.Deserialize<FractalRegion>(json)!;
        Assert.Null(back.CentreExact);
        var vs2 = new FractalViewState();
        back.CopyCentreTo(vs2);
        Assert.Null(vs2.CenterExact);
        Assert.True(ExactCentre.SameLimbs(vs.GetCenter(), vs2.GetCenter()));
    }

    [Theory]
    [InlineData("not a number", "0.5")]
    [InlineData("0.5", null)]
    [InlineData("1e", "2")]
    public void A_Damaged_Exact_Centre_Is_Dropped_And_The_Limbs_Rule(string? x, string? y)
    {
        var r = new FractalRegion { Name = "damaged", CenterX = -0.75, CenterY = 0.1, CenterExactX = x, CenterExactY = y };
        var back = JsonSerializer.Deserialize<FractalRegion>(JsonSerializer.Serialize(r))!;
        Assert.Null(back.CenterExactX);
        Assert.Null(back.CenterExactY);
        Assert.Equal(-0.75, back.CenterX);
        Assert.Equal(0.1, back.CenterY);
    }

    [Fact]
    public void An_Exact_Pair_Whose_Limbs_Were_Edited_Rewrites_Them()
    {
        var c = Centre();
        var r = new FractalRegion { Name = "edited" };
        r.SetCentreExact(c);
        r.CenterX = 0.25; r.CenterX5 = 0; r.CenterY3 = 1e-40;   // a hand edit of the limbs only
        var back = JsonSerializer.Deserialize<FractalRegion>(JsonSerializer.Serialize(r))!;
        Assert.Equal(c, back.CentreExact);
        Assert.True(ExactCentre.SameLimbs(c.ToDeep(), back.Centre));
    }

    // A hand-written pair (no CenterExactBits) is read at its digit count.
    [Fact]
    public void A_Hand_Written_Pair_Is_Read_At_Its_Digit_Count()
    {
        var r = new FractalRegion { Name = "hand", CenterExactX = Xs, CenterExactY = Ys };
        var back = JsonSerializer.Deserialize<FractalRegion>(JsonSerializer.Serialize(r))!;
        Assert.Equal(Centre(), back.CentreExact);
        AssertIsDecimal(Xs, back.CentreExact!.Value.Re);
        Assert.True(ExactCentre.SameLimbs(Centre().ToDeep(), back.Centre));
    }

    // A centre with every bit significant (made by arithmetic, not parsed from a shorter
    // decimal): its text needs all of BigFloat's round-trip digits.
    [Fact]
    public void A_Full_Precision_Centre_Round_Trips_Through_Json()
    {
        var c = Centre();
        int p = c.Precision;
        var full = new ExactCentre(BigFloat.Mul(c.Re, c.Im, p), BigFloat.Add(BigFloat.Mul(c.Im, c.Im, p), c.Re, p));
        var r = new FractalRegion { Name = "full" };
        r.SetCentreExact(full);
        var back = JsonSerializer.Deserialize<FractalRegion>(JsonSerializer.Serialize(r))!;
        Assert.Equal(full, back.CentreExact);
        var vs = new FractalViewState();
        vs.SetCenterExact(full);
        var r2 = new FractalRegion();
        r2.CopyCentreFrom(vs);
        Assert.Equal(full, JsonSerializer.Deserialize<FractalRegion>(JsonSerializer.Serialize(r2))!.CentreExact);
    }
}
