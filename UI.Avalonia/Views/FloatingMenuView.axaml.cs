using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FracturingFog.UI.Avalonia.Views;

/// <summary>
/// Avalonia port of <c>FloatingMenu</c>. Main floating control panel —
/// region navigation, theme library, post-FX sliders, slideshow + video
/// launchers. All host coupling flows through
/// <see cref="ViewModels.FloatingMenuViewModel"/> events; host populates
/// the combo lists via <c>SetRegions</c> / <c>SetThemes</c> /
/// <c>SetResolutions</c> / <c>SetQualities</c> at startup and after each
/// import / delete / reload.
/// </summary>
public sealed partial class FloatingMenuView : Window
{
    public FloatingMenuView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
