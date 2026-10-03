// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Engine/Export/SceneThemeResolver.cs
//
// #1052 — resolve a scene shot's SceneThemePlan against the live theme / region
// libraries. The single place both the offline exporter (SceneVideoRenderer)
// and live scene playback (HostColorThemeService.ResolveSceneShotThemes) get
// their theme schedule from, so the two can't drift.

using System;
using System.Collections.Generic;

using FracturingFog.Abstractions.Animation;

namespace FracturingFog.Models
{
    public static class SceneThemeResolver
    {
        /// <summary>Theme schedule for <paramref name="shot"/>: shot theme → the
        /// region's first valid curated theme → HSV, with optional rotation.</summary>
        public static SceneThemePlan Plan(SceneShot shot)
        {
            ArgumentNullException.ThrowIfNull(shot);
            FractalRegion? region = string.IsNullOrEmpty(shot.RegionName)
                ? null
                : FractalRegionLibrary.Instance.FindByName(shot.RegionName);
            return Plan(shot, region);
        }

        public static SceneThemePlan Plan(SceneShot shot, FractalRegion? region)
        {
            var known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in ColorPalette.GetPaletteNames()) known.TryAdd(n, n);

            string? Canonical(string name)
            {
                if (known.TryGetValue(name, out var exact)) return exact;
                var aliased = LegacyNameAliases.Resolve(name);
                return aliased != null && known.TryGetValue(aliased, out var current) ? current : null;
            }

            return SceneThemePlan.For(shot, region?.CuratedThemes, Canonical, HsvPalette.Name);
        }
    }
}
