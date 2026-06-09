using System;

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;

using FracturingFog.UI.Avalonia.Controls;
using FracturingFog.UI.Avalonia.Input;
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
    private ColorThemeEditorViewModel? _boundVm;

    public ColorThemeEditorView()
    {
        AvaloniaXamlLoader.Load(this);
        EscapeCloseBehavior.Attach(this);
        Opened += (_, _) => AttachSortMenus();
        DataContextChanged += OnDataContextChanged;

        // Route any pointer-press inside a stop row up to the parent VM as
        // a selection. Inner controls (NumericUpDown, ColorPicker) consume
        // their own clicks first; this handler fires on bubble for the
        // Border background and any unhandled hits, which is the closest
        // approximation to "click anywhere on the row to select it".
        AddHandler(InputElement.PointerPressedEvent, OnAnyPointerPressed,
            RoutingStrategies.Bubble, handledEventsToo: false);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_boundVm != null)
        {
            _boundVm.ScrollStopIntoViewRequested -= OnScrollStopRequested;
            _boundVm.ScrollBandIntoViewRequested -= OnScrollBandRequested;
            _boundVm.FocusNameRequested -= OnFocusNameRequested;
        }
        _boundVm = DataContext as ColorThemeEditorViewModel;
        if (_boundVm != null)
        {
            _boundVm.ScrollStopIntoViewRequested += OnScrollStopRequested;
            _boundVm.ScrollBandIntoViewRequested += OnScrollBandRequested;
            _boundVm.FocusNameRequested += OnFocusNameRequested;
        }
    }

    private void OnFocusNameRequested(object? sender, EventArgs e)
    {
        // After "Save" pick: bring this window to front and put keyboard
        // focus + caret in the Name field so the user can rename + Save.
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (!IsVisible) return;
                Activate();
                var tb = this.FindControl<TextBox>("NameField");
                if (tb != null)
                {
                    tb.Focus();
                    tb.SelectAll();
                }
            }
            catch { }
        }, DispatcherPriority.Background);
    }

    private void OnScrollStopRequested(object? sender, ColorStopRowVm row)
        => ScrollItemIntoView("StopsItems", row);

    private void OnScrollBandRequested(object? sender, MaterialBandRowVm row)
        => ScrollItemIntoView("BandsItems", row);

    /// <summary>Walks the named ItemsControl, finds the container for the
    /// row item, and scrolls it into view. Containers are realised lazily
    /// inside a virtualizing stack panel, so a Dispatcher hop covers the
    /// case where the container hasn't been materialised yet.</summary>
    private void ScrollItemIntoView(string itemsControlName, object item)
    {
        var ic = this.FindControl<ItemsControl>(itemsControlName);
        if (ic == null) return;
        Dispatcher.UIThread.Post(() =>
        {
            var container = ic.ContainerFromItem(item) as Control;
            container?.BringIntoView();
        }, DispatcherPriority.Background);
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
