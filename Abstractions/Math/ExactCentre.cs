// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Math/ExactCentre.cs
//
// #1832 (epic #1428, C2a) — a view centre at any precision: a BigFloat (#1830) pair.
// The OD limbs a view state, region or calculator keeps stay the render contract up to
// 1e100; this is the authority above it, persisted as exact decimal strings.

using System;
using System.Numerics;

namespace FracturingFog.FFMath
{
    /// <summary>#1832 — the view centre as an arbitrary-precision complex value.</summary>
    public readonly record struct ExactCentre(BigFloat Re, BigFloat Im)
    {
        /// <summary>Largest coordinate text accepted (digits per part): about 1e1,000,000
        /// zoom, far past anything renderable, and a bound for imported files.</summary>
        public const int MaxDigits = 1_000_000;

        /// <summary>The working precision of the pair (the wider part's).</summary>
        public int Precision => Math.Max(Re.Precision, Im.Precision);

        /// <summary>The OD limbs of this centre (each part's greedy 8-limb split).</summary>
        public DeepComplex ToDeep() => new(Re.ToOD(), Im.ToOD());

        /// <summary>The OD centre exactly, at <paramref name="precision"/> bits.</summary>
        public static ExactCentre FromDeep(DeepComplex c, int precision)
            => new(BigFloat.FromOD(c.Re, precision), BigFloat.FromOD(c.Im, precision));

        /// <summary>Both parts rounded to <paramref name="precision"/> bits.</summary>
        public ExactCentre WithPrecision(int precision) => new(Re.WithPrecision(precision), Im.WithPrecision(precision));

        /// <summary>Bits that hold a decimal of <paramref name="digits"/> significant digits
        /// exactly enough to round-trip it: ⌈digits·log₂10⌉ + 8.</summary>
        public static int PrecisionForDigits(int digits)
            => Math.Max(BigFloat.MinPrecision, (int)Math.Ceiling(Math.Max(1, digits) * 3.3219280948873622) + 8);

        /// <summary>Decimal text of each part with BigFloat's round-trip digits for
        /// <see cref="Precision"/>. It parses back to the same value at that precision (pass it
        /// to <see cref="TryParse"/>); at another precision the same text rounds differently.</summary>
        public (string X, string Y) ToStrings()
        {
            int p = Precision;
            return (Re.WithPrecision(p).ToString(BigFloat.RoundTripDigits(p)), Im.WithPrecision(p).ToString(BigFloat.RoundTripDigits(p)));
        }

        /// <summary>Parses a decimal pair at <paramref name="precision"/> bits (the precision
        /// <see cref="ToStrings"/> was written at), or, when 0, at the longer string's digit
        /// count (<see cref="PrecisionForDigits"/>: hand-written text). False for empty,
        /// malformed or over-long text.</summary>
        public static bool TryParse(string? x, string? y, out ExactCentre centre, int precision = 0)
        {
            centre = default;
            if (string.IsNullOrWhiteSpace(x) || string.IsNullOrWhiteSpace(y)) return false;
            if (x.Length > MaxDigits + 32 || y.Length > MaxDigits + 32) return false;
            int p = precision > 0 ? precision : PrecisionForDigits(Math.Max(SignificantDigits(x), SignificantDigits(y)));
            if (!BigFloat.TryParse(x, p, out var re) || !BigFloat.TryParse(y, p, out var im)) return false;
            centre = new ExactCentre(re, im);
            return true;
        }

        /// <summary>#1833 — significant digits of a decimal's mantissa (leading zeros not
        /// counted, the exponent ignored). The one count every exact-text reader uses, so
        /// the same text always gets the same precision.</summary>
        public static int SignificantDigits(string s)
        {
            int n = 0; bool started = false;
            foreach (char c in s)
            {
                if (c == 'e' || c == 'E') break;
                if (c < '0' || c > '9') continue;
                if (c != '0') started = true;
                if (started) n++;
            }
            return n;
        }

        /// <summary>True when the two OD centres have the same sixteen limbs.</summary>
        public static bool SameLimbs(DeepComplex a, DeepComplex b)
            => a.Re.X0 == b.Re.X0 && a.Re.X1 == b.Re.X1 && a.Re.X2 == b.Re.X2 && a.Re.X3 == b.Re.X3
            && a.Re.X4 == b.Re.X4 && a.Re.X5 == b.Re.X5 && a.Re.X6 == b.Re.X6 && a.Re.X7 == b.Re.X7
            && a.Im.X0 == b.Im.X0 && a.Im.X1 == b.Im.X1 && a.Im.X2 == b.Im.X2 && a.Im.X3 == b.Im.X3
            && a.Im.X4 == b.Im.X4 && a.Im.X5 == b.Im.X5 && a.Im.X6 == b.Im.X6 && a.Im.X7 == b.Im.X7;
    }
}
