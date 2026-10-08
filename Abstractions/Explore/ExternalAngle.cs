// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Explore/ExternalAngle.cs
//
// Interesting-location finder S4 (#1188, epic #1184). Design:
// Docs/Technical/Interesting-Location-Finder-DesignPlan.md §3.5.
//
// A rational external angle θ ∈ [0, 1), kept EXACTLY as binary
//     θ = .pre(per)  — a preperiodic bit string followed by a repeating one.
// Angle doubling θ ↦ 2θ mod 1 is the dynamics on the circle, so the n-th bit is
// the half of the circle (θ < ½ → 0, θ ≥ ½ → 1) the angle sits in after n−1
// doublings: the "left/right" itinerary. A periodic θ (no pre part) lands on
// the root of a hyperbolic component of that period; a preperiodic θ lands on
// a Misiurewicz point (Douady–Hubbard).
//
// Accepted text forms: ".(011)", "0.01(10)", ".0101" (terminating = period
// "0"), and fractions "1/7", "5/12". Fractions are converted exactly: with
// q = 2^a·b (b odd), the pre part has a bits and the period is the
// multiplicative order of 2 mod b.

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;

namespace FracturingFog.Abstractions.Explore;

/// <summary>Exact rational external angle in binary pre(per) form.</summary>
public sealed class ExternalAngle : IEquatable<ExternalAngle>
{
    /// <summary>Longest pre / period accepted (bits). Keeps the exact
    /// arithmetic and the ray trace bounded.</summary>
    public const int MaxBits = 4096;

    /// <summary>Preperiodic bits (may be empty).</summary>
    public string Pre { get; }

    /// <summary>Repeating bits (never empty; "0" for a terminating angle).</summary>
    public string Per { get; }

    public int Preperiod => Pre.Length;
    public int Period => Per.Length;
    public bool IsPeriodic => Pre.Length == 0;

    private ExternalAngle(string pre, string per)
    {
        (Pre, Per) = Canonical(pre, per);
    }

    /// <summary>Build from bit strings; reduced to the canonical (shortest) form.</summary>
    public static ExternalAngle FromBits(string pre, string per)
    {
        if (pre == null || per == null) throw new ArgumentNullException(pre == null ? nameof(pre) : nameof(per));
        if (per.Length == 0) throw new ArgumentException("The periodic part must not be empty (use \"0\" for a terminating angle).", nameof(per));
        if (pre.Length > MaxBits || per.Length > MaxBits) throw new ArgumentException($"At most {MaxBits} bits per part.");
        foreach (char ch in pre + per)
            if (ch != '0' && ch != '1') throw new ArgumentException($"Not a binary digit: '{ch}'.");
        return new ExternalAngle(pre, per);
    }

    /// <summary>Exact numerator / denominator (reduced), θ = N / D.</summary>
    public (BigInteger N, BigInteger D) Fraction
    {
        get
        {
            // θ = (pre·(2^per − 1) + per) / (2^pre · (2^per − 1))
            BigInteger pre = Bits(Pre), per = Bits(Per);
            BigInteger m = (BigInteger.One << Per.Length) - 1;
            BigInteger n = pre * m + per;
            BigInteger d = (BigInteger.One << Pre.Length) * m;
            BigInteger g = BigInteger.GreatestCommonDivisor(n, d);
            if (g.IsZero) return (BigInteger.Zero, BigInteger.One);
            return (n / g, d / g);
        }
    }

    /// <summary>Bit n (1-based) of θ: 0 if 2^{n−1}θ mod 1 &lt; ½.</summary>
    public int Bit(int n)
    {
        if (n < 1) throw new ArgumentOutOfRangeException(nameof(n));
        if (n <= Pre.Length) return Pre[n - 1] - '0';
        return Per[(n - 1 - Pre.Length) % Per.Length] - '0';
    }

    /// <summary>2^k·θ mod 1 as a double (53 bits of the doubled angle).</summary>
    public double DoubledValue(int k)
    {
        double v = 0, w = 0.5;
        for (int i = 1; i <= 60; i++, w *= 0.5) v += Bit(k + i) * w;
        return v;
    }

    public override string ToString() => "." + Pre + "(" + Per + ")";

    /// <summary>Parse ".pre(per)", "0.pre(per)", ".bits" or "p/q".</summary>
    public static bool TryParse(string? text, out ExternalAngle? angle, out string? error)
    {
        angle = null; error = null;
        string s = (text ?? "").Trim().Replace(" ", "").Replace("_", "");
        if (s.Length == 0) { error = "Enter an angle such as .(011) or 1/7."; return false; }
        try
        {
            int slash = s.IndexOf('/');
            if (slash >= 0)
            {
                if (!BigInteger.TryParse(s[..slash], out var p) || !BigInteger.TryParse(s[(slash + 1)..], out var q) || q <= 0 || p < 0)
                { error = "A fraction needs whole numbers p/q with q > 0."; return false; }
                angle = FromFraction(p, q);
                return true;
            }
            if (s.StartsWith("0.")) s = s[1..];
            if (!s.StartsWith(".")) { error = "Binary angles start with '.', e.g. .(011) or .01(10)."; return false; }
            s = s[1..];
            int open = s.IndexOf('(');
            if (open < 0) { angle = FromBits(s, "0"); return true; }   // terminating
            if (!s.EndsWith(")") || s.IndexOf(')') != s.Length - 1)
            { error = "The repeating part goes in one pair of brackets at the end, e.g. .01(10)."; return false; }
            angle = FromBits(s[..open], s[(open + 1)..^1]);
            return true;
        }
        catch (ArgumentException ex) { error = ex.Message; return false; }
    }

    /// <summary>Exact p/q → binary pre(per).</summary>
    public static ExternalAngle FromFraction(BigInteger p, BigInteger q)
    {
        if (q <= 0) throw new ArgumentOutOfRangeException(nameof(q));
        p = ((p % q) + q) % q;                       // into [0, 1)
        var g = BigInteger.GreatestCommonDivisor(p, q);
        if (!g.IsZero && !g.IsOne) { p /= g; q /= g; }
        if (p.IsZero) return new ExternalAngle("", "0");

        int a = 0;
        BigInteger b = q;
        while (b.IsEven) { b >>= 1; a++; }
        if (a > MaxBits) throw new ArgumentException($"Preperiod over {MaxBits} bits.");

        // Pre bits: the first a binary digits of p/q.
        var pre = new StringBuilder();
        BigInteger r = p;
        for (int i = 0; i < a; i++) { r <<= 1; if (r >= q) { pre.Append('1'); r -= q; } else pre.Append('0'); }
        // r/q is now r/(2^a b) with denominator dividing… normalise to r'/b.
        BigInteger rb = r / (BigInteger.One << a);   // exact: r is a multiple of 2^a here
        if (b.IsOne) return new ExternalAngle(pre.ToString(), "0");

        // Period: order of 2 mod b; bits: the repeating expansion of rb/b.
        var per = new StringBuilder();
        BigInteger x = rb;
        do
        {
            x <<= 1;
            if (x >= b) { per.Append('1'); x -= b; } else per.Append('0');
            if (per.Length > MaxBits) throw new ArgumentException($"Period over {MaxBits} bits.");
        } while (x != rb);
        return new ExternalAngle(pre.ToString(), per.ToString());
    }

    // ── Kneading + internal address ──────────────────────────────────────

    /// <summary>Kneading sequence ν₁ν₂… of θ: νₙ = 1 when 2^{n−1}θ lies in the
    /// open arc (θ/2, (θ+1)/2), 0 outside it, '*' on its boundary (which ends
    /// the sequence for a periodic θ). Exact rational arithmetic.</summary>
    public string Kneading(int maxTerms)
    {
        var (n, d) = Fraction;
        var sb = new StringBuilder();
        BigInteger x = n;                 // 2^{k−1}θ mod 1 = x / d
        BigInteger lo = n, hi = n + d;    // compare 2x against θ·d·… : 2x vs n and n+d
        for (int k = 1; k <= maxTerms; k++)
        {
            BigInteger two = x << 1;
            if (two == lo || two == hi) { sb.Append('*'); break; }
            sb.Append(two > lo && two < hi ? '1' : '0');
            x = (x << 1) % d;
        }
        return sb.ToString();
    }

    /// <summary>Internal address 1 → S₁ → S₂ → … (Lau–Schleicher): S₀ = 1 and
    /// S_{j+1} = ρ(S_j), ρ(r) = min{ m &gt; r : ν_m ≠ ν_{m−r} }. For a periodic θ it
    /// ends at θ's period.</summary>
    public IReadOnlyList<int> InternalAddress(int maxTerms = 64, int maxIndex = 4 * MaxBits)
    {
        string nu = Kneading(maxIndex);
        int N = nu.Length;
        char At(int m) => nu[m - 1];
        var address = new List<int> { 1 };
        int s = 1;
        while (address.Count < maxTerms)
        {
            int next = -1;
            for (int m = s + 1; m <= N; m++)
                if (At(m) != At(m - s)) { next = m; break; }
            if (next < 0) break;
            address.Add(next);
            if (At(next) == '*') break;    // reached the period of a periodic angle
            s = next;
        }
        return address;
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private static BigInteger Bits(string s)
    {
        BigInteger v = BigInteger.Zero;
        foreach (char ch in s) v = (v << 1) + (ch - '0');
        return v;
    }

    // Shortest equivalent form: minimal period (per not a repetition of a
    // shorter word), then fold trailing pre bits into the period.
    private static (string Pre, string Per) Canonical(string pre, string per)
    {
        int len = per.Length;
        for (int d = 1; d < len; d++)
        {
            if (len % d != 0) continue;
            bool rep = true;
            for (int i = d; i < len && rep; i++) rep = per[i] == per[i - d];
            if (rep) { per = per[..d]; break; }
        }
        while (pre.Length > 0 && pre[^1] == per[^1])
        {
            per = per[^1] + per[..^1];
            pre = pre[..^1];
        }
        if (per == "1" && pre.Length == 0) per = "0";         // .(1) = 1 ≡ 0
        else if (per == "1")
        {
            // .x0(1) = .x1(0): normalise the 1-recurring tail.
            int i = pre.Length - 1;
            pre = pre[..i] + "1";
            per = "0";
            (pre, per) = Canonical(pre, per);
        }
        return (pre, per);
    }

    public bool Equals(ExternalAngle? other) => other is not null && Pre == other.Pre && Per == other.Per;
    public override bool Equals(object? obj) => Equals(obj as ExternalAngle);
    public override int GetHashCode() => HashCode.Combine(Pre, Per);
}
