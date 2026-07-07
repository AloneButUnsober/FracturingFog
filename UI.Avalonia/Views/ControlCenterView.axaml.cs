using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FracturingFog.UI.Avalonia.Views;

/// <summary>
/// Phase S1 Control Center shell — a SplitView nav-rail + sectioned content
/// that re-presents <see cref="ViewModels.FloatingMenuViewModel"/> /
/// <see cref="ViewModels.ShellViewModel"/>. Hybrid-shell: a UserControl hosted
/// modeless by MainWindow.SyncControlCenter (PanelHostWindow), so it can dock
/// or pop out and is 2nd-monitor aware.
/// </summary>
public sealed partial class ControlCenterView : UserControl
{
    public ControlCenterView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
