// Views/StopEditorPanel.axaml.cs
//
// UserControl bound to PaletteResultViewModel — renders one row per stop
// with hex preview, numeric Position + R + G + B editors, lock toggle,
// move-up / move-down / delete buttons.
//
// Mutations route through the parent PaletteResultViewModel so the export
// pipeline (which reads EffectiveStops) and the gradient/swatch strip
// previews stay in sync on the next render.

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using FracturingFog.UI.Avalonia.ViewModels;

namespace PaletteBuilder.Views;

public sealed partial class StopEditorPanel : UserControl
{
    public StopEditorPanel()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private PaletteResultViewModel? Vm => DataContext as PaletteResultViewModel;

    private int IndexOfTag(object? sender)
    {
        var vm = Vm;
        if (vm is null) return -1;
        if (sender is not Control c || c.Tag is not EditableStopViewModel stop) return -1;
        return vm.EditableStops.IndexOf(stop);
    }

    private void OnMoveUp(object? sender, RoutedEventArgs e)
    {
        int idx = IndexOfTag(sender);
        if (idx >= 0) Vm?.MoveStopUp(idx);
    }

    private void OnMoveDown(object? sender, RoutedEventArgs e)
    {
        int idx = IndexOfTag(sender);
        if (idx >= 0) Vm?.MoveStopDown(idx);
    }

    private void OnRemove(object? sender, RoutedEventArgs e)
    {
        int idx = IndexOfTag(sender);
        if (idx >= 0) Vm?.RemoveStop(idx);
    }

    private void OnNormalize(object? sender, RoutedEventArgs e)
        => Vm?.NormalizePositions();
}
