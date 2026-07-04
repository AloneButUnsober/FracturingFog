using Avalonia.Controls;
using Avalonia.Markup.Xaml;

using FracturingFog.UI.Avalonia.Input;

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
}
