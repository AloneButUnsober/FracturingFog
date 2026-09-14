// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;

using FracturingFog.Models;

namespace FracturingFog.UI.Avalonia.Services
{
    /// <summary>
    /// Single owner of the live global UI-scale value (#809 / S1 #810): the
    /// current zoom applied to every dialog/window except the render window and
    /// splash. This slice is state + change-event + persistence only — the
    /// window LayoutTransform wrapper (S2 #811), the popup font-size token
    /// (S3 #812), the Control Center buttons + shortcuts (S4 #813), and the
    /// workspace capture/restore (S5 #814) all consume this service.
    ///
    /// All value decisions delegate to <see cref="UiScaleLadder"/> (pure, in
    /// Abstractions); persistence delegates to <see cref="UiScaleStore"/>. The
    /// initial value is loaded from disk at first access — the field initializer
    /// runs before any subscriber exists, so no change event fires for the load
    /// (subscribers read <see cref="Scale"/> for the starting value instead).
    ///
    /// UI-thread affinity: mutate only from the UI thread (that is where the
    /// buttons/shortcuts fire and where subscribers apply the transform).
    /// </summary>
    public static class UiScaleService
    {
        private static double _scale = UiScaleStore.Load(); // already snapped

        /// <summary>Raised after <see cref="Scale"/> changes, with the new value.
        /// Never raised for the initial disk load.</summary>
        public static event Action<double>? ScaleChanged;

        /// <summary>The current global UI scale (a <see cref="UiScaleLadder"/>
        /// step; 1.0 = 100%).</summary>
        public static double Scale => _scale;

        /// <summary>Smallest selectable scale (for enabling/labeling the − button).</summary>
        public static double Min => UiScaleLadder.Min;

        /// <summary>Largest selectable scale (for enabling/labeling the + button).</summary>
        public static double Max => UiScaleLadder.Max;

        /// <summary>Sets the scale to the ladder step nearest
        /// <paramref name="value"/>, persisting and raising
        /// <see cref="ScaleChanged"/> only when the snapped value actually
        /// differs from the current one.</summary>
        public static void Set(double value)
        {
            double snapped = UiScaleLadder.Snap(value);
            if (snapped == _scale) return;
            _scale = snapped;
            UiScaleStore.Save(_scale);
            ScaleChanged?.Invoke(_scale);
        }

        /// <summary>Steps up one ladder rung (saturates at <see cref="Max"/>).</summary>
        public static void Increase() => Set(UiScaleLadder.Next(_scale));

        /// <summary>Steps down one ladder rung (saturates at <see cref="Min"/>).</summary>
        public static void Decrease() => Set(UiScaleLadder.Prev(_scale));

        /// <summary>Returns to the neutral 100% scale.</summary>
        public static void Reset() => Set(UiScaleLadder.Default);
    }
}
