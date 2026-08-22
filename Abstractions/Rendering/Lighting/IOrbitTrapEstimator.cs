// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// IOrbitTrapEstimator.cs
//
// Roadmap slice S9 (3D-Rendering-Roadmap.md §S9, #391) — a view-INDEPENDENT,
// fractal-MEANINGFUL colour driver for the mesh export. The mesh colour source
// cannot replay the screen's driver (raymarch step count + view depth are
// view-dependent), and its fallback — radial distance from the centre — carries the
// palette but says nothing about the fractal's structure. An orbit trap does: it is
// the classic escape-time colouring, a scalar derived from how close the iteration
// orbit passes to a trap shape (here the origin). It is a property of the POINT, so
// it is stable per surface vertex and reproducible off-screen.
//
// Optional companion to <see cref="IDistanceEstimator"/>: a DE struct that iterates
// can also report its orbit trap for the same point. The mesh colour source prefers
// this when the estimator implements it, and falls back to the radial driver
// otherwise — so families gain fractal-meaningful mesh colour one at a time without
// changing the export contract.

namespace FracturingFog.Rendering.Lighting;

/// <summary>Reports a normalized [0, 1] orbit-trap value at an object-space point —
/// a view-independent, fractal-meaningful scalar for driving the mesh colour map
/// (roadmap S9, #391). Implemented alongside <see cref="IDistanceEstimator"/> by DE
/// structs whose iteration exposes a trap.</summary>
public interface IOrbitTrapEstimator
{
    /// <summary>Orbit-trap value at (x, y, z), normalized to [0, 1]. Larger = the
    /// orbit passed farther from the trap. Deterministic and view-independent.</summary>
    double OrbitTrap(double x, double y, double z);
}
