// Models/Quat.cs
//
// Quaternion type for User Bulb 3D unified-mode rendering. When axis mode
// is Quat, UserBulbCalculator compiles user step as Quat→Quat and projects
// the 3D z.X/Y/Z slice into the raymarch position; z.W picks the 4D slice
// plane (UserBulbQuatSliceW).
//
// User source signature in Quat mode:
//   Quat Step(Quat z, Quat c, int n)
//
// Hamilton multiplication: (a+bi+cj+dk)(e+fi+gj+hk) = standard quaternion
// product. Use case: Julia-style quaternion fractals (z² + c with z,c quat).

using System;
using System.Runtime.CompilerServices;

namespace FracturingFog.Models
{
    public readonly record struct Quat(double W, double X, double Y, double Z)
    {
        public static readonly Quat Zero = new(0, 0, 0, 0);
        public static readonly Quat Identity = new(1, 0, 0, 0);

        public double Length
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Math.Sqrt(W * W + X * X + Y * Y + Z * Z);
        }
        public double LengthSquared
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => W * W + X * X + Y * Y + Z * Z;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Quat operator +(Quat a, Quat b) => new(a.W + b.W, a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Quat operator -(Quat a, Quat b) => new(a.W - b.W, a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Quat operator -(Quat a) => new(-a.W, -a.X, -a.Y, -a.Z);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Quat operator *(Quat a, double s) => new(a.W * s, a.X * s, a.Y * s, a.Z * s);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Quat operator *(double s, Quat a) => new(a.W * s, a.X * s, a.Y * s, a.Z * s);

        /// <summary>Hamilton product.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Quat operator *(Quat a, Quat b) => new(
            a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z,
            a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
            a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
            a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Quat Conjugate() => new(W, -X, -Y, -Z);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double Dot(Quat a, Quat b) => a.W * b.W + a.X * b.X + a.Y * b.Y + a.Z * b.Z;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Quat FromVec3(Vec3 v, double w = 0) => new(w, v.X, v.Y, v.Z);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vec3 ToVec3() => new(X, Y, Z);

        public override string ToString() => $"({W:G6}, {X:G6}, {Y:G6}, {Z:G6})";
    }
}
