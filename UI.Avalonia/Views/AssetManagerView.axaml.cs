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
}
