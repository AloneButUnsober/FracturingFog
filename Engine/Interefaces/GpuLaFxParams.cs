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

        /// <summary>The frame's parameters (RowBase / Wave / BandRows / Band are set per band).</summary>
        public static GpuLaFxParams Make(int width, int height, double scale, int maxIter, double escapeRadius2,
            double offsetX0, double offsetY0, int refLen, int stages, GpuLaAt at)
        {
            if (!(scale > 0) || !double.IsFinite(scale)) throw new ArgumentOutOfRangeException(nameof(scale));
            int e = Math.ILogB(scale);
            var p = new GpuLaFxParams
            {
                ScaleM = (float)Math.ScaleB(scale, -e), ScaleE = e,
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
