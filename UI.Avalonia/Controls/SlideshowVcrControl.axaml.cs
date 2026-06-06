using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FracturingFog.UI.Avalonia.Controls;

/// <summary>
/// Avalonia port of the legacy WinForms <c>SlideshowVcrPanel</c>. UserControl
/// designed to be hosted in the bottom-center of the main render surface
/// (or anywhere a transient control overlay is appropriate). All wiring goes
/// through the bound view model — the control itself owns no state.
/// </summary>
public sealed partial class SlideshowVcrControl : UserControl
{
    public SlideshowVcrControl()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
