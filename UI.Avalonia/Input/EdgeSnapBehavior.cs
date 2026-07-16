// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// EdgeSnapBehavior.cs
//
// Single-line opt-in: in a floating panel Window constructor call
// EdgeSnapBehavior.Attach(this). The window then magnetically snaps its
// edges to the screen work-area and to other open app windows (including the
// render MainWindow) when the user pauses a drag near an alignment.
//
// Snap-on-pause, NOT snap-during-drag: each PositionChanged restarts a short
// debounce timer; the snap is computed + applied only once movement settles.
// That keeps us out of the OS title-bar drag loop (setting Position mid-drag
// fights the cursor and jitters), while still feeling magnetic when the user
// lets a window come to rest. Programmatic snap moves are guarded so they
// don't retrigger the timer.
//
// Everything is in physical pixels: Window.Position is physical; screen
// WorkingArea is physical; sibling frame sizes are DIPs scaled by each
// window's own DesktopScaling.

using System;
using System.Collections.Generic;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace FracturingFog.UI.Avalonia.Input;

internal static class EdgeSnapBehavior
{
    // Physical-pixel gap within which an edge clicks into alignment.
    private const int SnapThresholdPx = 14;

    // Movement must pause this long before we snap (drag-settle detection).
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(120);

    public static void Attach(Window window)
    {
        bool applying = false;
        var timer = new DispatcherTimer { Interval = SettleDelay };

        void Snap()
        {
            timer?.Stop();
            if (window.WindowState != WindowState.Normal) return;

            var self = FrameRectOf(window);
            if (self.Width <= 0 || self.Height <= 0) return;

            int left = self.X, top = self.Y;
            int w = self.Width, h = self.Height;

            // ── candidate X (vertical guide lines) ──
            int bestX = left, bestXDist = SnapThresholdPx + 1;
            void ConsiderX(int candLeft)
            {
                int d = Math.Abs(left - candLeft);
                if (d <= SnapThresholdPx && d < bestXDist) { bestXDist = d; bestX = candLeft; }
            }
            int bestY = top, bestYDist = SnapThresholdPx + 1;
            void ConsiderY(int candTop)
            {
                int d = Math.Abs(top - candTop);
                if (d <= SnapThresholdPx && d < bestYDist) { bestYDist = d; bestY = candTop; }
            }

            // Screen work area (snap inside its edges).
            var wa = window.Screens?.ScreenFromWindow(window)?.WorkingArea;
            if (wa is { } area)
            {
                ConsiderX(area.X);                       // left edge to WA left
                ConsiderX(area.X + area.Width - w);      // right edge to WA right
                ConsiderY(area.Y);                       // top edge to WA top
                ConsiderY(area.Y + area.Height - h);     // bottom edge to WA bottom
            }

            // Sibling windows (dock edge-to-edge or align edges).
            foreach (var sib in Siblings(window))
            {
                var r = FrameRectOf(sib);
                if (r.Width <= 0 || r.Height <= 0) continue;

                ConsiderX(r.X);                    // left aligns to sib left
                ConsiderX(r.X + r.Width);          // left docks to sib right
                ConsiderX(r.X - w);                // right docks to sib left
                ConsiderX(r.X + r.Width - w);      // right aligns to sib right

                ConsiderY(r.Y);
                ConsiderY(r.Y + r.Height);
                ConsiderY(r.Y - h);
                ConsiderY(r.Y + r.Height - h);
            }

            if (bestX == left && bestY == top) return; // nothing to snap

            applying = true;
            try { window.Position = new PixelPoint(bestX, bestY); }
            finally { applying = false; }
        }

        timer.Tick += (_, _) => Snap();

        window.PositionChanged += (_, _) =>
        {
            if (applying) return;
            timer.Stop();
            timer.Start();
        };

        // Stop the timer when the window goes away so a queued tick can't fire
        // against a closed window.
        window.Closed += (_, _) => timer.Stop();
    }

    private static PixelRect FrameRectOf(Window w)
    {
        double scale = w.DesktopScaling;
        var fs = w.FrameSize ?? new Size(w.Width, w.Height);
        int pw = (int)Math.Round(fs.Width * scale);
        int ph = (int)Math.Round(fs.Height * scale);
        return new PixelRect(w.Position, new PixelSize(Math.Max(0, pw), Math.Max(0, ph)));
    }

    private static IEnumerable<Window> Siblings(Window self)
    {
        if (global::Avalonia.Application.Current?.ApplicationLifetime
            is not IClassicDesktopStyleApplicationLifetime desktop)
            yield break;

        foreach (var w in desktop.Windows)
        {
            if (ReferenceEquals(w, self)) continue;
            if (!w.IsVisible) continue;
            if (w.WindowState != WindowState.Normal) continue;
            yield return w;
        }
    }
}
