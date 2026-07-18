// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Interefaces/INewtonColorMap.cs
//
// Newton-specific coloring contract. NewtonCalculator dispatches color
// computation through this interface when the active ColorMap implements
// it; otherwise it falls back to its built-in HSV-per-basin shading.
//
// Inputs unique to Newton:
//   basin        Index of the root the iterate converged to (0 .. totalBasins-1).
//                Pass -1 when the iterate failed to converge in the iteration
//                budget; themes should paint a sensible "interior" colour.
//   totalBasins  Polynomial degree d (number of roots, hence number of basins).
//   iter         Iterations consumed to reach convergence.
//   maxIter      Iteration budget for the frame.
//   zr, zi       Final z components at convergence (≈ position of the root).

namespace FracturingFog.Interefaces
{
    public interface INewtonColorMap : IColorMap
    {
        int MapNewton(int basin, int totalBasins, int iter, int maxIter, double zr, double zi);
    }
}
