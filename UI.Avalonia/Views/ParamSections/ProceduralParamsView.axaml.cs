using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FracturingFog.UI.Avalonia.Views.ParamSections;

/// <summary>Wave 5.8 — procedural / stochastic param sections extracted from
/// FractalParamsView. Shares the parent's FractalParamsViewModel DataContext.</summary>
public sealed partial class ProceduralParamsView : UserControl
{
    public ProceduralParamsView() => AvaloniaXamlLoader.Load(this);
}
