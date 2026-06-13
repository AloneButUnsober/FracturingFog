// Models/QuatGpuOps.cs
//
// Device-safe mirrors of Quat helpers for ILGPU kernels emitted by
// UserBulbSandboxGpuCompiler in Quat axis mode. Mirrors CPU semantics but
// swaps the IL Throw opcode (ILGPU JIT rejects exception flow) for silent
// guards:
//   - Quat.Pow's non-integer / negative exponent throws on CPU. GPU mirror
//     rounds to int, clamps to [0, MaxIter], and returns Identity when the
//     exponent is invalid (out-of-range / non-finite). Quat.Pow's literal-int
//     fast path is already inlined to a chain of `*` by the emitter, so this
//     helper only fires on runtime exponents.
//
// All other Quat ops (+, -, *Hamilton, *scalar, .Conjugate, .Length,
// .FromVec3, .ToVec3) are throw-free on CPU and run as-is on GPU.

using System;
using System.Runtime.CompilerServices;

namespace FracturingFog.Models
{
    public static class QuatGpuOps
    {
        /// <summary>Upper bound on the runtime exponent loop. Matches a sane
        /// fractal-iteration ceiling and keeps ILGPU's loop unroll bounded.</summary>
        public const int MaxIter = 16;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Quat Pow(Quat q, double exp)
        {
            int n = (int)Math.Round(exp);
            if (n < 0) n = 0;
            if (n > MaxIter) n = MaxIter;
            var r = Quat.Identity;
            for (int i = 0; i < n; i++) r = r * q;
            return r;
        }
    }
}
