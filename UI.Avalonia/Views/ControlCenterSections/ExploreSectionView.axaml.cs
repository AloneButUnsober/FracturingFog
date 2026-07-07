using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FracturingFog.UI.Avalonia.Views.ControlCenterSections;

/// <summary>Control Center "Explore" section (region navigation, coordinates,
/// deep-zoom diagnostics). See <see cref="ViewSectionView"/> for the detach
/// rationale.</summary>
public sealed partial class ExploreSectionView : UserControl
{
    public ExploreSectionView() => AvaloniaXamlLoader.Load(this);
}
