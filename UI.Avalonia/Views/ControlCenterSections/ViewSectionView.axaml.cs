using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FracturingFog.UI.Avalonia.Views.ControlCenterSections;

/// <summary>Control Center "View" section, extracted so it can render inline in
/// <see cref="ControlCenterView"/> and, independently, in a detached
/// PanelHostWindow. DataContext is the shared ControlCenterViewModel either
/// way, so the two stay in lock-step.</summary>
public sealed partial class ViewSectionView : UserControl
{
    public ViewSectionView() => AvaloniaXamlLoader.Load(this);
}
