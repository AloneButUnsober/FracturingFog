// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// UI.Avalonia/Services/WorkspaceService.cs
//
// Workspace capture/restore (#433, slice 3/3 — #471). Snapshots the current
// window arrangement into a WorkspaceLayout (slice 1 model) and applies a saved
// one back onto the live windows (via the slice 2 WindowService registry).
//
// Ordering is the crux of restore: the render-window MODE (Standard/Mini/Toy/
// Span) is applied FIRST, because Mini/Toy/Span mutate MainWindow geometry and
// stash the prior geometry (MainWindow.axaml.cs _preMini*/_preToy*). Applying
// geometry before the mode settles would fight that stash — so geometry is
// applied on a deferred UI-thread post, after the mode transition. Satellites
// restore last.
//
// Monitor handling: Avalonia Position is virtual-desktop absolute, so replaying
// the saved absolute Position reproduces the monitor placement when the display
// layout is unchanged. WindowService.EnsureOnScreen is the net for when a saved
// monitor is gone — it nudges an off-screen window back onto a real screen.

using System;
using System.Collections.Generic;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

using FracturingFog.Models;
using FracturingFog.UI.Avalonia.ViewModels;

namespace FracturingFog.UI.Avalonia.Services
{
    /// <summary>Captures and restores the live window arrangement as a
    /// <see cref="WorkspaceLayout"/>. UI-thread only.</summary>
    public static class WorkspaceService
    {
        // ── Capture ──────────────────────────────────────────────────────────

        /// <summary>Snapshot the current arrangement into a named workspace.</summary>
        public static WorkspaceLayout Capture(string name, ShellViewModel shell)
        {
            var layout = new WorkspaceLayout { Name = name ?? string.Empty };

            var rw = layout.RenderWindow;
            rw.Shape = CurrentShape(shell);
            rw.ResolutionName = shell.FloatingMenu.SelectedResolution;
            rw.Topmost = shell.IsRenderTopmost;
            rw.ToolbarVisible = shell.IsToolbarVisible;
            rw.StatusBarVisible = shell.IsStatusBarVisible;

            var main = WindowService.ActiveMainWindow;
            if (main != null)
            {
                rw.DisplayState = ToDisplayState(main.WindowState);
                rw.X = main.Position.X;
                rw.Y = main.Position.Y;
                rw.Width = (int)Math.Round(main.Bounds.Width);
                rw.Height = (int)Math.Round(main.Bounds.Height);
                rw.Monitor = WindowService.CaptureMonitor(main);
            }

            foreach (var (role, win) in WindowService.RegisteredWindows())
            {
                if (role == WindowRole.RenderWindow) continue;
                layout.Satellites.Add(new SatelliteWindowState
                {
                    Role = role,
                    Visible = win.IsVisible,
                    DisplayState = ToDisplayState(win.WindowState),
                    X = win.Position.X,
                    Y = win.Position.Y,
                    Width = (int)Math.Round(win.Bounds.Width),
                    Height = (int)Math.Round(win.Bounds.Height),
                    Monitor = WindowService.CaptureMonitor(win),
                });
            }

            return layout;
        }

        // ── Restore ──────────────────────────────────────────────────────────

        /// <summary>Apply a saved workspace onto the live windows. Mode first,
        /// then geometry (deferred), then satellites (deferred). Safe to call with
        /// a null/empty layout (no-op).</summary>
        public static void Restore(WorkspaceLayout? layout, ShellViewModel shell)
        {
            if (layout?.RenderWindow == null) return;
            var rw = layout.RenderWindow;

            // 1. Mode FIRST — this may mutate MainWindow geometry (Mini/Toy/Span).
            ApplyShape(shell, rw.Shape);

            // 2. Always-on-top is a plain flag flip (MainWindow mirrors it onto
            //    Window.Topmost).
            shell.IsRenderTopmost = rw.Topmost;

            // 3. Geometry after the mode transition settles.
            Dispatcher.UIThread.Post(
                () => ApplyRenderGeometry(shell, rw),
                DispatcherPriority.Background);

            // 4. Satellites last, after the render window has taken its shape.
            Dispatcher.UIThread.Post(
                () => RestoreSatellites(layout, shell),
                DispatcherPriority.Background);
        }

        private static void ApplyRenderGeometry(ShellViewModel shell, RenderWindowState rw)
        {
            var main = WindowService.ActiveMainWindow;
            if (main == null) return;

            // Mini/Toy/Span own their own geometry AND their own toolbar/status
            // visibility — the mode set them. Only a Standard window takes the
            // saved position/size/state/chrome; touching a borderless-fullscreen
            // (Span) or compact (Mini/Toy) window here would undo the mode.
            if (rw.Shape != RenderWindowShape.Standard) return;

            // Chrome visibility is part of the captured arrangement.
            shell.IsToolbarVisible = rw.ToolbarVisible;
            shell.IsStatusBarVisible = rw.StatusBarVisible;

            try
            {
                main.WindowState = ToWindowState(rw.DisplayState);

                if (rw.X != 0 || rw.Y != 0)
                    main.Position = new PixelPoint(rw.X, rw.Y);

                // Prefer the saved resolution preset (it drives the render size the
                // host resizes to); only fall back to raw window size when no
                // preset was captured. Both are irrelevant while maximized.
                if (main.WindowState == WindowState.Normal)
                {
                    if (!string.IsNullOrEmpty(rw.ResolutionName))
                        shell.FloatingMenu.SelectedResolution = rw.ResolutionName;
                    else if (rw.Width > 0 && rw.Height > 0)
                    {
                        main.Width = rw.Width;
                        main.Height = rw.Height;
                    }
                }

                WindowService.EnsureOnScreen(main);
            }
            catch { /* best-effort — a bad saved geometry must not crash restore */ }
        }

        // Bounded retries for placing a reopened satellite. Openers reopen a
        // closed window via Dispatcher.Post (async), so the window often does not
        // exist yet on the first placement attempt — retry until it appears.
        private const int SatellitePlaceAttempts = 15;

        private static void RestoreSatellites(WorkspaceLayout layout, ShellViewModel shell)
        {
            // Reconcile EVERY satellite role, not just the ones in this layout —
            // otherwise recalling workspace B leaves windows that workspace A
            // opened still on screen (the "jumble" when switching workspaces).
            // A role present + visible → open/place; anything else → hide.
            var byRole = new Dictionary<WindowRole, SatelliteWindowState>();
            foreach (var s in layout.Satellites) byRole[s.Role] = s;

            foreach (WindowRole role in Enum.GetValues(typeof(WindowRole)))
            {
                if (role == WindowRole.RenderWindow) continue;

                if (byRole.TryGetValue(role, out var s) && s.Visible)
                {
                    // Ensure open (reopens via the slice-2 opener when closed), then
                    // place once the window actually exists and is shown.
                    WindowService.Open(role);
                    PlaceWhenReady(s, SatellitePlaceAttempts);
                }
                else
                {
                    HideSatellite(role, shell);
                }
            }
        }

        // Close/hide a satellite the target workspace does not want open.
        //
        // Flag-backed windows (MiniMap/MiniDepth/PostFxHud/ColorTheme) route through
        // their shell visibility flag so the shell's own Sync* path Hides them and
        // the flag stays consistent — and their re-open (flag = true) re-shows the
        // same hidden instance correctly.
        //
        // The bootstrap-owned singletons (UserEquation/Sandbox/UserBulb/Relief3D/
        // LightingFx/AsciiFx) must be CLOSED, not hidden. Their openers key off a
        // static s_*Win singleton: a hidden-but-non-null window makes the opener
        // Activate() a hidden window (never re-shows) or, for LightingFx, toggle it
        // shut — the "needs two Recall clicks" / "form never displays" bugs. Closing
        // nulls the singleton (via each window's Closed handler) so the next Open
        // creates a fresh, visible window.
        private static void HideSatellite(WindowRole role, ShellViewModel shell)
        {
            switch (role)
            {
                case WindowRole.MiniMap:          shell.IsMiniMapVisible = false; return;
                case WindowRole.MiniDepth:        shell.IsMiniDepthVisible = false; return;
                case WindowRole.PostFxHud:        shell.IsPostFxHudVisible = false; return;
                case WindowRole.ColorThemeEditor: shell.IsColorThemeEditorVisible = false; return;
                default:
                    var win = WindowService.Find(role);
                    if (win != null)
                    {
                        try { win.Close(); } catch { }
                    }
                    return;
            }
        }

        // How many extra times to re-assert a satellite's geometry after it first
        // becomes visible. A freshly-shown window can re-run its CenterOwner
        // startup placement a tick or two after IsVisible flips true, which would
        // otherwise clobber our position; re-asserting overrides that.
        private const int SatelliteReasserts = 3;

        // Applies a satellite's geometry once its window is live + visible. When
        // the opener is still creating the window (async), re-posts up to
        // <paramref name="attemptsLeft"/> times so a reopened Lighting/FX or
        // Color-Theme editor lands where it was saved instead of at its default
        // CenterOwner placement.
        private static void PlaceWhenReady(SatelliteWindowState s, int attemptsLeft)
        {
            Dispatcher.UIThread.Post(() =>
            {
                var win = WindowService.Find(s.Role);
                if (win == null || !win.IsVisible)
                {
                    if (attemptsLeft > 0) PlaceWhenReady(s, attemptsLeft - 1);
                    return;
                }
                ApplyGeometry(win, s);
                Reassert(s, SatelliteReasserts);
            }, DispatcherPriority.Background);
        }

        // Re-apply the saved geometry on the next few UI ticks to beat any late
        // CenterOwner re-centering the window performs after becoming visible.
        private static void Reassert(SatelliteWindowState s, int left)
        {
            if (left <= 0) return;
            Dispatcher.UIThread.Post(() =>
            {
                var win = WindowService.Find(s.Role);
                if (win is { IsVisible: true }) ApplyGeometry(win, s);
                Reassert(s, left - 1);
            }, DispatcherPriority.Background);
        }

        private static void ApplyGeometry(Window win, SatelliteWindowState s)
        {
            try
            {
                // Manual disables the window's CenterOwner startup placement, which
                // otherwise re-centers over the render window and clobbers X/Y.
                win.WindowStartupLocation = WindowStartupLocation.Manual;
                if (s.Width > 0) win.Width = s.Width;
                if (s.Height > 0) win.Height = s.Height;
                if (s.X != 0 || s.Y != 0)
                    win.Position = new PixelPoint(s.X, s.Y);
                WindowService.EnsureOnScreen(win);
            }
            catch { }
        }

        // ── Mode helpers ─────────────────────────────────────────────────────

        private static RenderWindowShape CurrentShape(ShellViewModel shell)
        {
            if (shell.IsSpanning) return RenderWindowShape.Span;
            if (shell.IsToyMode) return RenderWindowShape.Toy;
            if (shell.IsMiniMode) return RenderWindowShape.Mini;
            return RenderWindowShape.Standard;
        }

        // Reach the target shape from any current one: clear the modes that are
        // not the target first (each exit restores its own stashed geometry), then
        // enter the target. Mini/Toy are mutually exclusive; Span is independent
        // but we treat all four as one selector here.
        private static void ApplyShape(ShellViewModel shell, RenderWindowShape shape)
        {
            if (shape != RenderWindowShape.Span && shell.IsSpanning)
                shell.SetSpanState(false);
            if (shape != RenderWindowShape.Mini && shell.IsMiniMode)
                shell.IsMiniMode = false;
            if (shape != RenderWindowShape.Toy && shell.IsToyMode)
                shell.IsToyMode = false;

            switch (shape)
            {
                case RenderWindowShape.Span:
                    shell.SetSpanState(true);
                    break;
                case RenderWindowShape.Mini:
                    shell.IsMiniMode = true;
                    break;
                case RenderWindowShape.Toy:
                    shell.IsToyMode = true;
                    break;
                case RenderWindowShape.Standard:
                default:
                    break; // all modes already cleared above
            }
        }

        // ── Enum bridges (UI-neutral model ↔ Avalonia) ───────────────────────

        private static WindowDisplayState ToDisplayState(WindowState s) => s switch
        {
            WindowState.Minimized => WindowDisplayState.Minimized,
            WindowState.Maximized => WindowDisplayState.Maximized,
            WindowState.FullScreen => WindowDisplayState.FullScreen,
            _ => WindowDisplayState.Normal,
        };

        private static WindowState ToWindowState(WindowDisplayState s) => s switch
        {
            WindowDisplayState.Minimized => WindowState.Minimized,
            WindowDisplayState.Maximized => WindowState.Maximized,
            WindowDisplayState.FullScreen => WindowState.FullScreen,
            _ => WindowState.Normal,
        };
    }
}
