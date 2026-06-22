using FracturingFog.Interefaces;
using System;
using System.Collections.Generic;
using System.Text;

namespace FracturingFog.Models
{
    public class Pastelly : IColorMap, IGpuHlslPalette
    {
        public static string Name => "Pastelly";

        public ColorPaletteType Type { get; } = ColorPaletteType.Algorithmic;

        public int MaxIterations { get; set; } = 1000;

        public string HlslPrelude => HlslPaletteHelpers.HsvAndMods;

        // Mirrors Map() — saturation expression `c * (t / c) % 1.0` algebraically
        // simplifies to `t % 1.0` (same as hue). NaN when distance == 0 on CPU
        // and on GPU — cg_pack_bgra saturates the eventual write.
        public string HlslPaletteBody => @"
    float t = in_smooth * 0.05;
    float hue = t - floor(t);
    float sat = t - floor(t);
    float baseV = (in_isInSet > 0.5) ? -0.01 : 1.0;
    float lightness = 1.35 - min(t * 0.04, 1.0);
    float value = saturate(baseV * (lightness + 0.3 * exp(-in_dist * 0.2)));
    return cg_hsv_to_rgb(hue, sat, value);
";

        public string PaletteId => "Pastelly/v1";

        //public int Map(float smooth, float distance, int iterations)
        //{
        //    float t = smooth * 0.015f;
        //    float hue = t % 1.0f;
        //    hue += MathF.Floor(hue);

        //    float saturation = Math.Clamp(0.9f / t, 0.0f, 1.0f);
        //    float baseValue = t < iterations ? 1.0f : -0.01f;
        //    float lightness = 1.35f - MathF.Min(t * 0.04f, 1.0f);
        //    float value = baseValue * (lightness + (0.3f * MathF.Exp(-distance * 0.2f)));

        //    return Fractals.HsvToRgb(hue, saturation, value);
        //}

        public int Map(float smooth, float distance, int iterations)
        {
            float t = smooth * 0.05f;
            float c = distance % iterations * t; 
            // Hue — unchanged. Cycles cleanly once every ~67 smooth-units.
            float hue = t % 1.0f;
            // Removed: hue += MathF.Floor(hue);
            // This was always adding 0 (hue after % 1.0f is already in [0,1)
            // so Floor is always 0). No-op removed for clarity.

            // Saturation — CHANGED.
            // Old: Math.Clamp(0.9f / t, 0.0f, 1.0f)
            //   Problem: flat at 1.0 for all smooth < 60 (dead zone), then sudden
            //   cliff to a hyperbolic drop — a C1 discontinuity visible as a hard band.
            //
            // New: 0.9f / (t + 0.9f)
            //   Shifting the denominator by 0.9 (the numerator value) means the formula
            //   reaches exactly 1.0 only at t = 0, which escaped pixels never reach
            //   (smooth is always > 0). No clamping needed, no cliff, no dead zone.
            //   Same artistic shape: vivid for fast-escaping, muted for slow-escaping.
            //   Tuning: increase 0.9f denomininator offset to preserve saturation longer;
            //   decrease it to accelerate the drop-off.
            float saturation = c * (t / c) % 1.0f; // 0.6f; // / (t + 0.999999f);

            // Value — logic UNCHANGED, clamp ADDED.
            // Old: value could reach 1.65 at distance = 0 (lightness 1.35 + glow 0.3).
            //   HsvToRgb clipped channels to 255, producing flat white patches.
            // New: Math.Clamp caps at 1.0 with no other changes to the formula.
            float baseValue = t < iterations ? 1.0f : -0.01f;
            float lightness = 1.35f - MathF.Min(t * 0.04f, 1.0f);
            float value = Math.Clamp(
                                  baseValue * (lightness + (0.3f * MathF.Exp(-distance * 0.2f))),
                                  0f, 1f);

            return Fractals.HsvToRgb(hue, saturation, value);
        }
    }
}
