// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Engine/Rendering/FractalRenderHost.LiveColorFade.cs
//
// #987 (#940 slice A) — live palette cross-fade for the slideshow. The image
// slideshow's theme transition used to freeze the view (snapshot → CPU lerp
// of two static frames), so a running leg animation had to stop for the fade.
// Here the incoming theme is blended per pixel through a BlendedColorMap (the
// same mechanism the video slideshow's in-leg theme fade uses) while renders
// keep flowing, so animation-bus frames colour through the blend and the
// animation never pauses.
//
// Only maps that need nothing beyond plain Map() can be blended: a map that
// carries an extra colour-map capability (orbit / interior / post-process /
// in-set / Newton / vector / billiard …) is detected by the calculators via
// type checks that the wrapper would hide, so the fade would render wrong.
// Those — and 3D views — refuse, and the caller falls back to the static fade.

using System;

using FracturingFog.Calculators;
using FracturingFog.Interefaces;
using FracturingFog.ViewState;

namespace FracturingFog.Rendering
{
    public sealed partial class FractalRenderHost
    {
        // Active live fade; both non-null between Begin and End. Read on the UI
        // thread only (the calc thread just sees the BlendedColorMap installed
        // as ColorMap and its volatile-enough float T).
        private BlendedColorMap? _liveFade;
        private IColorMap? _liveFadeTo;

        /// <inheritdoc/>
        public bool BeginLiveColorFade(Func<bool> applyTarget)
        {
            ArgumentNullException.ThrowIfNull(applyTarget);
            if (_disposed) return false;

            // Commit any fade still in flight so we never nest blends.
            EndLiveColorFade();

            var from = ColorMap;
            if (from == null || FractalViewState.IsThreeD(ViewState.FractalType)) return false;

            bool applied;
            try { applied = applyTarget(); }
            catch { applied = false; }

            var to = ColorMap;
            if (!applied || to == null || ReferenceEquals(from, to)
                || !CanBlendLive(from) || !CanBlendLive(to))
            {
                // Put the outgoing map back so the caller's static fallback
                // starts from exactly the state it had before this call.
                if (!ReferenceEquals(ColorMap, from)) ColorMap = from;
                return false;
            }

            // Seed the iteration range so consumers that sample the map before
            // the next Calculate (overlay contrast luma) see a sane scale.
            var blended = new BlendedColorMap(from, to, 0f) { MaxIterations = from.MaxIterations };
            ColorMap = blended;
            _liveFade = blended;
            _liveFadeTo = to;
            return true;
        }

        /// <inheritdoc/>
        public void SetLiveColorFade(double t)
        {
            var fade = _liveFade;
            if (fade == null) return;
            fade.T = (float)Math.Clamp(t, 0.0, 1.0);
        }

        /// <inheritdoc/>
        public void EndLiveColorFade()
        {
            var to = _liveFadeTo;
            _liveFade = null;
            _liveFadeTo = null;
            if (to != null && !_disposed) ColorMap = to;
        }

        /// <summary>True when <paramref name="map"/> exposes no colour-map
        /// capability beyond what <see cref="BlendedColorMap"/> itself forwards
        /// (<see cref="IColorMapWithPixelScale"/>, <see cref="INamedColorMap"/>).
        /// Any other IColorMap-derived interface is something a calculator
        /// type-checks for, which the wrapper would hide.</summary>
        public static bool CanBlendLive(IColorMap map)
        {
            foreach (var i in map.GetType().GetInterfaces())
            {
                if (i == typeof(IColorMap) || i == typeof(IColorMapWithPixelScale)) continue;
                if (typeof(IColorMap).IsAssignableFrom(i)) return false;
            }
            return true;
        }
    }
}
