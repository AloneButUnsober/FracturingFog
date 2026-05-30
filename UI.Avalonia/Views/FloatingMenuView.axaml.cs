using Avalonia.Controls;
using Avalonia.Markup.Xaml;

using FracturingFog.UI.Avalonia.Controls;
using FracturingFog.UI.Avalonia.ViewModels;

namespace FracturingFog.UI.Avalonia.Views;

/// <summary>
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

    public FloatingMenuView()
    {
        AvaloniaXamlLoader.Load(this);
        // Attach right-click sort menus once the window (and its bound VM) is up.
        Opened += (_, _) => AttachSortMenus();
    }

    private void AttachSortMenus()
    {
        if (_sortMenusAttached || DataContext is not FloatingMenuViewModel vm) return;
        ComboSortMenu.Attach(this.FindControl<ComboBox>("RegionCombo"), vm.BuildRegionSortMenu);
        ComboSortMenu.Attach(this.FindControl<ComboBox>("ThemeCombo"), vm.BuildThemeSortMenu);
        _sortMenusAttached = true;
    }
}
