// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Imaging/PaletteExtraction/PerceptualKMeansExtractor.cs
//
// Roadmap slice S10-LW.5 (PaletteBuilder-Design.md §4a, #392 / #691) — expose the S10.5
// perceptual extraction core (ColorCore PaletteExtractionCore.KMeansOkLab, PR #676) as a
// selectable extraction METHOD alongside the existing extractors. Unlike KMeansExtractor
// (which clusters in RGB / Lab / HSL feature space), this clusters in OkLab — perceptual
// distances, k-means++ seeded — so the swatches sit where the eye groups colour. Thin
// adapter: the clustering + weighting lives in the tested core; this only marshals the
// sampler's RGB buffer in and the ExtractedColor list out.

using System;
using System.Collections.Generic;

namespace FracturingFog.Imaging.PaletteExtraction
{
    public sealed class PerceptualKMeansExtractor : IPaletteExtractor
    {
        public string Name => "Perceptual K-Means (OkLab)";

        public IReadOnlyList<ExtractedColor> Extract(byte[] rgb, int pixelCount, PaletteExtractionOptions opts)
        {
            if (rgb == null || pixelCount <= 0) return Array.Empty<ExtractedColor>();

            var samples = new List<(byte r, byte g, byte b)>(pixelCount);
            for (int i = 0; i < pixelCount; i++)
            {
                int j = i * 3;
                samples.Add((rgb[j], rgb[j + 1], rgb[j + 2]));
            }

            int k = Math.Max(2, opts.ColorCount);
            var clusters = PaletteExtractionCore.KMeansOkLab(samples, k, opts.RandomSeed);

            var result = new List<ExtractedColor>(clusters.Count);
            foreach (var c in clusters)
                result.Add(new ExtractedColor(c.R, c.G, c.B, c.Weight));
            return result;
        }
    }
}
