// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Models/Vec3.cs
//
// Lightweight 3D vector used by UserBulbCalculator for user-supplied step
// functions. Public surface mirrors the System.Numerics.Vector3 ergonomics
// but stays in double precision (matches the 2D Complex path used by
// UserEquationCalculator) and exposes component-wise helpers that show up
// directly in the Roslyn script namespace.
//
// Roslyn ScriptOptions in UserBulbCalculator reference this assembly and
// import "FracturingFog.Models" so user source can write:
//   return new Vec3(Sin(z.X)*Cosh(z.Y), ...) + c;

using System;
using System.Runtime.CompilerServices;

namespace FracturingFog.Models
{
    public readonly record struct Vec3(double X, double Y, double Z)
    {
        public static readonly Vec3 Zero = new(0.0, 0.0, 0.0);
        public static readonly Vec3 One  = new(1.0, 1.0, 1.0);

        public double Length
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Math.Sqrt(X * X + Y * Y + Z * Z);
        }

        public double LengthSquared
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => X * X + Y * Y + Z * Z;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vec3 operator -(Vec3 a)         => new(-a.X, -a.Y, -a.Z);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vec3 operator *(Vec3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vec3 operator *(double s, Vec3 a) => new(a.X * s, a.Y * s, a.Z * s);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vec3 operator /(Vec3 a, double s) => new(a.X / s, a.Y / s, a.Z / s);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double Dot(Vec3 a, Vec3 b) =>
            a.X * b.X + a.Y * b.Y + a.Z * b.Z;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vec3 Cross(Vec3 a, Vec3 b) => new(
            a.Y * b.Z - a.Z * b.Y,
            a.Z * b.X - a.X * b.Z,
            a.X * b.Y - a.Y * b.X);

        /// <summary>Component-wise sin. Convenience for user step functions.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vec3 Sin(Vec3 v)  => new(Math.Sin(v.X),  Math.Sin(v.Y),  Math.Sin(v.Z));
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vec3 Cos(Vec3 v)  => new(Math.Cos(v.X),  Math.Cos(v.Y),  Math.Cos(v.Z));
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vec3 Sinh(Vec3 v) => new(Math.Sinh(v.X), Math.Sinh(v.Y), Math.Sinh(v.Z));
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vec3 Cosh(Vec3 v) => new(Math.Cosh(v.X), Math.Cosh(v.Y), Math.Cosh(v.Z));
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vec3 Exp(Vec3 v)  => new(Math.Exp(v.X),  Math.Exp(v.Y),  Math.Exp(v.Z));
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vec3 Abs(Vec3 v)  => new(Math.Abs(v.X),  Math.Abs(v.Y),  Math.Abs(v.Z));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vec3 Normalized()
        {
            double len = Length;
            return len < 1e-12 ? Zero : new Vec3(X / len, Y / len, Z / len);
        }

        // ── Fractal-authoring helpers ───────────────────────────────────────

        /// <summary>Triplex spherical power. Real Mandelbulb formula:
        /// r=|v|, θ=atan2(y,x), φ=asin(z/r) → r^n·(cos(nφ)cos(nθ), cos(nφ)sin(nθ), sin(nφ)).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vec3 Pow(Vec3 v, double n)
        {
            double r = v.Length;
            if (r < 1e-12) return Zero;
            double theta = Math.Atan2(v.Y, v.X) * n;
            double phi = Math.Asin(Math.Clamp(v.Z / r, -1.0, 1.0)) * n;
            double rn = Math.Pow(r, n);
            double cosp = Math.Cos(phi);
            return new Vec3(rn * cosp * Math.Cos(theta), rn * cosp * Math.Sin(theta), rn * Math.Sin(phi));
        }

        /// <summary>Rotate v around axis by angle (radians) — Rodrigues formula.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vec3 Rot(Vec3 v, Vec3 axis, double angle)
        {
            var k = axis.Normalized();
            double c = Math.Cos(angle), s = Math.Sin(angle);
            return v * c + Cross(k, v) * s + k * (Dot(k, v) * (1.0 - c));
        }

        /// <summary>Per-axis box fold: abs(x) &gt; limit ? sign(x)·2·limit − x : x. Mandelbox.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vec3 BoxFold(Vec3 v, double limit) => new(
            FoldAxis(v.X, limit), FoldAxis(v.Y, limit), FoldAxis(v.Z, limit));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static double FoldAxis(double x, double limit) =>
            Math.Abs(x) > limit ? Math.Sign(x) * 2.0 * limit - x : x;

        /// <summary>Sphere fold (inversion): inside rMin → ·(rMax²/rMin²); between → ·(rMax²/r²); outside → no-op.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vec3 SphereFold(Vec3 v, double rMin, double rMax)
        {
            double r2 = v.LengthSquared;
            double rMin2 = rMin * rMin;
            double rMax2 = rMax * rMax;
            if (r2 < rMin2) return v * (rMax2 / rMin2);
            if (r2 < rMax2) return v * (rMax2 / r2);
            return v;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vec3 AbsX(Vec3 v) => new(Math.Abs(v.X), v.Y, v.Z);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vec3 AbsY(Vec3 v) => new(v.X, Math.Abs(v.Y), v.Z);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vec3 AbsZ(Vec3 v) => new(v.X, v.Y, Math.Abs(v.Z));

        /// <summary>Periodic space repeat per axis: v − period·floor(v/period + 0.5).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vec3 Mod(Vec3 v, double period) => new(
            v.X - period * Math.Floor(v.X / period + 0.5),
            v.Y - period * Math.Floor(v.Y / period + 0.5),
            v.Z - period * Math.Floor(v.Z / period + 0.5));

        /// <summary>Smooth min: −log(exp(−k·a) + exp(−k·b)) / k. DE blend with C¹ continuity.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double SMin(double a, double b, double k) =>
            -Math.Log(Math.Exp(-k * a) + Math.Exp(-k * b)) / k;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static (double R, double Theta, double Phi) ToSpherical(Vec3 v)
        {
            double r = v.Length;
            if (r < 1e-12) return (0, 0, 0);
            return (r, Math.Atan2(v.Y, v.X), Math.Asin(Math.Clamp(v.Z / r, -1.0, 1.0)));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vec3 FromSpherical(double r, double theta, double phi)
        {
            double cp = Math.Cos(phi);
            return new Vec3(r * cp * Math.Cos(theta), r * cp * Math.Sin(theta), r * Math.Sin(phi));
        }

        public override string ToString() => $"({X:G6}, {Y:G6}, {Z:G6})";
    }
}
