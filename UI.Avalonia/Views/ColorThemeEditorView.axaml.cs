using Avalonia.Controls;
using Avalonia.Markup.Xaml;

using FracturingFog.UI.Avalonia.Controls;
using FracturingFog.UI.Avalonia.ViewModels;

namespace FracturingFog.UI.Avalonia.Views;

/// <summary>
/// Avalonia port of <c>ColorThemeEditor</c>. Floating modeless editor for
/// data-driven colour themes. The host wires the VM's events:
/// PreviewRequested, RegionRequested, EditorThemeSelected,
/// ThemeSavedToLibrary, HelpRequested, MessageRequested, SaveFileRequested,
/// FromImageRequested.
/// </summary>
public sealed partial class ColorThemeEditorView : Window
{
    private bool _sortMenusAttached;

    public ColorThemeEditorView()
    {
        AvaloniaXamlLoader.Load(this);
        Opened += (_, _) => AttachSortMenus();
    }

    private void AttachSortMenus()
    {
        if (_sortMenusAttached || DataContext is not ColorThemeEditorViewModel vm) return;
        ComboSortMenu.Attach(this.FindControl<ComboBox>("RegionCombo"), vm.BuildRegionSortMenu);
        ComboSortMenu.Attach(this.FindControl<ComboBox>("ThemeCombo"), vm.BuildThemeSortMenu);
        _sortMenusAttached = true;
    }
}
