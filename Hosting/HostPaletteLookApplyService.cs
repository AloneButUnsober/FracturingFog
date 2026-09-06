// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Hosting/HostPaletteLookApplyService.cs
//
// Roadmap slice S10-LW.4b (PaletteBuilder-Design.md §4a, #392 / #695) — host-side apply of
// a scene "look"'s material + light tints to the live render. Writes into the shared
// ViewState.FractalParameters.Lighting (the same LightingFxData the Fractal-Params / FX
// dialog edits) and re-renders via FractalRenderHost.Trigger — so a look adopted in the
// palette tool takes effect immediately. Mirrors HostPaletteExtractionService /
// HostPaletteViewParamService: the Engine-facing glue lives here, the VM sees only the
// FracturingFog.Imaging interface.

using System;
using FracturingFog.Imaging;
using FracturingFog.Rendering;

namespace FracturingFog.Hosting
{
    /// <summary>Host implementation of <see cref="IPaletteLookApplyService"/> (roadmap
    /// S10-LW.4b, #695): writes a look's material + light tints into the live render.</summary>
    public sealed class HostPaletteLookApplyService : IPaletteLookApplyService
    {
        private readonly FractalRenderHost? _renderHost;

        public HostPaletteLookApplyService(FractalRenderHost? renderHost)
        {
            _renderHost = renderHost;
        }

        /// <inheritdoc/>
        public void ApplyLook(
            double roughness, double metallic,
            (byte r, byte g, byte b) keyTint,
            (byte r, byte g, byte b) skyTint)
        {
            var host = _renderHost;
            var p = host?.ViewState?.FractalParameters;
            if (host is null || p is null) return;

            // LightingFxData is a struct field on the (class) FractalParameters — mutate a
            // copy and write it back, then re-render. Colours are packed 0xAARRGGBB
            // (shading decodes R at <<16, G at <<8, B at <<0).
            var fx = p.Lighting;
            fx.Roughness = Math.Clamp(roughness, 0.0, 1.0);
            fx.Metallic = Math.Clamp(metallic, 0.0, 1.0);
            fx.Light1.Color = Pack(keyTint);
            fx.BgTopColor = Pack(skyTint);
            fx.BgBottomColor = Pack(skyTint);
            p.Lighting = fx;

            host.Trigger();
        }

        private static uint Pack((byte r, byte g, byte b) c)
            => 0xFF000000u | ((uint)c.r << 16) | ((uint)c.g << 8) | c.b;
    }
}
