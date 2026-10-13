// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Explore/AddressAngles.cs
//
// Finder S4b (#1237): (angled) internal address → the external angle pair of
// its hyperbolic component, combinatorially (exact, BigInteger).
//
// Three facts do the work:
//  • The k/m bulb of the main cardioid: its m-cycle x_0 < … < x_{m−1} under
//    doubling has rotation number k/m, so x_i's j-th binary digit is
//    [(i + j·k) mod m ≥ m − k]; the bulb's angles bound the shortest gap of
//    the cycle (width 1/(2^m − 1)).
//  • Tuning: the k/m sub-wake of a component with angle words w₋, w₊ (its
//    angles' period blocks) has the main-cardioid k/m angles with every 0
//    replaced by w₋ and every 1 by w₊.
//  • Wakes nest or are disjoint, and the components of one period n in an
//    interval free of lower-period wakes pair up as consecutive period-n
//    angles j / (2ⁿ − 1).
// An internal address s₀ = 1 → s₁ → … lists, from the main cardioid out to
// the component, the lowest periods met on the way. So the step s → q goes
// into a k/m sub-wake of the current component (s·m ≥ q) and lands on:
//  • its satellite bulb, when s·m = q (nothing lies between); else
//  • a period-q component inside the sub-wake whose own internal address is
//    the address so far plus q: its angles are the period-q angles j/(2^q − 1)
//    in the sub-wake whose internal address (ExternalAngle.InternalAddress,
//    the Lau–Schleicher ρ function) matches, paired consecutively. (Wakes of
//    different periods nest either way, so "lowest period inside" is not
//    enough: the period-4 bulb's wake holds the period-3 airplane.)
// The denominator m is fixed by the address; the numerator (and, rarely, a
// choice between same-period components) picks one of the conjugates — given
// by an angled address (1_1/3 → 3) or, when absent, the first that fits.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace FracturingFog.Abstractions.Explore;

/// <summary>One element of an angled internal address: a period and, on
/// every element but the last, an optional sublimb angle k/m taken there.</summary>
public readonly record struct AddressStep(int Period, int Num = 0, int Den = 0)
{
    public bool HasAngle => Den > 0;
}

/// <summary>The result of <see cref="AddressAngles.ToAngles"/>.</summary>
public sealed record AddressResult(ExternalAngle Lower, ExternalAngle Upper, IReadOnlyList<AddressStep> Resolved, bool Ambiguous);

public static class AddressAngles
{
    /// <summary>Largest period handled (exact angles grow as 2^period).</summary>
    public const int MaxPeriod = 1024;

    /// <summary>Parse "1 → 2 → 4", "1->3->4", "1_1/3 → 3", "1 (2/5) → 5 → 7".</summary>
    public static bool TryParse(string? text, out List<AddressStep> steps, out string? error)
    {
        steps = new List<AddressStep>();
        error = null;
        string s = (text ?? "").Replace("→", "->").Replace("−>", "->").Replace("–>", "->");
        var parts = s.Split("->", StringSplitOptions.TrimEntries);
        if (parts.Length < 2) { error = "An internal address looks like 1 → 2 → 4 (angled: 1_1/3 → 3)."; return false; }
        foreach (var raw in parts)
        {
            string t = raw.Replace("(", " ").Replace(")", " ").Replace("[", " ").Replace("]", " ").Replace("_", " ").Trim();
            var bits = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (bits.Length is 0 or > 2 || !int.TryParse(bits[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int per) || per < 1 || per > MaxPeriod)
            { error = $"'{raw}' is not a period (1..{MaxPeriod}), optionally with an angle like 1_1/3."; return false; }
            int k = 0, m = 0;
            if (bits.Length == 2)
            {
                var f = bits[1].Split('/');
                if (f.Length != 2 || !int.TryParse(f[0], out k) || !int.TryParse(f[1], out m) || m < 2 || k < 1 || k >= m
                    || BigInteger.GreatestCommonDivisor(k, m) != 1)
                { error = $"'{bits[1]}' is not a sublimb angle k/m (0 < k < m, coprime)."; return false; }
            }
            steps.Add(new AddressStep(per, k, m));
        }
        if (steps[0].Period != 1) { error = "An internal address starts at 1 (the main cardioid)."; return false; }
        for (int i = 1; i < steps.Count; i++)
            if (steps[i].Period <= steps[i - 1].Period) { error = "Periods in an internal address increase."; return false; }
        if (steps[^1].HasAngle) { error = "The last element takes no angle."; return false; }
        return true;
    }

    /// <summary>Search budget (component candidates tried) for an address.</summary>
    public const int MaxNodes = 20000;

    /// <summary>The angle pair of the component the address names, or null
    /// with the reason (no such component). A plain address leaves the
    /// conjugate open at each step, and an early choice can strand a later
    /// step, so the steps are searched depth-first with backtracking.</summary>
    public static AddressResult? ToAngles(IReadOnlyList<AddressStep> steps, out string? error)
        => ToAngles(steps, null, out error);

    /// <summary>The angled internal address of the component an angle lands
    /// on: its internal address with, at every step, the sublimb k/m whose
    /// wake holds the angle — so pasting it back picks exactly that component
    /// rather than a conjugate. Null when the angle is not periodic or the
    /// search fails.</summary>
    public static IReadOnlyList<AddressStep>? AngledAddressOf(ExternalAngle angle)
    {
        if (!angle.IsPeriodic || angle.Period < 2) return null;
        var steps = new List<AddressStep>();
        foreach (int p in angle.InternalAddress(MaxPeriod)) steps.Add(new AddressStep(p));
        if (steps.Count < 2 || steps[^1].Period != angle.Period) return null;
        return ToAngles(steps, angle, out _)?.Resolved;
    }

    // Resolve the address; with <paramref name="within"/>, keep only the
    // candidates whose wake holds that angle (its own component's chain).
    private static AddressResult? ToAngles(IReadOnlyList<AddressStep> steps, ExternalAngle? within, out string? error)
    {
        error = null;
        var path = new List<AddressStep>();
        int nodes = 0, deepest = 0;
        bool ambiguous = false;
        var hit = Search(1, 1, "0", "1");
        if (hit is var (lo, hi))
        {
            path.Add(new AddressStep(steps[^1].Period));
            return new AddressResult(ExternalAngle.FromBits("", lo), ExternalAngle.FromBits("", hi), path, ambiguous);
        }
        var at = steps[System.Math.Min(deepest, steps.Count - 1)];
        error = nodes >= MaxNodes
            ? "The address is too long to resolve."
            : deepest + 1 < steps.Count
                ? $"No period-{steps[deepest + 1].Period} component follows period {at.Period}"
                  + (at.HasAngle ? $" in its {at.Num}/{at.Den} sublimb." : ": not an internal address.")
                : "Not an internal address.";
        return null;

        // Resolve steps[i..] from the current component (period s, angle words).
        (string, string)? Search(int i, int s, string wLo, string wHi)
        {
            if (i == steps.Count) return (wLo, wHi);
            deepest = System.Math.Max(deepest, i - 1);
            int q = steps[i].Period;
            var here = steps[i - 1];
            var prefix = new int[i + 1];
            for (int t = 0; t <= i; t++) prefix[t] = steps[t].Period;
            var options = Candidates(here, s, q, wLo, wHi, prefix);
            if (within != null) options = options.FindAll(o => Holds(o.Lo, o.Hi, within));
            if (!here.HasAngle && options.Count > 1) ambiguous = true;
            foreach (var (lo, hi, k, m) in options)
            {
                if (++nodes > MaxNodes) return null;
                path.Add(new AddressStep(here.Period, k, m));
                var r = Search(i + 1, q, lo, hi);
                if (r != null) return r;
                path.RemoveAt(path.Count - 1);
            }
            return null;
        }
    }

    // The next components of period q after the current one (period s,
    // angle words wLo / wHi), in each admissible k/m sub-wake (s·m ≥ q).
    private static List<(string Lo, string Hi, int K, int M)> Candidates(
        AddressStep here, int s, int q, string wLo, string wHi, int[] address)
    {
        var list = new List<(string, string, int, int)>();
        for (int m = System.Math.Max(2, (q + s - 1) / s); m <= q; m++)
            for (int k = 1; k < m; k++)
            {
                if (BigInteger.GreatestCommonDivisor(k, m) != 1) continue;
                if (here.HasAngle && (here.Num != k || here.Den != m)) continue;
                var (a, b) = BulbAngles(k, m);
                string subLo = Tune(a, wLo, wHi), subHi = Tune(b, wLo, wHi);
                if (s * m == q) { list.Add((subLo, subHi, k, m)); continue; }
                foreach (var (lo, hi) in ComponentsWithAddress(subLo, subHi, q, address)) list.Add((lo, hi, k, m));
            }
        return list;
    }

    /// <summary>The main cardioid's k/m bulb angles as period-m bit words
    /// (θ₋ &lt; θ₊).</summary>
    public static (string Lo, string Hi) BulbAngles(int k, int m)
    {
        // Cycle points x_i, i = 0..m−1, ascending; x_i's digit j is
        // [(i + j·k) mod m ≥ m − k].
        var words = new string[m];
        var vals = new BigInteger[m];
        for (int i = 0; i < m; i++)
        {
            var sb = new StringBuilder(m);
            for (int j = 0; j < m; j++) sb.Append((i + (long)j * k) % m >= m - k ? '1' : '0');
            words[i] = sb.ToString();
            vals[i] = Value(words[i]);
        }
        // The bulb's angles bound the shortest gap between consecutive points.
        int best = 0;
        BigInteger bestGap = -1;
        for (int i = 0; i + 1 < m; i++)
        {
            var g = vals[i + 1] - vals[i];
            if (bestGap < 0 || g < bestGap) { bestGap = g; best = i; }
        }
        return (words[best], words[best + 1]);
    }

    /// <summary>Tune a periodic word: 0 → <paramref name="w0"/>, 1 → <paramref name="w1"/>.</summary>
    public static string Tune(string word, string w0, string w1)
    {
        var sb = new StringBuilder(word.Length * w0.Length);
        foreach (char c in word) sb.Append(c == '0' ? w0 : w1);
        return sb.ToString();
    }

    // A periodic angle j/(2ⁿ − 1) as an exact fraction, for comparisons.
    private readonly record struct Frac(BigInteger N, BigInteger D)
    {
        public static Frac Of(string periodicWord) => new(Value(periodicWord), (BigInteger.One << periodicWord.Length) - 1);
        public static bool Less(Frac x, Frac y) => x.N * y.D < y.N * x.D;
    }

    /// <summary>Search budget (digit-tree nodes) for one sub-wake.</summary>
    public const int MaxSearchNodes = 2_000_000;

    /// <summary>The period-q components in the open interval (.(lo), .(hi))
    /// whose internal address is <paramref name="address"/>, as angle word
    /// pairs (ascending), paired consecutively (same-address components'
    /// wakes are disjoint).
    ///
    /// The address fixes the kneading sequence ν₁…ν_{q−1} of those angles
    /// (<see cref="KneadingOf"/>), and a periodic angle's kneading is its
    /// itinerary under doubling: ν_j = 1 exactly when 2^{j−1}θ lies in the
    /// half-circle (θ/2, (θ+1)/2). So the digits of θ are built depth-first
    /// and a branch is dropped as soon as its known digits leave the interval
    /// or decide some ν_j wrongly — exact integer interval arithmetic, any
    /// depth. (Enumerating every period-q angle in the interval instead grows
    /// ~8× per step along an antenna.)</summary>
    public static List<(string Lo, string Hi)> ComponentsWithAddress(string lo, string hi, int q, int[] address)
    {
        var result = new List<(string, string)>();
        var nu = KneadingOf(address);
        if (nu.Length != q - 1) return result;
        Frac a = Frac.Of(lo), b = Frac.Of(hi);
        var match = new List<string>();
        var bits = new char[q];
        int nodes = 0;
        Walk(0, BigInteger.Zero);
        match.Sort((x, y) => Value(x).CompareTo(Value(y)));
        for (int i = 0; i + 1 < match.Count; i += 2) result.Add((match[i], match[i + 1]));
        return result;

        // k digits fixed; P = their value (θ ∈ [P/2^k, (P+1)/2^k]).
        void Walk(int k, BigInteger P)
        {
            if (++nodes > MaxSearchNodes) return;
            BigInteger D = BigInteger.One << k;
            // Interval test: [P/D, (P+1)/D] must meet (a, b).
            if ((P + 1) * a.D <= a.N * D || P * b.D >= b.N * D) return;
            if (!KneadingConsistent(P, k)) return;
            if (k == q)
            {
                string w = new string(bits);
                BigInteger J = P;
                var f = new Frac(J, (BigInteger.One << q) - 1);
                if (!Frac.Less(a, f) || !Frac.Less(f, b) || ExactPeriod(J, q) != q) return;
                if (SameAddress(ExternalAngle.FromBits("", w), address)) match.Add(w);
                return;
            }
            bits[k] = '0'; Walk(k + 1, P << 1);
            bits[k] = '1'; Walk(k + 1, (P << 1) + 1);
        }

        // With k leading digits P of θ known, check every ν_j (j ≤ k, j < q)
        // that those digits already decide.
        bool KneadingConsistent(BigInteger P, int k)
        {
            // Over the common denominator D2 = 2^{k+1}:
            //   θ/2 ∈ [P, P + 1] / D2;
            //   2^{j−1}θ mod 1 ∈ [L_j·2^j, (L_j + 1)·2^j] / D2, L_j = low k−j+1 digits.
            BigInteger D2 = BigInteger.One << (k + 1), half = D2 >> 1;
            for (int jj = 1; jj <= k && jj < q; jj++)
            {
                int known = k - jj + 1;
                BigInteger L = P & ((BigInteger.One << known) - 1);
                BigInteger scale = BigInteger.One << jj;
                BigInteger lo = L * scale - P - 1, hi = (L + 1) * scale - P;   // u = y − θ/2
                lo = Mod(lo, D2); hi = lo + (hi - (L * scale - P - 1));       // keep the width
                // u ∈ [lo, hi] (mod D2): decided when it avoids 0 and D2/2.
                int decided;
                if (hi < half && lo > 0) decided = 1;
                else if (lo > half && hi < D2) decided = 0;
                else continue;
                if (decided != nu[jj - 1] - '0') return false;
            }
            return true;
        }
    }

    // .(lo) ≤ θ ≤ .(hi)
    private static bool Holds(string lo, string hi, ExternalAngle theta)
    {
        var (n, d) = theta.Fraction;
        Frac a = Frac.Of(lo), b = Frac.Of(hi);
        return a.N * d <= n * a.D && n * b.D <= b.N * d;
    }

    private static BigInteger Mod(BigInteger x, BigInteger m) { var r = x % m; return r.Sign < 0 ? r + m : r; }

    /// <summary>The kneading sequence ν₁…ν_{q−1} (as '0'/'1') of the
    /// components with internal address s₀ = 1 → … → s_n = q: ν₁ = 1; between
    /// s_k and s_{k+1} it continues s_k-periodically, and ν_{s_{k+1}} breaks
    /// that period (Lau–Schleicher: s_{k+1} = ρ(s_k), the first place the
    /// s_k-periodic continuation fails).</summary>
    public static string KneadingOf(int[] address)
    {
        int q = address[^1];
        var nu = new char[System.Math.Max(0, q - 1)];
        if (q < 2) return string.Empty;
        nu[0] = '1';
        for (int k = 0; k + 1 < address.Length; k++)
        {
            int sk = address[k], next = address[k + 1];
            for (int jj = sk + 1; jj < next && jj <= q - 1; jj++) nu[jj - 1] = nu[jj - sk - 1];
            if (next <= q - 1) nu[next - 1] = nu[next - sk - 1] == '1' ? '0' : '1';
        }
        return new string(nu);
    }

    private static bool SameAddress(ExternalAngle angle, int[] address)
    {
        var got = angle.InternalAddress(address.Length + 1);
        if (got.Count != address.Length) return false;
        for (int i = 0; i < address.Length; i++) if (got[i] != address[i]) return false;
        return true;
    }

    // The exact period of j / (2ⁿ − 1) under doubling: the least divisor e of n
    // with (2^e − 1)·j divisible by 2ⁿ − 1.
    private static int ExactPeriod(BigInteger j, int n)
    {
        BigInteger d = (BigInteger.One << n) - 1;
        for (int e = 1; e < n; e++)
            if (n % e == 0 && (((BigInteger.One << e) - 1) * j) % d == 0) return e;
        return n;
    }

    private static BigInteger Value(string word)
    {
        BigInteger v = BigInteger.Zero;
        foreach (char c in word) v = (v << 1) + (c == '1' ? 1 : 0);
        return v;
    }

    private static string Word(BigInteger v, int n)
    {
        var sb = new StringBuilder(n);
        for (int i = n - 1; i >= 0; i--) sb.Append(((v >> i) & 1) == 1 ? '1' : '0');
        return sb.ToString();
    }

    /// <summary>Format an address with its sublimb angles: 1_1/2 → 2_1/2 → 3.</summary>
    public static string Format(IReadOnlyList<AddressStep> steps)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < steps.Count; i++)
        {
            if (i > 0) sb.Append(" → ");
            sb.Append(steps[i].Period.ToString(CultureInfo.InvariantCulture));
            if (steps[i].HasAngle) sb.Append('_').Append(steps[i].Num).Append('/').Append(steps[i].Den);
        }
        return sb.ToString();
    }
}
