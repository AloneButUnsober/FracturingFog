// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// GeneratedCalculatorAttributes.cs
//
// Wave 2.13 (D-7.29) — Roslyn source-gen registry. Each
// [assembly: GeneratedCalculator(...)] entry replaces a previously
// hand-checked-in file under Engine/Calculators/Generated/. The
// CalculatorGen.SourceGen analyzer emits the full calculator body at
// compile time (one .g.cs per attribute instance plus optional self-test).
//
// To add a calculator, add an entry below and rebuild — no
// `dotnet run -p CalculatorGen` step needed. To change an equation,
// edit the string and rebuild.
//
// Equations mirror what the legacy CLI produced. See
// Docs/Technical/CalculatorGen-Roadmap.md item 29 for context.

using FracturingFog.CalculatorGen;

// ── Stock Mandelbrot family — pure z^d + c (SA Tier-5 polynomial). ───
[assembly: GeneratedCalculator("z*z + c",            "MandelbrotZ2",         IncludeSelfTest = true)]
[assembly: GeneratedCalculator("z*z*z + c",          "MandelbrotZ3",         IncludeSelfTest = true)]
[assembly: GeneratedCalculator("z*z*z*z + c",        "MandelbrotZ4",         IncludeSelfTest = true)]
[assembly: GeneratedCalculator("z*z*z*z*z + c",      "MandelbrotZ5",         IncludeSelfTest = true)]

// ── Anti-holomorphic + folded variants ───────────────────────────────
[assembly: GeneratedCalculator("conj(z)*conj(z) + c", "Tricorn",             IncludeSelfTest = true)]
[assembly: GeneratedCalculator("conj(z)*conj(z) + c", "MandelbrotTricorn",   IncludeSelfTest = true)]
[assembly: GeneratedCalculator("fold(z)*fold(z) + c", "BurningShip",         IncludeSelfTest = true)]

// ── Conditional / recurrence / sample DSL ────────────────────────────
[assembly: GeneratedCalculator("if abs(z) > 4 then z else z*z + c",
                                                      "MandelbrotBurningShip", IncludeSelfTest = true)]
[assembly: GeneratedCalculator("z*z + c + 0.5*prev",  "MandelbrotPhoenix",   IncludeSelfTest = true)]
[assembly: GeneratedCalculator("z*z + c * i",         "UserDslEquation",     IncludeSelfTest = true)]
