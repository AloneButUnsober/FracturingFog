// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// NebulabrotCalculator.cs
//
// Nebulabrot — escape-orbit Monte Carlo identical to Buddhabrot. Distinct
// FractalType so the toolbar can carry a separate default
// BuddhaColorMode.NebulabrotBands without the user having to flip the
// composite mode manually. Same shared core as BuddhaFamilyCalculator.

namespace FracturingFog;

public sealed class NebulabrotCalculator : BuddhaFamilyCalculator
{
    protected override bool IsInSet => false;

    public NebulabrotCalculator(int width, int height) : base(width, height) { }
}
