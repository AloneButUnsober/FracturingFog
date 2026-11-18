// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

namespace FracturingFog.Rendering
{
    /// <summary>#1630 — the frame's approximation transform as the LA perturbation kernel
    /// takes it (<c>FFMath.LaAt</c>'s fields; <see cref="Len"/> 0 = no AT).</summary>
    public readonly record struct GpuLaAt(
        int Len, double ThrC, double Esc2,
        double RefCRe, double RefCIm, double CRe, double CIm, double InvZRe, double InvZIm)
    {
        /// <summary>#1886 (epic #1428, C5g) — the float-exp AT's bound and coefficients, past
        /// double (null: the doubles above). Only the float-exp LA kernel reads them
        /// (<see cref="GpuLaFxParams.Make"/>).</summary>
        public FracturingFog.FFMath.FloatExp? ThrCFx { get; init; }
        public FracturingFog.FFMath.CFx? CFx { get; init; }
        public FracturingFog.FFMath.CFx? InvZFx { get; init; }

        /// <summary>#1886 — the float-exp LA kernel's AT from a <see cref="FracturingFog.FFMath.LaAtFx"/>
        /// (RefC and Esc² are doubles there too).</summary>
        public static GpuLaAt FromFx(in FracturingFog.FFMath.LaAtFx a)
            => new(a.Len, double.NaN, a.Esc2, a.RefCRe, a.RefCIm, double.NaN, double.NaN, double.NaN, double.NaN)
            { ThrCFx = a.ThrC, CFx = a.C, InvZFx = a.InvZ };
    }
}
