// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// BulbVocabulary.cs
//
// #1363 — the User Bulb 3D language's word lists, hoisted out of
// SandboxBulbExpression (Engine) so UI-side tooling (the DslCodeEditor
// highlighter, later completion #1366) reads the same lists the parser uses.
// UI.Avalonia does not reference Engine; it does reach this project.

using System;
using System.Collections.Generic;

namespace FracturingFog.Models
{
    public static class BulbVocabulary
    {
        /// <summary>#1100 — function names, for Did-you-mean, help and the
        /// editor. Lower-case; calls are matched case-insensitively.</summary>
        public static readonly IReadOnlyList<string> FunctionNames = new[]
        {
            "sin", "cos", "tan", "sinh", "cosh", "tanh", "exp", "log", "sqrt", "abs", "norm",
            "length", "normalize", "absx", "absy", "absz", "floor", "sign",
            "dot", "cross", "triplex", "boxfold", "mod", "pow", "min", "max",
            "vec", "rot", "spherefold", "smin", "clamp", "qvec",
            "qmul", "qpow", "qconj", "qexp", "qlog", "qsqrt", "qinv",
            "qsin", "qcos", "qtan", "qsinh", "qcosh", "qtanh",
            "qasin", "qacos", "qatan", "qasinh", "qacosh", "qatanh",
            "qcsc", "qsec", "qcot", "qcsch", "qsech", "qcoth",
        };

        /// <summary>Declaration type keywords (`Vec3 v = …`).</summary>
        public static readonly IReadOnlySet<string> TypeKeywords =
            new HashSet<string>(StringComparer.Ordinal) { "var", "Vec3", "Quat", "double", "int", "float" };

        /// <summary>Always-in-scope names: the step inputs and constants. The DE
        /// body (#281) also sees dr / de, and animation adds t.</summary>
        public static readonly IReadOnlyList<string> BuiltInIdentifiers =
            new[] { "z", "c", "n", "pi", "e", "t", "dr", "de" };
    }
}
