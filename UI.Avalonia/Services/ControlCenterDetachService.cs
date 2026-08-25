// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// UI.Avalonia/Services/ControlCenterDetachService.cs
//
// Opens a Control Center section as its own floating (detached) window (#494).
//
// Previously this logic lived only in ControlCenterView code-behind, so a
// detached panel could not be reopened unless the Control Center view was alive
// and the user clicked detach. Workspaces need to reopen a saved detached panel
// on recall (possibly at app start, before the Control Center is ever shown), so
// the creation moves here — reachable by both the view's detach button and the
// workspace restore opener. Each detached window registers under its section's
// WindowRole so workspace capture/restore/reconcile see it like any satellite.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

using FracturingFog.Models;
using FracturingFog.UI.Avalonia.ViewModels;
using FracturingFog.UI.Avalonia.Views.ControlCenterSections;

namespace FracturingFog.UI.Avalonia.Services;

/// <summary>Creates/activates detached Control Center section windows, keyed by
/// <see cref="WindowRole"/> through the <see cref="WindowService"/> registry.</summary>
public static class ControlCenterDetachService
{
    /// <summary>The registry role for a section's detached window.</summary>
    public static WindowRole RoleFor(ControlCenterSection section) => section switch
    {
        ControlCenterSection.View       => WindowRole.DetachedViewPanel,
        ControlCenterSection.Explore    => WindowRole.DetachedExplorePanel,
        ControlCenterSection.ColorLight => WindowRole.DetachedColorLightPanel,
        ControlCenterSection.Capture    => WindowRole.DetachedCapturePanel,
        ControlCenterSection.Assets     => WindowRole.DetachedAssetsPanel,
        ControlCenterSection.Advanced   => WindowRole.DetachedAdvancedPanel,
        _                               => WindowRole.DetachedViewPanel,
    };

    /// <summary>The section a detached role maps back to, or null when the role is
    /// not a detached-panel role.</summary>
    public static ControlCenterSection? SectionFor(WindowRole role) => role switch
    {
        WindowRole.DetachedViewPanel       => ControlCenterSection.View,
        WindowRole.DetachedExplorePanel    => ControlCenterSection.Explore,
        WindowRole.DetachedColorLightPanel => ControlCenterSection.ColorLight,
        WindowRole.DetachedCapturePanel    => ControlCenterSection.Capture,
        WindowRole.DetachedAssetsPanel     => ControlCenterSection.Assets,
        WindowRole.DetachedAdvancedPanel   => ControlCenterSection.Advanced,
        _                                  => null,
    };

    private static Control NewSectionView(ControlCenterSection section) => section switch
    {
        ControlCenterSection.View       => new ViewSectionView(),
        ControlCenterSection.Explore    => new ExploreSectionView(),
        ControlCenterSection.ColorLight => new ColorLightSectionView(),
        ControlCenterSection.Capture    => new CaptureSectionView(),
        ControlCenterSection.Assets     => new AssetsSectionView(),
        ControlCenterSection.Advanced   => new AdvancedSectionView(),
        _                               => new ViewSectionView(),
    };

    /// <summary>Open the section in its own window (or re-focus the existing one),
    /// bound to <paramref name="vm"/> so docked + detached stay in lock-step. The
    /// window is owned by the render MainWindow — not the Control Center window —
    /// so it survives the Control Center closing. Registered under its role for
    /// workspace capture/restore.</summary>
    public static void Open(ControlCenterSection section, ControlCenterViewModel vm)
    {
        if (vm == null) return;
        var role = RoleFor(section);

        var owner = (Application.Current?.ApplicationLifetime
            as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

        var existing = WindowService.Find(role);
        if (existing != null)
        {
            try
            {
                if (!existing.IsVisible)
                {
                    if (owner != null) existing.Show(owner);
                    else existing.Show();
                }
                existing.Activate();
            }
            catch { }
            return;
        }

        var content = NewSectionView(section);
        content.DataContext = vm;
        // Inset on the content Margin (not ScrollViewer Padding) so the last
        // control can scroll fully into view — see ControlCenterView note.
        content.Margin = new Thickness(12);
        var scroller = new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        var host = new PanelHostWindow(
            scroller,
            new PanelHostOptions(
                "Control Center — " + ControlCenterViewModel.LabelFor(section),
                Width: 360, MinWidth: 300, Height: 520, MinHeight: 200,
                SizeToContentHeight: false, CanResize: true, ShowInTaskbar: true,
                StartupLocation: WindowStartupLocation.CenterOwner,
                Background: new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E))));

        WindowService.RegisterWindow(role, host);

        if (owner != null) host.Show(owner);
        else host.Show();
    }
}
