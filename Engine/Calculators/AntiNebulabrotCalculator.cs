// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// AntiNebulabrotCalculator.cs
//
// Anti-Nebulabrot — three-band composite of the in-set orbit replay. Same
// predicate as AntiBuddhabrot; differs only in the default
// BuddhaColorMode (NebulabrotBands). Splits in-set orbits across R/G/B by
// a pseudo-length derived from the final |z|² (deeper interior → high band,
// near-boundary → low band).

namespace FracturingFog;

public sealed class AntiNebulabrotCalculator : BuddhaFamilyCalculator
{
    protected override bool IsInSet => true;

    public AntiNebulabrotCalculator(int width, int height) : base(width, height) { }
}
