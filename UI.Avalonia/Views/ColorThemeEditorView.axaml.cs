using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;

using FracturingFog.UI.Avalonia.Controls;
using FracturingFog.UI.Avalonia.ViewModels;

namespace FracturingFog.UI.Avalonia.Views;

/// <summary>
/// Avalonia port of <c>ColorThemeEditor</c>. Floating modeless editor for
/// data-driven colour themes. The host wires the VM's events:
/// PreviewRequested, RegionRequested, EditorThemeSelected,
/// ThemeSavedToLibrary, HelpRequested, MessageRequested, SaveFileRequested,
/// FromImageRequested, ImportPaletteRequested, ExportPaletteRequested,
/// SampleColorRequested.
/// </summary>
public sealed partial class ColorThemeEditorView : Window
{
    private bool _sortMenusAttached;

    public ColorThemeEditorView()
    {
        AvaloniaXamlLoader.Load(this);
        Opened += (_, _) => AttachSortMenus();

        // Route any pointer-press inside a stop row up to the parent VM as
        // a selection. Inner controls (NumericUpDown, ColorPicker) consume
        // their own clicks first; this handler fires on bubble for the
        // Border background and any unhandled hits, which is the closest
        // approximation to "click anywhere on the row to select it".
        AddHandler(InputElement.PointerPressedEvent, OnAnyPointerPressed,
            RoutingStrategies.Bubble, handledEventsToo: false);
    }

    private void AttachSortMenus()
    {
        if (_sortMenusAttached || DataContext is not ColorThemeEditorViewModel vm) return;
        ComboSortMenu.Attach(this.FindControl<ComboBox>("RegionCombo"), vm.BuildRegionSortMenu);
        ComboSortMenu.Attach(this.FindControl<ComboBox>("ThemeCombo"), vm.BuildThemeSortMenu);
        _sortMenusAttached = true;
    }

    private void OnAnyPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not ColorThemeEditorViewModel vm) return;
        // Walk up the visual ancestry until we find a Border whose
        // DataContext is a ColorStopRowVm — that's the row container the
        // ItemsControl template wrapped each entry in.
        var src = e.Source as global::Avalonia.Controls.Control;
        while (src != null)
        {
            if (src.DataContext is ColorStopRowVm row)
            {
                vm.SelectRow(row);
                return;
            }
            src = src.GetVisualParent() as global::Avalonia.Controls.Control;
        }
    }
}
