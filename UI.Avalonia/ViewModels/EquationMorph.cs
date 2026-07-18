// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using FracturingFog.CalculatorGen.Parser;

namespace FracturingFog.UI.Avalonia.ViewModels;

// Wave 2.9 — Animation: morph equations (D-6.25). Build a synthetic DSL
// source that linearly interpolates two equations A and B per parameter
// t ∈ [0, 1]. The hot-load + render pipeline is unchanged: the synthetic
// source is just another DSL string handed to CalcGen.
//
// Synthesis: wrap A and B as ((1 - t) * (A)) + (t * (B)) with t baked
// in as a numeric literal. We do NOT try to be clever — no AST mixing,
// no per-iter recurrence swap. Treating the iteration body as a single
// expression and blending the result is what the spec asks for and is
// what reads visually as "halfway between z²+c and z³+c".
//
// SA is implicitly disabled during morph: the equation's structure
// changes per frame, so series-approximation coefficients computed from
// one frame's equation would mis-evaluate at the next. Render path
// already gates SA off when the DSL contains ops it can't handle (sin,
// conj, fold, div, etc.). The cross term `(1-t)*(A) + t*(B)` is a
// generic polynomial-or-worse mix; once either side trips a gate, SA
// stays off for the whole sweep. Adequate.
public static class EquationMorph
{
    /// <summary>
    /// Produce a CalcGen DSL string representing
    /// (1 - t)·(<paramref name="dslA"/>) + t·(<paramref name="dslB"/>).
    /// Sources are parenthesised so operator precedence in the parent
    /// expression doesn't break A's or B's intended associativity.
    /// </summary>
    public static string Synthesize(string dslA, string dslB, double t)
    {
        ArgumentNullException.ThrowIfNull(dslA);
        ArgumentNullException.ThrowIfNull(dslB);
        string a = dslA.Trim();
        string b = dslB.Trim();
        if (a.Length == 0) throw new ArgumentException("dslA empty.", nameof(dslA));
        if (b.Length == 0) throw new ArgumentException("dslB empty.", nameof(dslB));
        // Endpoint shortcut — avoids `0.0 * (foo)` noise the parser still
        // accepts but the user has to read in error messages.
        if (t <= 0.0) return a;
        if (t >= 1.0) return b;
        double oneMinusT = 1.0 - t;
        string ts = t.ToString("G6", System.Globalization.CultureInfo.InvariantCulture);
        string omts = oneMinusT.ToString("G6", System.Globalization.CultureInfo.InvariantCulture);
        return $"({omts}) * ({a}) + ({ts}) * ({b})";
    }

    /// <summary>
    /// Quick sanity check on a pair of source strings before kicking off a
    /// frame sweep. Returns the first error message or null on success.
    /// </summary>
    public static string? Validate(string dslA, string dslB)
    {
        if (string.IsNullOrWhiteSpace(dslA)) return "Equation A is empty.";
        if (string.IsNullOrWhiteSpace(dslB)) return "Equation B is empty.";
        try { EquationParser.Parse(dslA.Trim()); }
        catch (Exception ex) { return $"Equation A: {ex.Message}"; }
        try { EquationParser.Parse(dslB.Trim()); }
        catch (Exception ex) { return $"Equation B: {ex.Message}"; }
        // Also confirm the synth at t=0.5 parses — catches cases where
        // both sides parse individually but the wrapped form trips a
        // limit (rare; defensive).
        try { EquationParser.Parse(Synthesize(dslA, dslB, 0.5)); }
        catch (Exception ex) { return $"Mid-morph parse failed: {ex.Message}"; }
        return null;
    }
}
