// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// DslSignatures.cs
//
// #1366 (S5 of #1360) — one entry per built-in function per dialect: parameter
// names and a one-line description, for the DSL editor's completion list,
// signature help and hover docs. The descriptions follow the evaluators
// (SandboxExpression for 2D, SandboxBulbExpression / Vec3 for Bulb). Guard
// tests pin these tables to the parsers: same names, same arity.

using System;
using System.Collections.Generic;
using System.Linq;

namespace FracturingFog.Models
{
    public sealed record DslSignature(string Name, IReadOnlyList<string> Params, string Doc)
    {
        public int Arity => Params.Count;

        /// <summary>"pow(x, y)".</summary>
        public string Display => $"{Name}({string.Join(", ", Params)})";
    }

    public static class DslSignatures
    {
        private static DslSignature S(string name, string doc, params string[] ps) => new(name, ps, doc);

        private static readonly DslSignature[] Complex2D =
        {
            S("sin", "Sine (complex).", "x"),
            S("cos", "Cosine (complex).", "x"),
            S("tan", "Tangent (complex).", "x"),
            S("sinh", "Hyperbolic sine.", "x"),
            S("cosh", "Hyperbolic cosine.", "x"),
            S("tanh", "Hyperbolic tangent.", "x"),
            S("exp", "e^x.", "x"),
            S("log", "Natural log (principal branch; complex for x <= 0).", "x"),
            S("sqrt", "Square root (principal branch; complex for x < 0).", "x"),
            S("sqr", "x*x.", "x"),
            S("abs", "Modulus |x| (real).", "x"),
            S("norm", "Squared modulus |x|^2 = re^2 + im^2 (cheaper than abs).", "x"),
            S("conj", "Complex conjugate re - i*im.", "x"),
            S("re", "Real part.", "x"),
            S("im", "Imaginary part.", "x"),
            S("arg", "Argument (angle) atan2(im, re), radians.", "x"),
            S("asin", "Inverse sine.", "x"),
            S("acos", "Inverse cosine.", "x"),
            S("atan", "Inverse tangent.", "x"),
            S("asinh", "Inverse hyperbolic sine.", "x"),
            S("acosh", "Inverse hyperbolic cosine.", "x"),
            S("atanh", "Inverse hyperbolic tangent.", "x"),
            S("floor", "Floor, per component.", "x"),
            S("sign", "Sign (-1, 0, 1), per component.", "x"),
            S("fold", "Absolute value per component: |re| + i|im| (Burning Ship fold).", "x"),
            S("fract", "Fractional part x - floor(x), per component.", "x"),
            S("round", "Round to nearest, per component.", "x"),
            S("ceil", "Ceiling, per component.", "x"),
            S("trunc", "Truncate toward zero, per component.", "x"),
            S("pow", "x^y (complex power; same as the ^ operator).", "x", "y"),
            S("atan2", "atan2(y, x) on real values (complex operands use their modulus).", "y", "x"),
            S("min", "Smaller of two real values (complex operands use their modulus).", "a", "b"),
            S("max", "Larger of two real values (complex operands use their modulus).", "a", "b"),
            S("mod", "Centred repeat x - p*floor(x/p + 0.5), per component.", "x", "period"),
            S("clamp", "Clamp a real value to [lo, hi].", "x", "lo", "hi"),
        };

        private static readonly DslSignature[] Bulb3D =
        {
            S("sin", "Sine, per component (real or Vec3).", "x"),
            S("cos", "Cosine, per component.", "x"),
            S("tan", "Tangent, per component.", "x"),
            S("sinh", "Hyperbolic sine, per component.", "x"),
            S("cosh", "Hyperbolic cosine, per component.", "x"),
            S("tanh", "Hyperbolic tangent, per component.", "x"),
            S("exp", "e^x, per component.", "x"),
            S("log", "Natural log, per component.", "x"),
            S("sqrt", "Square root, per component.", "x"),
            S("abs", "Absolute value, per component (also on Quat).", "x"),
            S("norm", "Squared length: x^2, dot(v, v) or |q|^2.", "x"),
            S("length", "Vector length |v|.", "v"),
            S("normalize", "Unit vector v / |v|.", "v"),
            S("absx", "Fold the x component: (|x|, y, z).", "v"),
            S("absy", "Fold the y component: (x, |y|, z).", "v"),
            S("absz", "Fold the z component: (x, y, |z|).", "v"),
            S("floor", "Floor of a real value.", "x"),
            S("sign", "Sign (-1, 0, 1) of a real value.", "x"),
            S("dot", "Dot product of two vectors.", "a", "b"),
            S("cross", "Cross product of two vectors.", "a", "b"),
            S("triplex", "Triplex spherical power (Mandelbulb): r^n with angles times n.", "v", "n"),
            S("boxfold", "Per-axis box fold at +/-limit (Mandelbox).", "v", "limit"),
            S("mod", "Periodic repeat per axis: v - period*floor(v/period + 0.5).", "v", "period"),
            S("pow", "x^y (same as the ^ operator; triplex power on Vec3).", "x", "y"),
            S("min", "Smaller of two real values.", "a", "b"),
            S("max", "Larger of two real values.", "a", "b"),
            S("vec", "Build a Vec3 from three reals.", "x", "y", "z"),
            S("rot", "Rotate v about axis by angle (radians), Rodrigues formula.", "v", "axis", "angle"),
            S("spherefold", "Sphere fold (inversion): scale by rMax^2/rMin^2 inside rMin, rMax^2/r^2 between.", "v", "rMin", "rMax"),
            S("smin", "Smooth minimum -log(exp(-k*a) + exp(-k*b))/k, for blending distances.", "a", "b", "k"),
            S("clamp", "Clamp a real value to [lo, hi].", "x", "lo", "hi"),
            S("qvec", "Build a quaternion (x, y, z, w).", "x", "y", "z", "w"),
            S("qmul", "Quaternion product a*b.", "a", "b"),
            S("qpow", "Quaternion power q^n.", "q", "n"),
            S("qconj", "Quaternion conjugate.", "q"),
            S("qexp", "Quaternion exponential.", "q"),
            S("qlog", "Quaternion logarithm.", "q"),
            S("qsqrt", "Quaternion square root.", "q"),
            S("qinv", "Quaternion inverse.", "q"),
            S("qsin", "Quaternion sine.", "q"),
            S("qcos", "Quaternion cosine.", "q"),
            S("qtan", "Quaternion tangent.", "q"),
            S("qsinh", "Quaternion hyperbolic sine.", "q"),
            S("qcosh", "Quaternion hyperbolic cosine.", "q"),
            S("qtanh", "Quaternion hyperbolic tangent.", "q"),
            S("qasin", "Quaternion inverse sine.", "q"),
            S("qacos", "Quaternion inverse cosine.", "q"),
            S("qatan", "Quaternion inverse tangent.", "q"),
            S("qasinh", "Quaternion inverse hyperbolic sine.", "q"),
            S("qacosh", "Quaternion inverse hyperbolic cosine.", "q"),
            S("qatanh", "Quaternion inverse hyperbolic tangent.", "q"),
            S("qcsc", "Quaternion cosecant 1/sin(q).", "q"),
            S("qsec", "Quaternion secant 1/cos(q).", "q"),
            S("qcot", "Quaternion cotangent 1/tan(q).", "q"),
            S("qcsch", "Quaternion hyperbolic cosecant.", "q"),
            S("qsech", "Quaternion hyperbolic secant.", "q"),
            S("qcoth", "Quaternion hyperbolic cotangent.", "q"),
        };

        private static readonly IReadOnlyDictionary<string, DslSignature> Map2D =
            Complex2D.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);

        private static readonly IReadOnlyDictionary<string, DslSignature> MapBulb =
            Bulb3D.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);

        /// <summary>All built-in functions of <paramref name="dialect"/>, keyed
        /// case-insensitively (calls are matched that way).</summary>
        public static IReadOnlyDictionary<string, DslSignature> For(DslDialect dialect)
            => dialect == DslDialect.Bulb3D ? MapBulb : Map2D;

        public static DslSignature? Find(DslDialect dialect, string name)
            => For(dialect).TryGetValue(name, out var s) ? s : null;
    }
}
