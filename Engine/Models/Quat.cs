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
//
// ESCAPE CONTRACT — these ops NEVER throw.
//   The Quat DE hot loop (UserBulbCalculator.UserBulbQuatDE) has no try/catch
//   by design: Compile() smoke-tests the delegate once with finite inputs,
//   then the loop trusts it and only guards `!double.IsFinite(r)` → break.
//   A throw whose trigger depends on RUNTIME values (a value the smoke test
//   never hit) would sail past that check and crash the whole render, whereas
//   a NaN/Inf result simply escapes the pixel. So every op here returns a
//   non-finite quat for undefined inputs instead of throwing. Divide-style
//   ops (Inverse, Tan, Csc, ...) already degrade this way; Pow and Sqrt now
//   match. Do not reintroduce `throw` in this file.

using System;
using System.Diagnostics;
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

        public static Quat One => new(1.0, 0.0, 0.0, 0.0);

        public static Quat Pi = new(Math.PI, 0.0, 0.0, 0.0);

        public static Quat HalfPi = new(Math.PI / 2.0, 0.0, 0.0, 0.0);

        // Returns the pure-quaternion "imaginary unit" built from q's own axis.
        // This is what makes Asin/Acos/Atan valid: q lives entirely in the
        // 2D plane spanned by {1, I}, so it behaves like an ordinary complex number.
        public static Quat QuatAxis(Quat q)
        {
            Vec3 v = q.ToVec3();
            double r = v.Length;

            if (r < 1e-8)
            {
                // Degenerate: q is (numerically) a pure real quaternion.
                // No defined rotation axis - fall back to the x-axis by convention.
                // Result will match the real-valued asin/acos/atan branch behavior.
                return new Quat(0.0, 1.0, 0.0, 0.0);

                /*QuatAxis's degenerate fallback is a real design choice, not a neutral default. 
                 * When q is (numerically) pure real, there's no natural rotation axis, so I arbitrarily pick the x-axis. 
                 * This only affects the direction of the resulting vector part when the output is non-real 
                 * (e.g. Asin of a real number > 1, which is complex/non-real even for real input).
                 * If you see a hard seam artifact along the x-axis in a fractal using these, this fallback is almost 
                 * certainly why — you may want a different fallback convention depending on how your renderer slices the 4D space.
               */
            }

            Vec3 vNorm = v / r;
            return Quat.FromVec3(vNorm, 0.0);
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

        /// <summary>q raised to a real exponent. Non-negative integer exponents
        /// use exact Hamilton self-multiply (also defines Pow(0,0)=Identity and
        /// Pow(0,n)=0). Fractional or negative exponents use the analytic
        /// exp(exp·log q) form on q's principal axis — this is what makes Sqrt
        /// and the inverse-trig helpers (which call Sqrt) work. Never throws:
        /// an undefined case (e.g. q=0 with exp≤0) yields a non-finite quat
        /// that the DE loop's IsFinite guard turns into an escaped pixel.</summary>
        public static Quat Pow(Quat q, double exp)
        {
            int n = (int)Math.Round(exp);
            if (Math.Abs(exp - n) < 1e-9 && n >= 0)
            {
                var r = Identity;
                for (int i = 0; i < n; i++) r = r * q;
                return r;
            }
            // Fractional / negative exponent: q^exp = exp(exp · log q).
            return Exp(Scale(Log(q), exp));
        }

        public static Quat Exp(Quat q)
        {
            double a = q.W;
            Vec3 v = q.ToVec3();
            double r = v.Length;

            if (r < 1e-8) return new(Math.Exp(a), 0.0, 0.0, 0.0);

            Vec3 vNorm = v / r;
            double ea = Math.Exp(a);
            double scalar = ea * Math.Sin(r);
            Vec3 scaledV = vNorm * scalar;
            double w = ea * Math.Cos(r);
            return Quat.FromVec3(scaledV, w);
        }

        public static Quat Log(Quat q)
        {
            double r = q.Length;
            Vec3 v = q.ToVec3();
            double vLen = v.Length;

            if (vLen < 1e-8)
            {
                // Pure real quaternion - no rotation axis=
                return new Quat(Math.Log(r), 0.0, 0.0, 0.0);
            }

            Vec3 vNorm = v / vLen;
            // Manual clamp, not Math.Clamp: the latter emits a Throw branch for
            // lo>hi that the ILGPU JIT rejects, and this method is on the GPU
            // path for quat transcendentals (qlog/qsqrt/qasin/...).
            double cw = q.W / r;
            if (cw < -1.0) cw = -1.0; else if (cw > 1.0) cw = 1.0; // guard fp drift
            double theta = Math.Acos(cw);
            return Quat.FromVec3(theta * vNorm, Math.Log(r));
        }

        public static Quat Scale(Quat q, double n) => new(q.W * n, q.X * n, q.Y * n, q.Z * n);

        public static Quat Inverse(Quat q)
        {
            double normSq = q.W * q.W + q.X * q.X + q.Y * q.Y + q.Z * q.Z;
            Quat conj = q.Conjugate();
            return new(conj.W / normSq, conj.X / normSq, conj.Y / normSq, conj.Z / normSq);
        }

        public static Quat Sqrt(Quat q) => Quat.Pow(q, 0.5);

        // ---- Sin ----
        public static Quat Sin(Quat q)
        {
            double a = q.W;
            Vec3 v = q.ToVec3();
            double r = v.Length;

            if (r < 1e-8)
            {
                return new(Math.Sin(a), 0.0, 0.0, 0.0);
            }

            Vec3 vNorm = v / r;
            double scalar = Math.Cos(a) * Math.Sinh(r);
            Vec3 scaledV = vNorm * scalar;
            double w = Math.Sin(a) * Math.Cosh(r);

            return Quat.FromVec3(scaledV, w);
        }

        // ---- Cos ----

        public static Quat Cos(Quat q)
        {
            double a = q.W;
            Vec3 v = q.ToVec3();
            double r = v.Length;

            if (r < 1e-8)
            {
                return new(Math.Cos(a), 0.0, 0.0, 0.0);
            }

            Vec3 vNorm = v / r;
            double scalar = -Math.Sin(a) * Math.Sinh(r);   // note the sign flip vs Sin
            Vec3 scaledV = vNorm * scalar;
            double w = Math.Cos(a) * Math.Cosh(r);

            return Quat.FromVec3(scaledV, w);
        }

        // ---- Tan = Sin * Cos^-1 ----

        public static Quat Tan(Quat q)
        {
            Quat s = Sin(q);
            Quat c = Cos(q);
            return s * Quat.Inverse(c);
        }

        // ---- Csc = 1 / Sin ----

        public static Quat Csc(Quat q)
        {
            return Quat.Inverse(Sin(q));
        }

        // ---- Sec = 1 / Cos ----

        public static Quat Sec(Quat q)
        {
            return Quat.Inverse(Cos(q));
        }

        // ---- Cot = Cos * Sin^-1 ----

        public static Quat Cot(Quat q)
        {
            Quat s = Sin(q);
            Quat c = Cos(q);
            return c * Quat.Inverse(s);
        }

        // ---- Sinh ----

        public static Quat Sinh(Quat q)
        {
            double a = q.W;
            Vec3 v = q.ToVec3();
            double r = v.Length;

            if (r < 1e-8)
            {
                return new(Math.Sinh(a), 0.0, 0.0, 0.0);
            }

            Vec3 vNorm = v / r;
            double scalar = Math.Cosh(a) * Math.Sin(r);
            Vec3 scaledV = vNorm * scalar;
            double w = Math.Sinh(a) * Math.Cos(r);

            return Quat.FromVec3(scaledV, w);
        }

        // ---- Cosh ----

        public static Quat Cosh(Quat q)
        {
            double a = q.W;
            Vec3 v = q.ToVec3();
            double r = v.Length;

            if (r < 1e-8)
            {
                return new(Math.Cosh(a), 0.0, 0.0, 0.0);
            }

            Vec3 vNorm = v / r;
            double scalar = Math.Sinh(a) * Math.Sin(r);
            Vec3 scaledV = vNorm * scalar;
            double w = Math.Cosh(a) * Math.Cos(r);

            return Quat.FromVec3(scaledV, w);
        }

        // ---- Tanh = Sinh * Cosh^-1 ----

        public static Quat Tanh(Quat q)
        {
            Quat s = Sinh(q);
            Quat c = Cosh(q);
            return s * Quat.Inverse(c);
        }

        // ---- Csch = 1 / Sinh ----

        public static Quat Csch(Quat q)
        {
            return Quat.Inverse(Sinh(q));
        }

        // ---- Sech = 1 / Cosh ----

        public static Quat Sech(Quat q)
        {
            return Quat.Inverse(Cosh(q));
        }

        // ---- Coth = Cosh * Sinh^-1 ----

        public static Quat Coth(Quat q)
        {
            Quat s = Sinh(q);
            Quat c = Cosh(q);
            return c * Quat.Inverse(s);
        }

        // ---- Asin ----

        public static Quat Asin(Quat q)
        {
            Quat I = QuatAxis(q);
            Quat iz = I * q;
            Quat oneMinusZSq = One - (q * q);
            Quat sqrtTerm = Quat.Sqrt(oneMinusZSq);
            Quat inner = iz + sqrtTerm;
            Quat lnInner = Quat.Log(inner);

            return -(I) * lnInner;
        }

        // ---- Acos = pi/2 - Asin(q) ----

        public static Quat Acos(Quat q)
        {
            return HalfPi - Asin(q);
        }

        // ---- Atan = (I/2) * ln((1 - I*q) / (1 + I*q)) ----

        public static Quat Atan(Quat q)
        {
            Quat I = QuatAxis(q);
            Quat iz = I * q;

            Quat numerator = One - iz;
            Quat denominator = One + iz;
            Quat ratio = numerator * Quat.Inverse(denominator);
            Quat lnRatio = Quat.Log(ratio);

            return Quat.Scale(I, 0.5) * lnRatio;
        }

        // ---- Asinh = ln(q + sqrt(q^2 + 1)) ----

        public static Quat Asinh(Quat q)
        {
            Quat qSqPlus1 = (q * q) + One;
            Quat sqrtTerm = Quat.Sqrt(qSqPlus1);
            return Quat.Log(q + sqrtTerm);
        }

        // ---- Acosh = ln(q + sqrt(q^2 - 1)) ----

        public static Quat Acosh(Quat q)
        {
            Quat qSqMinus1 = (q * q) - One;
            Quat sqrtTerm = Quat.Sqrt(qSqMinus1);
            return Quat.Log(q + sqrtTerm);
        }

        // ---- Atanh = 0.5 * ln((1+q) / (1-q)) ----

        public static Quat Atanh(Quat q)
        {
            Quat numerator = One + q;
            Quat denominator = One - q;
            Quat ratio = numerator * Quat.Inverse(denominator);
            return Quat.Scale(Quat.Log(ratio), 0.5);
        }

        public override string ToString() => $"({W:G6}, {X:G6}, {Y:G6}, {Z:G6})";
    }
}
