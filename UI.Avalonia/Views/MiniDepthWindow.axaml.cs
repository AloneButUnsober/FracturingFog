using System;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using FracturingFog.UI.Avalonia.Controls;

namespace FracturingFog.UI.Avalonia.Views;

/// <summary>Topmost borderless window hosting the MiniDepthControl.
/// Configure(...) on the inner control is wired in MainWindow code-behind
/// the first time the user opens the panel.</summary>
public sealed partial class MiniDepthWindow : Window
{
    public MiniDepthControl Inner { get; private set; } = null!;

    public MiniDepthWindow()
    {
        AvaloniaXamlLoader.Load(this);
        Inner = this.FindControl<MiniDepthControl>("Depth")
            ?? throw new InvalidOperationException("MiniDepthControl x:Name=Depth missing");
    }
}
