// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using Avalonia.Controls;
using Avalonia.Markup.Xaml;

using FracturingFog.UI.Avalonia.Controls;
using FracturingFog.UI.Avalonia.Input;
using FracturingFog.UI.Avalonia.ViewModels;

namespace FracturingFog.UI.Avalonia.Views;

/// <summary>
/// DEPRECATED (rc3-ui-prep). The FloatingMenu VIEW is retired — the Control
/// Center (<see cref="ControlCenterView"/> / <see cref="ViewModels.ControlCenterViewModel"/>)
/// is the main menu form going forward. No UI path opens this window anymore
/// (toolbar Menu button, the "M" hotkey, and the render-window right-click all
/// route to the Control Center). Do NOT wire this view back up or add features
/// to it; build new menu surfaces in the Control Center instead. Only
/// <see cref="ViewModels.FloatingMenuViewModel"/> lives on — the Control Center
/// reuses it as the shared command/state hub.
///
/// Avalonia port of <c>FloatingMenu</c>. Main floating control panel —
/// region navigation, theme library, post-FX sliders, slideshow + video
/// launchers. All host coupling flows through
/// <see cref="ViewModels.FloatingMenuViewModel"/> events; host populates
/// the combo lists via <c>SetRegions</c> / <c>SetThemes</c> /
/// <c>SetResolutions</c> / <c>SetQualities</c> at startup and after each
/// import / delete / reload.
/// </summary>
public sealed partial class FloatingMenuView : Window
{
    private bool _sortMenusAttached;
    private bool _coordFocusHooked;

    public FloatingMenuView()
    {
        AvaloniaXamlLoader.Load(this);
        EscapeCloseBehavior.Attach(this);
        // Attach right-click sort menus once the window (and its bound VM) is up.
        Opened += (_, _) =>
        {
            AttachSortMenus();
            HookCoordFocusTracking();
        };
    }

    private void AttachSortMenus()
    {
        if (_sortMenusAttached || DataContext is not FloatingMenuViewModel vm) return;
        ComboSortMenu.Attach(this.FindControl<ComboBox>("RegionCombo"), vm.BuildRegionSortMenu);
        ComboSortMenu.Attach(this.FindControl<ComboBox>("ThemeCombo"), vm.BuildThemeSortMenu);
        _sortMenusAttached = true;
    }

    // Track which coord textbox the user is editing so the host's FrameCompleted
    // refresh skips it. Without this, every completed frame overwrites the box
    // mid-typing and Go applies a stale value.
    private void HookCoordFocusTracking()
    {
        if (_coordFocusHooked || DataContext is not FloatingMenuViewModel vm) return;
        Hook("CoordCX",   "CX");
        Hook("CoordCY",   "CY");
        Hook("CoordZoom", "Zoom");
        Hook("CoordIter", "Iter");
        _coordFocusHooked = true;

        void Hook(string ctrlName, string field)
        {
            var box = this.FindControl<TextBox>(ctrlName);
            if (box == null) return;
            box.GotFocus  += (_, _) => vm.ActiveCoordField = field;
            box.LostFocus += (_, _) => { if (vm.ActiveCoordField == field) vm.ActiveCoordField = null; };
        }
    }
}
