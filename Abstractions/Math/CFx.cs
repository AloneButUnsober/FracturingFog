// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Math/CFx.cs
//
// #1847 (epic #1428, C5c) — a complex number in FloatExp, for the floatexp LA tables.
// #1866 (C7a) — moved here from Engine/Math/LaStageTableFx.cs so the explore tools
// (DeepNucleusFinder) share it, and given subtraction, division and magnitude.

namespace FracturingFog.FFMath
{
    /// <summary>#1847 — a complex number in FloatExp.</summary>
    public readonly struct CFx
    {
        public readonly FloatExp Re, Im;
        public CFx(FloatExp re, FloatExp im) { Re = re; Im = im; }
        public static CFx Of(double re, double im) => new(FloatExp.FromDouble(re), FloatExp.FromDouble(im));
        public static readonly CFx One = new(FloatExp.One, FloatExp.Zero);
        public static readonly CFx Zero = new(FloatExp.Zero, FloatExp.Zero);
        public static CFx operator *(CFx a, CFx b) => new(a.Re * b.Re - a.Im * b.Im, a.Re * b.Im + a.Im * b.Re);
        public static CFx operator +(CFx a, CFx b) => new(a.Re + b.Re, a.Im + b.Im);
        public CFx Twice() => new(Re.ScaleB(1), Im.ScaleB(1));
        public CFx PlusOne() => new(Re + 1.0, Im);
        /// <summary>max(|re|, |im|) — LaStageTable's Cheb.</summary>
        public FloatExp Cheb() => FloatExp.Max(Re.Abs(), Im.Abs());
        public bool IsFinite => double.IsFinite(Re.M) && double.IsFinite(Im.M);

        // #1866 — for Newton in floatexp.
        public static CFx operator -(CFx a, CFx b) => new(a.Re - b.Re, a.Im - b.Im);
        public static CFx operator -(CFx a) => new(-a.Re, -a.Im);
        public CFx Scale(double s) => new(Re * s, Im * s);
        public bool IsZero => Re.IsZero && Im.IsZero;

        /// <summary>|this| (√(re² + im²), at any exponent).</summary>
        public FloatExp Magnitude() => FloatExp.Norm2(Re, Im).Sqrt();

        /// <summary>log₂|this| (−∞ for 0).</summary>
        public double Log2Abs() => Magnitude().Log2Abs();

        /// <summary>1 / this (this ≠ 0).</summary>
        public CFx Reciprocal()
        {
            // Scale both parts by the larger exponent first so re² + im² stays in range.
            var n = FloatExp.Norm2(Re, Im);
            return new CFx(Re / n, -Im / n);
        }

        public static CFx operator /(CFx a, CFx b) => a * b.Reciprocal();
    }
}
