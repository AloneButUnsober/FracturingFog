// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// ImportSubscripts.cs
//
// #1421 — shared by LatexImporter (#765) and MathmlImporter (#764). Fractal
// formulas found online are written as sequences: z_{n+1} = z_n^2 + c_n. The
// DSL is the body of one iteration step over a single z, so:
//   z_n, z_k        -> z      (the current iterate)
//   z_{n-1}         -> prev
//   c_n, c_0, c_k   -> c      (constant per pixel)
//   z_{n+1}         -> only as the left-hand side of the recurrence, which the
//                      importers drop (also z =, z \mapsto, f(z) =, f_c(z) =)
// Anything else (z_0, other sequences such as w_n, other subscripts) gets an
// error that names what the DSL cannot express.

using System.Text.RegularExpressions;

namespace FracturingFog.CalculatorGen.Parser;

internal static class ImportSubscripts
{
    // Letters commonly used for the iteration index.
    private const string Index = "[nkijt]";

    private static readonly Regex Current = new($"^{Index}$");
    private static readonly Regex Previous = new($"^{Index}-1$");
    private static readonly Regex Next = new($"^{Index}\\+1$");
    private static readonly Regex Constant = new($"^({Index}|\\d+)$");

    // Left-hand sides of a recurrence, on a brace/space-free spelling such as
    // "z_n+1", "z", "f(z)", "f_c(z)", "P_c(z_n)".
    private static readonly Regex RecurrenceLhs = new(
        $"^(z(_{Index}\\+1)?|[A-Za-z](_[A-Za-z0-9]+)?\\(z(_{Index})?\\))$");

    /// <summary>Strip spaces, braces and one pair of surrounding parentheses
    /// from a subscript ("{(n - 1)}" -> "n-1").</summary>
    public static string Normalise(string sub)
    {
        string s = sub.Replace(" ", string.Empty).Replace("{", string.Empty).Replace("}", string.Empty);
        if (s.Length > 2 && s[0] == '(' && s[s.Length - 1] == ')') s = s.Substring(1, s.Length - 2);
        return s.Replace("−", "-");
    }

    /// <summary>DSL for <paramref name="name"/> subscripted by
    /// <paramref name="sub"/>, or an error naming why it is not expressible.</summary>
    public static (string? Dsl, string? Error) Map(string name, string sub)
    {
        string s = Normalise(sub);
        string shown = $"{name}_{{{s}}}";
        if (name == "z")
        {
            if (Current.IsMatch(s)) return ("z", null);
            if (Previous.IsMatch(s)) return ("prev", null);
            if (Next.IsMatch(s))
                return (null, $"{shown} (the next iterate) can only be the left-hand side, as in '{shown} = z_{{{s[0]}}}^2 + c'.");
            if (s == "0")
                return (null, "z_0 (the starting value) is not part of the step; set it with the editor's seed instead.");
            return (null, $"unsupported subscript {shown}; use z_n (current), z_{{n-1}} (prev) or c_n.");
        }
        if (name == "c")
        {
            if (Constant.IsMatch(s)) return ("c", null);
            return (null, $"unsupported subscript {shown}; c is constant per pixel (c, c_0, c_n).");
        }
        return (null, $"{name}_{{{s}}}: the DSL iterates a single z, so other sequences such as {name}_n are not supported.");
    }

    /// <summary>True when <paramref name="lhs"/> (a brace/space-free spelling)
    /// is the left-hand side of a recurrence definition.</summary>
    public static bool IsRecurrenceLhs(string lhs) => RecurrenceLhs.IsMatch(lhs);
}
