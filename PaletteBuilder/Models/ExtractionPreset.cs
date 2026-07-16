// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Models/ExtractionPreset.cs
//
// JSON-serializable snapshot of every option the user can tweak in the
// extraction panel. Persisted via PresetStore so the user can save a tuning
// they like ("Architectural Photo", "Sunset", "Anime") and reload it on a
// future image.
//
// Mirrors the public option properties on ImagePaletteViewModel one-for-one.
// Bumping a property here without a matching VM property is harmless (it's
// ignored on apply); the reverse drops the new option from saved presets
// until added — keep them in sync.

namespace PaletteBuilder.Models;

public sealed class ExtractionPreset
{
    public string Name { get; set; } = "Untitled";

    public int MethodIndex { get; set; }
    public int ColorCount { get; set; } = 8;
    public int SpaceIndex { get; set; } = 1;      // 0=RGB 1=Lab 2=HSL 3=OkLab
    public int DownsampleMax { get; set; } = 256;
    public int SortIndex { get; set; }
    public double DedupDeltaE { get; set; } = 2.0;
    public bool WeightedPositions { get; set; }
    public bool ExcludeNearBlack { get; set; }
    public bool ExcludeNearWhite { get; set; }

    // Phase 1 additions.
    public int DedupMetricIndex { get; set; }     // 0=ΔE76 1=ΔE2000
    public bool GammaCorrect { get; set; }

    // Phase 2 additions (extractor-specific tuning).
    public double Bandwidth { get; set; } = 25.0;
    public double DbscanEpsilon { get; set; } = 8.0;
    public int DbscanMinPts { get; set; } = 20;
    public double SpatialWeight { get; set; } = 0.5;

    // Phase 3 additions (preprocessing). ROI is deliberately NOT persisted —
    // it makes no sense to apply yesterday's crop rect to today's image.
    public bool ExcludeTransparent { get; set; }
    public double MinSaturation { get; set; }
    public double MaxSaturation { get; set; } = 1.0;
    public double MinLightness { get; set; }
    public double MaxLightness { get; set; } = 1.0;

    public bool UseSaliency { get; set; }
    public double SaliencyThreshold { get; set; } = 0.3;
}
