using System.Linq;

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;

using FracturingFog.UI.Avalonia.Input;
using FracturingFog.UI.Avalonia.ViewModels;

namespace FracturingFog.UI.Avalonia.Views;

/// <summary>
/// Avalonia Asset Manager. Read-only three-pane browser over every saved asset
/// type (Animation Roadmap Sub-goal A, phase A1). Host wires the VM's
/// CloseRequested event; Esc closes. Edit routing to each type's own editor
/// lands in A2.
/// </summary>
public sealed partial class AssetManagerView : Window
{
    public AssetManagerView()
    {
        AvaloniaXamlLoader.Load(this);
        EscapeCloseBehavior.Attach(this);
    }

    // Double-clicking a row routes it to its type's editor (A2), same as the
    // detail-pane "Edit in editor…" button.
    private void OnAssetDoubleTapped(object? sender, TappedEventArgs e)
    {
        (DataContext as AssetManagerViewModel)?.RaiseOpen();
    }

    // Bulk export (A3): gather the middle list's multi-selection and hand it to
    // the VM, which builds the zip and raises ExportRequested for the host.
    private void OnExportBundle(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not AssetManagerViewModel vm) return;
        var list = this.FindControl<ListBox>("AssetsList");
        if (list == null) return;

        var rows = list.SelectedItems?
            .OfType<AssetRowViewModel>()
            .ToList() ?? new System.Collections.Generic.List<AssetRowViewModel>();
        vm.ExportBundle(rows);
    }

    // Bulk import (A3 import): the VM raises ImportRequested, which the shell
    // bubbles to the host (open picker + overwrite prompt + file read + report).
    private void OnImportBundle(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        (DataContext as AssetManagerViewModel)?.RequestImport();
    }
}
