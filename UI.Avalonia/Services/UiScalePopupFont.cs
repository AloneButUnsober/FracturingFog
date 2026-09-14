// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;

using Avalonia;
using Avalonia.Threading;

namespace FracturingFog.UI.Avalonia.Services
{
    /// <summary>
    /// Keeps the <c>SizeFontPopup</c> application resource at
    /// <c>SizeFontBase × UiScaleService.Scale</c> (#809 / S3 #812).
    ///
    /// Popups (tooltips, combobox dropdowns, menus, flyouts) render in their own
    /// top-levels, OUTSIDE the per-window LayoutTransform that S2 (#811) applies,
    /// so that transform never scales them. The popup-leaf styles in
    /// ControlThemes.axaml bind their FontSize to <c>SizeFontPopup</c> as a
    /// DynamicResource; this driver publishes the live value so popup text tracks
    /// the global UI scale. In-window controls are deliberately NOT touched here —
    /// they already scale via the LayoutTransform, and bumping their font too
    /// would double-scale them.
    /// </summary>
    public static class UiScalePopupFont
    {
        private const double FallbackBase = 14.0;

        private static bool s_installed;

        /// <summary>Seed <c>SizeFontPopup</c> for the current scale and keep it in
        /// sync thereafter. Call once at app startup, on the UI thread. Idempotent.</summary>
        public static void Install()
        {
            if (s_installed) return;
            s_installed = true;

            Publish(UiScaleService.Scale);
            UiScaleService.ScaleChanged += OnScaleChanged;
        }

        private static void OnScaleChanged(double scale)
        {
            // ScaleChanged is raised on the UI thread today, but marshal defensively
            // so a future off-thread setter can't corrupt the resource dictionary.
            if (Dispatcher.UIThread.CheckAccess()) Publish(scale);
            else Dispatcher.UIThread.Post(() => Publish(scale));
        }

        private static void Publish(double scale)
        {
            var app = Application.Current;
            if (app == null) return;

            double baseSize =
                app.Resources.TryGetResource("SizeFontBase", app.ActualThemeVariant, out var v)
                && v is double d && d > 0
                    ? d
                    : FallbackBase;

            app.Resources["SizeFontPopup"] = baseSize * scale;
        }
    }
}
