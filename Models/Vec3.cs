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

        public override string ToString() => $"({X:G6}, {Y:G6}, {Z:G6})";
    }
}
