using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FracturingFog.UI.Avalonia.Views.ParamSections;

/// <summary>Wave 5.8 — 2D escape-time param sections extracted from
/// FractalParamsView. Shares the parent's FractalParamsViewModel DataContext.</summary>
public sealed partial class EscapeTimeParamsView : UserControl
{
    public EscapeTimeParamsView() => AvaloniaXamlLoader.Load(this);
}
