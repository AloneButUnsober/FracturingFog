using FracturingFog.Interefaces;

using System;
using System.Collections.Generic;
using System.Text;

namespace FracturingFog.Models
{
    public class ColorPalette
    {
        public static List<IColorMap> Palettes { get; } = new List<IColorMap>
        {
            new HsvPalette(),
            new HsvModified(),
            new RedAndBlack(),
            new GrayscalePalette(),
            new FirePalette(),
            new AuroraColorMap(),
            new DeepSpaceBlueMap(),
            new BlackbodyColorMap(),
            new DistanceGlowMap(),
            new EarthToneMap(),
            new GoldenRatioMap(),
            new IcefireColorMap(),
            new InfernoColorMap(),
            new MonoBandMap(),
            new OceanDepthMap(),
            new RainbowColorMap(),
            new TriColorMap()
        };

        public static IColorMap GetPaletteByName(string name)
        {
            foreach (var palette in Palettes)
            {
                if (palette.GetType().GetProperty("Name")?.GetValue(null)?.ToString() == name)
                {
                    return palette;
                }
            }
            return new HsvPalette(); // or throw an exception if preferred
        }

        public static List<string> GetPaletteNames()
        {
            List<string> names = new List<string>();
            foreach (var palette in Palettes)
            {
                var name = palette.GetType().GetProperty("Name")?.GetValue(null)?.ToString();
                if (!string.IsNullOrEmpty(name))
                {
                    names.Add(name);
                }
            }
            return names;
        }
    }
}
