// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Hosting/HostPaletteThemeSourceService.cs
//
// Feature #709 (PaletteBuilder, #392) — host-side read of the live render's active theme
// as gradient stops, so the palette viewer can load the current theme for refinement.
// Reads FractalRenderHost.ColorMap (the active IColorMap); a gradient theme exposes its
// stops via GradientColorMap.ExportStops. Mirrors the other palette host services: the
// Engine-facing glue lives here, the VM sees only the FracturingFog.Imaging interface.

using System.Collections.Generic;
using FracturingFog.Imaging;
using FracturingFog.Models;
using FracturingFog.Rendering;

namespace FracturingFog.Hosting
{
    /// <summary>Host implementation of <see cref="IPaletteThemeSourceService"/> (feature
    /// #709): exposes the live render's active gradient theme as stops.</summary>
    public sealed class HostPaletteThemeSourceService : IPaletteThemeSourceService
    {
        private readonly FractalRenderHost? _renderHost;

        public HostPaletteThemeSourceService(FractalRenderHost? renderHost)
        {
            _renderHost = renderHost;
        }

        /// <inheritdoc/>
        public bool TryGetActiveTheme(out IReadOnlyList<PaletteStop> stops, out string name)
        {
            stops = System.Array.Empty<PaletteStop>();
            name = "";
            var host = _renderHost;
            if (host is null) return false;

            name = string.IsNullOrWhiteSpace(host.ThemeName) ? "Current theme" : host.ThemeName!;

            // Only gradient themes expose stops; procedural / cosine maps don't.
            if (host.ColorMap is GradientColorMap g)
            {
                var list = new List<PaletteStop>();
                foreach (var cs in g.ExportStops)
                    list.Add(new PaletteStop(cs.Position, cs.Color.R, cs.Color.G, cs.Color.B));
                if (list.Count >= 2)
                {
                    stops = list;
                    return true;
                }
            }
            return false;
        }
    }
}
