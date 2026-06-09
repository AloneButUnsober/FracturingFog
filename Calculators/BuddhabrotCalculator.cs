// BuddhabrotCalculator.cs
//
// Buddhabrot — replays orbits of c values whose escape time falls inside one
// of the three iteration bands and accumulates a per-pixel hit count. Output
// composition is decided by FractalParameters.BuddhaColorMode (see
// BuddhaFamilyCalculator for the shared Monte Carlo core).
//
// Use FractalType.BuddhaBrot for the pure (single-channel via ColorMap)
// look. FractalType.Nebulabrot uses NebulabrotCalculator with the band
// composite enabled by default.

namespace FracturingFog;

public sealed class BuddhabrotCalculator : BuddhaFamilyCalculator
{
    protected override bool IsInSet => false;

    public BuddhabrotCalculator(int width, int height) : base(width, height) { }
}
