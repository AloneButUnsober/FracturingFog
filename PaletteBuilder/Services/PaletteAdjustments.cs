// Services/PaletteAdjustments.cs
//
// Lightweight global colour adjustments applied to a swatch list after
// extraction and before display / export. Phase 4.7 covers temperature +
// tint; future passes will add brightness, saturation boost, etc.
//
// Temperature is a blue↔yellow shift: positive values warm (push R+G,
// pull B), negative cool. Tint is a green↔magenta shift: positive pushes
// R+B and pulls G. Both are normalised to [-1, +1]; magnitude maxes out
// at a ±64 byte shift on the most-affected channel — strong enough to be
// visible but tame enough that the swatch remains recognisably itself.

using System;
using System.Collections.Generic;

namespace PaletteBuilder.Services
{
    public static class PaletteAdjustments
    {
        /// <summary>Apply temperature + tint to a single swatch.</summary>
        public static (byte R, byte G, byte B) Apply((byte R, byte G, byte B) c, double temperature, double tint)
        {
            if (temperature == 0 && tint == 0) return c;

            double t = Math.Clamp(temperature, -1.0, 1.0);
            double m = Math.Clamp(tint, -1.0, 1.0);
            double scale = 64.0;

            double r = c.R + t * scale + m * scale;
            double g = c.G + t * (scale * 0.5) - m * scale;
            double b = c.B - t * scale + m * scale;

            return (Clamp(r), Clamp(g), Clamp(b));
        }

        /// <summary>Apply temperature + tint to an entire swatch list, returning a new list.</summary>
        public static List<(byte R, byte G, byte B)> ApplyAll(IReadOnlyList<(byte R, byte G, byte B)> swatches,
                                                               double temperature, double tint)
        {
            var output = new List<(byte R, byte G, byte B)>(swatches.Count);
            for (int i = 0; i < swatches.Count; i++)
                output.Add(Apply(swatches[i], temperature, tint));
            return output;
        }

        private static byte Clamp(double v) => (byte)Math.Clamp((int)Math.Round(v), 0, 255);
    }
}
