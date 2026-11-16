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

        /// <summary>The value of a float-exp complex, in double (tests, diagnostics).</summary>
        public static (double Re, double Im) Decode(float mr, float mi, int e)
            => e == ZeroExp ? (0, 0) : (Math.ScaleB(mr, e), Math.ScaleB(mi, e));
    }
}
