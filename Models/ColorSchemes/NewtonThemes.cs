// Models/ColorSchemes/NewtonThemes.cs
//
// Newton-fractal colour themes. Each theme implements INewtonColorMap so
// NewtonCalculator routes per-pixel colour decisions through MapNewton(),
// receiving basin index, iteration count and convergence position.
//
// Seven theme "kinds" are represented, three or more themes per kind:
//   A. Basin categorical      — flat hue per root, no shading
//   B. Basin + iter shade     — hue per root, brightness fades with speed
//   C. Iteration gradient     — iter count walks a linear gradient
//   D. Iteration cyclic       — iter count cycles a closed gradient
//   E. Argument (final z)     — hue from arg(z) at convergence
//   F. Distance-to-root       — bright glow on root fading by |z − root|
//   G. Banded                 — discrete iteration bands per basin
//
// Each theme also implements the 3-parameter IColorMap.Map(smooth, distance,
// iterations) so it produces something defensible when accidentally selected
// for a non-Newton fractal; that path is not the intended use.

using System;
using FracturingFog.Interefaces;

namespace FracturingFog.Models
{
    // ── Shared helpers ────────────────────────────────────────────────────────

    internal static class NewtonColorHelper
    {
        public static int Rgb(byte r, byte g, byte b)
            => unchecked((int)0xFF000000 | (r << 16) | (g << 8) | b);

        public static int ApplyShade(int rgb, float shade)
        {
            shade = Math.Clamp(shade, 0f, 1f);
            int r = (int)(((rgb >> 16) & 0xFF) * shade);
            int g = (int)(((rgb >> 8) & 0xFF) * shade);
            int b = (int)((rgb & 0xFF) * shade);
            return unchecked((int)0xFF000000 | (r << 16) | (g << 8) | b);
        }

        public static int HsvArgb(float h, float s, float v)
        {
            h = (h % 1f + 1f) % 1f;
            int i = (int)(h * 6f);
            float f = h * 6f - i;
            float p = v * (1f - s);
            float q = v * (1f - f * s);
            float t = v * (1f - (1f - f) * s);
            float rF, gF, bF;
            switch (i % 6)
            {
                case 0: rF = v; gF = t; bF = p; break;
                case 1: rF = q; gF = v; bF = p; break;
                case 2: rF = p; gF = v; bF = t; break;
                case 3: rF = p; gF = q; bF = v; break;
                case 4: rF = t; gF = p; bF = v; break;
                default: rF = v; gF = p; bF = q; break;
            }
            byte rr = (byte)(Math.Clamp(rF, 0f, 1f) * 255);
            byte gg = (byte)(Math.Clamp(gF, 0f, 1f) * 255);
            byte bb = (byte)(Math.Clamp(bF, 0f, 1f) * 255);
            return Rgb(rr, gg, bb);
        }

        public static int Lerp(int a, int b, float t)
        {
            t = Math.Clamp(t, 0f, 1f);
            int ar = (a >> 16) & 0xFF, ag = (a >> 8) & 0xFF, ab = a & 0xFF;
            int br = (b >> 16) & 0xFF, bg = (b >> 8) & 0xFF, bb = b & 0xFF;
            int r = (int)(ar + (br - ar) * t);
            int g = (int)(ag + (bg - ag) * t);
            int bC = (int)(ab + (bb - ab) * t);
            return unchecked((int)0xFF000000 | (r << 16) | (g << 8) | bC);
        }

        public static int SampleStops((float pos, int rgb)[] stops, float t)
        {
            t = Math.Clamp(t, 0f, 1f);
            for (int i = 0; i < stops.Length - 1; i++)
            {
                if (t >= stops[i].pos && t <= stops[i + 1].pos)
                {
                    float range = stops[i + 1].pos - stops[i].pos;
                    float local = range <= 0 ? 0 : (t - stops[i].pos) / range;
                    return Lerp(stops[i].rgb, stops[i + 1].rgb, local);
                }
            }
            return stops[^1].rgb;
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Kind A — Basin Categorical (flat hue per root)
    // ══════════════════════════════════════════════════════════════════════════

    public abstract class NewtonBasinCategoricalBase : INewtonColorMap
    {
        public int MaxIterations { get; set; } = 1000;
        public ColorPaletteType Type => ColorPaletteType.Algorithmic;

        protected abstract int BasinColor(int basin, int totalBasins);

        public int Map(float smooth, float distance, int iterations)
        {
            const int fakeBasins = 6;
            int basin = ((int)Math.Abs(smooth)) % fakeBasins;
            return BasinColor(basin, fakeBasins);
        }

        public int MapNewton(int basin, int totalBasins, int iter, int maxIter, double zr, double zi)
        {
            if (basin < 0) return unchecked((int)0xFF000000);
            return BasinColor(basin, totalBasins);
        }
    }

    public sealed class NewtonBasinClassicMap : NewtonBasinCategoricalBase
    {
        public static string Name => "Newton Basin Classic";
        public static string Category => "Newton";
        public static string Description => "Pure HSV hue per root basin, no shading.";
        public static ColorMapFeatures Features => ColorMapFeatures.None;

        protected override int BasinColor(int basin, int totalBasins)
            => NewtonColorHelper.HsvArgb((float)basin / Math.Max(1, totalBasins), 1f, 1f);
    }

    public sealed class NewtonBasinPastelMap : NewtonBasinCategoricalBase
    {
        public static string Name => "Newton Basin Pastel";
        public static string Category => "Newton";
        public static string Description => "Soft pastel hues per root, flat fill.";
        public static ColorMapFeatures Features => ColorMapFeatures.None;

        protected override int BasinColor(int basin, int totalBasins)
            => NewtonColorHelper.HsvArgb((float)basin / Math.Max(1, totalBasins), 0.40f, 1f);
    }

    public sealed class NewtonBasinBoldMap : NewtonBasinCategoricalBase
    {
        private static readonly int[] Palette =
        {
            NewtonColorHelper.Rgb(220,  30,  30),
            NewtonColorHelper.Rgb( 30, 130, 220),
            NewtonColorHelper.Rgb(240, 200,  20),
            NewtonColorHelper.Rgb( 30, 180,  80),
            NewtonColorHelper.Rgb(180,  30, 200),
            NewtonColorHelper.Rgb(240, 120,  20),
            NewtonColorHelper.Rgb( 20, 200, 200),
            NewtonColorHelper.Rgb(160, 160, 160),
        };

        public static string Name => "Newton Basin Bold";
        public static string Category => "Newton";
        public static string Description => "Bold primary palette per root, flat fill.";
        public static ColorMapFeatures Features => ColorMapFeatures.None;

        protected override int BasinColor(int basin, int totalBasins)
            => Palette[Math.Max(0, basin) % Palette.Length];
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Kind B — Basin + Iteration Shading
    // ══════════════════════════════════════════════════════════════════════════

    public abstract class NewtonBasinShadedBase : INewtonColorMap
    {
        public int MaxIterations { get; set; } = 1000;
        public ColorPaletteType Type => ColorPaletteType.Algorithmic;

        protected abstract int BasinHue(int basin, int totalBasins);
        protected virtual float ShadeCurve(float t) => 1f - MathF.Min(t, 0.9f);

        public int Map(float smooth, float distance, int iterations)
        {
            const int fakeBasins = 6;
            int basin = ((int)Math.Abs(smooth) / 8) % fakeBasins;
            float t = iterations > 0 ? (smooth % iterations) / iterations : 0f;
            return NewtonColorHelper.ApplyShade(BasinHue(basin, fakeBasins), ShadeCurve(t));
        }

        public int MapNewton(int basin, int totalBasins, int iter, int maxIter, double zr, double zi)
        {
            if (basin < 0) return unchecked((int)0xFF000000);
            float shade = ShadeCurve(iter / (float)Math.Max(1, maxIter));
            return NewtonColorHelper.ApplyShade(BasinHue(basin, totalBasins), shade);
        }
    }

    public sealed class NewtonBasinShadedMap : NewtonBasinShadedBase
    {
        public static string Name => "Newton Basin Shaded";
        public static string Category => "Newton";
        public static string Description => "Hue per root, shade fades linearly with iteration count.";
        public static ColorMapFeatures Features => ColorMapFeatures.UsesSmooth;

        protected override int BasinHue(int basin, int totalBasins)
            => NewtonColorHelper.HsvArgb((float)basin / Math.Max(1, totalBasins), 1f, 1f);
    }

    public sealed class NewtonBasinDeepShadeMap : NewtonBasinShadedBase
    {
        public static string Name => "Newton Basin Deep Shade";
        public static string Category => "Newton";
        public static string Description => "Strong dark fade — slow convergers go nearly black.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.HighContrast;

        protected override int BasinHue(int basin, int totalBasins)
            => NewtonColorHelper.HsvArgb((float)basin / Math.Max(1, totalBasins), 1f, 1f);

        protected override float ShadeCurve(float t)
            => MathF.Pow(1f - MathF.Min(t, 1f), 1.8f);
    }

    public sealed class NewtonBasinBrightEdgeMap : NewtonBasinShadedBase
    {
        public static string Name => "Newton Basin Bright Edge";
        public static string Category => "Newton";
        public static string Description => "Bright basin boundaries — slow convergers glow.";
        public static ColorMapFeatures Features => ColorMapFeatures.UsesSmooth;

        protected override int BasinHue(int basin, int totalBasins)
            => NewtonColorHelper.HsvArgb((float)basin / Math.Max(1, totalBasins), 0.85f, 1f);

        protected override float ShadeCurve(float t)
            => 0.35f + 0.65f * MathF.Min(t * 2f, 1f);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Kind C — Iteration Gradient (Linear)
    // ══════════════════════════════════════════════════════════════════════════

    public abstract class NewtonIterGradientLinearBase : INewtonColorMap
    {
        public int MaxIterations { get; set; } = 1000;
        public ColorPaletteType Type => ColorPaletteType.GradientLinear;

        protected abstract (float, int)[] Stops { get; }

        public int Map(float smooth, float distance, int iterations)
        {
            float t = iterations > 0 ? smooth / iterations : 0f;
            return NewtonColorHelper.SampleStops(Stops, t);
        }

        public int MapNewton(int basin, int totalBasins, int iter, int maxIter, double zr, double zi)
        {
            float t = MathF.Min(iter / (float)Math.Max(1, maxIter) * 3f, 1f);
            return NewtonColorHelper.SampleStops(Stops, t);
        }
    }

    public sealed class NewtonSunsetGradientMap : NewtonIterGradientLinearBase
    {
        public static string Name => "Newton Sunset";
        public static string Category => "Newton";
        public static string Description => "Iteration → deep purple to amber sunset gradient.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.GradientBased;

        private static readonly (float, int)[] _stops =
        {
            (0.00f, NewtonColorHelper.Rgb( 15,   0,  30)),
            (0.30f, NewtonColorHelper.Rgb( 80,  20,  90)),
            (0.55f, NewtonColorHelper.Rgb(200,  60,  80)),
            (0.80f, NewtonColorHelper.Rgb(245, 170,  60)),
            (1.00f, NewtonColorHelper.Rgb(255, 240, 200)),
        };
        protected override (float, int)[] Stops => _stops;
    }

    public sealed class NewtonForestGradientMap : NewtonIterGradientLinearBase
    {
        public static string Name => "Newton Forest";
        public static string Category => "Newton";
        public static string Description => "Dark moss → bright lime gradient by iteration count.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.GradientBased;

        private static readonly (float, int)[] _stops =
        {
            (0.00f, NewtonColorHelper.Rgb(  5,  15,   5)),
            (0.30f, NewtonColorHelper.Rgb( 15,  60,  30)),
            (0.60f, NewtonColorHelper.Rgb( 70, 140,  60)),
            (0.85f, NewtonColorHelper.Rgb(180, 220, 100)),
            (1.00f, NewtonColorHelper.Rgb(240, 255, 200)),
        };
        protected override (float, int)[] Stops => _stops;
    }

    public sealed class NewtonIceGradientMap : NewtonIterGradientLinearBase
    {
        public static string Name => "Newton Ice";
        public static string Category => "Newton";
        public static string Description => "Deep navy → glacier white gradient.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.GradientBased;

        private static readonly (float, int)[] _stops =
        {
            (0.00f, NewtonColorHelper.Rgb(  2,   6,  25)),
            (0.30f, NewtonColorHelper.Rgb( 20,  50, 110)),
            (0.60f, NewtonColorHelper.Rgb(100, 180, 230)),
            (0.85f, NewtonColorHelper.Rgb(200, 235, 250)),
            (1.00f, NewtonColorHelper.Rgb(255, 255, 255)),
        };
        protected override (float, int)[] Stops => _stops;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Kind D — Iteration Cyclic
    // ══════════════════════════════════════════════════════════════════════════

    public abstract class NewtonIterCyclicBase : INewtonColorMap
    {
        public int MaxIterations { get; set; } = 1000;
        public ColorPaletteType Type => ColorPaletteType.GradientCyclic;

        protected abstract (float, int)[] Stops { get; }
        protected virtual float CyclesAcrossMaxIter => 4f;

        public int Map(float smooth, float distance, int iterations)
        {
            float t = ((smooth * 0.05f) % 1f + 1f) % 1f;
            return NewtonColorHelper.SampleStops(Stops, t);
        }

        public int MapNewton(int basin, int totalBasins, int iter, int maxIter, double zr, double zi)
        {
            if (basin < 0) return unchecked((int)0xFF000000);
            float raw = iter / (float)Math.Max(1, maxIter) * CyclesAcrossMaxIter;
            float t = (raw % 1f + 1f) % 1f;
            return NewtonColorHelper.SampleStops(Stops, t);
        }
    }

    public sealed class NewtonCyclicAuroraMap : NewtonIterCyclicBase
    {
        public static string Name => "Newton Cyclic Aurora";
        public static string Category => "Newton";
        public static string Description => "Cycling aurora — teals, greens, magentas by iter.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.Cyclic | ColorMapFeatures.GradientBased;

        private static readonly (float, int)[] _stops =
        {
            (0.00f, NewtonColorHelper.Rgb(  5,  30,  40)),
            (0.25f, NewtonColorHelper.Rgb( 20, 200, 130)),
            (0.50f, NewtonColorHelper.Rgb( 60, 220, 230)),
            (0.75f, NewtonColorHelper.Rgb(180,  80, 220)),
            (1.00f, NewtonColorHelper.Rgb(  5,  30,  40)),
        };
        protected override (float, int)[] Stops => _stops;
    }

    public sealed class NewtonCyclicPlasmaMap : NewtonIterCyclicBase
    {
        public static string Name => "Newton Cyclic Plasma";
        public static string Category => "Newton";
        public static string Description => "Magenta → orange → yellow plasma cycle.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.Cyclic | ColorMapFeatures.GradientBased;

        private static readonly (float, int)[] _stops =
        {
            (0.00f, NewtonColorHelper.Rgb( 13,   8, 135)),
            (0.30f, NewtonColorHelper.Rgb(156,  23, 158)),
            (0.60f, NewtonColorHelper.Rgb(237, 121,  83)),
            (0.85f, NewtonColorHelper.Rgb(252, 230,  56)),
            (1.00f, NewtonColorHelper.Rgb( 13,   8, 135)),
        };
        protected override (float, int)[] Stops => _stops;
    }

    public sealed class NewtonCyclicTwilightMap : NewtonIterCyclicBase
    {
        public static string Name => "Newton Cyclic Twilight";
        public static string Category => "Newton";
        public static string Description => "Cyan/lavender/ochre twilight cycle.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.Cyclic | ColorMapFeatures.GradientBased;

        private static readonly (float, int)[] _stops =
        {
            (0.00f, NewtonColorHelper.Rgb( 30,  30,  80)),
            (0.25f, NewtonColorHelper.Rgb(120,  90, 180)),
            (0.50f, NewtonColorHelper.Rgb(240, 200, 170)),
            (0.75f, NewtonColorHelper.Rgb(180, 120,  90)),
            (1.00f, NewtonColorHelper.Rgb( 30,  30,  80)),
        };
        protected override (float, int)[] Stops => _stops;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Kind E — Argument (phase of final z)
    // ══════════════════════════════════════════════════════════════════════════

    public abstract class NewtonArgumentBase : INewtonColorMap
    {
        public int MaxIterations { get; set; } = 1000;
        public ColorPaletteType Type => ColorPaletteType.Algorithmic;

        protected abstract int Render(float hue, float shade);

        public int Map(float smooth, float distance, int iterations)
        {
            float t = ((smooth * 0.05f) % 1f + 1f) % 1f;
            return Render(t, 1f);
        }

        public int MapNewton(int basin, int totalBasins, int iter, int maxIter, double zr, double zi)
        {
            if (basin < 0) return unchecked((int)0xFF000000);
            float ang = (float)Math.Atan2(zi, zr);
            float hue = (ang / (2f * MathF.PI) + 1f) % 1f;
            float shade = 1f - MathF.Min(iter / (float)Math.Max(1, maxIter), 0.85f);
            return Render(hue, shade);
        }
    }

    public sealed class NewtonArgumentHsvMap : NewtonArgumentBase
    {
        public static string Name => "Newton Argument HSV";
        public static string Category => "Newton";
        public static string Description => "Hue from arg(z) at convergence, shaded by speed.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesFinalZ;

        protected override int Render(float hue, float shade)
            => NewtonColorHelper.HsvArgb(hue, 1f, shade);
    }

    public sealed class NewtonArgumentPastelMap : NewtonArgumentBase
    {
        public static string Name => "Newton Argument Pastel";
        public static string Category => "Newton";
        public static string Description => "Pastel hue from arg(z), gentle shading.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesFinalZ;

        protected override int Render(float hue, float shade)
            => NewtonColorHelper.HsvArgb(hue, 0.40f, 0.55f + 0.45f * shade);
    }

    public sealed class NewtonArgumentSpectrumMap : NewtonArgumentBase
    {
        public static string Name => "Newton Argument Spectrum";
        public static string Category => "Newton";
        public static string Description => "Saturated spectrum from arg(z); dark on slow convergers.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesFinalZ | ColorMapFeatures.HighContrast;

        protected override int Render(float hue, float shade)
            => NewtonColorHelper.HsvArgb(hue, 1f, MathF.Pow(shade, 1.5f));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Kind F — Distance to Root (halo / glow)
    // ══════════════════════════════════════════════════════════════════════════

    public abstract class NewtonDistanceBase : INewtonColorMap
    {
        public int MaxIterations { get; set; } = 1000;
        public ColorPaletteType Type => ColorPaletteType.Algorithmic;

        protected abstract int BasinHue(int basin, int totalBasins);
        protected virtual float GlowFalloff => 80.0f;
        protected virtual float BaseFloor => 0.4f;

        public int Map(float smooth, float distance, int iterations)
        {
            float s = 1f - MathF.Min(smooth / Math.Max(1, iterations), 1f);
            return NewtonColorHelper.ApplyShade(BasinHue(0, 3), s);
        }

        public int MapNewton(int basin, int totalBasins, int iter, int maxIter, double zr, double zi)
        {
            if (basin < 0) return unchecked((int)0xFF000000);
            double rootR = Math.Cos(2 * Math.PI * basin / totalBasins);
            double rootI = Math.Sin(2 * Math.PI * basin / totalBasins);
            double dx = zr - rootR, dy = zi - rootI;
            double dist = Math.Sqrt(dx * dx + dy * dy);
            float glow = (float)Math.Exp(-dist * GlowFalloff);
            float speed = 1f - MathF.Min(iter / (float)Math.Max(1, maxIter), 0.9f);
            float intensity = MathF.Max(glow, speed * BaseFloor);
            return NewtonColorHelper.ApplyShade(BasinHue(basin, totalBasins), intensity);
        }
    }

    public sealed class NewtonDistanceGlowMap : NewtonDistanceBase
    {
        public static string Name => "Newton Distance Glow";
        public static string Category => "Newton";
        public static string Description => "Bright glow at each root, exponential fall-off.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesFinalZ;

        protected override int BasinHue(int basin, int totalBasins)
            => NewtonColorHelper.HsvArgb((float)basin / Math.Max(1, totalBasins), 0.9f, 1f);
    }

    public sealed class NewtonDistanceHaloMap : NewtonDistanceBase
    {
        public static string Name => "Newton Distance Halo";
        public static string Category => "Newton";
        public static string Description => "Cool halos around each root, hue-shifted from classic.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesFinalZ;

        protected override float GlowFalloff => 50f;
        protected override float BaseFloor => 0.25f;

        protected override int BasinHue(int basin, int totalBasins)
            => NewtonColorHelper.HsvArgb((float)basin / Math.Max(1, totalBasins) + 0.5f, 0.60f, 1f);
    }

    public sealed class NewtonDistanceInfernoMap : NewtonDistanceBase
    {
        public static string Name => "Newton Distance Inferno";
        public static string Category => "Newton";
        public static string Description => "Hot red/orange glow on roots, fading to black.";
        public static ColorMapFeatures Features =>
            ColorMapFeatures.UsesSmooth | ColorMapFeatures.UsesFinalZ | ColorMapFeatures.HighContrast;

        protected override float GlowFalloff => 100f;
        protected override float BaseFloor => 0.15f;

        protected override int BasinHue(int basin, int totalBasins)
        {
            float h = 0.02f + 0.10f * (Math.Max(0, basin) % Math.Max(1, totalBasins));
            return NewtonColorHelper.HsvArgb(h, 1f, 1f);
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Kind G — Banded / Striped (discrete iter bands per basin)
    // ══════════════════════════════════════════════════════════════════════════

    public abstract class NewtonBandedBase : INewtonColorMap
    {
        public int MaxIterations { get; set; } = 1000;
        public ColorPaletteType Type => ColorPaletteType.Texture;

        protected abstract int[] BandColors { get; }
        protected virtual int BandWidth => 3;

        public int Map(float smooth, float distance, int iterations)
        {
            int band = ((int)Math.Abs(smooth) / BandWidth) % BandColors.Length;
            return BandColors[band];
        }

        public int MapNewton(int basin, int totalBasins, int iter, int maxIter, double zr, double zi)
        {
            if (basin < 0) return unchecked((int)0xFF000000);
            int idx = (iter / BandWidth + Math.Max(0, basin)) % BandColors.Length;
            return BandColors[idx];
        }
    }

    public sealed class NewtonBandedMonoMap : NewtonBandedBase
    {
        public static string Name => "Newton Banded Mono";
        public static string Category => "Newton";
        public static string Description => "Monochrome iteration bands, rotated per basin.";
        public static ColorMapFeatures Features => ColorMapFeatures.UsesSmooth;

        private static readonly int[] _bands =
        {
            NewtonColorHelper.Rgb( 20,  20,  20),
            NewtonColorHelper.Rgb( 70,  70,  70),
            NewtonColorHelper.Rgb(130, 130, 130),
            NewtonColorHelper.Rgb(190, 190, 190),
            NewtonColorHelper.Rgb(240, 240, 240),
        };
        protected override int[] BandColors => _bands;
    }

    public sealed class NewtonBandedTricolorMap : NewtonBandedBase
    {
        public static string Name => "Newton Banded Tricolor";
        public static string Category => "Newton";
        public static string Description => "Red/white/blue iteration bands, rotated per basin.";
        public static ColorMapFeatures Features => ColorMapFeatures.UsesSmooth;

        private static readonly int[] _bands =
        {
            NewtonColorHelper.Rgb(200,  30,  30),
            NewtonColorHelper.Rgb(240, 240, 240),
            NewtonColorHelper.Rgb( 30,  50, 200),
        };
        protected override int[] BandColors => _bands;
    }

    public sealed class NewtonBandedSpectrumMap : NewtonBandedBase
    {
        public static string Name => "Newton Banded Spectrum";
        public static string Category => "Newton";
        public static string Description => "Rainbow spectrum bands rotating per basin.";
        public static ColorMapFeatures Features => ColorMapFeatures.UsesSmooth;

        private static readonly int[] _bands = BuildSpectrum();
        private static int[] BuildSpectrum()
        {
            var bands = new int[12];
            for (int i = 0; i < bands.Length; i++)
                bands[i] = NewtonColorHelper.HsvArgb(i / (float)bands.Length, 0.9f, 1f);
            return bands;
        }
        protected override int[] BandColors => _bands;
    }
}
