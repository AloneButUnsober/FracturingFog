// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;

namespace FracturingFog.Rendering
{
    /// <summary>#1664 — doubles as the float-exp numbers of the GPU kernels (#1331,
    /// MandelbrotKernelSource.FxHelpers): a float mantissa with max(|re|, |im|) in [1, 2)
    /// and one shared int exponent, value = m · 2^e; zero is (0, 0, <see cref="ZeroExp"/>).</summary>
    public static class GpuFloatExp
    {
        /// <summary>The kernels' FxZeroExp: the exponent of zero, below every other.</summary>
        public const int ZeroExp = -1000000;

        /// <summary>The complex (<paramref name="re"/>, <paramref name="im"/>) as float-exp.
        /// Finite inputs only.</summary>
        public static void Encode(double re, double im, out float mr, out float mi, out int e)
        {
            double a = Math.Max(Math.Abs(re), Math.Abs(im));
            if (a == 0) { mr = 0; mi = 0; e = ZeroExp; return; }
            e = Math.ILogB(a);
            mr = (float)Math.ScaleB(re, -e);
            mi = (float)Math.ScaleB(im, -e);
            // Rounding to float can carry the larger mantissa up to 2: renormalise.
            if (Math.Max(Math.Abs(mr), Math.Abs(mi)) >= 2f) { mr *= 0.5f; mi *= 0.5f; e++; }
        }

        /// <summary>A non-negative real as float-exp.</summary>
        public static void Encode(double v, out float m, out int e) => Encode(v, 0, out m, out _, out e);

        /// <summary>#1880 (epic #1428, C6a) — a host FloatExp complex (separate exponents)
        /// as the kernels' float-exp complex (one shared exponent): the same words as
        /// <see cref="Encode(double, double, out float, out float, out int)"/> gives its
        /// double value wherever double holds it. Past the int exponent's reach (FloatExp.Huge,
        /// the builders' x / 0) it is +∞ as that double would be.</summary>
        public static void Encode(FracturingFog.FFMath.CFx v, out float mr, out float mi, out int e)
        {
            bool zr = v.Re.IsZero, zi = v.Im.IsZero;
            if (zr && zi) { mr = 0; mi = 0; e = ZeroExp; return; }
            long eh = zr ? v.Im.E : zi ? v.Re.E : Math.Max(v.Re.E, v.Im.E);
            if (eh > MaxExp || eh < -MaxExp)
            {
                // Beyond any number the kernels meet: as the double ±∞ / 0 would encode.
                double inf = eh > 0 ? double.PositiveInfinity : 0;
                Encode(zr ? 0 : Math.CopySign(inf, v.Re.M), zi ? 0 : Math.CopySign(inf, v.Im.M), out mr, out mi, out e);
                return;
            }
            e = (int)eh;
            mr = zr ? 0f : (float)Math.ScaleB(v.Re.M, (int)Math.Max(v.Re.E - eh, -2000));
            mi = zi ? 0f : (float)Math.ScaleB(v.Im.M, (int)Math.Max(v.Im.E - eh, -2000));
            // Rounding to float can carry the larger mantissa up to 2: renormalise.
            if (Math.Max(Math.Abs(mr), Math.Abs(mi)) >= 2f) { mr *= 0.5f; mi *= 0.5f; e++; }
        }

        /// <summary>#1880 — a non-negative FloatExp real as float-exp.</summary>
        public static void Encode(FracturingFog.FFMath.FloatExp v, out float m, out int e)
            => Encode(new FracturingFog.FFMath.CFx(v, FracturingFog.FFMath.FloatExp.Zero), out m, out _, out e);

        // #1880 — the largest |exponent| a FloatExp is encoded with (far past any operand;
        // FloatExp.Huge is 2^60).
        private const long MaxExp = 1L << 28;

        /// <summary>The value of a float-exp complex, in double (tests, diagnostics).</summary>
        public static (double Re, double Im) Decode(float mr, float mi, int e)
            => e == ZeroExp ? (0, 0) : (Math.ScaleB(mr, e), Math.ScaleB(mi, e));
    }
}
