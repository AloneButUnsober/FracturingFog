using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FracturingFog.UI.Avalonia.Views.ParamSections;

/// <summary>Wave 5.8 — 3D/4D raymarcher param sections extracted from
/// FractalParamsView. Shares the parent's FractalParamsViewModel DataContext.</summary>
public sealed partial class Raymarch3DParamsView : UserControl
{
    public Raymarch3DParamsView() => AvaloniaXamlLoader.Load(this);
}
