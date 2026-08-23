// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;

using FracturingFog.Models;
using FracturingFog.UI.Avalonia.Views;

namespace FracturingFog.UI.Avalonia.Services
{
    /// <summary>
    /// Single owner of window opening for the Avalonia shell.
    ///
    /// Before this existed, every dialog self-showed with an ad-hoc mix of
    /// <c>ShowDialog(owner)</c> vs <c>Show()</c>, per-dialog
    /// <c>WindowStartupLocation</c>, scattered <c>Topmost=true</c>, and repeated
    /// <c>Opened += Activate()</c> Win32 nested-modal workarounds — the root
    /// cause of windows opening on-top/under unpredictably and of dialogs
    /// rendering off-screen or clipped on small monitors (nothing clamped a
    /// SizeToContent window to the display).
    ///
    /// Routing every open through here gives one place that resolves the owner,
    /// clamps the window to fit the target screen, applies the foreground fix,
    /// and (optionally) targets a specific monitor.
    /// </summary>
    public static class WindowService
    {
        /// <summary>The desktop lifetime's main window, or null in non-desktop
        /// hosts. Central resolver so call-sites stop re-deriving it.</summary>
        public static Window? ActiveMainWindow =>
            Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desk
                ? desk.MainWindow
                : null;

        /// <summary>Where a newly opened window should be positioned.</summary>
        public enum Placement
        {
            /// <summary>Centered over the owner (falls back to active screen).</summary>
            CenterOwner,
            /// <summary>Centered on the owner's current screen.</summary>
            CenterScreen,
            /// <summary>Centered on a non-primary monitor if one exists, else the
            /// owner's screen. The primitive behind "auto-populate second
            /// monitor" (the policy toggle itself lands later).</summary>
            SecondaryMonitor,
        }

        // Fraction of the target screen's working area a dialog may occupy.
        // Leaves a margin so title bar + buttons are never pushed off-screen.
        private const double DefaultFitFraction = 0.92;

        /// <summary>
        /// Prepares and shows <paramref name="win"/> as a modal dialog owned by
        /// <paramref name="owner"/> (or the main window when null). Applies the
        /// screen-fit clamp, the foreground-activation fix, and placement.
        /// Fire-and-forget friendly: callers that resolve via <c>Closed</c> can
        /// ignore the returned task. Falls back to a modeless <c>Show()</c> when
        /// no owner is available.
        /// </summary>
        public static Task ShowDialogAsync(
            Window win,
            Window? owner = null,
            Placement placement = Placement.CenterOwner,
            double fitFraction = DefaultFitFraction)
        {
            if (win == null) throw new ArgumentNullException(nameof(win));
            owner ??= ActiveMainWindow;

            Prepare(win, owner, placement, fitFraction);

            if (owner != null)
                return win.ShowDialog(owner);

            win.Show();
            return Task.CompletedTask;
        }

        /// <summary>
        /// Wraps a feature panel (<c>UserControl</c>) in a <see cref="PanelHostWindow"/>
        /// and shows it modally with the standard placement + screen-fit
        /// treatment. Resolves to the host's <see cref="PanelHostWindow.DialogResult"/>
        /// once closed (true = VM committed, false = cancelled, null = X/Esc).
        /// This is the pop-out path for the Hybrid shell's F3 conversion.
        /// </summary>
        public static Task<bool?> ShowPanelDialogAsync(
            Control panel,
            PanelHostOptions options,
            Window? owner = null,
            Placement placement = Placement.CenterOwner)
        {
            if (panel == null) throw new ArgumentNullException(nameof(panel));
            if (options == null) throw new ArgumentNullException(nameof(options));

            var host = new PanelHostWindow(panel, options);
            var tcs = new TaskCompletionSource<bool?>();
            host.Closed += (_, _) => tcs.TrySetResult(host.DialogResult);
            _ = ShowDialogAsync(host, owner, placement);
            return tcs.Task;
        }

        // ── Standalone Volumetric Lighting & FX window ───────────────────────
        //
        // Single app-wide instance. Every launcher (Fractal Params panel, Relief
        // 3D panel, the shell menu) routes through here so the window is ALWAYS
        // owned by the main render window — never the calling panel. An
        // owned-of-panel VL FX window closed with its owner (Avalonia cascades
        // owned windows shut), which is the "closing the calling form also
        // closed VL FX" bug; owning to the main window makes it truly standalone.
        // The Lighting/FX block is fractal-type-independent, so a single window
        // shared across callers is correct.
        private static PanelHostWindow? s_lightingFx;

        private static PanelHostOptions LightingFxOptions(string title) =>
            new PanelHostOptions(
                title,
                Width: 520, Height: 720, MinWidth: 440, MinHeight: 400,
                SizeToContentHeight: false, CanResize: true, ShowInTaskbar: true,
                StartupLocation: WindowStartupLocation.CenterOwner,
                Background: new SolidColorBrush(Color.FromRgb(0x28, 0x28, 0x28)));

        /// <summary>
        /// Opens the app-wide Volumetric Lighting &amp; FX window (or re-focuses
        /// it, rebinding to <paramref name="dataContext"/>, when already open).
        /// Non-modal and owned by the main window, so closing the launching panel
        /// never closes it. Returns the live window.
        /// </summary>
        public static PanelHostWindow ShowLightingFx(
            object dataContext, string title = "Volumetric Lighting & FX")
        {
            if (s_lightingFx is { IsVisible: true })
            {
                s_lightingFx.DataContext = dataContext;
                try { s_lightingFx.Activate(); } catch { }
                return s_lightingFx;
            }

            var win = new PanelHostWindow(new LightingFxDialog(), LightingFxOptions(title))
            {
                DataContext = dataContext,
            };
            win.Closed += (_, _) =>
            {
                if (ReferenceEquals(s_lightingFx, win)) s_lightingFx = null;
            };
            s_lightingFx = win;
            RegisterWindow(WindowRole.LightingFx, win);

            var owner = ActiveMainWindow;
            // Prepare() gives the standard screen-fit clamp, foreground fix, and
            // (crucially in Span/Mini/Toy mode) the render-window Topmost match so
            // the panel is not hidden behind the borderless render surface.
            Prepare(win, owner);
            if (owner != null) win.Show(owner);
            else win.Show();
            return win;
        }

        /// <summary>
        /// Menu behavior: closes the Lighting &amp; FX window if open, otherwise
        /// opens it via <see cref="ShowLightingFx"/> using a lazily built
        /// DataContext (so the VM is only constructed when actually shown).
        /// </summary>
        public static void ToggleLightingFx(
            Func<object> dataContextFactory, string title = "Volumetric Lighting & FX")
        {
            if (s_lightingFx != null)
            {
                try { s_lightingFx.Close(); } catch { }
                s_lightingFx = null;
                return;
            }
            ShowLightingFx(dataContextFactory(), title);
        }

        /// <summary>
        /// Prepares and shows <paramref name="win"/> modelessly (non-modal),
        /// e.g. tool/palette windows and pop-out panels. Same placement +
        /// screen-fit + activation treatment as <see cref="ShowDialogAsync"/>.
        /// </summary>
        public static void Show(
            Window win,
            Window? owner = null,
            Placement placement = Placement.CenterOwner,
            double fitFraction = DefaultFitFraction)
        {
            if (win == null) throw new ArgumentNullException(nameof(win));
            owner ??= ActiveMainWindow;
            Prepare(win, owner, placement, fitFraction);
            win.Show();
        }

        /// <summary>
        /// Applies the shared window treatment without showing: screen-fit
        /// <c>MaxWidth</c>/<c>MaxHeight</c> clamp, foreground activation on
        /// open, and placement. Public so pop-out hosts (Phase F3/S2) can reuse
        /// it on windows they show themselves.
        /// </summary>
        public static void Prepare(
            Window win,
            Window? owner,
            Placement placement = Placement.CenterOwner,
            double fitFraction = DefaultFitFraction)
        {
            var screen = ResolveScreen(win, owner, placement);
            ApplyScreenFit(win, screen, fitFraction);
            ApplyPlacement(win, owner, screen, placement);
            MatchRenderTopmost(win);

            // Centralized nested-modal foreground fix. A modal-of-a-modal on
            // Win32 (with ShowInTaskbar=false) does not reliably front; forcing
            // Activate once shown fixes it and is a no-op where it already
            // fronts (e.g. X11). Also re-clamps position in case the window
            // landed on a different screen than predicted.
            win.Opened += (_, _) =>
            {
                try { win.Activate(); } catch { }
                try { ClampIntoScreen(win); } catch { }
            };
        }

        // ── Topmost inheritance ──────────────────────────────────────────────

        /// <summary>
        /// Floats <paramref name="win"/> above the render window whenever that
        /// window is always-on-top — Mini / Toy / Span modes make it borderless
        /// Topmost, and the "On Top" toggle sets it too. Without this, a dialog
        /// (worse, a nested modal-of-a-modal, which does not reliably front on
        /// Win32) opens *behind* the topmost render surface and is unreachable —
        /// the user cannot see or move it (Avalonia has no Ctrl+Space to summon
        /// it). This is the general fix for #52 (Video Settings invisible when
        /// launched from Slideshow Settings in Mini/Toy mode); the per-launcher
        /// Topmost-matching in the Relief 3D / Lighting FX openers was the same
        /// fix applied one window at a time.
        /// </summary>
        private static void MatchRenderTopmost(Window win)
        {
            try
            {
                var main = ActiveMainWindow;
                if (main != null && main.Topmost && !ReferenceEquals(win, main))
                    win.Topmost = true;
            }
            catch { }
        }

        // ── Live persistent-window registry (#433 slice 2/3 — #470) ──────────
        //
        // WindowService owns window *opening* but historically tracked nothing
        // live except the lone Lighting/FX instance; the satellite editors track
        // themselves through scattered fields in MainWindow / AvaloniaShellBootstrap.
        // The workspace feature needs one place to ask "which persistent (modeless)
        // windows exist, where, on which monitor" (capture) and to reopen one by
        // role (restore). This registry is that place.
        //
        // Windows are held by WeakReference so a closed window that the owner also
        // dropped can be GC'd; a Closed handler prunes the entry eagerly too. The
        // satellite windows use Show/Hide (not Close) and stay alive in their
        // owner's field, so Find() returns them even while hidden — callers check
        // IsVisible themselves. Modal dialogs are never registered (out of scope).

        private static readonly Dictionary<WindowRole, WeakReference<Window>> s_registry = new();
        private static readonly Dictionary<WindowRole, Action> s_openers = new();
        private static readonly object s_regGate = new();

        /// <summary>Register (or replace) the live window for a role. Idempotent:
        /// re-registering a role swaps in the new window. Auto-unregisters when
        /// the window closes (a window reused via Show/Hide is never closed, so it
        /// stays registered across hide cycles — correct).</summary>
        public static void RegisterWindow(WindowRole role, Window win)
        {
            if (win == null) return;
            lock (s_regGate) s_registry[role] = new WeakReference<Window>(win);
            win.Closed += OnRegisteredWindowClosed;
        }

        private static void OnRegisteredWindowClosed(object? sender, EventArgs e)
        {
            if (sender is Window w)
            {
                w.Closed -= OnRegisteredWindowClosed;
                lock (s_regGate)
                {
                    // Only drop the entry if it still points at this window — a
                    // role re-registered to a different window must survive an old
                    // window's late Closed.
                    foreach (var kv in s_registry)
                    {
                        if (kv.Value.TryGetTarget(out var target) && ReferenceEquals(target, w))
                        {
                            s_registry.Remove(kv.Key);
                            break;
                        }
                    }
                }
            }
        }

        /// <summary>Explicitly drop a role's registration (rarely needed — Closed
        /// prunes automatically).</summary>
        public static void UnregisterWindow(WindowRole role)
        {
            lock (s_regGate) s_registry.Remove(role);
        }

        /// <summary>The live window for a role, or null when never registered or
        /// already collected. May be hidden — callers check <c>IsVisible</c>.</summary>
        public static Window? Find(WindowRole role)
        {
            lock (s_regGate)
            {
                if (s_registry.TryGetValue(role, out var wr) && wr.TryGetTarget(out var w))
                    return w;
                s_registry.Remove(role); // prune a dead entry
                return null;
            }
        }

        /// <summary>Every currently-live registered window with its role, pruning
        /// collected entries. Includes hidden windows — the caller decides what
        /// "open" means (typically <c>Window.IsVisible</c>). Snapshot, safe to
        /// enumerate while windows open/close.</summary>
        public static IReadOnlyList<(WindowRole Role, Window Window)> RegisteredWindows()
        {
            var live = new List<(WindowRole, Window)>();
            lock (s_regGate)
            {
                var dead = new List<WindowRole>();
                foreach (var kv in s_registry)
                {
                    if (kv.Value.TryGetTarget(out var w)) live.Add((kv.Key, w));
                    else dead.Add(kv.Key);
                }
                foreach (var role in dead) s_registry.Remove(role);
            }
            return live;
        }

        /// <summary>Register an opener delegate for a role — the action that opens
        /// that window from scratch (the existing OpenUserEquationEditor /
        /// ShowColorThemeEditor / ToggleMiniMap style entry points). Slice 3's
        /// restore uses <see cref="Open"/> to reopen a role that was saved-visible
        /// but is currently closed. Idempotent; last registration wins.</summary>
        public static void RegisterOpener(WindowRole role, Action opener)
        {
            if (opener == null) return;
            lock (s_regGate) s_openers[role] = opener;
        }

        /// <summary>Ensure the role's window is open and fronted. If a live window
        /// is registered it is shown/activated; otherwise the registered opener (if
        /// any) is invoked. Returns true when an open path existed (window or
        /// opener), false when the role can't be opened. Must run on the UI thread.</summary>
        public static bool Open(WindowRole role)
        {
            var win = Find(role);
            if (win != null)
            {
                try { if (!win.IsVisible) win.Show(); win.Activate(); } catch { }
                return true;
            }

            Action? opener;
            lock (s_regGate) s_openers.TryGetValue(role, out opener);
            if (opener != null)
            {
                try { opener(); } catch { }
                return true;
            }
            return false;
        }

        // ── Screen resolution ────────────────────────────────────────────────

        /// <summary>
        /// Chooses the target screen: for <see cref="Placement.SecondaryMonitor"/>
        /// the first non-primary screen if present, otherwise the owner's
        /// current screen (falling back to primary).
        /// </summary>
        private static Screen? ResolveScreen(Window win, Window? owner, Placement placement)
        {
            try
            {
                var screens = owner?.Screens ?? win.Screens;
                if (screens == null) return null;

                if (placement == Placement.SecondaryMonitor)
                {
                    var secondary = screens.All.FirstOrDefault(s => !s.IsPrimary);
                    if (secondary != null) return secondary;
                }

                if (owner != null)
                {
                    var s = screens.ScreenFromWindow(owner);
                    if (s != null) return s;
                }

                return screens.Primary ?? screens.All.FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }

        // ── Screen-fit clamp ─────────────────────────────────────────────────

        /// <summary>
        /// Constrains a window so it can never exceed the target screen's
        /// working area (× <paramref name="fitFraction"/>). Setting
        /// MaxWidth/MaxHeight before show makes SizeToContent windows fit small
        /// monitors instead of overflowing the top/bottom of the display.
        /// </summary>
        private static void ApplyScreenFit(Window win, Screen? screen, double fitFraction)
        {
            if (screen == null) return;
            double scaling = screen.Scaling <= 0 ? 1.0 : screen.Scaling;
            double waW = screen.WorkingArea.Width / scaling;
            double waH = screen.WorkingArea.Height / scaling;
            double frac = fitFraction <= 0 || fitFraction > 1 ? DefaultFitFraction : fitFraction;

            win.MaxWidth = Math.Max(240, waW * frac);
            win.MaxHeight = Math.Max(200, waH * frac);
        }

        // ── Placement ────────────────────────────────────────────────────────

        private static void ApplyPlacement(Window win, Window? owner, Screen? screen, Placement placement)
        {
            switch (placement)
            {
                case Placement.CenterOwner when owner != null:
                    win.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                    break;

                case Placement.CenterScreen:
                case Placement.SecondaryMonitor:
                    // WindowStartupLocation.CenterScreen centers on the primary
                    // screen only, so for an explicit/secondary screen we place
                    // manually once the window has measured (see Opened clamp).
                    win.WindowStartupLocation = WindowStartupLocation.Manual;
                    if (screen != null)
                        CenterOnScreen(win, screen);
                    break;

                default:
                    win.WindowStartupLocation = owner != null
                        ? WindowStartupLocation.CenterOwner
                        : WindowStartupLocation.CenterScreen;
                    break;
            }
        }

        /// <summary>Centers a window on the given screen using its declared
        /// Width/Height (best-effort before final measure; the Opened clamp
        /// corrects any overflow).</summary>
        private static void CenterOnScreen(Window win, Screen screen)
        {
            double scaling = screen.Scaling <= 0 ? 1.0 : screen.Scaling;
            var wa = screen.WorkingArea;
            double w = double.IsNaN(win.Width) ? win.MinWidth : win.Width;
            double h = double.IsNaN(win.Height) ? win.MinHeight : win.Height;
            int px = wa.X + (int)Math.Max(0, (wa.Width - w * scaling) / 2);
            int py = wa.Y + (int)Math.Max(0, (wa.Height - h * scaling) / 2);
            win.Position = new PixelPoint(px, py);
        }

        /// <summary>
        /// After the window is shown and measured, nudges it fully back onto its
        /// screen if any edge spilled past the working area — the structural
        /// guard against off-screen title bars / clipped buttons on small
        /// displays.
        /// </summary>
        private static void ClampIntoScreen(Window win)
        {
            var screen = win.Screens?.ScreenFromWindow(win)
                         ?? win.Screens?.Primary;
            if (screen == null) return;

            var wa = screen.WorkingArea;
            double scaling = screen.Scaling <= 0 ? 1.0 : screen.Scaling;
            int winW = (int)Math.Round(win.Bounds.Width * scaling);
            int winH = (int)Math.Round(win.Bounds.Height * scaling);

            int x = win.Position.X;
            int y = win.Position.Y;

            if (x + winW > wa.X + wa.Width) x = wa.X + wa.Width - winW;
            if (y + winH > wa.Y + wa.Height) y = wa.Y + wa.Height - winH;
            if (x < wa.X) x = wa.X;
            if (y < wa.Y) y = wa.Y;

            if (x != win.Position.X || y != win.Position.Y)
                win.Position = new PixelPoint(x, y);
        }
    }
}
