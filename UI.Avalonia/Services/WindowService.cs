using System;
using System.Linq;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Threading;

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
