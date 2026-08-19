// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Imaging/ViewTransform.cs
//
// Roadmap slice S2 (3D-Rendering-Roadmap.md, parent #389): the output-stage
// view-transform (tonemap) selector. The enum lives in Abstractions so
// FractalViewState / batch options can carry it; the pure operator math is
// ViewTransformOps in the Engine (FracturingFog.Imaging.ViewTransformOps).

namespace FracturingFog.Imaging
{
    /// <summary>Output-stage view transform (tonemap) selector. Default
    /// <see cref="None"/> = identity, preserving the current look until the user
    /// opts in.</summary>
    public enum ViewTransform
    {
        /// <summary>Identity — the buffer is emitted unchanged (byte-identical).</summary>
        None = 0,
        /// <summary>Reinhard <c>x/(1+x)</c> — the simplest highlight roll-off.</summary>
        Reinhard,
        /// <summary>ACES filmic (Narkowicz 2015 fit) — punchy, saturated contrast.</summary>
        AcesFilmic,
        /// <summary>AgX (Sobotka / Wrensch minimal fit) — gentle, desaturating
        /// highlights; the modern default (Blender 4.x view transform).</summary>
        AgX,
        /// <summary>Filmic (Hable / Uncharted 2) — classic shoulder + toe.</summary>
        Filmic,
    }
}
