// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Explore/BaseAngle.cs
//
// Finder S7 follow-up (#1242, epic #1184): external angles for z^d + c.
// For degree d the circle dynamics is θ ↦ d·θ mod 1, so a rational angle is
// kept EXACTLY as base-d digits θ = .pre(per): the n-th digit is the d-th of
// the circle that d^{n−1}·θ sits in. A periodic θ lands on a point of a
// hyperbolic component whose multiplier is 1 — the root or, for d > 2, one of
// the d − 2 co-roots (every component of the Multibrot set has d − 1 such
// points; the main body's are its cusps). A preperiodic θ lands on a
// Misiurewicz point. ExternalAngle stays the binary (z² + c) form with its
// kneading / internal-address machinery; this class is the plain base-d angle.
//
// Text forms: ".(01)", "0.2(01)", ".12" (terminating = period "0"), and
// fractions "1/8". Digits are 0–9 then a–z, so the base is at most 36. A
// fraction p/q with q = q₁·b, gcd(b, d) = 1 and q₁ | d^a has a pre part of
// a digits and a period equal to the multiplicative order of d mod b.

using System;
using System.Numerics;
using System.Text;

namespace FracturingFog.Abstractions.Explore;

/// <summary>Exact rational external angle in base-d pre(per) form.</summary>
public sealed class BaseAngle : IEquatable<BaseAngle>
{
    /// <summary>Longest pre / period accepted (digits).</summary>
    public const int MaxDigits = 2048;
    public const int MinBase = 2;
    public const int MaxBase = 36;

    public int Base { get; }
    /// <summary>Preperiodic digits (may be empty).</summary>
    public string Pre { get; }
    /// <summary>Repeating digits (never empty; "0" for a terminating angle).</summary>
    public string Per { get; }

    public int Preperiod => Pre.Length;
    public int Period => Per.Length;
    public bool IsPeriodic => Pre.Length == 0;

    private BaseAngle(int b, string pre, string per)
    {
        Base = b;
        (Pre, Per) = Canonical(b, pre, per);
    }

    /// <summary>Build from digit strings; reduced to the canonical (shortest) form.</summary>
    public static BaseAngle FromDigits(int b, string pre, string per)
    {
        CheckBase(b);
        ArgumentNullException.ThrowIfNull(pre);
        ArgumentNullException.ThrowIfNull(per);
        pre = pre.ToLowerInvariant(); per = per.ToLowerInvariant();
        if (per.Length == 0) throw new ArgumentException("The periodic part must not be empty (use \"0\" for a terminating angle).", nameof(per));
        if (pre.Length > MaxDigits || per.Length > MaxDigits) throw new ArgumentException($"At most {MaxDigits} digits per part.");
        foreach (char ch in pre + per)
            if (DigitValue(ch) is not int v || v >= b) throw new ArgumentException($"Not a base-{b} digit: '{ch}'.");
        return new BaseAngle(b, pre, per);
    }

    /// <summary>Exact numerator / denominator (reduced), θ = N / D.</summary>
    public (BigInteger N, BigInteger D) Fraction
    {
        get
        {
            // θ = (pre·(d^per − 1) + per) / (d^pre · (d^per − 1))
            BigInteger pre = Value(Base, Pre), per = Value(Base, Per);
            BigInteger m = BigInteger.Pow(Base, Per.Length) - 1;
            BigInteger n = pre * m + per;
            BigInteger d = BigInteger.Pow(Base, Pre.Length) * m;
            BigInteger g = BigInteger.GreatestCommonDivisor(n, d);
            if (g.IsZero) return (BigInteger.Zero, BigInteger.One);
            return (n / g, d / g);
        }
    }

    /// <summary>Digit n (1-based) of θ.</summary>
    public int Digit(int n)
    {
        if (n < 1) throw new ArgumentOutOfRangeException(nameof(n));
        char ch = n <= Pre.Length ? Pre[n - 1] : Per[(n - 1 - Pre.Length) % Per.Length];
        return DigitValue(ch)!.Value;
    }

    /// <summary>d^k·θ mod 1 as a double.</summary>
    public double ShiftedValue(int k)
    {
        double v = 0, w = 1.0 / Base;
        for (int i = 1; w > 1e-20; i++, w /= Base) v += Digit(k + i) * w;
        return v;
    }

    public override string ToString() => "." + Pre + "(" + Per + ")";

    /// <summary>Parse ".pre(per)", "0.pre(per)", ".digits" or "p/q" in base <paramref name="b"/>.</summary>
    public static bool TryParse(string? text, int b, out BaseAngle? angle, out string? error)
    {
        angle = null; error = null;
        CheckBase(b);
        string s = (text ?? "").Trim().Replace(" ", "").Replace("_", "");
        if (s.Length == 0) { error = "Enter an angle such as .(01) or 1/8."; return false; }
        try
        {
            int slash = s.IndexOf('/');
            if (slash >= 0)
            {
                if (!BigInteger.TryParse(s[..slash], out var p) || !BigInteger.TryParse(s[(slash + 1)..], out var q) || q <= 0 || p < 0)
                { error = "A fraction needs whole numbers p/q with q > 0."; return false; }
                angle = FromFraction(p, q, b);
                return true;
            }
            if (s.StartsWith("0.")) s = s[1..];
            if (!s.StartsWith(".")) { error = $"Base-{b} angles start with '.', e.g. .(01) or .1(02)."; return false; }
            s = s[1..];
            int open = s.IndexOf('(');
            if (open < 0) { angle = FromDigits(b, s, "0"); return true; }   // terminating
            if (!s.EndsWith(")") || s.IndexOf(')') != s.Length - 1)
            { error = "The repeating part goes in one pair of brackets at the end, e.g. .1(02)."; return false; }
            angle = FromDigits(b, s[..open], s[(open + 1)..^1]);
            return true;
        }
        catch (ArgumentException ex) { error = ex.Message; return false; }
    }

    /// <summary>Exact p/q → base-d pre(per).</summary>
    public static BaseAngle FromFraction(BigInteger p, BigInteger q, int b)
    {
        CheckBase(b);
        if (q <= 0) throw new ArgumentOutOfRangeException(nameof(q));
        p = ((p % q) + q) % q;
        var g = BigInteger.GreatestCommonDivisor(p, q);
        if (!g.IsZero && !g.IsOne) { p /= g; q /= g; }
        if (p.IsZero) return new BaseAngle(b, "", "0");

        // q = q₁·r with r coprime to the base; the pre part has a digits where
        // a is the least power with q₁ | b^a.
        BigInteger r = q;
        for (var gg = BigInteger.GreatestCommonDivisor(r, b); !gg.IsOne; gg = BigInteger.GreatestCommonDivisor(r, b)) r /= gg;
        BigInteger q1 = q / r;
        int a = 0;
        for (BigInteger pw = BigInteger.One; pw % q1 != 0; pw *= b)
            if (++a > MaxDigits) throw new ArgumentException($"Preperiod over {MaxDigits} digits.");

        var pre = new StringBuilder();
        BigInteger x = p;
        for (int i = 0; i < a; i++)
        {
            x *= b;
            pre.Append(DigitChar((int)(x / q)));
            x %= q;
        }
        if (r.IsOne) return new BaseAngle(b, pre.ToString(), "0");
        // x/q = y/r exactly (the remaining denominator divides r).
        BigInteger y = x / q1;
        var per = new StringBuilder();
        BigInteger t = y;
        do
        {
            t *= b;
            per.Append(DigitChar((int)(t / r)));
            t %= r;
            if (per.Length > MaxDigits) throw new ArgumentException($"Period over {MaxDigits} digits.");
        } while (t != y);
        return new BaseAngle(b, pre.ToString(), per.ToString());
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private static void CheckBase(int b)
    {
        if (b < MinBase || b > MaxBase) throw new ArgumentOutOfRangeException(nameof(b), $"Base must be {MinBase}–{MaxBase}.");
    }

    public static char DigitChar(int v) => (char)(v < 10 ? '0' + v : 'a' + v - 10);

    private static int? DigitValue(char ch)
        => ch is >= '0' and <= '9' ? ch - '0' : ch is >= 'a' and <= 'z' ? ch - 'a' + 10 : null;

    private static BigInteger Value(int b, string s)
    {
        BigInteger v = BigInteger.Zero;
        foreach (char ch in s) v = v * b + DigitValue(ch)!.Value;
        return v;
    }

    // Shortest equivalent form: minimal period, trailing pre digits folded into
    // the period, and a (d−1)-recurring tail rewritten as a terminating angle.
    private static (string Pre, string Per) Canonical(int b, string pre, string per)
    {
        int len = per.Length;
        for (int k = 1; k < len; k++)
        {
            if (len % k != 0) continue;
            bool rep = true;
            for (int i = k; i < len && rep; i++) rep = per[i] == per[i - k];
            if (rep) { per = per[..k]; break; }
        }
        while (pre.Length > 0 && pre[^1] == per[^1])
        {
            per = per[^1] + per[..^1];
            pre = pre[..^1];
        }
        char top = DigitChar(b - 1);
        if (per.Length == 1 && per[0] == top)
        {
            if (pre.Length == 0) return ("", "0");                 // .(d−1) = 1 ≡ 0
            // .x y (d−1)(d−1)… = .x (y+1): the last pre digit is not d−1 here.
            int last = DigitValue(pre[^1])!.Value;
            return Canonical(b, pre[..^1] + DigitChar(last + 1), "0");
        }
        return (pre, per);
    }

    public bool Equals(BaseAngle? other) => other is not null && Base == other.Base && Pre == other.Pre && Per == other.Per;
    public override bool Equals(object? obj) => Equals(obj as BaseAngle);
    public override int GetHashCode() => HashCode.Combine(Base, Pre, Per);
}
