// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Hosting/HostPaletteViewParamService.cs
//
// Roadmap slice S10-LW.1 (PaletteBuilder-Design.md §4a, #392 / #690) — the host-side
// implementation of the palette view-parameter keystone. Reads the live render's active
// smooth-iteration field (via FractalRenderHost) and normalises it to the palette
// parameter t∈[0,1] with the pure ColorCore helper, so the render-free palette VM can
// build the view histogram (S10.3) and re-tint the fractal locally (S10.2/S10.3) without
// a re-render. Mirrors HostPaletteExtractionService: the Engine-facing glue lives here,
// the VM only ever sees the FracturingFog.Imaging interface.

using FracturingFog.Imaging;
using FracturingFog.Rendering;

namespace FracturingFog.Hosting
{
    /// <summary>Host implementation of <see cref="IPaletteViewParamService"/> (roadmap
    /// S10-LW.1, #690): snapshots the live render's active view parameter.</summary>
    public sealed class HostPaletteViewParamService : IPaletteViewParamService
    {
        private readonly FractalRenderHost? _renderHost;

        public HostPaletteViewParamService(FractalRenderHost? renderHost)
        {
            _renderHost = renderHost;
        }

        /// <inheritdoc/>
        public bool TryGetViewParam(out PaletteViewSample sample)
        {
            sample = default;
            if (_renderHost is null) return false;
            if (!_renderHost.TryGetActiveSmoothField(out var smooth, out int maxIters, out int w, out int h))
                return false;

            var t = ViewParamNormalizer.NormalizeSmooth(smooth, maxIters, w * h);
            sample = new PaletteViewSample(t, w, h);
            return sample.HasData;
        }
    }
}
