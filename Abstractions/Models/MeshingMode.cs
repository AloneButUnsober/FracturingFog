// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Models/MeshingMode.cs
//
// Roadmap slice S9 (3D-Rendering-Roadmap.md §S9, #391) — which isosurface mesher the
// true-3D export uses. Lives in Abstractions so the snapshot (persistence) and the
// UI event args share one definition.

namespace FracturingFog.Models;

/// <summary>Isosurface meshing algorithm for the 3D mesh export.</summary>
public enum MeshingMode
{
    /// <summary>Marching Cubes — vertices on grid edges; smooth, rounds hard
    /// creases at grid resolution (the historical default).</summary>
    MarchingCubes = 0,

    /// <summary>Dual contouring — one QEF-solved vertex per cell; keeps hard creases
    /// (Mandelbox facets, KIFS corners) sharp.</summary>
    DualContouring = 1,
}
