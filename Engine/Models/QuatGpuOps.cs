// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Models/QuatGpuOps.cs
//
// Device-safe mirrors of Quat helpers for ILGPU kernels emitted by
// UserBulbSandboxGpuCompiler in Quat axis mode. Mirrors CPU semantics but
// avoids constructs the ILGPU JIT rejects (the IL Throw opcode; Math.Clamp,
// whose lo>hi guard emits a Throw):
//   - Quat.Pow (CPU) uses exact self-multiply for non-negative integer
//     exponents and the analytic exp(exp·log q) form for fractional/negative
//     exponents, and never throws. The only reason this GPU mirror still
//     exists is the integer loop: it is clamped to MaxIter here to keep
//     ILGPU's unroll bounded, whereas Quat.Pow's CPU loop is unbounded. The
//     analytic branch calls Quat.Exp/Quat.Log directly — both are now
//     Clamp/Throw-free and device-safe.
//   - Quat.Pow's literal-int fast path is already inlined to a chain of `*`
//     by the emitter, so this helper only fires on runtime exponents.
//
// Quat transcendentals (Sin/Cos/.../Exp/Log/Sqrt/Asin/...) are all throw-free
// and Clamp-free on CPU, so the emitter emits Quat.* for them directly on GPU
// with no mirror needed here.

using System;
using System.Runtime.CompilerServices;

namespace FracturingFog.Models
{
    public static class QuatGpuOps
    {
        /// <summary>Upper bound on the integer-exponent loop. Matches a sane
        /// fractal-iteration ceiling and keeps ILGPU's loop unroll bounded.</summary>
        public const int MaxIter = 16;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Quat Pow(Quat q, double exp)
        {
            int n = (int)Math.Round(exp);
            if (Math.Abs(exp - n) < 1e-9 && n >= 0)
            {
                if (n > MaxIter) n = MaxIter;
                var r = Quat.Identity;
                for (int i = 0; i < n; i++) r = r * q;
                return r;
            }
            // Fractional / negative exponent: q^exp = exp(exp · log q).
            return Quat.Exp(Quat.Scale(Quat.Log(q), exp));
        }
    }
}
