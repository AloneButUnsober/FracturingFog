// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System.Collections.Generic;

namespace FracturingFog.UI.Avalonia.ViewModels;

// Wave 2.8 — Equation cookbook + gallery (D-6.23). Curated CalcGen DSL
// equations the user can drop into the editor as a starting point. Each entry
// pairs a short DSL source with a default centre + zoom that frames the
// interesting structure for that equation (chosen by inspection — the
// classical Mandelbrot's (-0.5, 0, 1.5) doesn't suit Newton or Burning Ship).
//
// Equations are deliberately DSL-form (lowercase, no `return`, no `Complex.`
// prefix) so the cookbook dialog can drop them straight into the DSL tab.
// The User Equation tab is the C# variant; users who paste a cookbook entry
// there will hit the validator until they wrap the body in `return ... ;`.
//
// Adding entries: keep the source short and parser-clean. The preview panel
// gates SA / perturbation / DE off as soon as ops like conj/sin/cos/fold/div
// appear; that's surfaced in the description so the user understands the
// performance trade-off before they Compile & Load.
public readonly record struct CookbookEntry(
    string Name,
    string Description,
    string DslSource,
    double CenterX,
    double CenterY,
    double Zoom);

public static class EquationCookbook
{
    public static readonly IReadOnlyList<CookbookEntry> Entries = new[]
    {
        new CookbookEntry(
            "Classical Mandelbrot",
            "z² + c. The original. Full SA + perturbation + DE.",
            "z*z + c",
            -0.5, 0.0, 1.5),

        new CookbookEntry(
            "Cubic Mandelbrot",
            "z³ + c. Three-fold symmetric bulb. Full SA + perturbation + DE.",
            "z*z*z + c",
            0.0, 0.0, 1.2),

        new CookbookEntry(
            "Quartic Mandelbrot",
            "z⁴ + c. Four-fold symmetric. SA degree 4.",
            "z^4 + c",
            0.0, 0.0, 1.1),

        new CookbookEntry(
            "Quintic Mandelbrot",
            "z⁵ + c. Five-fold symmetric bulb cluster.",
            "z^5 + c",
            0.0, 0.0, 1.05),

        new CookbookEntry(
            "Tricorn (Mandelbar)",
            "conj(z)² + c. Anti-holomorphic — SA disabled, DE only.",
            "conj(z)*conj(z) + c",
            0.0, 0.0, 1.5),

        new CookbookEntry(
            "Burning Ship",
            "(|Re(z)| + i·|Im(z)|)² + c. Folded — SA disabled.",
            "fold(z)*fold(z) + c",
            -0.45, -0.6, 1.6),

        new CookbookEntry(
            "Phoenix",
            "z² + c + 0.5667·prev. Two-step recurrence — SA disabled, perturbation uses tier-2 path.",
            "z*z + c + 0.5667 * prev",
            0.0, 0.0, 0.7),

        new CookbookEntry(
            "Sin Mandelbrot",
            "sin(z) + c. Transcendental — SA + perturbation disabled; DE on.",
            "sin(z) + c",
            0.0, 0.0, 0.3),

        new CookbookEntry(
            "Cos Mandelbrot",
            "cos(z) + c. Like sin variant; symmetric about the real axis.",
            "cos(z) + c",
            0.0, 0.0, 0.3),

        new CookbookEntry(
            "Exp Mandelbrot",
            "exp(z) + c. Unbounded along Re(z); explore the strip near origin.",
            "exp(z) + c",
            -2.0, 0.0, 0.5),

        new CookbookEntry(
            "Lambda (Logistic)",
            "c · z · (1 − z). Renormalised Mandelbrot — connected at c = 1.",
            "c * z * (1 - z)",
            1.0, 0.0, 0.8),

        new CookbookEntry(
            "Newton z³ − 1",
            "Newton iteration for roots of z³ = 1. No c-dependence; rendering shows basins of attraction.",
            "z - (z*z*z - 1) / (3*z*z)",
            0.0, 0.0, 0.7),

        new CookbookEntry(
            "Magnet 1",
            "((z² + c − 1) / (2z + c − 2))². Rational map with division — SA off.",
            "((z*z + c - 1) / (2*z + c - 2))^2",
            0.5, 0.0, 0.6),

        new CookbookEntry(
            "Mixed quadratic",
            "z² + c². Mandelbrot with squared-c forcing — distorted bulb.",
            "z*z + c*c",
            0.0, 0.0, 1.0),
    };
}
