using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FracturingFog.UI.Avalonia.Views.ControlCenterSections;

/// <summary>Control Center "Assets" section (libraries + editor launchers). See
/// <see cref="ViewSectionView"/> for the detach rationale.</summary>
public sealed partial class AssetsSectionView : UserControl
{
    public AssetsSectionView() => AvaloniaXamlLoader.Load(this);
}
