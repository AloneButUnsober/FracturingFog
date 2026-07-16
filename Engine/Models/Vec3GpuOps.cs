// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Models/Vec3GpuOps.cs
//
// Device-safe mirrors of Vec3 helpers for ILGPU kernels emitted by
// UserBulbSandboxGpuCompiler. Identical math to Vec3.* but avoids the IL
// Throw opcode (ILGPU JIT rejects exception flow):
//   - Math.Clamp lowers to ThrowMinMaxException → manual Min/Max instead.
//   - Normalized's len<1e-12 branch returns Vec3.Zero — kept (no Throw).
//   - Vec3.Pow's Asin(Math.Clamp(...)) → inlined scalar clamp.
//
// Layout/results match the CPU Vec3 ops bit-for-bit on finite inputs; this
// is what makes Stage 3A GPU output parity-checkable against the CPU path.

using System;
using System.Runtime.CompilerServices;

namespace FracturingFog.Models
{
    public static class Vec3GpuOps
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double Clamp(double v, double lo, double hi)
        {
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vec3 Normalized(Vec3 v)
        {
            double len = v.Length;
            return len < 1e-12 ? Vec3.Zero : new Vec3(v.X / len, v.Y / len, v.Z / len);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vec3 Pow(Vec3 v, double n)
        {
            double r = v.Length;
            if (r < 1e-12) return Vec3.Zero;
            double theta = Math.Atan2(v.Y, v.X) * n;
            double zr = v.Z / r;
            if (zr > 1.0) zr = 1.0;
            if (zr < -1.0) zr = -1.0;
            double phi = Math.Asin(zr) * n;
            double rn = Math.Pow(r, n);
            double cosp = Math.Cos(phi);
            return new Vec3(rn * cosp * Math.Cos(theta), rn * cosp * Math.Sin(theta), rn * Math.Sin(phi));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vec3 Rot(Vec3 v, Vec3 axis, double angle)
        {
            var k = Normalized(axis);
            double c = Math.Cos(angle), s = Math.Sin(angle);
            double dotKV = k.X * v.X + k.Y * v.Y + k.Z * v.Z;
            double crossX = k.Y * v.Z - k.Z * v.Y;
            double crossY = k.Z * v.X - k.X * v.Z;
            double crossZ = k.X * v.Y - k.Y * v.X;
            double oneMinusC = 1.0 - c;
            return new Vec3(
                v.X * c + crossX * s + k.X * dotKV * oneMinusC,
                v.Y * c + crossY * s + k.Y * dotKV * oneMinusC,
                v.Z * c + crossZ * s + k.Z * dotKV * oneMinusC);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static double FoldAxis(double x, double limit)
        {
            if (x > limit) return 2.0 * limit - x;
            if (x < -limit) return -2.0 * limit - x;
            return x;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vec3 BoxFold(Vec3 v, double limit) => new(
            FoldAxis(v.X, limit), FoldAxis(v.Y, limit), FoldAxis(v.Z, limit));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vec3 SphereFold(Vec3 v, double rMin, double rMax)
        {
            double r2 = v.X * v.X + v.Y * v.Y + v.Z * v.Z;
            double rMin2 = rMin * rMin;
            double rMax2 = rMax * rMax;
            if (r2 < rMin2)
            {
                double s = rMax2 / rMin2;
                return new Vec3(v.X * s, v.Y * s, v.Z * s);
            }
            if (r2 < rMax2)
            {
                double s = rMax2 / r2;
                return new Vec3(v.X * s, v.Y * s, v.Z * s);
            }
            return v;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vec3 Mod(Vec3 v, double period) => new(
            v.X - period * Math.Floor(v.X / period + 0.5),
            v.Y - period * Math.Floor(v.Y / period + 0.5),
            v.Z - period * Math.Floor(v.Z / period + 0.5));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vec3 AbsX(Vec3 v) => new(Math.Abs(v.X), v.Y, v.Z);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vec3 AbsY(Vec3 v) => new(v.X, Math.Abs(v.Y), v.Z);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vec3 AbsZ(Vec3 v) => new(v.X, v.Y, Math.Abs(v.Z));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double SMin(double a, double b, double k) =>
            -Math.Log(Math.Exp(-k * a) + Math.Exp(-k * b)) / k;
    }
}
