// AntiBuddhabrotCalculator.cs
//
// Anti-Buddhabrot — Buddhabrot's complement. Replays orbits of c values that
// stay bounded inside the Mandelbrot set (orbits that do NOT escape within
// the iteration budget). Visually emphasises the in-set interior fine-
// structure where the regular Buddhabrot has zero hits.
//
// Default composition is BuddhaColorMode.ColorMap (single hit buffer routed
// through the active IColorMap). Switch to NebulabrotBands for the
// channel-split look.

namespace FracturingFog;

public sealed class AntiBuddhabrotCalculator : BuddhaFamilyCalculator
{
    protected override bool IsInSet => true;

    public AntiBuddhabrotCalculator(int width, int height) : base(width, height) { }
}
