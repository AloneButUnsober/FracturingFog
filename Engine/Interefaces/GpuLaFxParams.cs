// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Runtime.InteropServices;

namespace FracturingFog.Rendering
{
    /// <summary>#1664 — the cbuffer of the float-exp LA kernel (LaKernelSource.BuildFloatExp,
    /// <c>PerturbLaFxParams</c>), byte for byte: 28 32-bit scalars, 112 bytes. Shared by the
    /// D3D11 and Vulkan backends.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct GpuLaFxParams
    {
        public float ScaleM; public int ScaleE; public float OffX0, OffY0;
        public int Width, Height, MaxIter, RefLen, RowBase, Stages, AtLen, StepsBase, Wave, BandRows, Band;
        public float EscapeR2;
        public float AtThrCM; public int AtThrCE; public float AtEsc2;
        public float AtRefCR, AtRefCI;
        public float AtCR, AtCI; public int AtCE;
        public float AtInvZR, AtInvZI; public int AtInvZE;
        public int Pad0;

        public const int Bytes = 112;

        /// <summary>#1882 — the largest |binary exponent| of the pixel scale the kernel is
        /// given (zoom ~1e150,000): its float-exp exponents stay far from int overflow and
        /// from its zero exponent (<see cref="GpuFloatExp.ZeroExp"/>).</summary>
        public const int MaxScaleExponent = 500_000;

        /// <summary>The frame's parameters (RowBase / Wave / BandRows / Band are set per band).
        /// #1882 (epic #1428, C6b) — the pixel scale is <paramref name="scale"/> ·
        /// 2^<paramref name="scaleExp2"/>: past double's range (zoom past ~1e300) the host
        /// passes a FloatExp's mantissa and exponent.</summary>
        public static GpuLaFxParams Make(int width, int height, double scale, int maxIter, double escapeRadius2,
            double offsetX0, double offsetY0, int refLen, int stages, GpuLaAt at, int scaleExp2 = 0)
        {
            if (!(scale > 0) || !double.IsFinite(scale)) throw new ArgumentOutOfRangeException(nameof(scale));
            long el = (long)Math.ILogB(scale) + scaleExp2;
            if (el > MaxScaleExponent || el < -MaxScaleExponent) throw new ArgumentOutOfRangeException(nameof(scaleExp2));
            int e = (int)el;
            var p = new GpuLaFxParams
            {
                ScaleM = (float)Math.ScaleB(scale, -Math.ILogB(scale)), ScaleE = e,
                OffX0 = (float)offsetX0, OffY0 = (float)offsetY0,
                Width = width, Height = height, MaxIter = maxIter, RefLen = refLen,
                Stages = stages, AtLen = at.Len, StepsBase = 2 * stages,
                EscapeR2 = (float)escapeRadius2,
                AtEsc2 = (float)at.Esc2,
                AtRefCR = (float)at.RefCRe, AtRefCI = (float)at.RefCIm,
            };
            if (at.Len > 0)
            {
                GpuFloatExp.Encode(at.ThrC, out p.AtThrCM, out p.AtThrCE);
                GpuFloatExp.Encode(at.CRe, at.CIm, out p.AtCR, out p.AtCI, out p.AtCE);
                GpuFloatExp.Encode(at.InvZRe, at.InvZIm, out p.AtInvZR, out p.AtInvZI, out p.AtInvZE);
            }
            else
            {
                p.AtThrCE = p.AtCE = p.AtInvZE = GpuFloatExp.ZeroExp;
            }
            return p;
        }
    }
}
