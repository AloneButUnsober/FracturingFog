// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

namespace FracturingFog.Rendering
{
    /// <summary>#1630 — the frame's approximation transform as the LA perturbation kernel
    /// takes it (<c>FFMath.LaAt</c>'s fields; <see cref="Len"/> 0 = no AT).</summary>
    public readonly record struct GpuLaAt(
        int Len, double ThrC, double Esc2,
        double RefCRe, double RefCIm, double CRe, double CIm, double InvZRe, double InvZIm);
}
