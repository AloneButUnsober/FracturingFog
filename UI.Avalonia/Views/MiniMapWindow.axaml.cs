using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FracturingFog.UI.Avalonia.Views;

/// <summary>Topmost borderless window hosting the MiniMapControl. Created on
/// demand by MainWindow code-behind when ShellViewModel.IsMiniMapVisible
/// flips true; auto-positioned over the main render area.</summary>
public sealed partial class MiniMapWindow : Window
{
    public MiniMapWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
